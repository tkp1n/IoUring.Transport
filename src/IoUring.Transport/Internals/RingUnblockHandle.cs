using System;
using System.Runtime.CompilerServices;
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
        private readonly byte[] _eventfdBytes = GC.AllocateUninitializedArray<byte>(8, pinned: true);
        private readonly byte[] _eventfdIoVecBytes = GC.AllocateUninitializedArray<byte>(SizeOf.iovec, pinned: true);
        private int _blockingMode;

        public RingUnblockHandle(Ring ring)
        {
            _ring = ring;

            int res = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
            if (res == -1) ThrowHelper.ThrowNewErrnoException();
            _eventfd = res;

            IoVec->iov_base = Buffer;
            IoVec->iov_len = BufferLen;
        }

        private iovec* IoVec => (iovec*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_eventfdIoVecBytes);
        private void* Buffer => MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_eventfdBytes);
        private int BufferLen => _eventfdBytes.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void NotifyTransitionToBlockedAfterDoubleCheck()
            => Volatile.Write(ref _blockingMode,  True);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void NotifyTransitionToUnblocked()
            => Volatile.Write(ref _blockingMode,  False);

        public void NotifyStartOfEventLoop() => ReadPollEventFd();

        private void ReadPollEventFd()
        {
            int fd = _eventfd;
            _ring.PreparePollAdd(fd, (ushort)POLLIN, AsyncOperation.PollEventFd(fd).AsUlong());
        }

        private void ReadEventFd()
        {
            int fd = _eventfd;
            _ring.PrepareReadV(fd, IoVec, 1, userData: AsyncOperation.ReadEventFd(fd).AsUlong());
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
                HandleCompleteEventFdReadPollError(result);
                return;
            }

            ReadEventFd();
        }

        private void HandleCompleteEventFdReadPollError(int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EINTR)
            {
                ReadPollEventFd();
            }
            else
            {
                ThrowHelper.ThrowNewErrnoException(err);
            }
        }

        private void CompleteEventFdRead(int result)
        {
            if (result < 0)
            {
                HandleCompleteEventFdReadError(result);
                return;
            }

            ReadPollEventFd();
        }

        private void HandleCompleteEventFdReadError(int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EINTR)
            {
                ReadEventFd();
            }
            else
            {
                ThrowHelper.ThrowNewErrnoException(err);
            }
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
            byte* val = stackalloc byte[sizeof(ulong)];
            Unsafe.WriteUnaligned(val, 1UL);
            _eventfd.Write(val, sizeof(ulong));
        }

        public void Dispose()
        {
            close(_eventfd);
        }
    }
}