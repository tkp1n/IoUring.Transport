using System.Diagnostics;
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

        public unsafe void SetFlag(int flag)
        {
            int flags = fcntl(_fd, F_GETFL, 0);
            if ((flags & flag) != 0) return;
            fcntl(_fd, F_SETFL, flags | flag);
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

        public unsafe void TransferAndClose(LinuxSocket recipient)
        {
            byte dummyBuffer = 0;
            iovec iov = default;
            iov.iov_base = &dummyBuffer;
            iov.iov_len = 1;

            int controlLength = CMSG_SPACE(sizeof(int));
            byte* control = stackalloc byte[controlLength];

            msghdr header = default;
            header.msg_iov = &iov;
            header.msg_iovlen = 1;
            header.msg_control = control;
            header.msg_controllen = controlLength;

            cmsghdr* cmsg = CMSG_FIRSTHDR(&header);
            cmsg->cmsg_level = SOL_SOCKET;
            cmsg->cmsg_type = SCM_RIGHTS;
            cmsg->cmsg_len = CMSG_LEN(sizeof(int));
            int *fdptr = (int*)CMSG_DATA(cmsg);
            *fdptr = _fd;

            ssize_t rv;
            do
            {
                Debug.WriteLine($"Sending accepted socket {_fd} to {recipient}");
                rv = sendmsg(recipient, &header, MSG_NOSIGNAL);
            } while (rv < 0 && errno == EINTR);

            Close();
        }

        public void Close() => close(_fd);

        public static implicit operator LinuxSocket(int v) => new LinuxSocket(v);
        public static implicit operator int(LinuxSocket s) => s._fd;

        public override string ToString() => _fd.ToString();
    }
}