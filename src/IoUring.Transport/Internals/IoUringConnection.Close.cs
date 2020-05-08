using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals
{
    internal abstract partial class IoUringConnection
    {
        public void CompleteInbound(Ring ring, Exception error)
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Completing inbound half of {Socket}");
#endif
            Inbound.Complete(error);
            CleanupSocketEnd(ring);
        }

        public void CompleteOutbound(Ring ring, Exception error)
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Completing outbound half of {Socket}");
#endif
            Outbound.Complete(error);
            CancelReadFromSocket(ring);
            CleanupSocketEnd(ring);
        }

        private void CancelReadFromSocket(Ring ring)
        {
            var flags = Flags;
            if (HasFlag(flags, ConnectionState.ReadCancelled))
            {
                return;
            }

            if (HasFlag(flags, ConnectionState.PollingRead))
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Cancelling read poll for {Socket}");
#endif
                Cancel(ring, AsyncOperation.ReadPollFor(Socket));
            }
            else if (HasFlag(flags, ConnectionState.Reading))
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Cancelling read for {Socket}");
#endif
                Cancel(ring, AsyncOperation.ReadFrom(Socket));
            }

            Flags = SetFlag(flags, ConnectionState.ReadCancelled);

            CompleteInbound(ring, new ConnectionAbortedException());
        }

        private void CancelWriteToSocket(Ring ring)
        {
            var flags = Flags;

            if (HasFlag(flags, ConnectionState.WriteCancelled))
            {
                return;
            }

            if (HasFlag(flags, ConnectionState.PollingWrite))
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Cancelling write poll for {Socket}");
#endif
                Cancel(ring, AsyncOperation.WritePollFor(Socket));
            }
            else if (HasFlag(flags, ConnectionState.Writing))
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Cancelling write for {Socket}");
#endif
                Cancel(ring, AsyncOperation.WriteTo(Socket));
            }

            Flags = SetFlag(flags, ConnectionState.WriteCancelled);

            CompleteInbound(ring, null);
        }

        private void CleanupSocketEnd(Ring ring)
        {
            var flags = Flags;
            if (!HasFlag(flags, ConnectionState.HalfClosed))
            {
                Flags = SetFlag(flags, ConnectionState.HalfClosed);
                return;
            }

            if (HasFlag(flags, ConnectionState.Closed))
            {
                return;
            }

            Flags = SetFlag(flags, ConnectionState.Closed);
            Close(ring);
        }

        private void Cancel(Ring ring, AsyncOperation operation)
        {
            ring.PrepareCancel(operation.AsUlong(), AsyncOperation.CancelGeneric(operation.Socket).AsUlong());
        }

        private void Close(Ring ring)
        {
            if (ring.Supports(RingOperation.Close))
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Adding close on {Socket}");
#endif
                ring.PrepareClose(Socket, AsyncOperation.CloseConnection(Socket).AsUlong());
            }
            else
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Closing {Socket}");
#endif
                Socket.Close(); // pre v5.6
#if TRACE_IO_URING
                Trace.WriteLine($"Adding nop on {Socket}");
#endif
                ring.PrepareNop(AsyncOperation.CloseConnection(Socket).AsUlong());
            }
        }

        public void CompleteClosed()
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Close completed for {Socket}");
#endif
            ThreadPool.UnsafeQueueUserWorkItem(state => ((IoUringConnection)state).CancelConnectionClosedToken(), this);
        }

        // Invoked on thread pool to notify application that the connection is closed
        private void CancelConnectionClosedToken()
        {
            _connectionClosedTokenSource.Cancel();
            _waitForConnectionClosedTcs.SetResult(null);
        }

        public void Abort(Ring ring, Exception error)
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Aborting {Socket}");
#endif
            Outbound.CancelPendingRead();
            CancelWriteToSocket(ring);
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Aborting {Socket}");
#endif
            _scheduler.ScheduleAsyncAbort(Socket, abortReason);
        }

        public override async ValueTask DisposeAsync()
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Disposing {Socket}");
#endif
            Transport.Input.Complete();
            Transport.Output.Complete();

            Abort();

            await _waitForConnectionClosedTcs.Task;
            _connectionClosedTokenSource.Dispose();
        }
    }
}