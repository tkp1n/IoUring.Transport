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
            _scheduler.ScheduleAsyncAddAndAcceptPoll(context.Socket, context);

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
                if (operationType == OperationType.WritePoll)
                {
                    // hot path
                    _connections[socket].WritePoll(ring);
                }
                else
                {
                    RunLessFrequentAsyncOperations(socket, operationType);
                }
            }
        }

        private void RunLessFrequentAsyncOperations(int socket, OperationType operationType)
        {
            var ring = _ring;
            switch (operationType)
            {
                case OperationType.ReadPoll:
                    _connections[socket].ReadPoll(ring);
                    return;
                case OperationType.Unbind:
                    _acceptSockets[socket].Unbid(ring);
                    return;
                case OperationType.RecvSocketPoll:
                    _socketReceivers[socket].PollReceive(ring);
                    return;

                // below cases are only visited on ring overflow

                case OperationType.Read:
                    _connections[socket].Read(ring);
                    return;
                case OperationType.WritePoll:
                    _connections[socket].WritePoll(ring);
                    return;
                case OperationType.Write:
                    _connections[socket].Write(ring);
                    return;
                case OperationType.AcceptPoll:
                    _acceptSockets[socket].AcceptPoll(ring);
                    return;
                case OperationType.Accept:
                    _acceptSockets[socket].TryAccept(ring, out _); // will always run via ring (return false)
                    return;
                case OperationType.RecvSocket:
                    _socketReceivers[socket].Receive(ring);
                    return;
                case OperationType.Connect:
                    var context = (OutboundConnection) _connections[socket];
                    if (context.Connect(ring))
                    {
                        CompleteConnect(ring, context, 0);
                    }
                    return;
                case OperationType.WritePollDuringConnect:
                    ((OutboundConnection) _connections[socket]).WritePollDuringConnect(ring);
                    return;
                case OperationType.CancelRead:
                case OperationType.CancelReadPoll:
                case OperationType.CancelWrite:
                case OperationType.CancelWritePoll:
                    _connections[socket].Cancel(ring, operationType & ~OperationType.CancelGeneric);
                    return;
                case OperationType.CancelAccept:
                    _acceptSockets[socket].Unbid(ring);
                    return;
                case OperationType.CloseConnection:
                    _connections[socket].Close(ring);
                    return;
                case OperationType.CloseAcceptSocket:
                    _acceptSockets[socket].Close(ring);
                    return;
            }

            _asyncOperationStates.Remove(socket, out var state);
            switch (operationType)
            {
                case OperationType.AddAndAcceptPoll:
                    AddAndAcceptPoll(socket, state);
                    return;
                case OperationType.AddAndConnect:
                    AddAndConnect(socket, state);
                    return;
                case OperationType.CompleteInbound:
                    _connections[socket].CompleteInbound(ring, (Exception) state);
                    return;
                case OperationType.CompleteOutbound:
                    _connections[socket].CompleteOutbound(ring, (Exception) state);
                    return;
                case OperationType.Abort:
                    if (_connections.TryGetValue(socket, out var connection))
                        connection.Abort(ring, (ConnectionAbortedException) state);
                    return;
            }
        }

        private void AddAndAcceptPoll(int socket, object state)
        {
            var acceptSocket = (AcceptSocket) state;
            _acceptSockets[socket] = acceptSocket;
            acceptSocket.AcceptPoll(_ring);
        }

        private void AddAndConnect(int socket, object context)
        {
            var ring = _ring;
            var outboundConnectionContext = (OutboundConnection) context;
            _connections[socket] = outboundConnectionContext;
            if (outboundConnectionContext.Connect(ring))
            {
                CompleteConnect(ring, outboundConnectionContext, 0);
            }
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
                    case OperationType.Read:
                        ctx.CompleteRead(ring, result);
                        return;
                    case OperationType.ReadPoll:
                        ctx.CompleteReadPoll(ring, result);
                        return;
                    case OperationType.Write:
                        ctx.CompleteWrite(ring, result);
                        return;
                    case OperationType.WritePoll:
                        ctx.CompleteWritePoll(ring, result);
                        return;
                }
            }
            else
            {
                CompleteLessFrequentOperations(socket, operationType, result);
            }
        }

        private void CompleteLessFrequentOperations(int socket, OperationType operationType, int result)
        {
            var ring = _ring;
            switch (operationType)
            {
                case OperationType.AcceptPoll:
                    CompleteAcceptPoll(socket, result);
                    return;
                case OperationType.Accept:
                    CompleteAcceptViaRing(socket, result);
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
                case OperationType.WritePollDuringConnect:
                    CompleteWritePollDuringConnect(result, context, ring);
                    return;
                case OperationType.CloseConnection:
                    CompleteCloseConnection(context, socket);
                    return;
            }
        }

        private void CompleteAcceptPoll(int socket, int result)
        {
            var ring = _ring;
            if (!_acceptSockets.TryGetValue(socket, out var acceptSocket)) return; // socket already closed
            if (acceptSocket.CompleteAcceptPoll(ring, result, out var acceptedSocket)) // prepares accept via ring or accepts via syscall
            {
                // already accepted via syscall pre v5.5
                acceptSocket.TryCompleteAccept(ring, acceptedSocket, out var connection); // will always succeed as we already accepted
                CompleteAcceptDirect(connection, acceptSocket, ring);
            }
        }

        private void CompleteAcceptViaRing(int socket, int result)
        {
            var ring = _ring;
            if (!_acceptSockets.TryGetValue(socket, out var acceptSocket)) return; // socket already closed
            if (acceptSocket.TryCompleteAccept(ring, result, out var connection))
            {
                CompleteAccept(connection, acceptSocket, ring);
            }
        }

        private void CompleteAcceptDirect(InboundConnection connection, AcceptSocket acceptSocket, Ring ring)
        {
            CompleteAccept(connection, acceptSocket, ring);
        }

        private void CompleteAccept(InboundConnection connection, AcceptSocket acceptSocket, Ring ring)
        {
            _connections[connection.Socket] = connection;
            acceptSocket.AcceptQueue.TryWrite(connection);

            acceptSocket.AcceptPoll(ring);
            connection.ReadPoll(ring);
            connection.ReadFromApp(ring);
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

        private static void CompleteWritePollDuringConnect(int result, IoUringConnection context, Ring ring)
        {
            if (((OutboundConnection) context).CompleteWritePollDuringConnect(ring, result))
            {
                context.ReadPoll(ring);
                context.ReadFromApp(ring);
            }
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