using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;

namespace IoUring.Transport.Tests
{
    public class EchoClient
    {
        private Socket _socket;

        public EchoClient(EndPoint endPoint)
        {
            _socket = new Socket(endPoint.AddressFamily(), endPoint.SocketType(), endPoint.ProtocolType());

            _socket.Connect(endPoint);
        }

        public async Task ExchangeData()
        {
            var sendBuffer = ArrayPool<byte>.Shared.Rent(1024);
            var sendMemory = new Memory<byte>(sendBuffer, 0, 1024);
            new Random().NextBytes(sendBuffer);

            var recvBuffer = ArrayPool<byte>.Shared.Rent(1024);
            var recvMemory = new Memory<byte>(recvBuffer, 0, 1024);

            var sent = await _socket.SendAsync(sendMemory, SocketFlags.None);
            var received = await _socket.ReceiveAsync(recvMemory, SocketFlags.None);

            Assert.True(received <= sent);
            Assert.True(sendBuffer.AsSpan(0, received).SequenceEqual(recvBuffer.AsSpan(0, received)));
        }

        public void Close() => _socket.Close();
    }
}