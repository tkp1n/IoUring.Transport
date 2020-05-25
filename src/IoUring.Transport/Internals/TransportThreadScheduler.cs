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

        public void ScheduleAcceptPoll(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.PollAcceptFrom(socket));
        }

        public void ScheduleAsyncAddAndAccept(int socket, object acceptSocket)
        {
            _asyncOperationStates[socket] = acceptSocket;
            _asyncOperationQueue.Enqueue(AsyncOperation.AddAndAccept(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAccept(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.AcceptFrom(socket));
        }

        public void ScheduleConnect(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.ConnectOn(socket));
        }

        public void ScheduleWritePollDuringComplete(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.WritePollDuringConnect(socket));
        }

        public void ScheduleAsyncAddAndConnect(int socket, object state)
        {
            _asyncOperationStates[socket] = state;
            _asyncOperationQueue.Enqueue(AsyncOperation.AddAndConnect(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleReadPoll(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.ReadPollFor(socket));
        }

        public void ScheduleAsyncReadPoll(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.ReadPollFor(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleWritePoll(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.WritePollFor(socket));
        }

        public void ScheduleAsyncWritePoll(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.WritePollFor(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleRead(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.ReadFrom(socket));
        }

        public void ScheduleWrite(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.WriteTo(socket));
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

        public void SchedulePollReceive(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.RecvSocketPoll(socket));
        }

        public void ScheduleAsyncPollReceive(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.RecvSocketPoll(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleRecvSocket(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.RecvSocket(socket));
        }

        public void ScheduleCancel(AsyncOperation cancelOperation)
        {
            _asyncOperationQueue.Enqueue(cancelOperation);
        }

        public void ScheduleCloseConnection(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.CloseConnection(socket));
        }

        public void ScheduleCloseAcceptSocket(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.CloseAcceptSocket(socket));
        }
    }
}