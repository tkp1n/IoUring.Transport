using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal static class EndPointFormatter
    {
        public static unsafe void ToSockAddr(this IPEndPoint inetAddress, sockaddr_storage* addr, out int length)
        {
            if (inetAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                EndPoint4ToSockAddr(inetAddress, addr, out length);
            }
            else if (inetAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                EndPoint6ToSockAddr(inetAddress, addr, out length);
            }
            else
            {
                length = 0;
            }
        }

        private static unsafe void EndPoint4ToSockAddr(IPEndPoint inetAddress, sockaddr_storage* addr, out int length)
        {
            sockaddr_in* addrIn = (sockaddr_in*)addr;
            addrIn->sin_family = AF_INET;
            addrIn->sin_port = htons((ushort)inetAddress.Port);
            inetAddress.Address.TryWriteBytes(new Span<byte>(addrIn->sin_addr.s_addr, 4), out _);
            length = SizeOf.sockaddr_in;
        }

        private static unsafe void EndPoint6ToSockAddr(IPEndPoint inet6Address, sockaddr_storage* addr, out int length)
        {
            sockaddr_in6* addrIn = (sockaddr_in6*)addr;
            addrIn->sin6_family = AF_INET6;
            addrIn->sin6_port = htons((ushort)inet6Address.Port);
            addrIn->sin6_flowinfo = 0;
            addrIn->sin6_scope_id = 0;
            inet6Address.Address.TryWriteBytes(new Span<byte>(addrIn->sin6_addr.s6_addr, 16), out _);
            length = SizeOf.sockaddr_in6;
        }

        public static unsafe IPEndPoint AddrToIpEndPoint(sockaddr_storage* addr)
        {
            if (addr->ss_family == AF_INET)
            {
                sockaddr_in* addrIn = (sockaddr_in*)addr;
                long value = ((addrIn->sin_addr.s_addr[3] << 24 | addrIn->sin_addr.s_addr[2] << 16 | addrIn->sin_addr.s_addr[1] << 8 | addrIn->sin_addr.s_addr[0]) & 0x0FFFFFFFF);
                int port = ntohs(addrIn->sin_port);
                return new IPEndPoint(new IPAddress(value), port);
            }
            else if (addr->ss_family == AF_INET6)
            {
                sockaddr_in6* addrIn = (sockaddr_in6*)addr;
                Span<byte> bytes = stackalloc byte[16];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = addrIn->sin6_addr.s6_addr[i];
                }
                int port = ntohs(addrIn->sin6_port);
                return new IPEndPoint(new IPAddress(bytes, addrIn->sin6_scope_id), port);
            }
            else
            {
                ThrowHelper.ThrowNewNotSupportedException_AddressFamilyNotSupported();
                return null;
            }
        }

        public static unsafe void ToSockAddr(this UnixDomainSocketEndPoint domainSocketEndPoint, sockaddr_un* addr)
        {
            var unixPath = domainSocketEndPoint.ToString();
            *addr = default;
            addr->sun_family = AF_UNIX;
            var bytes = Encoding.UTF8.GetBytes(unixPath);
            var length = Math.Min(bytes.Length, sockaddr_un.sun_path_length - 1);
            bytes.AsSpan(0, length).CopyTo(new Span<byte>(addr->sun_path, length));
            addr->sun_path[length] = 0;
        }
    }
}