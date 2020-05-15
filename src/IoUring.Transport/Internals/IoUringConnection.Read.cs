using System;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Connections;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal abstract partial class IoUringConnection
    {
        public void ReadPoll(Ring ring)
        {
            int socket = Socket;
            if (!ring.TryPreparePollAdd(socket, (ushort) POLLIN, AsyncOperation.ReadPollFor(socket).AsUlong()))
            {
                _scheduler.ScheduleReadPoll(socket);
                return;
            }

            SetFlag(ConnectionState.PollingRead);
        }

        public void CompleteReadPoll(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.PollingRead);
            if (result < 0)
            {
                HandleCompleteReadPollError(ring, result);
                return;
            }

            int memoryRequirement = DetermineReadAllocation();
            int ioVecs = PrepareReadIoVecs(memoryRequirement);
            _readIoVecsInUse = (byte) ioVecs;
            Read(ring, ioVecs);
        }

        private void HandleCompleteReadPollError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                ReadPoll(ring);
            }
            else if (!(err == ECANCELED && HasFlag(ConnectionState.ReadCancelled)))
            {
                CompleteInbound(ring, new ErrnoException(err));
            }
        }

        private int DetermineReadAllocation()
        {
            var maxBufferSize = MemoryPool.MaxBufferSize;

            int lastRead = _state; // state is amount of bytes read previously
            int reserve;

            if (lastRead < maxBufferSize)
            {
                // This is the first read or we've read less than maxBufferSize, let's not ask for more this time either
                reserve = maxBufferSize;
            }
            else
            {
                // We've read maxBufferSize last time, there may be much more... lets' check
                reserve = Socket.GetReadableBytes();
                if (reserve == 0)
                {
                    reserve = maxBufferSize;
                }
            }

            return reserve;
        }

        private unsafe int PrepareReadIoVecs(int memoryRequirement)
        {
            int maxBufferSize = MemoryPool.MaxBufferSize;
            memoryRequirement = Math.Min(memoryRequirement, maxBufferSize * ReadIOVecCount);
            int i = 0;
            int advanced = 0;
            var writer = Inbound;
            var readVecs = ReadVecs;
            var handles = ReadHandles;
            while (memoryRequirement > maxBufferSize)
            {
                var memory = writer.GetMemory(maxBufferSize);
                var handle = memory.Pin();

                readVecs[i].iov_base = handle.Pointer;
                readVecs[i].iov_len = memory.Length;
                handles[i] = handle;

                writer.Advance(maxBufferSize);
                i++;
                advanced += memory.Length;
                memoryRequirement -= memory.Length;
            }

            _state = advanced; // Store already advanced number of bytes, to determine amount to advance after read.

            if (memoryRequirement > 0)
            {
                var memory = writer.GetMemory(memoryRequirement);
                var handle = memory.Pin();

                readVecs[i].iov_base = handle.Pointer;
                readVecs[i].iov_len = memory.Length;
                handles[i] = handle;
            }

            return i + 1;
        }

        public void Read(Ring ring)
        {
            Read(ring, _readIoVecsInUse);
        }

        private unsafe void Read(Ring ring, int ioVecs)
        {
            int socket = Socket;
            if (!ring.TryPrepareReadV(socket, ReadVecs, ioVecs, 0, 0, AsyncOperation.ReadFrom(socket).AsUlong()))
            {
                _scheduler.ScheduleRead(socket);
                return;
            }

            SetFlag(ConnectionState.Reading);
        }

        public void CompleteRead(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.Reading);
            if (result <= 0)
            {
                if (!HandleCompleteReadError(ring, result))
                {
                    DisposeReadHandles();
                }

                return;
            }

            DisposeReadHandles();

            int advanced = _state;
            uint toAdvance = (uint) (result - advanced);
            if (toAdvance > MemoryPool.MaxBufferSize)
            {
                ThrowHelper.ThrowNewInvalidOperationException();
            }

            Inbound.Advance((int) toAdvance);
            _state = result; // Store result as State to determine memory requirements for next read
            FlushRead(ring);
        }

        // Returns whether the read handles are still needed
        private bool HandleCompleteReadError(Ring ring, int result)
        {
            if (result == 0)
            {
                // EOF
                CompleteInbound(ring, null);
                return false;
            }

            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                Read(ring);
                return true;
            }

            if (HasFlag(ConnectionState.ReadCancelled) && err == ECANCELED)
            {
                return false;
            }

            Exception ex;
            if (err == ECONNRESET)
            {
                ex = new ErrnoException(ECONNRESET);
                ex = new ConnectionResetException(ex.Message, ex);
            }
            else
            {
                ex = new ErrnoException(err);
            }

            CompleteInbound(ring, ex);
            return false;
        }

        private unsafe void DisposeReadHandles()
        {
            foreach (var readHandle in ReadHandles)
            {
                if (readHandle.Pointer == (void*) IntPtr.Zero) break;
                readHandle.Dispose();
            }
        }

        private void FlushRead(Ring ring)
        {
            var result = FlushAsync();
            if (result.CompletedSuccessfully)
            {
              // likely
                ReadPoll(ring);
            }
            else if (result.CompletedExceptionally)
            {
                CompleteInbound(ring, result.GetError());
            }
        }

        private AsyncOperationResult FlushAsync()
        {
            var awaiter = Inbound.FlushAsync().GetAwaiter();
            _flushResultAwaiter = awaiter;
            if (awaiter.IsCompleted)
            {
                return HandleFlushedToApp(true);
            }

            awaiter.UnsafeOnCompleted(_onOnFlushedToApp);
            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private AsyncOperationResult HandleFlushedToApp(bool onTransportThread = false)
        {
            Exception error = null;
            try
            {
                var flushResult = _flushResultAwaiter.GetResult();
                if (flushResult.IsCompleted || flushResult.IsCanceled)
                {
                    error = AsyncOperationResult.CompleteWithoutErrorSentinel;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }

            var result = new AsyncOperationResult(onTransportThread, error);
            if (onTransportThread)
            {
                return result;
            }

            if (error != null)
            {
                _scheduler.ScheduleAsyncInboundCompletion(Socket, result.GetError());
            }
            else
            {
                _scheduler.ScheduleAsyncReadPoll(Socket);
            }

            return result;
        }
    }
}