using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal sealed unsafe class RingUnblockHandle : IDisposable
    {
        private const int True = 1;
        private const int False = 0;

        private readonly Ring _ring;
        private readonly LinuxSocket _eventfd;
        private readonly iovec* _eventfdIoVec;
        private GCHandle _eventfdBytes; // mutable struct, do not make this readonly
        private GCHandle _eventfdIoVecHandle; // mutable struct, do not make this readonly
        private int _blockingMode;

        public RingUnblockHandle(Ring ring)
        {
            _ring = ring;

            int res = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
            if (res == -1) throw new ErrnoException(errno);
            _eventfd = res;

            // Pin buffer for eventfd reads via io_uring
            byte[] bytes = new byte[8];
            _eventfdBytes = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            // Pin iovec used for eventfd reads via io_uring
            var eventfdIoVec = new iovec
            {
                iov_base = (void*) _eventfdBytes.AddrOfPinnedObject(),
                iov_len = bytes.Length
            };
            _eventfdIoVecHandle = GCHandle.Alloc(eventfdIoVec, GCHandleType.Pinned);
            _eventfdIoVec = (iovec*) _eventfdIoVecHandle.AddrOfPinnedObject();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void NotifyTransitionToBlockedAfterDoubleCheck()
            => Volatile.Write(ref _blockingMode,  True);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void NotifyTransitionToUnblocked()
            => Volatile.Write(ref _blockingMode,  False);

        public void NotifyStartOfEventLoop() => ReadPollEventFd();

        private void ReadPollEventFd()
        {
            Debug.WriteLine("Adding poll on eventfd");
            int fd = _eventfd;
            _ring.PreparePollAdd(fd, (ushort)POLLIN, AsyncOperation.PollEventFd(fd).AsUlong());
        }

        private void ReadEventFd()
        {
            Debug.WriteLine("Adding read on eventfd");
            int fd = _eventfd;
            _ring.PrepareReadV(fd, _eventfdIoVec, 1, userData: AsyncOperation.ReadEventFd(fd).AsUlong());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void HandleEventFdCompletion(OperationType operationType, int result)
        {
            if (operationType == OperationType.EventFdReadPoll) CompleteEventFdReadPoll(result);
            else if (operationType == OperationType.EventFdRead) CompleteEventFdRead(result);
        }

        private void CompleteEventFdReadPoll(int result)
        {
            if (result < 0)
            {
                var err = -result;
                if (err == EAGAIN || err == EINTR)
                {
                    Debug.WriteLine("polled eventfd for nothing");

                    ReadPollEventFd();
                    return;
                }

                throw new ErrnoException(err);
            }

            Debug.WriteLine("EventFd poll completed");
            ReadEventFd();
        }

        private void CompleteEventFdRead(int result)
        {
            if (result < 0)
            {
                var err = -result;
                if (err == EAGAIN || err == EINTR)
                {
                    Debug.WriteLine("read eventfd for nothing");

                    ReadEventFd();
                    return;
                }

                throw new ErrnoException(err);
            }

            Debug.WriteLine("EventFd read completed");
            ReadPollEventFd();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ShouldUnblock()
            => Interlocked.CompareExchange(ref _blockingMode, False, True) == True;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnblockIfRequired()
        {
            if (ShouldUnblock())
            {
                // The transport thread reported it is (probably still) blocking. We therefore must unblock it by
                // writing to the eventfd established for that purpose.
                Unblock();
            }

            // If the transport thread is not (yet) in blocking mode, we have the guarantee, that it will read
            // from the queues one more time before actually blocking. Therefore, it is safe not to unblock now.
        }

        private void Unblock()
        {
            Debug.WriteLine("Attempting to unblock thread on ring");

            byte* val = stackalloc byte[sizeof(ulong)];
            Unsafe.WriteUnaligned(val, 1UL);
            _eventfd.Write(val, sizeof(ulong));
        }

        public void Dispose()
        {
            if (_eventfdBytes.IsAllocated)
            {
                close(_eventfd);
                _eventfdBytes.Free();
                _eventfdIoVecHandle.Free();
            }
        }
    }
}