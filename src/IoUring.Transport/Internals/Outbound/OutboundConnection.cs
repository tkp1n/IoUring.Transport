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

        private OutboundConnection(LinuxSocket socket, EndPoint remote, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler, TaskCompletionSource<ConnectionContext> connectCompletion)
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

        public static OutboundConnection Create(EndPoint endpoint, TaskCompletionSource<ConnectionContext> connectCompletion, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
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

        public unsafe bool Connect(Ring ring)
        {
            if (!ring.Supports(RingOperation.Connect))
            {
                // pre v5.5
                if (Socket.Connect(Addr, AddrLen))
                {
                    return true;
                }

                // connect ongoing asynchronously until write poll completes
                WritePollDuringConnect(ring);
                return false;
            }

            int socket = Socket;
            if (!ring.TryPrepareConnect(socket, Addr, AddrLen, AsyncOperation.ConnectOn(socket).AsUlong()))
            {
                _scheduler.ScheduleConnect(socket);
            }

            return false;
        }

        public void WritePollDuringConnect(Ring ring)
        {
            var socket = Socket;
            if (!ring.TryPreparePollAdd(socket, (ushort) POLLOUT, AsyncOperation.WritePollDuringConnect(socket).AsUlong()))
            {
                _scheduler.ScheduleWritePollDuringComplete(socket);
            }
        }

        public bool CompleteWritePollDuringConnect(Ring ring, int result)
        {
            if (result < 0)
            {
                HandleCompleteWritePollDuringConnectError(ring, result);
                return false;
            }

            var completeResult =  Socket.GetOption(SOL_SOCKET, SO_ERROR);
            if (completeResult != 0)
            {
                _connectCompletion.TrySetException(new ErrnoException(completeResult));
            }
            else
            {
                SetupPostSuccessfulConnect();
            }

            return true;
        }

        private void HandleCompleteWritePollDuringConnectError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                WritePollDuringConnect(ring);
            }
            else
            {
                ThrowHelper.ThrowNewErrnoException(err);
            }
        }

        public void CompleteConnect(Ring ring, int result)
        {
            if (result < 0)
            {
                HandleCompleteConnectError(ring, result);
                return;
            }

            SetupPostSuccessfulConnect();
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

        private void SetupPostSuccessfulConnect()
        {
            var ep = Socket.GetLocalAddress();
            LocalEndPoint = ep ?? RemoteEndPoint;

            _connectCompletion.TrySetResult(this);
            _connectCompletion = null;
        }
    }
}
