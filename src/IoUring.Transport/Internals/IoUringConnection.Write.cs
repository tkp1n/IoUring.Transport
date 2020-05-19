using System;
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
            if (!ring.TryPreparePollAdd(socket, (ushort) POLLOUT, AsyncOperation.WritePollFor(socket).AsUlong()))
            {
                _scheduler.ScheduleWritePoll(socket);
                return;
            }

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

            var ioVecs = PrepareWriteIoVecs();
            _writeIoVecsInUse = (byte) ioVecs;
            Write(ring, ioVecs);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe int PrepareWriteIoVecs()
        {
            var buffer = CurrentWrite;

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

            return ctr;
        }

        public void Write(Ring ring)
        {
            Write(ring, _writeIoVecsInUse);
        }

        private unsafe void Write(Ring ring, int ioVecs)
        {
            int socket = Socket;
            if (!ring.TryPrepareWriteV(socket, WriteVecs, ioVecs, 0, 0, AsyncOperation.WriteTo(socket).AsUlong()))
            {
                _scheduler.ScheduleWrite(socket);
                return;
            }

            SetFlag(ConnectionState.Writing);
        }

        public void CompleteWrite(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.Writing);
            if (result < 0)
            {
                if (!HandleCompleteWriteError(ring, result))
                {
                    DisposeWriteHandles();
                }

                return;
            }

            DisposeWriteHandles();

            SequencePosition end;
            var currentWrite = CurrentWrite;
            if (result == 0)
            {
                end = currentWrite.Start;
            }
            else if (currentWrite.Length == result)
            {
                end = currentWrite.End;
            }
            else
            {
                end = currentWrite.GetPosition(result);
            }

            Outbound.AdvanceTo(end);
            ReadFromApp(ring);
        }

        private bool HandleCompleteWriteError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                Write(ring);
                return true;
            }

            if (HasFlag(ConnectionState.WriteCancelled) && err == ECANCELED)
            {
                return false;
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
            return false;
        }

        private unsafe void DisposeWriteHandles()
        {
            foreach (var writeHandle in WriteHandles)
            {
                if (writeHandle.Pointer == (void*) IntPtr.Zero) break;
                writeHandle.Dispose();
            }
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
                CurrentWrite = buffer;
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
                _scheduler.ScheduleAsyncWritePoll(Socket);
            }

            return result;
        }
    }
}