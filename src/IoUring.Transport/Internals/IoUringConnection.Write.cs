using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Connections;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal abstract partial class IoUringConnection
    {
        public void WritePoll(Ring ring)
        {
            int socket = Socket;
            ring.PreparePollAdd(socket, (ushort) POLLOUT, AsyncOperation.WritePollFor(socket).AsUlong());
            SetFlag(ConnectionState.PollingWrite);
        }

        public void CompleteWritePoll(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.PollingWrite);
            if (result < 0)
            {
                HandleCompleteWritePollError(ring, result);
                return;
            }

            Write(ring);
        }

        private void HandleCompleteWritePollError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                WritePoll(ring);
            }
            else if (!(err == ECANCELED && HasFlag(ConnectionState.WriteCancelled)))
            {
                CompleteOutbound(ring, new ErrnoException(err));
            }
        }

        private unsafe void Write(Ring ring)
        {
            var buffer = ReadResult;

            var writeHandles = WriteHandles;
            var writeVecs = WriteVecs;
            int ctr = 0;
            foreach (var memory in buffer)
            {
                var handle = memory.Pin();

                writeVecs[ctr].iov_base = handle.Pointer;
                writeVecs[ctr].iov_len = memory.Length;

                writeHandles[ctr] = handle;

                ctr++;
                if (ctr == writeHandles.Length) break;
            }

            LastWrite = buffer;
            int socket = Socket;
            ring.PrepareWriteV(socket, writeVecs ,ctr, 0 ,0, AsyncOperation.WriteTo(socket).AsUlong());
            SetFlag(ConnectionState.Writing);
        }

        public unsafe void CompleteWrite(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.Writing);
            foreach (var writeHandle in WriteHandles)
            {
                if (writeHandle.Pointer == (void*)IntPtr.Zero) break;
                writeHandle.Dispose();
            }

            var lastWrite = LastWrite;
            if (result < 0)
            {
                HandleCompleteWriteError(ring, result, lastWrite);
                return;
            }

            SequencePosition end;
            if (result == 0)
            {
                end = lastWrite.Start;
            }
            else if (lastWrite.Length == result)
            {
                end = lastWrite.End;
            }
            else
            {
                end = lastWrite.GetPosition(result);
            }

            Outbound.AdvanceTo(end);
            ReadFromApp(ring);
        }

        private void HandleCompleteWriteError(Ring ring, int result, ReadOnlySequence<byte> lastWrite)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                Outbound.AdvanceTo(lastWrite.Start);
                ReadFromApp(ring);
                return;
            }

            if (HasFlag(ConnectionState.WriteCancelled) && err == ECANCELED)
            {
                return;
            }

            Exception ex;
            if (err == ECONNRESET)
            {
                ex = new ErrnoException(err);
                ex = new ConnectionResetException(ex.Message, ex);
            }
            else
            {
                ex = new ErrnoException(err);
            }

            CompleteOutbound(ring, ex);
        }

        public void ReadFromApp(Ring ring)
        {
            var result = ReadAsync();
            if (result.CompletedSuccessfully)
            {
                // unlikely
                WritePoll(ring);
            }
            else if (result.CompletedExceptionally)
            {
                CompleteOutbound(ring, result.GetError());
            }
        }

        private AsyncOperationResult ReadAsync()
        {
            var awaiter = Outbound.ReadAsync().GetAwaiter();
            _readResultAwaiter = awaiter;
            if (awaiter.IsCompleted)
            {
                return HandleReadFromApp(true);
            }

            awaiter.UnsafeOnCompleted(_onReadFromApp);
            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private AsyncOperationResult HandleReadFromApp(bool onTransportThread = false)
        {
            Exception error = null;
            try
            {
                var readResult = _readResultAwaiter.GetResult();
                var buffer = readResult.Buffer;
                ReadResult = buffer;
                if ((buffer.IsEmpty && readResult.IsCompleted) || readResult.IsCanceled)
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
                _scheduler.ScheduleAsyncOutboundCompletion(Socket, result.GetError());
            }
            else
            {
                _scheduler.ScheduleAsyncWrite(Socket);
            }

            return result;
        }
    }
}