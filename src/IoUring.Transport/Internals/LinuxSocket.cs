using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal readonly struct LinuxSocket
    {
        private readonly int _fd;

        public LinuxSocket(int fd)
        {
            _fd = fd;
        }

        public LinuxSocket(int domain, int type, int protocol, bool blocking)
        {
            type |= SOCK_CLOEXEC;
            if (!blocking)
            {
                type |= SOCK_NONBLOCK;
            }

            var fd = socket(domain, type, protocol);
            if (fd < 0) throw new ErrnoException(errno);

            _fd = fd;
        }

        public unsafe void SetOption(int level, int option, int value)
        {
            var rv = setsockopt(_fd, level, option, (byte*) &value, 4);
            if (rv != 0) throw new ErrnoException(errno);
        }

        public unsafe void Bind(IPEndPoint endPoint)
        {
            sockaddr_storage addr;
            endPoint.ToSockAddr(&addr, out var length);
            var rv = bind(_fd, (sockaddr*) &addr, length);
            if (rv < 0)
            {
                var error = errno;
                if (error == EADDRINUSE)
                {
                    throw new AddressInUseException("Address in use.");
                }

                if (error == EADDRNOTAVAIL)
                {
                    throw new AddressNotAvailableException("Address not available.");
                }

                throw new ErrnoException(errno);
            }
        }

        public unsafe void Bind(UnixDomainSocketEndPoint endPoint)
        {
            sockaddr_un addr;
            endPoint.ToSockAddr(&addr);
            var rv = bind(_fd, (sockaddr*)&addr, SizeOf.sockaddr_un);

            if (rv < 0) throw new ErrnoException(errno);
        }

        public void Listen(int backlog)
        {
            var rv = listen(_fd, backlog);
            if (rv < 0) throw new ErrnoException(errno);
        }

        public unsafe EndPoint GetLocalAddress()
        {
            sockaddr_storage addr;
            socklen_t length = SizeOf.sockaddr_storage;
            if (getsockname(_fd, (sockaddr*) &addr, &length) != 0) throw new ErrnoException(errno);
            if (addr.ss_family == AF_INET || addr.ss_family == AF_INET6)
            {
                return EndPointFormatter.AddrToIpEndPoint(&addr);
            }

            if (addr.ss_family == AF_UNIX)
            {
                return EndPointFormatter.AddrToUnixDomainSocketEndPoint(&addr);
            }

            return null;
        }

        public unsafe EndPoint GetPeerAddress()
        {
            sockaddr_storage addr;
            socklen_t length = SizeOf.sockaddr_storage;
            if (getpeername(_fd, (sockaddr*) &addr, &length) != 0) throw new ErrnoException(errno);
            return EndPointFormatter.AddrToIpEndPoint(&addr);
        }

        public unsafe int GetReadableBytes() // TODO avoid if possible
        {
            int readableBytes;
            int rv = ioctl(_fd, FIONREAD, &readableBytes);
            if (rv == -1)
            {
                throw new ErrnoException(errno);
            }

            return readableBytes;
        }

        public unsafe void Write(byte* buffer, size_t length)
        {
            int rv;
            int error = 0;
            do
            {
                rv = (int) write(_fd, buffer, length);
            } while (rv == -1 && (error = errno) == EINTR);
            if (rv == -1) throw new ErrnoException(error);
        }

        public void Close() => close(_fd);

        public static implicit operator LinuxSocket(int v) => new LinuxSocket(v);
        public static implicit operator int(LinuxSocket s) => s._fd;
    }
}