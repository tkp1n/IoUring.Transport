using System;
using System.Buffers;
using System.Diagnostics;
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
        private readonly MemoryPool<byte> _memoryPool;
        private readonly IoUringOptions _options;
        private readonly TransportThreadScheduler _scheduler;
        private readonly byte[] _addr;
        private readonly byte[] _addrLen;

        private AcceptSocket(LinuxSocket socket, EndPoint endPoint, ChannelWriter<ConnectionContext> acceptQueue, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
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

        public static AcceptSocket Bind(IPEndPoint ipEndPoint, ChannelWriter<ConnectionContext> acceptQueue, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Binding to {ipEndPoint}");
#endif
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
#if TRACE_IO_URING
            Trace.WriteLine($"Binding to {unixDomainSocketEndPoint}");
#endif
            var socketPath = unixDomainSocketEndPoint.ToString();
            var s = new LinuxSocket(AF_UNIX, SOCK_STREAM, 0, blocking: false);
            File.Delete(socketPath);
            s.Bind(unixDomainSocketEndPoint);
            s.Listen(options.ListenBacklog);

            return new AcceptSocket(s, unixDomainSocketEndPoint, acceptQueue, null, options, null);
        }

        public static AcceptSocket Bind(FileHandleEndPoint fileHandleEndPoint, ChannelWriter<ConnectionContext> acceptQueue, IoUringOptions options)
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Binding to {fileHandleEndPoint}");
#endif
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
#if TRACE_IO_URING
            Trace.WriteLine($"Polling for accept on {socket}");
#endif
            ring.PreparePollAdd(socket, (ushort) POLLIN, AsyncOperation.PollAcceptFrom(socket).AsUlong());
        }

        public void CompleteAcceptPoll(Ring ring, int result)
        {
            if (result >= 0)
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Completed accept poll on {(int)Socket}");
#endif
                Accept(ring);
            }
            else
            {
                HandleCompleteAcceptPollError(ring, result);
            }
        }

        private void HandleCompleteAcceptPollError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
#if TRACE_IO_URING
                    Trace.WriteLine("Polled accept for nothing");
#endif

                if (!IsUnbinding)
                {
                    AcceptPoll(ring);
                }
            }
            else if (err == ECANCELED && IsUnbinding)
            {
#if TRACE_IO_URING
                    Trace.WriteLine("Accept cancelled while unbinding");
#endif
            }
        }

        public void Accept(Ring ring)
        {
            int socket = Socket;
#if TRACE_IO_URING
            Trace.WriteLine($"Adding accept on {socket}");
#endif

            if (IsIpSocket)
            {
                _addr.AsSpan().Clear();
                *AddrLen = SizeOf.sockaddr_storage;
            }

            ring.PrepareAccept(socket, Addr, AddrLen, SOCK_NONBLOCK | SOCK_CLOEXEC, AsyncOperation.AcceptFrom(socket).AsUlong());
        }

        public bool TryCompleteAcceptSocket(Ring ring, int result, out LinuxSocket socket)
        {
            socket = default;
            if (result < 0)
            {
                if (!HandleCompleteAcceptSocketError(ring, result)) return false;
            }

#if TRACE_IO_URING
            Trace.WriteLine($"Accepted {result} on {Socket}");
#endif
            socket = result;
            return true;
        }

        private bool HandleCompleteAcceptSocketError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EINTR || err == EMFILE)
            {
#if TRACE_IO_URING
                    Trace.WriteLine($"Accepted for nothing on {Socket}");
#endif

                if (!IsUnbinding)
                {
                    Accept(ring);
                }

                return false;
            }

            if (err == ECANCELED && IsUnbinding)
            {
#if TRACE_IO_URING
                    Trace.WriteLine("Accept cancelled while unbinding");
#endif
                return false;
            }

            ThrowHelper.ThrowNewErrnoException(err);
            return true;
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
#if TRACE_IO_URING
            Trace.WriteLine($"Adding async cancel on {socket} to unbind");
#endif
            ring.PrepareCancel(AsyncOperation.AcceptFrom(Socket).AsUlong(), AsyncOperation.CancelAccept(socket).AsUlong());
        }

        public void Close(Ring ring)
        {
            if (ring.Supports(RingOperation.Close))
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Adding close on {Socket}");
#endif
                ring.PrepareClose(Socket, AsyncOperation.CloseAcceptSocket(Socket).AsUlong());
            }
            else
            {
#if TRACE_IO_URING
                Trace.WriteLine($"Closing {Socket}");
#endif
                Socket.Close(); // pre v5.6
#if TRACE_IO_URING
                Trace.WriteLine($"Adding nop on {Socket}");
#endif
                ring.PrepareNop(AsyncOperation.CloseAcceptSocket(Socket).AsUlong());
            }
        }

        public void CompleteClose()
        {
#if TRACE_IO_URING
            Trace.WriteLine($"Close (unbind) completed for {Socket}");
#endif
            _unbindCompletion.TrySetResult(null);
        }
    }
}