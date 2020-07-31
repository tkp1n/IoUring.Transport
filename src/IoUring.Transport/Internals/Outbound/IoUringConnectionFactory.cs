using System.Net;
using System.Net.Connections;
using System.Threading;
using System.Threading.Tasks;

namespace IoUring.Transport.Internals.Outbound
{
    internal sealed class IoUringConnectionFactory : ConnectionFactory
    {
        private const int True = 1;
        private readonly IoUringTransport _transport;
        private int _threadIndex = -1; // -1 so we start at 0 at first Increment
        private int _disposed;

        public IoUringConnectionFactory(IoUringTransport transport)
        {
            _transport = transport;
            _transport.IncrementThreadRefCount();
        }

        public override ValueTask<Connection> ConnectAsync(EndPoint endPoint, IConnectionProperties options = null, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) == True) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.ConnectionFactory);

            var threads = _transport.TransportThreads;
            var index = Interlocked.Increment(ref _threadIndex) % threads.Length;
            var thread = threads[index];

            // TODO: handle cancellation
            if (options == null)
            {
                return thread.Connect(endPoint);
            }

            return ConnectWithOptionsAsync(thread, endPoint, options);
        }

        private async ValueTask<Connection> ConnectWithOptionsAsync(TransportThread thread, EndPoint endPoint, IConnectionProperties options)
        {
            var connection = await thread.Connect(endPoint);
            ((IoUringConnection) connection).ProvidedProperties = options;
            return connection;
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, True) == True)
            {
                return;
            }

            _transport.DecrementThreadRefCount();
        }
    }
}