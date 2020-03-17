using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
    internal sealed unsafe class TransportThread : IAsyncDisposable
    {
        private const int RingSize = 4096;
        private const int ListenBacklog = 128;
        private const ulong ReadMask =            (ulong) OperationType.Read << 32;
        public const ulong ReadPollMask =         (ulong) OperationType.ReadPoll << 32;
        private const ulong WriteMask =           (ulong) OperationType.Write << 32;
        public const ulong WritePollMask =        (ulong) OperationType.WritePoll << 32;
        private const ulong EventFdReadPollMask = (ulong) OperationType.EventFdReadPoll << 32;
        private const ulong EventFdReadMask =     (ulong) OperationType.EventFdRead << 32;
        private const ulong ConnectMask =         (ulong) OperationType.Connect << 32;
        private const ulong AcceptMask =          (ulong) OperationType.Accept << 32;

        private static int _threadId;

        private readonly Ring _ring = new Ring(RingSize);
        private readonly ConcurrentQueue<ulong> _asyncOperationQueue = new ConcurrentQueue<ulong>();
        private readonly ConcurrentDictionary<int, object> _asyncOperationStates = new ConcurrentDictionary<int, object>();
        private readonly Dictionary<int, AcceptSocketContext> _acceptSockets = new Dictionary<int, AcceptSocketContext>();
        private readonly Dictionary<int, IoUringConnectionContext> _connections = new Dictionary<int, IoUringConnectionContext>();
        private readonly TaskCompletionSource<object> _threadCompletion = new TaskCompletionSource<object>();
        private readonly TransportThreadContext _threadContext;
        private readonly int _maxBufferSize;
        private readonly int _eventfd;
        private readonly GCHandle _eventfdBytes;
        private readonly GCHandle _eventfdIoVecHandle;
        private readonly iovec* _eventfdIoVec;

        private volatile bool _disposed;

        public TransportThread(IoUringOptions options)
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

            var memoryPool = new SlabMemoryPool();
            _threadContext = new TransportThreadContext(options, memoryPool, _eventfd, _asyncOperationQueue);
            _maxBufferSize = memoryPool.MaxBufferSize;
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
            ReadPollEventFd();

            while (!_disposed)
            {
                RunAsyncOperations();
                Submit();
                Complete();
            }

            _ring.Dispose();
            _threadCompletion.TrySetResult(null);
        }

        private void RunAsyncOperations()
        {
            while (_asyncOperationQueue.TryDequeue(out var operation))
            {
                var socket = (int) operation;
                var operationType = (OperationType) (operation >> 32);

                switch (operationType)
                {
                    case OperationType.ReadPoll when _connections.TryGetValue(socket, out var context):
                        ReadPoll(context);
                        break;
                    case OperationType.WritePoll when _connections.TryGetValue(socket, out var context):
                        WritePoll(context);
                        break;
                    case OperationType.Accept when _asyncOperationStates.Remove(socket, out var context):
                        AddAndAccept(socket, context);
                        break;
                    case OperationType.Connect when _asyncOperationStates.Remove(socket, out var context):
                        AddAndConnect(socket, context);
                        break;
                }
            }
        }

        private void ReadPollEventFd()
        {
            Debug.WriteLine("Adding poll on eventfd");
            int fd = _eventfd;
            _ring.PreparePollAdd(fd, (ushort)POLLIN, Mask(fd, EventFdReadPollMask));
        }

        private void ReadEventFd()
        {
            Debug.WriteLine("Adding read on eventfd");
            int fd = _eventfd;
            _ring.PrepareReadV(fd, _eventfdIoVec, 1, userData: Mask(fd, EventFdReadMask));
        }

        private void AddAndAccept(int socket, object context)
        {
            Debug.Assert(context is AcceptSocketContext);
            var acceptSocketContext = Unsafe.As<AcceptSocketContext>(context);
            _acceptSockets[socket] = acceptSocketContext;
            Accept(acceptSocketContext);
        }

        private void Accept(AcceptSocketContext context)
        {
            var socket = context.Socket;
            _ring.PrepareAccept(socket, (sockaddr*) context.Addr, context.AddrLen, SOCK_NONBLOCK | SOCK_CLOEXEC, Mask(socket, AcceptMask));
        }

        private void AddAndConnect(int socket, object context)
        {
            Debug.Assert(context is OutboundConnectionContext);
            var outboundConnectionContext = Unsafe.As<OutboundConnectionContext>(context);
            _connections[socket] = outboundConnectionContext;
            Connect(outboundConnectionContext);
        }

        private void Connect(OutboundConnectionContext context)
        {
            var socket = context.Socket;
            _ring.PrepareConnect(socket, (sockaddr*) context.Addr, context.AddrLen, Mask(socket, ConnectMask));
        }

        private void ReadPoll(IoUringConnectionContext context)
        {
            var socket = context.Socket;
            _ring.PreparePollAdd(socket, (ushort) POLLIN, Mask(socket, ReadPollMask));
        }

        private void Read(IoUringConnectionContext context)
        {
            var writer = context.Input;
            var readHandles = context.ReadHandles;
            var readVecs = context.ReadVecs;

            var memory = writer.GetMemory(_maxBufferSize);
            var handle = memory.Pin();

            readVecs[0].iov_base = handle.Pointer;
            readVecs[0].iov_len = memory.Length;

            readHandles[0] = handle;

            var socket = context.Socket;
            Debug.WriteLine($"Adding read on {(int)socket}");
            _ring.PrepareReadV(socket, readVecs, 1, 0, 0, Mask(socket, ReadMask));
        }

        private void WritePoll(IoUringConnectionContext context)
        {
            var socket = context.Socket;
            _ring.PreparePollAdd(socket, (ushort) POLLOUT, Mask(socket, WritePollMask));
        }

        private void Write(IoUringConnectionContext context)
        {
            var result = context.ReadResult.Result;
            var buffer = result.Buffer;
            var socket = context.Socket;
            if ((buffer.IsEmpty && result.IsCompleted) || result.IsCanceled)
            {
                context.DisposeAsync();
                _connections.Remove(socket);
                socket.Close();
                return;
            }

            var writeHandles = context.WriteHandles;
            var writeVecs = context.WriteVecs;
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
            _ring.PrepareWriteV(socket, writeVecs ,ctr, 0 ,0, Mask(socket, WriteMask));
        }

        private void Submit()
        {
            uint minComplete;
            if (_ring.SubmissionEntriesUsed == 0)
            {
                minComplete = 1;
                _threadContext.SetBlockingMode(true);
            }
            else
            {
                minComplete = 0;
            }

            _ring.SubmitAndWait(minComplete, out _);
            _threadContext.SetBlockingMode(false);
        }

        private void Complete()
        {
            while (_ring.TryRead(out Completion c))
            {
                var socket = (int) c.userData;
                var operationType = (OperationType) (c.userData >> 32);

                switch (operationType)
                {
                    case OperationType.EventFdReadPoll:
                        CompleteEventFdReadPoll(c.result);
                        break;
                    case OperationType.EventFdRead:
                        CompleteEventFdRead(c.result);
                        break;
                    case OperationType.Accept:
                        CompleteAccept(_acceptSockets[socket], c.result);
                        break;
                }

                if (!_connections.TryGetValue(socket, out var context)) continue;

                switch (operationType)
                {
                    case OperationType.ReadPoll:
                        CompleteReadPoll(context, c.result);
                        break;
                    case OperationType.Read:
                        CompleteRead(context, c.result);
                        break;
                    case OperationType.WritePoll:
                        CompleteWritePoll(context, c.result);
                        break;
                    case OperationType.Write:
                        CompleteWrite(context, c.result);
                        break;
                    case OperationType.Connect:
                        CompleteConnect((OutboundConnectionContext) context, c.result);
                        break;
                }
            }
        }

        private void CompleteEventFdReadPoll(int result)
        {
            if (result < 0)
            {
                var err = -result;
                if (err == EAGAIN || err == EINTR)
                {
                    Debug.WriteLine("polled eventfd for nothing");

                    ReadPollEventFd();
                    return;
                }

                throw new ErrnoException(err);
            }

            Debug.WriteLine("EventFd poll completed");
            ReadEventFd();
        }

        private void CompleteEventFdRead(int result)
        {
            if (result < 0)
            {
                var err = -result;
                if (err == EAGAIN || err == EINTR)
                {
                    Debug.WriteLine("read eventfd for nothing");

                    ReadEventFd();
                    return;
                }

                throw new ErrnoException(err);
            }

            Debug.WriteLine("EventFd read completed");
            ReadPollEventFd();
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
            ReadPoll(context);
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

            context.ConnectCompletion = null; // no need to hold on to this

            context.LocalEndPoint = context.Socket.GetLocalAddress();
            completion.TrySetResult(context);

            ReadPoll(context);
            ReadFromApp(context);
        }

        private void CompleteReadPoll(IoUringConnectionContext context, int result)
        {
            if (result < 0)
            {
                if (-result != EAGAIN || -result != EINTR)
                {
                    throw new ErrnoException(-result);
                }

                Debug.WriteLine("Polled read for nothing");
                ReadPoll(context);
                return;
            }

            Debug.WriteLine($"Completed read poll on {(int)context.Socket}");
            Read(context);
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
                else if (-result != EAGAIN && -result != EWOULDBLOCK && -result != EINTR)
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
                ReadPoll(context);
                return;
            }

            flushResult.GetAwaiter().UnsafeOnCompleted(context.OnFlushedToApp);
        }

        private void CompleteWritePoll(IoUringConnectionContext context, int result)
        {
            if (result < 0)
            {
                if (-result != EAGAIN || -result != EINTR)
                {
                    throw new ErrnoException(-result);
                }

                Debug.WriteLine("Polled write for nothing");
                WritePoll(context);
                return;
            }

            Debug.WriteLine($"Completed write poll on {(int)context.Socket}");
            Write(context);
        }

        private void CompleteWrite(IoUringConnectionContext context, int result)
        {
            var writeHandles = context.WriteHandles;
            for (int i = 0; i < writeHandles.Length; i++)
            {
                writeHandles[i].Dispose();
            }

            var lastWrite = context.LastWrite;
            if (result >= 0)
            {
                SequencePosition end;
                if (result == 0)
                {
                    Debug.WriteLine($"Wrote {result} bytes to {(int)context.Socket}");
                    end = lastWrite.Start;
                }
                else if (lastWrite.Length == result)
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
                else if (-result == EAGAIN || -result == EWOULDBLOCK || -result == EINTR)
                {
                    Debug.WriteLine("Wrote for nothing");
                    context.Output.AdvanceTo(lastWrite.Start);
                    ReadFromApp(context);
                }
                else
                {
                    throw new ErrnoException(-result);
                }
            }
        }

        private void ReadFromApp(IoUringConnectionContext context)
        {
            var readResult = context.Output.ReadAsync();
            context.ReadResult = readResult;
            if (readResult.IsCompleted)
            {
                // unlikely
                Debug.WriteLine($"Read from app for {(int)context.Socket} synchronously");
                context.ReadFromAppSynchronously();
                WritePoll(context);
                return;
            }

            readResult.GetAwaiter().UnsafeOnCompleted(context.OnReadFromApp);
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