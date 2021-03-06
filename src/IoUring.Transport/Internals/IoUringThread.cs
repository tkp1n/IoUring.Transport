﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace IoUring.Transport.Internals
{
    internal abstract class IoUringThread : IAsyncDisposable
    {
        public const int NoCpuAffinity = -1;
        private static readonly Dictionary<string, int> _threadIds = new();

        protected readonly IoUringOptions _options;
        protected readonly Ring _ring;
        protected readonly RingUnblockHandle _unblockHandle;
        private readonly int _cpuId;
        private readonly Thread _thread;
        private readonly TaskCompletionSource _threadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _disposed;

        protected IoUringThread(string name, IoUringOptions options, int cpuId)
        {
            _options = options;
            _ring = new Ring(options.RingSize);
            _unblockHandle = new RingUnblockHandle(_ring);
            _cpuId = cpuId;

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
        protected abstract void Complete(int socket, OperationType operationType, int result);

        public void Run()
        {
            if (!_thread.ThreadState.HasFlag(System.Threading.ThreadState.Unstarted)) ThrowHelper.ThrowNewInvalidOperationException();
            _thread.Start(this);
        }

        private void Loop()
        {
            SetAffinity();

            var state = LoopState.Running;
            _unblockHandle.NotifyStartOfEventLoop(_ring);
            uint skip = 0;

            while (!_disposed)
            {
                RunAsyncOperations();
                state = Submit(state, skip);
                if (state == LoopState.WillBlock) continue; // Check operation queue again before blocking
                skip = Complete();
            }

            _threadCompletion.TrySetResult();
        }

        private LoopState Submit(LoopState state, uint skip)
        {
            uint minComplete;
            if (_ring.SubmissionEntriesUsed - skip == 0)
            {
                if (state == LoopState.Running)
                {
                    _unblockHandle.NotifyTransitionToBlockedAfterDoubleCheck();
                    return LoopState.WillBlock;
                }
                minComplete = 1;
            }
            else
            {
                minComplete = 0;
            }

            _ring.SubmitAndWait(minComplete, skip, out _);
            _unblockHandle.NotifyTransitionToUnblocked();
            return LoopState.Running;
        }

        private uint Complete()
        {
            // Reserve a submission to prepare operations on the eventFd later on
            var eventFdSubmissionAvailable = _ring.TryGetSubmissionQueueEntryUnsafe(out var eventFdSubmission);
            uint skip = 1;

            foreach (var completion in _ring.Completions)
            {
                var (result, userData) = completion;
                var (socket, operationType) = AsyncOperation.FromUlong(userData);
                if ((operationType & OperationType.EventFdOperation) == 0)
                {
                    // hot path
                    Complete(socket, operationType, result);
                }
                else
                {
                    if (!eventFdSubmissionAvailable) ThrowHelper.ThrowNewSubmissionQueueFullException();
                    if (operationType == OperationType.EventFdReadPoll)
                    {
                        _unblockHandle.CompleteEventFdReadPoll(eventFdSubmission, result);
                    }
                    else
                    {
                        _unblockHandle.CompleteEventFdRead(eventFdSubmission, result);
                    }

                    skip = 0; // Don't skip eventFd submission, now that we used it
                }
            }

            return skip;
        }

        private void SetAffinity()
        {
            if (_cpuId == NoCpuAffinity) return;

            Scheduler.SetCurrentThreadAffinity(_cpuId);
        }

        public virtual async ValueTask DisposeAsync()
        {
            _disposed = true;
            _unblockHandle.UnblockIfRequired();

            await _threadCompletion.Task;
            _thread.Join();

            _ring.Dispose();
            _unblockHandle.Dispose();
        }
    }
}