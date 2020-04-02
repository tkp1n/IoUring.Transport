using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Tmds.Linux;

namespace IoUring.Transport.Internals.Outbound
{
    internal sealed class OutboundConnectionContext : IoUringConnectionContext, IConnectionInherentKeepAliveFeature
    {
        private GCHandle _addrHandle;
        private TaskCompletionSource<ConnectionContext> _connectCompletion;

        public OutboundConnectionContext(LinuxSocket socket, EndPoint remote, TransportThreadContext threadContext, TaskCompletionSource<ConnectionContext> connectCompletion)
            : base(socket, null, remote, threadContext)
        {
            if (!(remote is IPEndPoint)) throw new NotSupportedException(); // TODO

            unsafe
            {
                sockaddr_storage addr = default;
                var addrHandle = GCHandle.Alloc(addr, GCHandleType.Pinned);
                Addr = (sockaddr_storage*) addrHandle.AddrOfPinnedObject();
                _addrHandle = addrHandle;

                ((IPEndPoint) remote).ToSockAddr(Addr, out var addrLength);
                AddrLen = addrLength;
            }

            _connectCompletion = connectCompletion;
            
            // Add IConnectionInherentKeepAliveFeature to the tcp connection impl since Kestrel doesn't implement
            // the IConnectionHeartbeatFeature
            Features.Set<IConnectionInherentKeepAliveFeature>(this);
        }

        // We claim to have inherent keep-alive so the client doesn't kill the connection when it hasn't seen ping frames.
        public bool HasInherentKeepAlive => true;

        public unsafe sockaddr_storage* Addr { get; }

        public socklen_t AddrLen { get; }

        public void CompleteConnect()
        {
            Debug.Assert(_connectCompletion != null);

            LocalEndPoint = Socket.GetLocalAddress();
            _connectCompletion.TrySetResult(this);
            _connectCompletion = null;
        }

        public void CompleteConnect(Exception ex)
        {
            Debug.Assert(_connectCompletion != null);

            _connectCompletion.TrySetException(ex);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
       
            if (_addrHandle.IsAllocated)
                _addrHandle.Free();
        }
    }
}