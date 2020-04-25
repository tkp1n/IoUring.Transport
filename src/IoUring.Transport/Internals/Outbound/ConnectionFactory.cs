using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals.Outbound
{
    internal sealed class ConnectionFactory : IConnectionFactory, IAsyncDisposable
    {
        // TODO: state machine for class
        private readonly IoUringTransport _transport;
        private int _threadIndex = -1; // -1 so we start at 0 at first Increment

        public ConnectionFactory(IoUringTransport transport)
        {
            _transport = transport;

            _transport.IncrementThreadRefCount();
        }

        public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            Debug.WriteLine($"Connecting via ConnectionFactory to {endpoint}");

            var threads = _transport.TransportThreads;
            var index = Interlocked.Increment(ref _threadIndex) % threads.Length;

            return threads[index].Connect(endpoint);
        }

        public ValueTask DisposeAsync()
        {
            Debug.WriteLine($"Disposing ConnectionFactory");

            _transport.DecrementThreadRefCount();
            return default;
        }
    }
}