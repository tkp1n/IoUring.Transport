using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed class AcceptThread : IoUringThread
    {
        private readonly ConcurrentQueue<AsyncOperation> _asyncOperationQueue = new ConcurrentQueue<AsyncOperation>();
        private readonly ConcurrentDictionary<int, AcceptSocket> _acceptSockets = new ConcurrentDictionary<int, AcceptSocket>();
        private readonly AcceptThreadScheduler _scheduler;
        private readonly TransportThreadScheduler[] _schedulers;
        private int _schedulerIndex = -1; // -1 so we start at 0 at first Increment

        public AcceptThread(IoUringOptions options, TransportThreadScheduler[] schedulers)
         : base("IoUring Accept Thread", options)
        {
            _scheduler = new AcceptThreadScheduler(_unblockHandle, _asyncOperationQueue);
            _schedulers = schedulers;
        }

        public void Bind(UnixDomainSocketEndPoint unixDomainSocketEndPoint, ChannelWriter<ConnectionContext> acceptQueue)
        {
            var context = AcceptSocket.Bind(unixDomainSocketEndPoint, acceptQueue, _options);
        }

        public void Bind(FileHandleEndPoint fileHandleEndPoint, ChannelWriter<ConnectionContext> acceptQueue)
        {
            var context = AcceptSocket.Bind(fileHandleEndPoint, acceptQueue, _options);
        }

        public ValueTask Unbind(EndPoint endPoint)
        {
            throw null; // TODO: impl
        }

        protected override void RunAsyncOperations()
        {
            // TODO: impl
        }

        protected override void Complete()
        {
            // TODO: impl
        }
    }
}