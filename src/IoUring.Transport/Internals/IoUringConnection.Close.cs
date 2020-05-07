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
            Debug.WriteLine($"Completing inbound half of {Socket}");
            Inbound.Complete(error);
            CleanupSocketEnd(ring);
        }

        public void CompleteOutbound(Ring ring, Exception error)
        {
            Debug.WriteLine($"Completing outbound half of {Socket}");
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
                Debug.WriteLine($"Cancelling read poll for {Socket}");
                Cancel(ring, AsyncOperation.ReadPollFor(Socket));
            }
            else if (HasFlag(flags, ConnectionState.Reading))
            {
                Debug.WriteLine($"Cancelling read for {Socket}");
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
                Debug.WriteLine($"Cancelling write poll for {Socket}");
                Cancel(ring, AsyncOperation.WritePollFor(Socket));
            }
            else if (HasFlag(flags, ConnectionState.Writing))
            {
                Debug.WriteLine($"Cancelling write for {Socket}");
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
                Debug.WriteLine($"Adding close on {Socket}");
                ring.PrepareClose(Socket, AsyncOperation.CloseConnection(Socket).AsUlong());
            }
            else
            {
                Debug.WriteLine($"Closing {Socket}");
                Socket.Close(); // pre v5.6
                Debug.WriteLine($"Adding nop on {Socket}");
                ring.PrepareNop(AsyncOperation.CloseConnection(Socket).AsUlong());
            }
        }

        public void CompleteClosed()
        {
            Debug.WriteLine($"Close completed for {Socket}");
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
            Debug.WriteLine($"Aborting {Socket}");
            Outbound.CancelPendingRead();
            CancelWriteToSocket(ring);
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            Debug.WriteLine($"Aborting {Socket}");
            _scheduler.ScheduleAsyncAbort(Socket, abortReason);
        }

        public override async ValueTask DisposeAsync()
        {
            Debug.WriteLine($"Disposing {Socket}");

            Transport.Input.Complete();
            Transport.Output.Complete();

            Abort();

            await _waitForConnectionClosedTcs.Task;
            _connectionClosedTokenSource.Dispose();
        }
    }
}