using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals.Outbound
{
    internal sealed class OutboundConnection : IoUringConnection, IConnectionInherentKeepAliveFeature
    {
        private readonly byte[] _addr = GC.AllocateUninitializedArray<byte>(SizeOf.sockaddr_storage, pinned: true);
        private TaskCompletionSource<ConnectionContext> _connectCompletion;

        private OutboundConnection(LinuxSocket socket, EndPoint remote, SlabMemoryPool memoryPool, IoUringOptions options, TransportThreadScheduler scheduler, TaskCompletionSource<ConnectionContext> connectCompletion)
            : base(socket, null, remote, memoryPool, options, scheduler)
        {
            unsafe
            {
                switch (remote)
                {
                    case IPEndPoint ipEndPoint:
                    {
                        ipEndPoint.ToSockAddr((sockaddr_storage*) Addr, out var addrLength);
                        AddrLen = addrLength;
                        break;
                    }
                    case UnixDomainSocketEndPoint unixDomainSocketEndPoint:
                    {
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

        public static OutboundConnection Create(EndPoint endpoint, TaskCompletionSource<ConnectionContext> connectCompletion, SlabMemoryPool memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
        {
            LinuxSocket s = default;
            switch (endpoint)
            {
                case IPEndPoint _:
                    var domain = endpoint.AddressFamily == AddressFamily.InterNetwork ? AF_INET : AF_INET6;
                    s = new LinuxSocket(domain, SOCK_STREAM, IPPROTO_TCP, blocking: false);
                    if (options.TcpNoDelay)
                    {
                        s.SetOption(SOL_TCP, TCP_NODELAY, 1);
                    }
                    break;
                case UnixDomainSocketEndPoint _:
                    s = new LinuxSocket(AF_UNIX, SOCK_STREAM, 0, blocking: false);
                    break;
                case FileHandleEndPoint fileHandleEndPoint:
                    s = (int) fileHandleEndPoint.FileHandle;
                    break;
                default:
                    ThrowHelper.ThrowNewNotSupportedException_EndPointNotSupported();
                    break;
            }

            return new OutboundConnection(s, endpoint, memoryPool, options, scheduler, connectCompletion);
        }

        // We claim to have inherent keep-alive so the client doesn't kill the connection when it hasn't seen ping frames.
        public bool HasInherentKeepAlive => true;
        private unsafe sockaddr* Addr => (sockaddr*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_addr);
        private socklen_t AddrLen { get; }

        public unsafe void Connect(Ring ring)
        {
            int socket = Socket;
            if (!ring.TryPrepareConnect(socket, Addr, AddrLen, AsyncOperation.ConnectOn(socket).AsUlong()))
            {
                _scheduler.ScheduleConnect(socket);
            }
        }

        public void CompleteConnect(Ring ring, int result)
        {
            if (result < 0)
            {
                HandleCompleteConnectError(ring, result);
                return;
            }

            var ep = Socket.GetLocalAddress();
            LocalEndPoint = ep ?? RemoteEndPoint;

            _connectCompletion.TrySetResult(this);
            _connectCompletion = null;
        }

        private void HandleCompleteConnectError(Ring ring, int result)
        {
            if (-result == EAGAIN && -result == EINTR)
            {
                Connect(ring);
            }
            else
            {
                _connectCompletion.TrySetException(new ErrnoException(-result));
            }
        }
    }
}