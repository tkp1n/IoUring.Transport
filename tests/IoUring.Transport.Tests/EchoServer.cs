using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using Xunit.Abstractions;

namespace IoUring.Transport.Tests
{
    public class EchoServer : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly EndPoint _endPoint;
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts;
        private volatile bool _disposed;

        public EchoServer(EndPoint endPoint, ITestOutputHelper output)
        {
            _output = output;
            _socket = new Socket(endPoint.AddressFamily(), endPoint.SocketType(), endPoint.ProtocolType());

            _socket.Bind(endPoint);
            _socket.Listen();
            _endPoint = _socket.LocalEndPoint;

            _cts = new CancellationTokenSource();
            Task.Run(async () => await Accept());
        }

        public EndPoint EndPoint => _endPoint;

        public async Task Accept()
        {
            try
            {
                while (!_disposed)
                {
                    var accepted = await _socket.AcceptAsync();

                    _output.WriteLine("Accepted socket");
                    _output.WriteLine($"local: {accepted.LocalEndPoint}");
                    _output.WriteLine($"remote: {accepted.RemoteEndPoint}");

                    _ = Task.Run(async () => await Handle(accepted));
                }
            }
            catch { }
            finally
            {
                _socket.Close();
            }
        }

        public async Task Handle(Socket socket)
        {
            try
            {
                var buffer = ArrayPool<byte>.Shared.Rent(1024);
                var memory = new Memory<byte>(buffer);
                while (!_disposed)
                {
                    int received = await socket.ReceiveAsync(memory, SocketFlags.None, _cts.Token);
                    _output.WriteLine($"Received {received} bytes from {socket.RemoteEndPoint}");

                    await socket.SendAsync(memory.Slice(0, received), SocketFlags.None, _cts.Token);
                    _output.WriteLine($"Sent data back to {socket.RemoteEndPoint}");
                }

                ArrayPool<byte>.Shared.Return(buffer);
            }
            catch { }
            finally
            {
                socket.Close();
            }
        }

        public void Shutdown()
        {
            _disposed = true;
            _cts.Cancel();
            _socket.Close();
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}