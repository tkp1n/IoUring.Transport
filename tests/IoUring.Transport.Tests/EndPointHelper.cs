using System;
using System.Net;
using System.Net.Sockets;

namespace IoUring.Transport.Tests
{
    public static class EndPointHelper
    {
        public static AddressFamily AddressFamily(this EndPoint endPoint) => endPoint switch {
            IPEndPoint ipEp => ipEp.AddressFamily,
            UnixDomainSocketEndPoint _ => System.Net.Sockets.AddressFamily.Unix,
            _ => throw new NotSupportedException()
        };

        public static SocketType SocketType(this EndPoint endPoint) => endPoint switch {
            IPEndPoint _ => System.Net.Sockets.SocketType.Stream,
            UnixDomainSocketEndPoint _ => System.Net.Sockets.SocketType.Stream,
            _ => throw new NotSupportedException()
        };

        public static ProtocolType ProtocolType(this EndPoint endPoint) => endPoint switch {
            IPEndPoint _ => System.Net.Sockets.ProtocolType.Tcp,
            UnixDomainSocketEndPoint _ => System.Net.Sockets.ProtocolType.Unspecified,
            _ => throw new NotSupportedException()
        };
    }
}