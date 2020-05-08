using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using IoUring.Transport.Internals.Inbound;
using IoUring.Transport.Internals.Outbound;
using Microsoft.AspNetCore.Connections;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal sealed class TransportThread : IoUringThread
    {
        private readonly ConcurrentQueue<AsyncOperation> _asyncOperationQueue = new ConcurrentQueue<AsyncOperation>();
        private readonly ConcurrentDictionary<int, object> _asyncOperationStates = new ConcurrentDictionary<int, object>();
        private readonly ConcurrentDictionary<IPEndPoint, AcceptSocket> _acceptSocketsPerEndPoint = new ConcurrentDictionary<IPEndPoint, AcceptSocket>();
        private readonly Dictionary<int, AcceptSocket> _acceptSockets = new Dictionary<int, AcceptSocket>();
        private readonly Dictionary<int, IoUringConnection> _connections = new Dictionary<int, IoUringConnection>();
        private readonly ConcurrentDictionary<int, SocketReceiver> _socketReceivers = new ConcurrentDictionary<int, SocketReceiver>();
        private readonly TransportThreadScheduler _scheduler;
        private readonly MemoryPool<byte> _memoryPool;

        public TransportThread(IoUringOptions options)
            : base("IoUring Transport Thread", options)
        {
            _memoryPool = new SlabMemoryPool();
            _scheduler = new TransportThreadScheduler(_unblockHandle, _asyncOperationQueue, _asyncOperationStates);
        }

        public ValueTask<ConnectionContext> Connect(EndPoint endpoint)
        {
            var tcs = new TaskCompletionSource<ConnectionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = OutboundConnection.Create(endpoint, tcs, _memoryPool, _options, _scheduler);
            _scheduler.ScheduleAsyncConnect(context.Socket, context);

            return new ValueTask<ConnectionContext>(tcs.Task);
        }

        public EndPoint Bind(IPEndPoint endpoint, ChannelWriter<ConnectionContext> connectionSource)
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Binding to new endpoint {endpoint}");
#endif
            var context = AcceptSocket.Bind(endpoint, connectionSource, _memoryPool, _options, _scheduler);
            _acceptSocketsPerEndPoint[endpoint] = context;
            _scheduler.ScheduleAsyncBind(context.Socket, context);

            return context.EndPoint;
        }

        public ValueTask Unbind(IPEndPoint endPoint)
        {
            if (_acceptSocketsPerEndPoint.TryRemove(endPoint, out var acceptSocket))
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Unbinding from {endPoint}");
#endif
                _scheduler.ScheduleAsyncUnbind(acceptSocket.Socket);
                return new ValueTask(acceptSocket.UnbindCompletion);
            }

            return default;
        }

        public LinuxSocket RegisterHandlerFor(ChannelWriter<ConnectionContext> acceptQueue, EndPoint endPoint)
        {
            var acceptSocketPair = new LinuxSocketPair(AF_UNIX, SOCK_STREAM, 0, blocking: false);
            var socketReceiver = new SocketReceiver(acceptSocketPair.Socket1, acceptQueue, endPoint, _memoryPool, _options, _scheduler);

            _socketReceivers[acceptSocketPair.Socket1] = socketReceiver;
            _scheduler.ScheduleAsyncPollReceive(acceptSocketPair.Socket1);

            return acceptSocketPair.Socket2;
        }

        protected override void RunAsyncOperations()
        {
            var ring = _ring;
            while (_asyncOperationQueue.TryDequeue(out var operation))
            {
                var (socket, operationType) = operation;
                if ((operationType & (OperationType.ReadPoll | OperationType.WritePoll)) != 0)
                {
                    // hot path
                    var context = _connections[socket];
                    if (operationType == OperationType.ReadPoll)
                    {
                        context.ReadPoll(ring);
                    }
                    else
                    {
                        context.WritePoll(ring);
                    }
                }
                else
                {
                    switch (operationType)
                    {
                        case OperationType.Unbind:
                            _acceptSockets[socket].Unbid(ring);
                            continue;
                        case OperationType.RecvSocketPoll:
                            _socketReceivers[socket].PollReceive(ring);
                            continue;
                    }

                    _asyncOperationStates.Remove(socket, out var state);
                    switch (operationType)
                    {
                        case OperationType.Bind:
                            AddAndAccept(socket, state);
                            break;
                        case OperationType.Connect:
                            AddAndConnect(socket, state);
                            break;
                        case OperationType.CompleteInbound:
                            _connections[socket].CompleteInbound(ring, (Exception) state);
                            break;
                        case OperationType.CompleteOutbound:
                            _connections[socket].CompleteOutbound(ring, (Exception) state);
                            break;
                        case OperationType.Abort:
                            if (_connections.TryGetValue(socket, out var connection))
                                connection.Abort(ring, (ConnectionAbortedException) state);
                            break;
                    }
                }
            }
        }

        private void AddAndAccept(int socket, object state)
        {
            var acceptSocket = (AcceptSocket) state;
            _acceptSockets[socket] = acceptSocket;
            acceptSocket.Accept(_ring);
        }

        private void AddAndConnect(int socket, object context)
        {
            var outboundConnectionContext = (OutboundConnection) context;
            _connections[socket] = outboundConnectionContext;
            outboundConnectionContext.Connect(_ring);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        protected override void Complete()
        {
            var ring = _ring;
            while (ring.TryRead(out Completion c))
            {
                var (socket, operationType) = AsyncOperation.FromUlong(c.userData);

                if ((operationType & (OperationType.Read | OperationType.ReadPoll | OperationType.Write | OperationType.WritePoll)) != 0 &&
                    _connections.TryGetValue(socket, out var ctx))
                {
                    // hot path
                    switch (operationType)
                    {
                        case OperationType.ReadPoll:
                            ctx.CompleteReadPoll(ring, c.result);
                            break;
                        case OperationType.Read:
                            ctx.CompleteRead(ring, c.result);
                            break;
                        case OperationType.WritePoll:
                            ctx.CompleteWritePoll(ring, c.result);
                            break;
                        case OperationType.Write:
                            ctx.CompleteWrite(ring, c.result);
                            break;
                    }
                }
                else
                {
                    switch (operationType)
                    {
                        case OperationType.EventFdReadPoll:
                        case OperationType.EventFdRead:
                            _unblockHandle.HandleEventFdCompletion(operationType, c.result);
                            continue;
                        case OperationType.Accept:
                            CompleteAccept(socket, c.result);
                            continue;
                        case OperationType.CancelAccept:
                            _acceptSockets[socket].Close(ring);
                            continue;
                        case OperationType.CloseAcceptSocket:
                            CompleteCloseAcceptSocket(socket);
                            break;
                        case OperationType.RecvSocketPoll:
                            _socketReceivers[socket].CompleteReceivePoll(ring, c.result);
                            break;
                        case OperationType.RecvSocket:
                            CompleteSocketReceive(socket, c.result);
                            break;
                    }

                    if (!_connections.TryGetValue(socket, out var context)) continue;
                    switch (operationType)
                    {
                        case OperationType.Connect:
                            CompleteConnect(_ring, (OutboundConnection) context, c.result);
                            break;
                        case OperationType.CloseConnection:
                            CompleteCloseConnection(context, socket);
                            break;
                    }
                }
            }
        }

        private void CompleteAccept(int socket, int result)
        {
            var ring = _ring;
            if (!_acceptSockets.TryGetValue(socket, out var acceptSocket)) return; // socket already closed
            if (acceptSocket.TryCompleteAccept(ring, result, out var connection))
            {
                _connections[connection.Socket] = connection;
                acceptSocket.AcceptQueue.TryWrite(connection);

                acceptSocket.Accept(ring);
                connection.ReadPoll(ring);
                connection.ReadFromApp(ring);
            }
        }

        private void CompleteCloseAcceptSocket(int socket)
        {
            if (_acceptSockets.Remove(socket, out var acceptSocket))
                acceptSocket.CompleteClose();
        }

        private void CompleteSocketReceive(int socket, int result)
        {
            var ring = _ring;
            if (!_socketReceivers.TryGetValue(socket, out var receiver)) return;
            if (receiver.TryCompleteReceive(ring, result, out var connection))
            {
                _connections[connection.Socket] = connection;
                receiver.AcceptQueue.TryWrite(connection);

                receiver.PollReceive(ring);
                connection.ReadPoll(ring);
                connection.ReadFromApp(ring);
            }
        }

        private static void CompleteConnect(Ring ring, OutboundConnection context, int result)
        {
            context.CompleteConnect(ring, result);

            context.ReadPoll(ring);
            context.ReadFromApp(ring);
        }

        private void CompleteCloseConnection(IoUringConnection context, int socket)
        {
            _connections.Remove(socket);
            context.CompleteClosed();
        }

        public override async ValueTask DisposeAsync()
        {
#if TRACE_IO_URING
            Trace.WriteLine("Disposing TransportThread");
#endif

            foreach (var (endpoint, _) in _acceptSocketsPerEndPoint)
            {
                await Unbind(endpoint);
            }

            await base.DisposeAsync();
        }
    }
}