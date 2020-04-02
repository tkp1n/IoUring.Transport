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
        public const ulong ConnectMask =          (ulong) OperationType.Connect << 32;
        public const ulong AcceptMask =           (ulong) OperationType.Accept << 32;
        public const ulong CompleteInboundMask =  (ulong) OperationType.CompleteInbound << 32;
        public const ulong CompleteOutboundMask = (ulong) OperationType.CompleteOutbound << 32;
        private const ulong CancelMask =          (ulong) OperationType.Cancel << 32;
        public const ulong AbortMask =            (ulong) OperationType.Abort << 32;

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
            _threadContext = new TransportThreadContext(options, memoryPool, _eventfd, _asyncOperationQueue, _asyncOperationStates);
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

            var tcs = new TaskCompletionSource<ConnectionContext>(TaskCreationOptions.RunContinuationsAsynchronously); // Ensure the transport thread doesn't run continuations
            var context = new OutboundConnectionContext(s, endpoint, _threadContext, tcs);

            _threadContext.ScheduleAsyncConnect(s, context);

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
            _threadContext.ScheduleAsyncAccept(s, context);
        }

        public void Run() => new Thread(obj => ((TransportThread)obj).Loop())
        {
            IsBackground = true,
            Name = $"IoUring Transport Thread - {Interlocked.Increment(ref _threadId)}"
        }.Start(this);

        private void Loop()
        {
            var state = LoopState.Running;
            ReadPollEventFd();

            while (!_disposed)
            {
                RunAsyncOperations();
                state = Submit(state);
                if (state == LoopState.WillBlock) continue; // Check operation queue again before blocking
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
                    case OperationType.ReadPoll:
                        ReadPoll(_connections[socket]);
                        continue;
                    case OperationType.WritePoll:
                        WritePoll(_connections[socket]);
                        continue;
                }

                _asyncOperationStates.Remove(socket, out var state);
                switch (operationType)
                {
                    case OperationType.Accept:
                        AddAndAccept(socket, state);
                        continue;
                    case OperationType.Connect:
                        AddAndConnect(socket, state);
                        continue;
                    case OperationType.CompleteInbound:
                        CompleteInbound(_connections[socket], (Exception) state);
                        continue;
                    case OperationType.CompleteOutbound:
                        CompleteOutbound(_connections[socket], (Exception) state);
                        continue;
                    case OperationType.Abort:
                        Abort(_connections[socket], (ConnectionAbortedException) state);
                        continue;
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
            var acceptSocketContext = (AcceptSocketContext) context;
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
            var outboundConnectionContext = (OutboundConnectionContext) context;
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
            context.SetFlag(ConnectionState.PollingRead);
        }

        private void Read(IoUringConnectionContext context)
        {
            var writer = context.Inbound;
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
            context.SetFlag(ConnectionState.Reading);
        }

        private void WritePoll(IoUringConnectionContext context)
        {
            var socket = context.Socket;
            _ring.PreparePollAdd(socket, (ushort) POLLOUT, Mask(socket, WritePollMask));
            context.SetFlag(ConnectionState.PollingWrite);
        }

        private void Write(IoUringConnectionContext context)
        {
            var buffer = context.ReadResult;

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
                if (ctr == writeHandles.Length) break;
            }

            context.LastWrite = buffer;
            var socket = context.Socket;
            Debug.WriteLine($"Adding write on {(int)socket}");
            _ring.PrepareWriteV(socket, writeVecs ,ctr, 0 ,0, Mask(socket, WriteMask));
            context.SetFlag(ConnectionState.Writing);
        }

        private void CompleteInbound(IoUringConnectionContext context, Exception error)
        {
            context.Inbound.Complete(error);
            CleanupSocketEnd(context);
        }

        private void CompleteOutbound(IoUringConnectionContext context, Exception error)
        {
            context.Outbound.Complete(error);
            CancelReadFromSocket(context);
            CleanupSocketEnd(context);
        }

        private void CancelReadFromSocket(IoUringConnectionContext context)
        {
            var flags = context.Flags;
            if (HasFlag(flags, ConnectionState.ReadCancelled))
            {
                return;
            }

            if (HasFlag(flags, ConnectionState.PollingRead))
            {
                Cancel(context.Socket, ReadPollMask);
            } 
            else if (HasFlag(flags, ConnectionState.Reading))
            {
                Cancel(context.Socket, ReadMask);
            }

            context.Flags = SetFlag(flags, ConnectionState.ReadCancelled);

            CompleteInbound(context, new ConnectionAbortedException());
        }

        private void Abort(IoUringConnectionContext context, Exception error)
        {
            context.Outbound.CancelPendingRead();
            CancelWriteToSocket(context);
        }

        private void CancelWriteToSocket(IoUringConnectionContext context)
        {
            var flags = context.Flags;

            if (HasFlag(flags, ConnectionState.WriteCancelled))
            {
                return;
            }

            if (HasFlag(flags, ConnectionState.PollingWrite))
            {
                Cancel(context.Socket, WritePollMask);
            } 
            else if (HasFlag(flags, ConnectionState.Writing))
            {
                Cancel(context.Socket, WriteMask);
            }

            context.Flags = SetFlag(flags, ConnectionState.WriteCancelled);

            CompleteInbound(context, null);
        }

        private void CleanupSocketEnd(IoUringConnectionContext context)
        {
            var flags = context.Flags;
            if (!HasFlag(flags, ConnectionState.HalfClosed))
            {
                context.Flags = SetFlag(flags, ConnectionState.HalfClosed);
                return;
            }

            if (HasFlag(flags, ConnectionState.Closed))
            {
                return;
            }

            context.Flags = SetFlag(flags, ConnectionState.Closed);
            Close(context);
        }

        private void Cancel(int socket, ulong mask)
        {
            _ring.PrepareCancel(Mask(socket, mask), Mask(socket, CancelMask));
        }

        private void Close(IoUringConnectionContext context)
        {
            int socket = context.Socket;
            close(socket); // TODO: replace with close via io_uring in 5.6

            _connections.Remove(socket);
            context.NotifyClosed();
        }

        private LoopState Submit(LoopState state)
        {
            uint minComplete;
            if (_ring.SubmissionEntriesUsed == 0)
            {
                if (state == LoopState.Running)
                {
                    _threadContext.SetBlockingMode(true);
                    return LoopState.WillBlock;
                }
                minComplete = 1;
            }
            else
            {
                minComplete = 0;
            }

            _ring.SubmitAndWait(minComplete, out _);
            _threadContext.SetBlockingMode(false);
            return LoopState.Running;
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
                    case OperationType.Cancel:
                        continue; // Ignore the result of the cancellation request
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
            if (result < 0)
            {
                if (-result != EAGAIN || -result != EINTR)
                {
                    context.CompleteConnect(new ErrnoException(-result));
                }

                Connect(context);
                return;
            }

            context.CompleteConnect();

            ReadPoll(context);
            ReadFromApp(context);
        }

        private void CompleteReadPoll(IoUringConnectionContext context, int result)
        {
            context.RemoveFlag(ConnectionState.PollingRead);
            if (result >= 0)
            {
                Debug.WriteLine($"Completed read poll on {(int)context.Socket}");
                Read(context);
            }
            else
            {
                var err = -result;
                if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
                {
                    Debug.WriteLine("Polled read for nothing");
                    ReadPoll(context);
                }
                else if (context.HasFlag(ConnectionState.ReadCancelled) && err == ECANCELED)
                {
                    Debug.WriteLine("Read poll was cancelled");
                }
                else
                {
                    CompleteInbound(context, new ErrnoException(err));
                }
            }
        }

        private void CompleteRead(IoUringConnectionContext context, int result)
        {
            context.RemoveFlag(ConnectionState.Reading);
            foreach (var readHandle in context.ReadHandles)
            {
                readHandle.Dispose();
            }

            if (result > 0)
            {
                Debug.WriteLine($"Read {result} bytes from {(int)context.Socket}");
                context.Inbound.Advance(result);
                FlushRead(context);
                return;
            }

            Exception ex;
            if (result < 0)
            {
                var err = -result;
                if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
                {
                    Debug.WriteLine("Read for nothing");
                    Read(context);
                    return;
                }

                if (context.HasFlag(ConnectionState.ReadCancelled) && err == ECANCELED)
                {
                    Debug.WriteLine("Read was cancelled");
                    return;
                }

                if (-result == ECONNRESET)
                {
                    ex = new ErrnoException(ECONNRESET);
                    ex = new ConnectionResetException(ex.Message, ex);
                }
                else
                {
                    ex = new ErrnoException(-result);
                }
            }
            else
            {
                // EOF
                ex = null;
            }

            CompleteInbound(context, ex);
        }

        private void FlushRead(IoUringConnectionContext context)
        {
            var result = context.FlushAsync();
            if (result.CompletedSuccessfully)
            {
                // likely
                Debug.WriteLine($"Flushed read from {(int)context.Socket} synchronously");
                ReadPoll(context);
            } 
            else if (result.CompletedExceptionally)
            {
                CompleteInbound(context, result.GetError());
            }
        }

        private void CompleteWritePoll(IoUringConnectionContext context, int result)
        {
            context.RemoveFlag(ConnectionState.PollingWrite);
            if (result >= 0)
            {
                Debug.WriteLine($"Completed write poll on {(int)context.Socket}");
                Write(context);
            }
            else
            {
                var err = -result;
                if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
                {
                    Debug.WriteLine("Polled write for nothing");
                    WritePoll(context);
                } 
                else if (context.HasFlag(ConnectionState.WriteCancelled) && err == ECANCELED)
                {
                    Debug.WriteLine("Write poll was cancelled");
                }
                else
                {
                    CompleteOutbound(context, new ErrnoException(err));
                }
            }
        }

        private void CompleteWrite(IoUringConnectionContext context, int result)
        {
            context.RemoveFlag(ConnectionState.Writing);
            foreach (var writeHandle in context.WriteHandles)
            {
                writeHandle.Dispose();
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

                context.Outbound.AdvanceTo(end);
                ReadFromApp(context);
                return;
            }

            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                Debug.WriteLine("Wrote for nothing");
                context.Outbound.AdvanceTo(lastWrite.Start);
                ReadFromApp(context);
                return;
            }

            if (context.HasFlag(ConnectionState.WriteCancelled) && err == ECANCELED)
            {
                Debug.WriteLine("Write was cancelled");
                return;
            }

            Exception ex;
            if (err == ECONNRESET)
            {
                ex = new ErrnoException(err);
                ex = new ConnectionResetException(ex.Message, ex);
            }
            else
            {
                ex = new ErrnoException(err);
            }

            CompleteOutbound(context, ex);
        }

        private void ReadFromApp(IoUringConnectionContext context)
        {
            var result = context.ReadAsync();
            if (result.CompletedSuccessfully)
            {
                // unlikely
                Debug.WriteLine($"Read from app for {(int)context.Socket} synchronously");
                WritePoll(context);
            } 
            else if (result.CompletedExceptionally)
            {
                CompleteOutbound(context, result.GetError());
            }
        }

        public static ulong Mask(int socket, ulong mask)
        {
            var socketUl = (ulong)socket;
            return socketUl | mask;
        }

        private static bool HasFlag(ConnectionState flag, ConnectionState test) => (flag & test) != 0;
        private static ConnectionState SetFlag(ConnectionState flag, ConnectionState newFlag) => flag | newFlag;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return new ValueTask(_threadCompletion.Task);
        }

        private enum LoopState
        {
            Running,
            WillBlock
        }
    }
}