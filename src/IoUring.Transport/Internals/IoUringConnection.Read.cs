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
            ring.PreparePollAdd(socket, (ushort) POLLIN, AsyncOperation.ReadPollFor(socket).AsUlong());
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

            Read(ring);
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

        private unsafe void Read(Ring ring)
        {
            var memory = Inbound.GetMemory(MemoryPool.MaxBufferSize);
            var handle = memory.Pin();

            var readVecs = ReadVecs;
            readVecs[0].iov_base = handle.Pointer;
            readVecs[0].iov_len = memory.Length;

            ReadHandles[0] = handle;

            int socket = Socket;
            ring.PrepareReadV(socket, readVecs, 1, 0, 0, AsyncOperation.ReadFrom(socket).AsUlong());
            SetFlag(ConnectionState.Reading);
        }

        public void CompleteRead(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.Reading);
            foreach (var readHandle in ReadHandles)
            {
                readHandle.Dispose();
            }

            if (result <= 0)
            {
                HandleCompleteReadError(ring, result);
                return;
            }

            Inbound.Advance(result);
            FlushRead(ring);
        }

        private void HandleCompleteReadError(Ring ring, int result)
        {
            Exception ex;
            if (result >= 0)
            {
                // EOF
                CompleteInbound(ring, null);
                return;
            }

            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                Read(ring);
                return;
            }

            if (HasFlag(ConnectionState.ReadCancelled) && err == ECANCELED)
            {
                return;
            }

            if (-result == ECONNRESET)
            {
                ex = new ErrnoException(ECONNRESET);
                ex = new ConnectionResetException(ex.Message, ex);
            }
            else
            {
                ex = new ErrnoException(-result);
            }

            CompleteInbound(ring, ex);
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
                _scheduler.ScheduleAsyncRead(Socket);
            }

            return result;
        }
    }
}