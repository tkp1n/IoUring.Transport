using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Tmds.Linux;

namespace IoUring.Transport.Internals.Outbound
{
    internal sealed unsafe class OutboundConnectionContext : IoUringConnectionContext, IConnectionInherentKeepAliveFeature
    {
        private readonly sockaddr_storage* _addr;
        private GCHandle _addrHandle;

        public OutboundConnectionContext(LinuxSocket socket, EndPoint remote, TransportThreadContext threadContext)
            : base(socket, null, remote, threadContext)
        {
            sockaddr_storage addr = default;
            var addrHandle = GCHandle.Alloc(addr, GCHandleType.Pinned);
            _addr = (sockaddr_storage*) addrHandle.AddrOfPinnedObject();
            _addrHandle = addrHandle;
            
            // Add IConnectionInherentKeepAliveFeature to the tcp connection impl since Kestrel doesn't implement
            // the IConnectionHeartbeatFeature
            Features.Set<IConnectionInherentKeepAliveFeature>(this);
        }

        // We claim to have inherent keep-alive so the client doesn't kill the connection when it hasn't seen ping frames.
        public bool HasInherentKeepAlive => true;

        public sockaddr_storage* Addr => _addr;
        public socklen_t AddrLen { get; set; }

        public TaskCompletionSource<ConnectionContext> ConnectCompletion { get; set; }

        public override ValueTask DisposeAsync()
        {
            if (_addrHandle.IsAllocated)
                _addrHandle.Free();

            return base.DisposeAsync();
        }
    }
}