using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals
{
    internal abstract partial class IoUringConnection
    {
        public void CompleteInbound(Ring ring, Exception error)
        {
            Inbound.Complete(error);
            CleanupSocketEnd(ring);
        }

        public void CompleteOutbound(Ring ring, Exception error)
        {
            Outbound.Complete(error);
            CancelReadFromSocket(ring);
            CleanupSocketEnd(ring);
        }

        private void CancelReadFromSocket(Ring ring)
        {
            var flags = _flags;
            if (HasFlag(flags, ConnectionState.ReadCancelled))
            {
                return;
            }

            if (HasFlag(flags, ConnectionState.PollingRead))
            {
                Cancel(ring, OperationType.ReadPoll);
            }
            else if (HasFlag(flags, ConnectionState.Reading))
            {
                Cancel(ring, OperationType.Read);
            }

            _flags = SetFlag(flags, ConnectionState.ReadCancelled);

            CompleteInbound(ring, new ConnectionAbortedException());
        }

        private void CancelWriteToSocket(Ring ring)
        {
            var flags = _flags;

            if (HasFlag(flags, ConnectionState.WriteCancelled))
            {
                return;
            }

            if (HasFlag(flags, ConnectionState.PollingWrite))
            {
                Cancel(ring,  OperationType.WritePoll);
            }
            else if (HasFlag(flags, ConnectionState.Writing))
            {
                Cancel(ring, OperationType.Write);
            }

            _flags = SetFlag(flags, ConnectionState.WriteCancelled);

            CompleteInbound(ring, null);
        }

        private void CleanupSocketEnd(Ring ring)
        {
            var flags = _flags;
            if (!HasFlag(flags, ConnectionState.HalfClosed))
            {
                _flags = SetFlag(flags, ConnectionState.HalfClosed);
                return;
            }

            if (HasFlag(flags, ConnectionState.Closed))
            {
                return;
            }

            _flags = SetFlag(flags, ConnectionState.Closed);
            Close(ring);
        }

        public void Cancel(Ring ring, OperationType op)
        {
            int socket = Socket;
            if (!ring.TryPrepareCancel(new AsyncOperation(socket, op).AsUlong(), AsyncOperation.CancelOperation(op, socket).AsUlong()))
            {
                _scheduler.ScheduleCancel(AsyncOperation.CancelOperation(op, socket));
            }
        }

        public void Close(Ring ring)
        {
            int socket = Socket;
            if (ring.Supports(RingOperation.Close))
            {
                if (!ring.TryPrepareClose(socket, AsyncOperation.CloseConnection(socket).AsUlong()))
                {
                    _scheduler.ScheduleCloseConnection(socket);
                }
            }
            else
            {
                if (ring.TryPrepareNop(AsyncOperation.CloseConnection(socket).AsUlong()))
                {
                    Socket.Close(); // pre v5.6
                }
                else
                {
                    _scheduler.ScheduleCloseConnection(socket);
                }
            }
        }

        public void CompleteClosed()
        {
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
            Outbound.CancelPendingRead();
            CancelWriteToSocket(ring);
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            _scheduler.ScheduleAsyncAbort(Socket, abortReason);
        }

        public override async ValueTask DisposeAsync()
        {
            await Transport.Input.CompleteAsync();
            await Transport.Output.CompleteAsync();

            Abort();

            await _waitForConnectionClosedTcs.Task;
            _connectionClosedTokenSource.Dispose();
        }
    }
}