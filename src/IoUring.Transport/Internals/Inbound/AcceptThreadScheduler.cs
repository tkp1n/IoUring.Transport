using System.Collections.Concurrent;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed class AcceptThreadScheduler
    {
        private readonly RingUnblockHandle _unblockHandle;
        private readonly ConcurrentQueue<AsyncOperation> _asyncOperationQueue;

        public AcceptThreadScheduler(RingUnblockHandle unblockHandle, ConcurrentQueue<AsyncOperation> asyncOperationQueue)
        {
            _unblockHandle = unblockHandle;
            _asyncOperationQueue = asyncOperationQueue;
        }

        public void ScheduleAsyncAcceptPoll(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.PollAcceptFrom(socket));
            _unblockHandle.UnblockIfRequired();
        }

        public void ScheduleAsyncUnbind(int socket)
        {
            _asyncOperationQueue.Enqueue(AsyncOperation.UnbindFrom(socket));
            _unblockHandle.UnblockIfRequired();
        }
    }
}