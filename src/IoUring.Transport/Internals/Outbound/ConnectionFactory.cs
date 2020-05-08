using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals.Outbound
{
    internal sealed class ConnectionFactory : IConnectionFactory, IAsyncDisposable
    {
        private const int True = 1;
        private readonly IoUringTransport _transport;
        private int _threadIndex = -1; // -1 so we start at 0 at first Increment
        private int _disposed;

        public ConnectionFactory(IoUringTransport transport)
        {
            _transport = transport;
            _transport.IncrementThreadRefCount();
        }

        public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) == True) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.ConnectionFactory);

            var threads = _transport.TransportThreads;
            var index = Interlocked.Increment(ref _threadIndex) % threads.Length;

            return threads[index].Connect(endpoint);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, True) == True)
            {
                return default;
            }

            _transport.DecrementThreadRefCount();
            return default;
        }
    }
}