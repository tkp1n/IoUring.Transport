﻿using System;
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

        private readonly LinuxSocket _eventfd;
        private readonly byte[] _eventfdBytes = GC.AllocateUninitializedArray<byte>(8, pinned: true);
        private readonly byte[] _eventfdIoVecBytes;
        private int _blockingMode;
        private bool _supportsRead;
        private bool _supportsFastPoll;

        public RingUnblockHandle(Ring ring)
        {
            _supportsRead = ring.Supports(RingOperation.Read);
            _supportsFastPoll = ring.SupportsFastPoll;

            int res = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
            if (res == -1) ThrowHelper.ThrowNewErrnoException();
            _eventfd = res;

            if (!_supportsRead)
            {
                _eventfdIoVecBytes = GC.AllocateUninitializedArray<byte>(SizeOf.iovec, pinned: true);
                IoVec->iov_base = Buffer;
                IoVec->iov_len = BufferLen;
            }
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

        public void NotifyStartOfEventLoop(Ring ring)
        {
            int fd = _eventfd;
            if (_supportsFastPoll)
            {
                ring.PrepareRead(fd, Buffer, (uint) BufferLen, default, AsyncOperation.ReadEventFd(fd).AsUlong());
            }
            else
            {
                ring.PreparePollAdd(fd, (ushort) POLLIN, AsyncOperation.PollEventFd(fd).AsUlong());
            }
        }

        private void ReadPollEventFd(Submission submission)
        {
            int fd = _eventfd;
            submission.PreparePollAdd(fd, (ushort) POLLIN, AsyncOperation.PollEventFd(fd).AsUlong());
        }

        public void CompleteEventFdReadPoll(Submission submission, int result)
        {
            if (result < 0)
            {
                HandleCompleteEventFdReadPollError(submission, result);
                return;
            }

            ReadEventFd(submission);
        }

        private void HandleCompleteEventFdReadPollError(Submission submission, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EINTR)
            {
                ReadPollEventFd(submission);
            }
            else
            {
                ThrowHelper.ThrowNewErrnoException(err);
            }
        }

        private void ReadEventFd(Submission submission)
        {
            int fd = _eventfd;
            if (_supportsRead)
            {
                submission.PrepareRead(fd, Buffer, (uint) BufferLen, default, userData: AsyncOperation.ReadEventFd(fd).AsUlong());
            }
            else
            {
                submission.PrepareReadV(fd, IoVec, 1, userData: AsyncOperation.ReadEventFd(fd).AsUlong());
            }
        }

        public void CompleteEventFdRead(Submission submission, int result)
        {
            if (result < 0 && HandleCompleteEventFdReadError(result))
            {
                return;
            }

            if (_supportsFastPoll)
            {
                ReadEventFd(submission);
            }
            else
            {
                ReadPollEventFd(submission);
            }
        }

        private bool HandleCompleteEventFdReadError(int result)
        {
            var err = -result;
            if (err != EAGAIN && err != EINTR)
            {
                ThrowHelper.ThrowNewErrnoException(err);
            }

            return true;
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