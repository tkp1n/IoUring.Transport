using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed unsafe class AcceptSocket
    {
        private readonly TaskCompletionSource<object> _unbindCompletion;
        private readonly SlabMemoryPool _memoryPool;
        private readonly IoUringOptions _options;
        private readonly TransportThreadScheduler _scheduler;
        private readonly byte[] _addr;
        private readonly byte[] _addrLen;

        private AcceptSocket(LinuxSocket socket, EndPoint endPoint, ChannelWriter<ConnectionContext> acceptQueue, SlabMemoryPool memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
        {
            Socket = socket;
            EndPoint = endPoint;
            AcceptQueue = acceptQueue;
            _memoryPool = memoryPool;
            _options = options;
            _scheduler = scheduler;

            _unbindCompletion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (endPoint is IPEndPoint) // _addr is null by intention for the remaining EndPoint types
            {
                _addr = GC.AllocateUninitializedArray<byte>(SizeOf.sockaddr_storage, pinned: true);
                _addrLen = GC.AllocateUninitializedArray<byte>(SizeOf.socklen_t, pinned: true);
            }
        }

        public static AcceptSocket Bind(IPEndPoint ipEndPoint, ChannelWriter<ConnectionContext> acceptQueue, SlabMemoryPool memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
        {
            var domain = ipEndPoint.AddressFamily == AddressFamily.InterNetwork ? AF_INET : AF_INET6;
            LinuxSocket s = new LinuxSocket(domain, SOCK_STREAM, IPPROTO_TCP, blocking: true);
            s.SetOption(SOL_SOCKET, SO_REUSEADDR, 1);
            s.SetOption(SOL_SOCKET, SO_REUSEPORT, 1);
            s.Bind(ipEndPoint);
            s.Listen(options.ListenBacklog);

            return new AcceptSocket(s, s.GetLocalAddress(), acceptQueue, memoryPool, options, scheduler);
        }

        public static AcceptSocket Bind(UnixDomainSocketEndPoint unixDomainSocketEndPoint, ChannelWriter<ConnectionContext> acceptQueue, IoUringOptions options)
        {
            var socketPath = unixDomainSocketEndPoint.ToString();
            var s = new LinuxSocket(AF_UNIX, SOCK_STREAM, 0, blocking: false);
            File.Delete(socketPath);
            s.Bind(unixDomainSocketEndPoint);
            s.Listen(options.ListenBacklog);

            return new AcceptSocket(s, unixDomainSocketEndPoint, acceptQueue, null, options, null);
        }

        public static AcceptSocket Bind(FileHandleEndPoint fileHandleEndPoint, ChannelWriter<ConnectionContext> acceptQueue, IoUringOptions options)
        {
            LinuxSocket s = (int) fileHandleEndPoint.FileHandle;
            var endPoint = s.GetLocalAddress();
            return new AcceptSocket(s, endPoint ?? fileHandleEndPoint, acceptQueue, null, options, null);
        }

        public LinuxSocket Socket { get; }
        public EndPoint EndPoint { get; }
        public ChannelWriter<ConnectionContext> AcceptQueue { get; }
        public Task UnbindCompletion => _unbindCompletion.Task;
        public LinuxSocket[] Handlers { get; set; }
        private sockaddr* Addr => IsIpSocket ? (sockaddr*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_addr) : (sockaddr*) 0;
        private socklen_t* AddrLen => IsIpSocket ? (socklen_t*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_addrLen) : (socklen_t*) 0;
        private bool IsIpSocket => _addr != null;
        private bool IsUnbinding { get; set; }

        public void AcceptPoll(Ring ring)
        {
            int socket = Socket;
            if (!ring.TryPreparePollAdd(socket, (ushort) POLLIN, AsyncOperation.PollAcceptFrom(socket).AsUlong()))
            {
                _scheduler.ScheduleAcceptPoll(socket);
            }
        }

        public void CompleteAcceptPoll(Ring ring, int result)
        {
            if (result < 0)
            {
                HandleCompleteAcceptPollError(ring, result);
                return;
            }

            Accept(ring);
        }

        private void HandleCompleteAcceptPollError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                if (!IsUnbinding)
                {
                    AcceptPoll(ring);
                }
            }
            else if (err == ECANCELED && IsUnbinding)
            {
            }
        }

        public void Accept(Ring ring)
        {
            if (IsIpSocket)
            {
                _addr.AsSpan().Clear();
                *AddrLen = SizeOf.sockaddr_storage;
            }

            int socket = Socket;
            if (!ring.TryPrepareAccept(socket, Addr, AddrLen, SOCK_NONBLOCK | SOCK_CLOEXEC, AsyncOperation.AcceptFrom(socket).AsUlong()))
            {
                _scheduler.ScheduleAccept(socket);
            }
        }

        public bool TryCompleteAcceptSocket(Ring ring, int result, out LinuxSocket socket)
        {
            socket = default;
            if (result < 0)
            {
                HandleCompleteAcceptSocketError(ring, result);
                return false;
            }

            socket = result;
            return true;
        }

        private void HandleCompleteAcceptSocketError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EINTR || err == EMFILE)
            {
                if (!IsUnbinding)
                {
                    Accept(ring);
                }
            }
            else if (!(err == ECANCELED && IsUnbinding))
            {
                ThrowHelper.ThrowNewErrnoException(err);
            }
        }

        public bool TryCompleteAccept(Ring ring, int result, [NotNullWhen(true)] out InboundConnection connection)
        {
            connection = default;
            if (!TryCompleteAcceptSocket(ring, result, out var socket))
            {
                return false;
            }

            EndPoint remoteEndPoint = null;
            if (IsIpSocket)
            {
                if (_options.TcpNoDelay)
                {
                    socket.SetOption(SOL_TCP, TCP_NODELAY, 1);
                }

                remoteEndPoint = EndPointFormatter.AddrToIpEndPoint((sockaddr_storage*) Addr);
            }

            connection = new InboundConnection(socket, EndPoint, remoteEndPoint, _memoryPool, _options, _scheduler);
            return true;
        }

        public void Unbid(Ring ring)
        {
            IsUnbinding = true;
            int socket = Socket;
            if (!ring.TryPrepareCancel(AsyncOperation.AcceptFrom(socket).AsUlong(), AsyncOperation.CancelAccept(socket).AsUlong()))
            {
                _scheduler.ScheduleCancel(AsyncOperation.CancelAccept(socket));
            }
        }

        public void Close(Ring ring)
        {
            int socket = Socket;
            if (ring.Supports(RingOperation.Close))
            {
                if (!ring.TryPrepareClose(socket, AsyncOperation.CloseAcceptSocket(socket).AsUlong()))
                {
                    _scheduler.ScheduleCloseAcceptSocket(socket);
                }
            }
            else
            {
                if (ring.TryPrepareNop(AsyncOperation.CloseAcceptSocket(socket).AsUlong()))
                {
                    Socket.Close(); // pre v5.6
                }
                else
                {
                    _scheduler.ScheduleCloseAcceptSocket(socket);
                }
            }
        }

        public void CompleteClose()
        {
            _unbindCompletion.TrySetResult(null);
        }
    }
}