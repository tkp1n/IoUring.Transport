using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed class ConnectionListenerFactory : IConnectionListenerFactory
    {
        private readonly IoUringTransport _ioUringTransport;
        private readonly IoUringOptions _options;

        public ConnectionListenerFactory(IoUringTransport ioUringTransport, IOptions<IoUringOptions> options)
        {
            _ioUringTransport = ioUringTransport;
            _options = options.Value;
        }

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
            => ConnectionListener.BindAsync(endpoint, _ioUringTransport, _options);
    }
}