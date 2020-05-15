using System;
using System.Collections.Concurrent;

namespace IoUring.Transport.Internals
{
    internal sealed class TransportThreadScheduler
    {
        private readonly RingUnblockHandle _unblockHandle;
        private readonly ConcurrentQueue<AsyncOperation> _asyncOperationQueue;
        private readonly ConcurrentDictionary<int, object> _asyncOperationStates;

        public TransportThreadScheduler(RingUnblockHandle unblockHandle, ConcurrentQueue<AsyncOperation> asyncOperationQueue, ConcurrentDictionary<int, object> asyncOperationStates)
        {
            _unblockHandle = unblockHandle;
            _asyncOperationQueue = asyncOperationQueue;
            _asyncOperationStates = asyncOperationStates;
        }

        public void ScheduleAsyncBind(int socket, object acceptSocket)
        {
            _asyncOperationStates[socket] = acceptSocket;
            _asyncOperationQueue.Enqueue(AsyncOperation.BindTo(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncConnect(int socket, object state)
        {
            _asyncOperationStates[socket] = state;
            _asyncOperationQueue.Enqueue(AsyncOperation.ConnectOn(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncReadPoll(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.ReadPollFor(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncWritePoll(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.WritePollFor(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncInboundCompletion(int socket, Exception error)
        {
            _asyncOperationStates[socket] = error;
            _asyncOperationQueue.Enqueue(AsyncOperation.CompleteInboundOf(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncOutboundCompletion(int socket, Exception error)
        {
            _asyncOperationStates[socket] = error;
            _asyncOperationQueue.Enqueue(AsyncOperation.CompleteOutboundOf(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncAbort(int socket, Exception error)
        {
            _asyncOperationStates[socket] = error;
            _asyncOperationQueue.Enqueue(AsyncOperation.Abort(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncUnbind(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.UnbindFrom(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncPollReceive(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.RecvSocketPoll(socket));
            _unblockHandle.UnblockIfRequired();
        }
    }
}