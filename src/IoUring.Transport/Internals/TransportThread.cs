using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using IoUring.Transport.Internals.Inbound;
using IoUring.Transport.Internals.Outbound;
using Microsoft.AspNetCore.Connections;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    [Flags]
    internal enum CompletionType : uint
    {
        Read        = 1,
        Write       = 1 << 1,
        EventFdPoll = 1 << 2,
        Connect     = 1 << 3,
        Accept      = 1 << 4
    }

    internal sealed unsafe class TransportThread : IAsyncDisposable
    {
        private const int RingSize = 4096;
        private const int ListenBacklog = 128;
        private const int MaxLoopsWithoutCompletion = 3;
        private const int MaxLoopsWithoutSubmission = 3;
        public const ulong ReadMask =        (ulong) CompletionType.Read << 32;
        public const ulong WriteMask =       (ulong) CompletionType.Write << 32;
        private const ulong EventFdPollMask = (ulong) CompletionType.EventFdPoll << 32;
        private const ulong ConnectMask =     (ulong) CompletionType.Connect << 32;
        private const ulong AcceptMask =      (ulong) CompletionType.Accept << 32;

        private static int _threadId;

        private readonly Ring _ring;
        private readonly ConcurrentQueue<ulong> _asyncOperationQueue = new ConcurrentQueue<ulong>();
        private readonly ConcurrentDictionary<int, object> _asyncOperationStates = new ConcurrentDictionary<int, object>();
        private readonly Dictionary<int, AcceptSocketContext> _acceptSockets = new Dictionary<int, AcceptSocketContext>();
        private readonly Dictionary<int, IoUringConnectionContext> _connections = new Dictionary<int, IoUringConnectionContext>();
        private readonly TransportThreadContext _threadContext;
        private readonly MemoryPool<byte> _memoryPool = new SlabMemoryPool();
        private readonly TaskCompletionSource<object> _threadCompletion = new TaskCompletionSource<object>();
        private volatile bool _disposed;
        private int _eventfd;
        private GCHandle _eventfdBytes;
        private GCHandle _eventfdIoVecHandle;
        private iovec* _eventfdIoVec;

        // variables to prevent useless spinning in the event loop
        private int _loopsWithoutSubmission;
        private int _loopsWithoutCompletion;

        public TransportThread(IoUringOptions options)
        {
            _ring = new Ring(RingSize);
            SetupEventFd();
            _threadContext = new TransportThreadContext(options, _memoryPool, _eventfd, _asyncOperationQueue);
        }

        public ValueTask<ConnectionContext> Connect(IPEndPoint endpoint)
        {
            var domain = endpoint.AddressFamily == AddressFamily.InterNetwork ? AF_INET : AF_INET6;
            LinuxSocket s = socket(domain, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, IPPROTO_TCP);
            if (_threadContext.Options.TcpNoDelay)
            {
                s.SetOption(SOL_TCP, TCP_NODELAY, 1);
            }

            var context = new OutboundConnectionContext(s, endpoint, _threadContext);
            var tcs = new TaskCompletionSource<ConnectionContext>(TaskCreationOptions.RunContinuationsAsynchronously); // Ensure the transport thread doesn't run continuations
            context.ConnectCompletion = tcs;

            endpoint.ToSockAddr(context.Addr, out var addrLength);
            context.AddrLen = addrLength;

            _asyncOperationStates[s] = context;
            _asyncOperationQueue.Enqueue(Mask(s, ConnectMask));
            _threadContext.Notify();

            return new ValueTask<ConnectionContext>(tcs.Task);
        }

        public void Bind(IPEndPoint endpoint, ChannelWriter<ConnectionContext> acceptQueue)
        {
            var domain = endpoint.AddressFamily == AddressFamily.InterNetwork ? AF_INET : AF_INET6;
            LinuxSocket s = socket(domain, SOCK_STREAM | SOCK_CLOEXEC, IPPROTO_TCP);
            s.SetOption(SOL_SOCKET, SO_REUSEADDR, 1);
            s.SetOption(SOL_SOCKET, SO_REUSEPORT, 1);
            s.Bind(endpoint);
            s.Listen(ListenBacklog);

            var context = new AcceptSocketContext(s, endpoint, acceptQueue);

            _asyncOperationStates[s] = context;
            _asyncOperationQueue.Enqueue(Mask(s, AcceptMask));
            _threadContext.Notify();
        }

        public void Run() => new Thread(obj => ((TransportThread)obj).Loop())
        {
            IsBackground = true,
            Name = $"IoUring Transport Thread - {Interlocked.Increment(ref _threadId)}"
        }.Start(this);

        private void Loop()
        {
            ReadEventFd();

            while (!_disposed)
            {
                while (_asyncOperationQueue.TryDequeue(out var operation))
                {
                    var socket = (int) operation;
                    var operationType = (CompletionType) (operation >> 32);

                    switch (operationType)
                    {
                        case CompletionType.Read when _connections.TryGetValue(socket, out var context):
                            Read(context);
                            break;
                        case CompletionType.Write when _connections.TryGetValue(socket, out var context):
                            Write(context);
                            break;
                        case CompletionType.Accept when _asyncOperationStates.Remove(socket, out var context):
                            var acceptSocketContext = (AcceptSocketContext) context;
                            _acceptSockets[socket] = acceptSocketContext;
                            Accept(acceptSocketContext);
                            break;
                        case CompletionType.Connect when _asyncOperationStates.Remove(socket, out var context):
                            var outboundConnectionContext = (OutboundConnectionContext) context;
                            _connections[socket] = outboundConnectionContext;
                            Connect(outboundConnectionContext);
                            break;
                    }
                }
                Submit();
                Complete();
            }

            _ring.Dispose();
            _threadCompletion.TrySetResult(null);
        }

        private void SetupEventFd()
        {
            int res = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
            if (res == -1) throw new ErrnoException(errno);
            _eventfd = res;

            // Pin buffer for eventfd reads via io_uring
            byte[] bytes = new byte[8];
            _eventfdBytes = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            // Pin iovec used for eventfd reads via io_uring
            var eventfdIoVec = new iovec
            {
                iov_base = (void*) _eventfdBytes.AddrOfPinnedObject(),
                iov_len = bytes.Length
            };
            _eventfdIoVecHandle = GCHandle.Alloc(eventfdIoVec, GCHandleType.Pinned);
            _eventfdIoVec = (iovec*) _eventfdIoVecHandle.AddrOfPinnedObject();
        }

        private void ReadEventFd()
        {
            Debug.WriteLine("Adding read on eventfd");
            _ring.PreparePollAdd(_eventfd, (ushort)POLLIN, options: SubmissionOption.Link);
            _ring.PrepareReadV(_eventfd, _eventfdIoVec, 1, userData: EventFdPollMask);
        }

        private void Accept(AcceptSocketContext context)
        {
            var socket = context.Socket;
            _ring.PrepareAccept(socket, (sockaddr*) context.Addr, context.AddrLen, SOCK_NONBLOCK | SOCK_CLOEXEC, Mask(socket, AcceptMask));
        }

        private void Connect(OutboundConnectionContext context)
        {
            var socket = context.Socket;
            _ring.PrepareConnect(socket, (sockaddr*) context.Addr, context.AddrLen, Mask(socket, ConnectMask));
        }

        private void Read(IoUringConnectionContext context)
        {
            var maxBufferSize = _memoryPool.MaxBufferSize;

            var writer = context.Input;
            var readHandles = context.ReadHandles;
            var readVecs = context.ReadVecs;

            var memory = writer.GetMemory(maxBufferSize);
            var handle = memory.Pin();

            readVecs[0].iov_base = handle.Pointer;
            readVecs[0].iov_len = memory.Length;

            readHandles[0] = handle;

            var socket = context.Socket;
            Debug.WriteLine($"Adding read on {(int)socket}");
            _ring.PreparePollAdd(socket, (ushort) POLLIN, options: SubmissionOption.Link);
            _ring.PrepareReadV(socket, readVecs, 1, 0, 0, Mask(socket, ReadMask));
        }

        private void Write(IoUringConnectionContext context)
        {
            var result = context.ReadResult.Result;

            var socket = context.Socket;
            if (result.IsCanceled || result.IsCompleted)
            {
                context.DisposeAsync();
                _connections.Remove(socket);
                socket.Close();
                return;
            }

            var writeHandles = context.WriteHandles;
            var writeVecs = context.WriteVecs;
            var buffer = result.Buffer;
            int ctr = 0;
            foreach (var memory in buffer)
            {
                var handle = memory.Pin();

                writeVecs[ctr].iov_base = handle.Pointer;
                writeVecs[ctr].iov_len = memory.Length;

                writeHandles[ctr] = handle;

                ctr++;
                if (ctr == IoUringConnectionContext.WriteIOVecCount) break;
            }

            context.LastWrite = buffer;
            Debug.WriteLine($"Adding write on {(int)socket}");
            _ring.PreparePollAdd(socket, (ushort) POLLOUT, options: SubmissionOption.Link);
            _ring.PrepareWriteV(socket, writeVecs ,ctr, 0 ,0, Mask(socket, WriteMask));
        }

        private void Submit()
        {
            uint minComplete = 0;
            if (_threadContext.BlockingMode)
            {
                minComplete = 1;
            }

            uint operationsSubmitted = 0;
            SubmitResult result;
            do
            {
                result = _ring.SubmitAndWait(minComplete, out var submitted);
                operationsSubmitted += submitted;
                if (result == SubmitResult.AwaitCompletions)
                {
                    Complete(); // TODO: Consider not appearing blocked while reaping completions
                }
            } while (result != SubmitResult.SubmittedSuccessfully);
            _threadContext.BlockingMode = false;

            if (operationsSubmitted == 0)
            {
                _loopsWithoutSubmission++;
            }
            else
            {
                _loopsWithoutSubmission = 0;
            }
        }

        private void Complete()
        {
            var completions = 0;
            while (_ring.TryRead(out Completion c))
            {
                completions++;
                var socket = (int) c.userData;
                var completionTyp = (CompletionType) (c.userData >> 32);

                if (completionTyp == CompletionType.EventFdPoll) 
                    CompleteEventFdPoll();
                else if (completionTyp == CompletionType.Accept) 
                    CompleteAccept(_acceptSockets[socket], c.result);

                if (!_connections.TryGetValue(socket, out var context)) continue;

                if (completionTyp == CompletionType.Read) 
                    CompleteRead(context, c.result);
                else if (completionTyp == CompletionType.Write) 
                    CompleteWrite(context, c.result);
                else if (completionTyp == CompletionType.Connect) 
                    CompleteConnect((OutboundConnectionContext) context, c.result);
            }

            if (completions == 0)
            {
                _loopsWithoutCompletion++;
                if (_loopsWithoutSubmission >= MaxLoopsWithoutSubmission && 
                    _loopsWithoutCompletion >= MaxLoopsWithoutCompletion && 
                    !_threadContext.BlockingMode)
                {
                    // we might spin forever, if we don't act now
                    _threadContext.BlockingMode = true;
                }
            }
            else
            {
                _loopsWithoutCompletion = 0;
            }
        }

        private void CompleteEventFdPoll()
        {
            Debug.WriteLine("EventFd completed");
            ReadEventFd();
        }

        private void CompleteAccept(AcceptSocketContext acceptContext, int result)
        {
            if (result < 0)
            {
                var err = -result;
                if (err == EAGAIN || err == EINTR || err == EMFILE)
                {
                    Debug.WriteLine("accepted for nothing");

                    Accept(acceptContext);
                    return;
                }

                throw new ErrnoException(err);
            }

            LinuxSocket socket = result;
            if (_threadContext.Options.TcpNoDelay)
            {
                socket.SetOption(SOL_TCP, TCP_NODELAY, 1);
            }

            Debug.WriteLine($"Accepted {(int) socket}");
            var remoteEndpoint = IPEndPointFormatter.AddrToIpEndPoint(acceptContext.Addr);
            var context = new InboundConnectionContext(socket, acceptContext.EndPoint, remoteEndpoint, _threadContext);

            _connections[socket] = context;
            acceptContext.AcceptQueue.TryWrite(context);

            Accept(acceptContext);
            Read(context);
            ReadFromApp(context);
        }

        private void CompleteConnect(OutboundConnectionContext context, int result)
        {
            var completion = context.ConnectCompletion;
            if (result < 0)
            {
                if (-result != EAGAIN || -result != EINTR)
                {
                    context.ConnectCompletion = null;
                    completion.TrySetException(new ErrnoException(-result));
                }

                Connect(context);
                return;
            }

            context.LocalEndPoint = context.Socket.GetLocalAddress();
            completion.TrySetResult(context);

            Read(context);
            ReadFromApp(context);

            context.ConnectCompletion = null; // no need to hold on to this
        }

        private void CompleteRead(IoUringConnectionContext context, int result)
        {
            if (result > 0)
            {
                Debug.WriteLine($"Read {result} bytes from {(int)context.Socket}");
                context.Input.Advance(result);
                FlushRead(context);
            }
            else if (result < 0)
            {
                if (-result == ECONNRESET)
                {
                    context.DisposeAsync();
                } 
                else if (-result != EAGAIN || -result != EWOULDBLOCK || -result != EINTR)
                {
                    throw new ErrnoException(-result);
                }

                Debug.WriteLine("Read for nothing");
            }
            else
            {
                // TODO: handle connection closed
            }
        }

        private void FlushRead(IoUringConnectionContext context)
        {
            var flushResult = context.Input.FlushAsync();
            context.FlushResult = flushResult;
            if (flushResult.IsCompleted)
            {
                // likely
                Debug.WriteLine($"Flushed read from {(int)context.Socket} synchronously");
                context.FlushedToAppSynchronously();
                Read(context);
            }
            else
            {
                flushResult.GetAwaiter().UnsafeOnCompleted(context.OnFlushedToApp);
            }
        }

        private void CompleteWrite(IoUringConnectionContext context, int result)
        {
            try
            {
                if (result > 0)
                {
                    var lastWrite = context.LastWrite;
                    SequencePosition end;
                    if (lastWrite.Length == result)
                    {
                        Debug.WriteLine($"Wrote all {result} bytes to {(int)context.Socket}");
                        end = lastWrite.End;
                    }
                    else
                    {
                        Debug.WriteLine($"Wrote some {result} bytes to {(int)context.Socket}");
                        end = lastWrite.GetPosition(result);
                    }

                    context.Output.AdvanceTo(end);
                    ReadFromApp(context);
                }
                else if (result < 0)
                {
                    if (-result == ECONNRESET || -result == EPIPE)
                    {
                        context.DisposeAsync();
                    } 
                    else if (-result != EAGAIN && -result != EWOULDBLOCK && -result != EINTR)
                    {
                        throw new ErrnoException(-result);
                    }

                    Debug.WriteLine("Wrote for nothing");
                }
            }
            finally
            {
                var writeHandles = context.WriteHandles;
                for (int i = 0; i < writeHandles.Length; i++)
                {
                    writeHandles[i].Dispose();
                }
            }
        }

        private void ReadFromApp(IoUringConnectionContext context)
        {
            var readResult = context.Output.ReadAsync();
            context.ReadResult = readResult;
            if (readResult.IsCompleted)
            {
                Debug.WriteLine($"Read from app for {(int)context.Socket} synchronously");
                context.ReadFromAppSynchronously();
                Write(context);
            }
            else
            {
                // likely
                readResult.GetAwaiter().UnsafeOnCompleted(context.OnReadFromApp);
            }
        }

        public static ulong Mask(int socket, ulong mask)
        {
            var socketUl = (ulong)socket;
            return socketUl | mask;
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return new ValueTask(_threadCompletion.Task);
        }
    }
}