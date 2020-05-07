using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Connections;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal abstract partial class IoUringConnection
    {
        public void WritePoll(Ring ring)
        {
            int socket = Socket;
            Debug.WriteLine($"Adding write poll on {socket}");
            ring.PreparePollAdd(socket, (ushort) POLLOUT, AsyncOperation.WritePollFor(socket).AsUlong());
            SetFlag(ConnectionState.PollingWrite);
        }

        public void CompleteWritePoll(Ring ring, int result)
        {
            RemoveFlag(ConnectionState.PollingWrite);
            if (result >= 0)
            {
                Debug.WriteLine($"Completed write poll on {(int)Socket}");
                Write(ring);
            }
            else
            {
                var err = -result;
                if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
                {
                    Debug.WriteLine("Polled write for nothing");
                    WritePoll(ring);
                }
                else if (HasFlag(ConnectionState.WriteCancelled) && err == ECANCELED)
                {
                    Debug.WriteLine("Write poll was cancelled");
                }
                else
                {
                    CompleteOutbound(ring, new ErrnoException(err));
                }
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
            Debug.WriteLine($"Adding write on {socket}");
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
                    Debug.WriteLine($"Wrote {result} bytes to {(int)Socket}");
                    end = lastWrite.Start;
                }
                else if (lastWrite.Length == result)
                {
                    Debug.WriteLine($"Wrote all {result} bytes to {(int)Socket}");
                    end = lastWrite.End;
                }
                else
                {
                    Debug.WriteLine($"Wrote some {result} bytes to {(int)Socket}");
                    end = lastWrite.GetPosition(result);
                }

                Outbound.AdvanceTo(end);
                ReadFromApp(ring);
                return;
            }

            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                Debug.WriteLine("Wrote for nothing");
                Outbound.AdvanceTo(lastWrite.Start);
                ReadFromApp(ring);
                return;
            }

            if (HasFlag(ConnectionState.WriteCancelled) && err == ECANCELED)
            {
                Debug.WriteLine("Write was cancelled");
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
                Debug.WriteLine($"Read from app for {(int)Socket} synchronously");
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
                Debug.WriteLine($"Read from app for {(int)Socket} asynchronously");
                _scheduler.ScheduleAsyncWrite(Socket);
            }

            return result;
        }
    }
}