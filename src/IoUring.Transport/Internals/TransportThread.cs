using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using System.Threading.Tasks;
using IoUring.Transport.Internals.Inbound;
using IoUring.Transport.Internals.Outbound;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals
{
    internal sealed class TransportThread : IoUringThread
    {
        private readonly ConcurrentQueue<AsyncOperation> _asyncOperationQueue = new ConcurrentQueue<AsyncOperation>();
        private readonly ConcurrentDictionary<int, object> _asyncOperationStates = new ConcurrentDictionary<int, object>();
        private readonly ConcurrentDictionary<IPEndPoint, AcceptSocket> _acceptSocketsPerEndPoint = new ConcurrentDictionary<IPEndPoint, AcceptSocket>();
        private readonly Dictionary<int, AcceptSocket> _acceptSockets = new Dictionary<int, AcceptSocket>();
        private readonly Dictionary<int, IoUringConnection> _connections = new Dictionary<int, IoUringConnection>();
        private readonly TransportThreadScheduler _scheduler;
        private readonly MemoryPool<byte> _memoryPool;

        public TransportThread(IoUringOptions options)
            : base("IoUring Transport Thread", options)
        {
            _memoryPool = new SlabMemoryPool();
            _scheduler = new TransportThreadScheduler(_unblockHandle, _asyncOperationQueue, _asyncOperationStates);
        }

        public TransportThreadScheduler Scheduler => _scheduler;

        public ValueTask<ConnectionContext> Connect(IPEndPoint endpoint)
        {
            var tcs = new TaskCompletionSource<ConnectionContext>(TaskCreationOptions.RunContinuationsAsynchronously); // Ensure the transport thread doesn't run continuations
            var context = OutboundConnection.Connect(endpoint, tcs, _memoryPool, _options, _scheduler);
            _scheduler.ScheduleAsyncConnect(context.Socket, context);

            return new ValueTask<ConnectionContext>(tcs.Task);
        }

        public void Bind(IPEndPoint endpoint, ChannelWriter<ConnectionContext> connectionSource)
        {
            Debug.WriteLine($"Binding to new endpoint {endpoint}");
            var context = AcceptSocket.Bind(endpoint, connectionSource, _options);
            _acceptSocketsPerEndPoint[endpoint] = context;
            _scheduler.ScheduleAsyncBind(context.Socket, context);
        }

        public ValueTask Unbind(IPEndPoint endPoint)
        {
            if (_acceptSocketsPerEndPoint.Remove(endPoint, out var acceptSocket))
            {
                Debug.WriteLine($"Unbinding from {endPoint}");
                _scheduler.ScheduleAsyncUnbind(acceptSocket.Socket);
                return new ValueTask(acceptSocket.UnbindCompletion);
            }

            return default;
        }

        protected override void RunAsyncOperations()
        {
            while (_asyncOperationQueue.TryDequeue(out var operation))
            {
                var (socket, operationType) = operation;
                switch (operationType)
                {
                    case OperationType.ReadPoll:
                        _connections[socket].ReadPoll(_ring);
                        continue;
                    case OperationType.WritePoll:
                        _connections[socket].WritePoll(_ring);
                        continue;
                    case OperationType.Unbind:
                        _acceptSockets[socket].Unbid(_ring);
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
                        _connections[socket].CompleteInbound(_ring, (Exception) state);
                        break;
                    case OperationType.CompleteOutbound:
                        _connections[socket].CompleteOutbound(_ring, (Exception) state);
                        break;
                    case OperationType.Abort:
                        _connections[socket].Abort(_ring, (ConnectionAbortedException) state);
                        break;
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

        protected override void Complete()
        {
            while (_ring.TryRead(out Completion c))
            {
                var (socket, operationType) = AsyncOperation.FromUlong(c.userData);
                switch (operationType)
                {
                    // Ignored case OperationType.CancelGeneric: continue;
                    case OperationType.EventFdReadPoll:
                    case OperationType.EventFdRead:
                        _unblockHandle.HandleEventFdCompletion(operationType, c.result);
                        continue;
                    case OperationType.Accept:
                        CompleteAccept(socket, c.result);
                        continue;
                    case OperationType.CancelAccept:
                        _acceptSockets[socket].Close(_ring);
                        continue;
                    case OperationType.CloseAcceptSocket:
                        CompleteCloseAcceptSocket(socket);
                        break;
                }

                if (!_connections.TryGetValue(socket, out var context)) continue;
                switch (operationType)
                {
                    case OperationType.ReadPoll:
                        context.CompleteReadPoll(_ring, c.result);
                        break;
                    case OperationType.Read:
                        context.CompleteRead(_ring, c.result);
                        break;
                    case OperationType.WritePoll:
                        context.CompleteWritePoll(_ring, c.result);
                        break;
                    case OperationType.Write:
                        context.CompleteWrite(_ring, c.result);
                        break;
                    case OperationType.Connect:
                        CompleteConnect((OutboundConnection) context, c.result);
                        break;
                    case OperationType.CloseConnection:
                        CompleteCloseConnection(context, socket);
                        break;
                }
            }
        }

        private void CompleteAccept(int socket, int result)
        {
            if (!_acceptSockets.TryGetValue(socket, out var acceptSocket)) return; // socket already closed
            if (acceptSocket.TryCompleteAccept(_ring, result, _memoryPool, _scheduler, out InboundConnection connection))
            {
                _connections[connection.Socket] = connection;
                acceptSocket.AcceptQueue.TryWrite(connection);

                acceptSocket.Accept(_ring);
                connection.ReadPoll(_ring);
                connection.ReadFromApp(_ring);
            }
        }

        private void CompleteCloseAcceptSocket(int socket)
        {
            _acceptSockets.Remove(socket, out var acceptSocket);
            acceptSocket.CompleteClose();
        }

        private void CompleteConnect(OutboundConnection context, int result)
        {
            context.CompleteConnect(_ring, result);

            context.ReadPoll(_ring);
            context.ReadFromApp(_ring);
        }

        private void CompleteCloseConnection(IoUringConnection context, int socket)
        {
            _connections.Remove(socket);
            context.CompleteClosed();
        }

        public override async ValueTask DisposeAsync()
        {
            Debug.WriteLine("Disposing TransportThread");

            foreach (var (endpoint, _) in _acceptSocketsPerEndPoint)
            {
                await Unbind(endpoint);
            }

            await base.DisposeAsync();
        }
    }
}