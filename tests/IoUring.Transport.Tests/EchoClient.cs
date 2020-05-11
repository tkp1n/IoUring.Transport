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

        public async Task ExchangeData(int length)
        {
            int sentTotal = 0;
            int receivedTotal = 0;

            var sendBuffer = ArrayPool<byte>.Shared.Rent(length);
            var recvBuffer = ArrayPool<byte>.Shared.Rent(length);

            new Random().NextBytes(sendBuffer);

            while (receivedTotal < length)
            {
                var sendMemory = new Memory<byte>(sendBuffer, sentTotal, length - sentTotal);
                var recvMemory = new Memory<byte>(recvBuffer, receivedTotal, length - receivedTotal);

                var sent = await _socket.SendAsync(sendMemory, SocketFlags.None);
                var received = await _socket.ReceiveAsync(recvMemory, SocketFlags.None);

                sentTotal += sent;
                receivedTotal += received;
            }

            Assert.Equal(sentTotal, receivedTotal);
            Assert.True(sendBuffer.AsSpan(0, length).SequenceEqual(recvBuffer.AsSpan(0, length)));
        }

        public void Close() => _socket.Close();
    }
}