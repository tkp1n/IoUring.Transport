using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        public TransportThread(IoUringOptions options, int cpuId)
            : base("IoUring Transport Thread", options, cpuId)
        {
            _memoryPool = new SlabMemoryPool();
            _scheduler = new TransportThreadScheduler(_unblockHandle, _asyncOperationQueue, _asyncOperationStates);
        }

        public ValueTask<ConnectionContext> Connect(EndPoint endpoint)
        {
            var tcs = new TaskCompletionSource<ConnectionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = OutboundConnection.Create(endpoint, tcs, _memoryPool, _options, _scheduler);
            _scheduler.ScheduleAsyncAddAndConnect(context.Socket, context);

            return new ValueTask<ConnectionContext>(tcs.Task);
        }

        public EndPoint Bind(IPEndPoint endpoint, ChannelWriter<ConnectionContext> connectionSource)
        {
            var context = AcceptSocket.Bind(endpoint, connectionSource, _memoryPool, _options, _scheduler);
            _acceptSocketsPerEndPoint[(IPEndPoint) context.EndPoint] = context;
            _scheduler.ScheduleAsyncAddAndAccept(context.Socket, context);

            return context.EndPoint;
        }

        public void SetReceiveOnIncomingCpu(IPEndPoint endPoint)
        {
            _acceptSocketsPerEndPoint[endPoint].Socket.ReceiveOnIncomingCpu();
        }

        public ValueTask Unbind(IPEndPoint endPoint)
        {
            if (_acceptSocketsPerEndPoint.TryRemove(endPoint, out var acceptSocket))
            {
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
            var sqesAvailable = ring.SubmissionEntriesAvailable;
            while (sqesAvailable-- > 0 && _asyncOperationQueue.TryDequeue(out var operation))
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

                        // below cases are only visited on ring overflow

                        case OperationType.ReadPoll:
                            _connections[socket].ReadPoll(ring);
                            break;
                        case OperationType.Read:
                            _connections[socket].Read(ring);
                            break;
                        case OperationType.WritePoll:
                            _connections[socket].WritePoll(ring);
                            break;
                        case OperationType.Write:
                            _connections[socket].Write(ring);
                            break;
                        case OperationType.AcceptPoll:
                            _acceptSockets[socket].AcceptPoll(ring);
                            break;
                        case OperationType.Accept:
                            _acceptSockets[socket].Accept(ring);
                            break;
                        case OperationType.RecvSocket:
                            _socketReceivers[socket].Receive(ring);
                            break;
                        case OperationType.Connect:
                            ((OutboundConnection) _connections[socket]).Connect(ring);
                            break;
                        case OperationType.CancelRead:
                        case OperationType.CancelReadPoll:
                        case OperationType.CancelWrite:
                        case OperationType.CancelWritePoll:
                            _connections[socket].Cancel(ring, operationType & ~OperationType.CancelGeneric);
                            break;
                        case OperationType.CancelAccept:
                            _acceptSockets[socket].Unbid(ring);
                            break;
                        case OperationType.CloseConnection:
                            _connections[socket].Close(ring);
                            break;
                        case OperationType.CloseAcceptSocket:
                            _acceptSockets[socket].Close(ring);
                            break;
                    }

                    _asyncOperationStates.Remove(socket, out var state);
                    switch (operationType)
                    {
                        case OperationType.AddAndAccept:
                            AddAndAccept(socket, state);
                            break;
                        case OperationType.AddAndConnect:
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void Complete(int socket, OperationType operationType, int result)
        {
            var ring = _ring;
            if ((operationType & (OperationType.Read | OperationType.ReadPoll | OperationType.Write | OperationType.WritePoll)) != 0 &&
                _connections.TryGetValue(socket, out var ctx))
            {
                // hot path
                switch (operationType)
                {
                    case OperationType.ReadPoll:
                        ctx.CompleteReadPoll(ring, result);
                        return;
                    case OperationType.Read:
                        ctx.CompleteRead(ring, result);
                        return;
                    case OperationType.WritePoll:
                        ctx.CompleteWritePoll(ring, result);
                        return;
                    case OperationType.Write:
                        ctx.CompleteWrite(ring, result);
                        return;
                }
            }
            else
            {
                switch (operationType)
                {
                    case OperationType.Accept:
                        CompleteAccept(socket, result);
                        return;
                    case OperationType.CancelAccept:
                        _acceptSockets[socket].Close(ring);
                        return;
                    case OperationType.CloseAcceptSocket:
                        CompleteCloseAcceptSocket(socket);
                        return;
                    case OperationType.RecvSocketPoll:
                        _socketReceivers[socket].CompleteReceivePoll(ring, result);
                        return;
                    case OperationType.RecvSocket:
                        CompleteSocketReceive(socket, result);
                        return;
                }

                if (!_connections.TryGetValue(socket, out var context)) return;
                switch (operationType)
                {
                    case OperationType.Connect:
                        CompleteConnect(ring, (OutboundConnection) context, result);
                        return;
                    case OperationType.CloseConnection:
                        CompleteCloseConnection(context, socket);
                        return;
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
            foreach (var (endpoint, _) in _acceptSocketsPerEndPoint)
            {
                await Unbind(endpoint);
            }

            await base.DisposeAsync();

            _memoryPool.Dispose();
        }
    }
}