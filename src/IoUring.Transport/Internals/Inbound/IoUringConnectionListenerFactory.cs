using System.Net;
using System.Net.Connections;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed class IoUringConnectionListenerFactory : ConnectionListenerFactory
    {
        private readonly IoUringTransport _ioUringTransport;
        private readonly IoUringOptions _ioUringOptions;

        public IoUringConnectionListenerFactory(IoUringTransport ioUringTransport, IOptions<IoUringOptions> options)
        {
            _ioUringTransport = ioUringTransport;
            _ioUringOptions = options.Value;
        }

        public override ValueTask<ConnectionListener> BindAsync(EndPoint endPoint, IConnectionProperties options = null, CancellationToken cancellationToken = default)
        {
            return IoUringConnectionListener.BindAsync(endPoint, _ioUringTransport, _ioUringOptions, options, cancellationToken);
        }
    }
}