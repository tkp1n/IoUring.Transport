using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed class AcceptThread : IoUringThread
    {
        private readonly ConcurrentQueue<AsyncOperation> _asyncOperationQueue = new ConcurrentQueue<AsyncOperation>();
        private readonly ConcurrentDictionary<int, AcceptSocket> _acceptSockets = new ConcurrentDictionary<int, AcceptSocket>();
        private readonly ConcurrentDictionary<EndPoint, AcceptSocket> _acceptSocketsPerEndPoint = new ConcurrentDictionary<EndPoint, AcceptSocket>();
        private readonly AcceptThreadScheduler _scheduler;
        private readonly TransportThread[] _transportThreads;
        private int _schedulerIndex;

        public AcceptThread(IoUringOptions options, TransportThread[] transportThreads)
         : base("IoUring Accept Thread", options)
        {
            _scheduler = new AcceptThreadScheduler(_unblockHandle, _asyncOperationQueue);
            _transportThreads = transportThreads;
        }

        public EndPoint Bind(UnixDomainSocketEndPoint unixDomainSocketEndPoint, ChannelWriter<ConnectionContext> acceptQueue)
        {
            var context = AcceptSocket.Bind(unixDomainSocketEndPoint, acceptQueue, _options);
            Bind(context);

            return context.EndPoint;
        }

        public EndPoint Bind(FileHandleEndPoint fileHandleEndPoint, ChannelWriter<ConnectionContext> acceptQueue)
        {
            var context = AcceptSocket.Bind(fileHandleEndPoint, acceptQueue, _options);
            Bind(context);

            return context.EndPoint;
        }

        private void Bind(AcceptSocket context)
        {
            var threads = _transportThreads;
            var handlers = new LinuxSocket[threads.Length];
            for (var i = 0; i < threads.Length; i++)
            {
                handlers[i] = threads[i].RegisterHandlerFor(context.AcceptQueue, context.EndPoint);
            }
            context.Handlers = handlers;

            int socket = context.Socket;
            _acceptSockets[socket] = context;
            _acceptSocketsPerEndPoint[context.EndPoint] = context;
            _scheduler.ScheduleAsyncAcceptPoll(socket);
        }

        public ValueTask Unbind(EndPoint endPoint)
        {
            if (_acceptSocketsPerEndPoint.TryRemove(endPoint, out var acceptSocket))
            {
                _scheduler.ScheduleAsyncUnbind(acceptSocket.Socket);
                return new ValueTask(acceptSocket.UnbindCompletion);
            }

            return default;
        }

        protected override void RunAsyncOperations()
        {
            var ring = _ring;
            var sqesAvailable = ring.SubmissionEntriesAvailable;
            while (sqesAvailable-- > 0 && _asyncOperationQueue.TryDequeue(out var operation))
            {
                var (socket, operationType) = operation;
                switch (operationType)
                {
                    case OperationType.AcceptPoll:
                        _acceptSockets[socket].AcceptPoll(ring);
                        break;
                    case OperationType.Unbind:
                        _acceptSockets[socket].Unbid(ring);
                        break;

                    // below cases are only visited on ring overflow

                    case OperationType.Accept:
                        _acceptSockets[socket].Accept(ring);
                        break;
                    case OperationType.CancelAccept:
                        _acceptSockets[socket].Unbid(ring);
                        break;
                    case OperationType.CloseAcceptSocket:
                        _acceptSockets[socket].Close(ring);
                        break;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void Complete(int socket, OperationType operationType, int result)
        {
            var ring = _ring;
            switch (operationType)
            {
                case OperationType.AcceptPoll:
                    _acceptSockets[socket].CompleteAcceptPoll(ring, result);
                    return;
                case OperationType.Accept:
                    CompleteAccept(socket, result);
                    return;
                case OperationType.CancelAccept:
                    _acceptSockets[socket].Close(ring);
                    return;
                case OperationType.CloseAcceptSocket:
                    CompleteCloseAcceptSocket(socket);
                    break;
            }
        }

        private void CompleteAccept(int socket, int result)
        {
            var ring = _ring;
            if (!_acceptSockets.TryGetValue(socket, out var acceptSocket)) return; // socket already closed
            if (acceptSocket.TryCompleteAcceptSocket(ring, result, out var acceptedSocket))
            {
                var handlers = acceptSocket.Handlers;
                var idx = (_schedulerIndex++) % handlers.Length;
                acceptedSocket.TransferAndClose(handlers[idx]);

                acceptSocket.AcceptPoll(ring);
            }
        }

        private void CompleteCloseAcceptSocket(int socket)
        {
            if  (_acceptSockets.TryRemove(socket, out var acceptSocket))
                acceptSocket.CompleteClose();
        }
    }
}