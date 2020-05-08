using System;
using System.Buffers;
using System.Diagnostics;
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
#if TRACE_IO_URING
            Trace.WriteLine($"Adding write poll on {socket}");
#endif
            ring.PreparePollAdd(socket, (ushort) POLLOUT, AsyncOperation.WritePollFor(socket).AsUlong());
            SetFlag(ConnectionState.PollingWrite);
        }

        public void CompleteWritePoll(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.PollingWrite);
            if (result >= 0)
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Completed write poll on {(int)Socket}");
#endif
                Write(ring);
            }
            else
            {
                HandleCompleteWritePollError(ring, result);
            }
        }

        private void HandleCompleteWritePollError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
#if TRACE_IO_URING
                Trace.WriteLine("Polled write for nothing");
#endif
                WritePoll(ring);
            }
            else if (HasFlag(ConnectionState.WriteCancelled) && err == ECANCELED)
            {
#if TRACE_IO_URING
                Trace.WriteLine("Write poll was cancelled");
#endif
            }
            else
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
#if TRACE_IO_URING
            Trace.WriteLine($"Adding write on {socket}");
#endif
            ring.PrepareWriteV(socket, writeVecs ,ctr, 0 ,0, AsyncOperation.WriteTo(socket).AsUlong());
            SetFlag(ConnectionState.Writing);
        }

        public void CompleteWrite(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.Writing);
            foreach (var writeHandle in WriteHandles)
            {
                writeHandle.Dispose();
            }

            var lastWrite = LastWrite;
            if (result >= 0)
            {
                SequencePosition end;
                if (result == 0)
                {
#if TRACE_IO_URING
                    Trace.WriteLine($"Wrote {result} bytes to {(int)Socket}");
#endif
                    end = lastWrite.Start;
                }
                else if (lastWrite.Length == result)
                {
#if TRACE_IO_URING
                    Trace.WriteLine($"Wrote all {result} bytes to {(int)Socket}");
#endif
                    end = lastWrite.End;
                }
                else
                {
#if TRACE_IO_URING
                    Trace.WriteLine($"Wrote some {result} bytes to {(int)Socket}");
#endif
                    end = lastWrite.GetPosition(result);
                }

                Outbound.AdvanceTo(end);
                ReadFromApp(ring);
            }
            else
            {
                HandleCompleteWriteError(ring, result, lastWrite);
            }
        }

        private void HandleCompleteWriteError(Ring ring, int result, ReadOnlySequence<byte> lastWrite)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
#if TRACE_IO_URING
                Trace.WriteLine("Wrote for nothing");
#endif
                Outbound.AdvanceTo(lastWrite.Start);
                ReadFromApp(ring);
                return;
            }

            if (HasFlag(ConnectionState.WriteCancelled) && err == ECANCELED)
            {
#if TRACE_IO_URING
                Trace.WriteLine("Write was cancelled");
#endif
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
#if TRACE_IO_URING
                Trace.WriteLine($"Read from app for {(int)Socket} synchronously");
#endif
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
#if TRACE_IO_URING
                Trace.WriteLine($"Read from app for {(int)Socket} asynchronously");
#endif
                _scheduler.ScheduleAsyncWrite(Socket);
            }

            return result;
        }
    }
}