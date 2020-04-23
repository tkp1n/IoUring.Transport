using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals.Outbound
{
    internal sealed class OutboundConnection : IoUringConnection, IConnectionInherentKeepAliveFeature
    {
        private GCHandle _addrHandle;
        private TaskCompletionSource<ConnectionContext> _connectCompletion;

        private OutboundConnection(LinuxSocket socket, EndPoint remote, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler, TaskCompletionSource<ConnectionContext> connectCompletion)
            : base(socket, null, remote, memoryPool, options, scheduler)
        {
            unsafe
            {
                switch (remote)
                {
                    case IPEndPoint ipEndPoint:
                    {
                        sockaddr_storage addr = default;
                        var addrHandle = GCHandle.Alloc(addr, GCHandleType.Pinned);
                        Addr = (sockaddr*) addrHandle.AddrOfPinnedObject();
                        _addrHandle = addrHandle;

                        ipEndPoint.ToSockAddr((sockaddr_storage*) Addr, out var addrLength);
                        AddrLen = addrLength;
                        break;
                    }
                    case UnixDomainSocketEndPoint unixDomainSocketEndPoint:
                    {
                        sockaddr_un addr = default;
                        var addrHandle = GCHandle.Alloc(addr, GCHandleType.Pinned);
                        Addr = (sockaddr*) addrHandle.AddrOfPinnedObject();
                        _addrHandle = addrHandle;

                        unixDomainSocketEndPoint.ToSockAddr((sockaddr_un*) Addr);
                        AddrLen = SizeOf.sockaddr_un;
                        break;
                    }
                }
            }

            _connectCompletion = connectCompletion;

            // Add IConnectionInherentKeepAliveFeature to the tcp connection impl since Kestrel doesn't implement
            // the IConnectionHeartbeatFeature
            Features.Set<IConnectionInherentKeepAliveFeature>(this);
        }

        public static OutboundConnection Connect(IPEndPoint endpoint, TaskCompletionSource<ConnectionContext> connectCompletion, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
        {
            var domain = endpoint.AddressFamily == AddressFamily.InterNetwork ? AF_INET : AF_INET6;
            LinuxSocket s = new LinuxSocket(domain, SOCK_STREAM, IPPROTO_TCP, blocking: false);
            if (options.TcpNoDelay)
            {
                s.SetOption(SOL_TCP, TCP_NODELAY, 1);
            }

            return new OutboundConnection(s, endpoint, memoryPool, options, scheduler, connectCompletion);
        }

        // We claim to have inherent keep-alive so the client doesn't kill the connection when it hasn't seen ping frames.
        public bool HasInherentKeepAlive => true;

        public unsafe sockaddr* Addr { get; }

        public socklen_t AddrLen { get; }

        public unsafe void Connect(Ring ring)
        {
            int socket = Socket;
            ring.PrepareConnect(socket, Addr, AddrLen, AsyncOperation.ConnectOn(socket).AsUlong());
        }

        public void CompleteConnect(Ring ring, int result)
        {
            Debug.Assert(_connectCompletion != null);

            if (result < 0)
            {
                if (-result == EAGAIN && -result == EINTR)
                {
                    Connect(ring);
                    return;
                }

                _connectCompletion.TrySetException(new ErrnoException(-result));
            }
            else
            {
                LocalEndPoint = Socket.GetLocalAddress();
                _connectCompletion.TrySetResult(this);
            }

            _connectCompletion = null;
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            if (_addrHandle.IsAllocated)
                _addrHandle.Free();
        }
    }
}