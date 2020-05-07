using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace IoUring.Transport.Internals
{
    internal abstract class IoUringThread : IAsyncDisposable
    {
        private static readonly Dictionary<string, int> _threadIds = new Dictionary<string, int>();

        protected readonly IoUringOptions _options;
        protected readonly Ring _ring;
        protected readonly RingUnblockHandle _unblockHandle;
        private readonly Thread _thread;
        private readonly TaskCompletionSource<object> _threadCompletion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _disposed;

        protected IoUringThread(string name, IoUringOptions options)
        {
            _options = options;
            _ring = new Ring(options.RingSize);
            _unblockHandle = new RingUnblockHandle(_ring);

            int id;
            lock (_threadIds)
            {
                if (!_threadIds.TryGetValue(name, out id))
                {
                    _threadIds[name] = id = -1;
                }

                _threadIds[name] = id += 1;
            }

            _thread = new Thread(obj => ((IoUringThread) obj).Loop())
            {
                IsBackground = true,
                Name = $"{name} - {id}"
            };
        }

        protected abstract void RunAsyncOperations();
        protected abstract void Complete();

        public void Run()
        {
            if (!_thread.ThreadState.HasFlag(System.Threading.ThreadState.Unstarted)) ThrowHelper.ThrowNewInvalidOperationException();
            _thread.Start(this);
        }

        private void Loop()
        {
            var state = LoopState.Running;
            _unblockHandle.NotifyStartOfEventLoop();

            while (!_disposed)
            {
                RunAsyncOperations();
                state = Submit(state);
                if (state == LoopState.WillBlock) continue; // Check operation queue again before blocking
                Complete();
            }

            Debug.WriteLine($"{Thread.CurrentThread.Name} is done");

            _threadCompletion.TrySetResult(null);
        }

        private LoopState Submit(LoopState state)
        {
            uint minComplete;
            if (_ring.SubmissionEntriesUsed == 0)
            {
                if (state == LoopState.Running)
                {
                    _unblockHandle.NotifyTransitionToBlockedAfterDoubleCheck();
                    return LoopState.WillBlock;
                }
                minComplete = 1;
                Debug.WriteLine($"{Thread.CurrentThread.Name} is going to block");
            }
            else
            {
                minComplete = 0;
            }

            _ring.SubmitAndWait(minComplete, out _);
            Debug.WriteLine($"{Thread.CurrentThread.Name} is unblocked");
            _unblockHandle.NotifyTransitionToUnblocked();
            return LoopState.Running;
        }

        public virtual async ValueTask DisposeAsync()
        {
            Debug.WriteLine("Disposing IoUringThread");
            _disposed = true;
            _unblockHandle.UnblockIfRequired();

            await _threadCompletion.Task;
            _thread.Join();

            _ring.Dispose();
            _unblockHandle.Dispose();
        }
    }
}