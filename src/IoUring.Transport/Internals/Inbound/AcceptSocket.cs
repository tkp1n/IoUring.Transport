using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed unsafe class AcceptSocket : IAsyncDisposable
    {
        private readonly TaskCompletionSource<object> _unbindCompletion;
        private readonly IoUringOptions _options;
        private GCHandle _addrHandle;
        private GCHandle _addrLenHandle;

        private AcceptSocket(LinuxSocket socket, EndPoint endPoint, ChannelWriter<ConnectionContext> acceptQueue, IoUringOptions options)
        {
            Socket = socket;
            EndPoint = endPoint;
            AcceptQueue = acceptQueue;
            _options = options;

            _unbindCompletion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (endPoint is IPEndPoint) // Addr is null by intention for the remaining EndPoint types
            {
                sockaddr_storage storage = default;
                var addrHandle = GCHandle.Alloc(storage, GCHandleType.Pinned);
                Addr = (sockaddr*) addrHandle.AddrOfPinnedObject();
                _addrHandle = addrHandle;

                socklen_t addrLen = SizeOf.sockaddr_storage;
                var addrLenHandle = GCHandle.Alloc(addrLen, GCHandleType.Pinned);
                AddrLen = (socklen_t*) addrLenHandle.AddrOfPinnedObject();
                _addrLenHandle = addrLenHandle;
            }
        }

        public static AcceptSocket Bind(IPEndPoint ipEndPoint, ChannelWriter<ConnectionContext> acceptQueue, IoUringOptions options)
        {
            var domain = ipEndPoint.AddressFamily == AddressFamily.InterNetwork ? AF_INET : AF_INET6;
            LinuxSocket s = new LinuxSocket(domain, SOCK_STREAM, IPPROTO_TCP, blocking: true);
            s.SetOption(SOL_SOCKET, SO_REUSEADDR, 1);
            s.SetOption(SOL_SOCKET, SO_REUSEPORT, 1);
            s.Bind(ipEndPoint);
            s.Listen(options.ListenBacklog);

            return new AcceptSocket(s, ipEndPoint, acceptQueue, options);
        }

        public static AcceptSocket Bind(UnixDomainSocketEndPoint unixDomainSocketEndPoint, ChannelWriter<ConnectionContext> acceptQueue, IoUringOptions options)
        {
            var socketPath = unixDomainSocketEndPoint.ToString();
            var s = new LinuxSocket(AF_UNIX, SOCK_STREAM, 0, blocking: false);
            File.Delete(socketPath);
            s.Bind(unixDomainSocketEndPoint);
            s.Listen(options.ListenBacklog);

            return new AcceptSocket(s, unixDomainSocketEndPoint, acceptQueue, options);
        }

        public static AcceptSocket Bind(FileHandleEndPoint fileHandleEndPoint, ChannelWriter<ConnectionContext> acceptQueue, IoUringOptions options)
        {
            return new AcceptSocket((int) fileHandleEndPoint.FileHandle, fileHandleEndPoint, acceptQueue, options);
        }

        public LinuxSocket Socket { get; }
        public EndPoint EndPoint { get; }
        public ChannelWriter<ConnectionContext> AcceptQueue { get; }
        public sockaddr* Addr { get; }
        public socklen_t* AddrLen { get; }
        public bool IsIpSocket => Addr == null;
        public bool IsUnbinding { get; set; }
        public Task UnbindCompletion => _unbindCompletion.Task;

        public void Accept(Ring ring)
        {
            int socket = Socket;
            ring.PrepareAccept(socket, Addr, AddrLen, SOCK_NONBLOCK | SOCK_CLOEXEC, AsyncOperation.AcceptFrom(socket).AsUlong());
        }

        public bool TryCompleteAccept(Ring ring, int result, out LinuxSocket socket)
        {
            socket = default;
            if (result < 0)
            {
                var err = -result;
                if (err == EAGAIN || err == EINTR || err == EMFILE)
                {
                    Debug.WriteLine("accepted for nothing");

                    Accept(ring);
                    return false;
                }

                if (err == ECANCELED && IsUnbinding)
                {
                    return false;
                }

                throw new ErrnoException(err);
            }

            Debug.WriteLine($"Accepted {result}");
            socket = result;
            return true;
        }

        public bool TryCompleteAccept(Ring ring, int result, MemoryPool<byte> memoryPool, TransportThreadScheduler scheduler, [NotNullWhen(true)] out InboundConnection connection)
        {
            connection = default;
            if (!TryCompleteAccept(ring, result, out LinuxSocket socket))
            {
                return false;
            }

            IPEndPoint remoteEndPoint = null;
            if (IsIpSocket)
            {
                if (_options.TcpNoDelay)
                {
                    socket.SetOption(SOL_TCP, TCP_NODELAY, 1);
                }

                remoteEndPoint = EndPointFormatter.AddrToIpEndPoint((sockaddr_storage*) Addr);
            }

            connection = new InboundConnection(socket, EndPoint, remoteEndPoint, memoryPool, _options, scheduler);
            return true;
        }

        public void Unbid(Ring ring)
        {
            IsUnbinding = true;
            int socket = Socket;
            ring.PrepareCancel(AsyncOperation.AcceptFrom(Socket).AsUlong(), AsyncOperation.CancelAccept(socket).AsUlong());
        }

        public void Close(Ring ring)
        {
            if (ring.Supports(RingOperation.Close))
            {
                ring.PrepareClose(Socket, AsyncOperation.CloseAcceptSocket(Socket).AsUlong());
            }
            else
            {
                Socket.Close(); // pre v5.6
                ring.PrepareNop(AsyncOperation.CloseAcceptSocket(Socket).AsUlong());
            }
        }

        public void CompleteClose()
        {
            _unbindCompletion.TrySetResult(null);
            _ = DisposeAsync();
        }

        public ValueTask DisposeAsync()
        {
            if (_addrHandle.IsAllocated)
                _addrHandle.Free();
            if (_addrLenHandle.IsAllocated)
                _addrLenHandle.Free();

            return default;
        }
    }
}