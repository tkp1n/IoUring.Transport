using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;
using IoUring.Transport.Internals;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IoUring.Transport.Internals.Outbound;
using System.IO.Pipelines;
using System.Collections.Generic;
using System.IO;
using Xunit.Abstractions;

namespace IoUring.Transport.Tests
{
    public class ConnectionFactoryTests
    {
        public ConnectionFactoryTests(ITestOutputHelper outputHelper)
        {
            OutputHelper = outputHelper;
        }

        private ITestOutputHelper OutputHelper { get; }

        private static readonly EndPoint[] EndPoints =
        {
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 0),
            new IPEndPoint(IPAddress.Parse("::1"), 0),
            new UnixDomainSocketEndPoint($"{Path.GetTempPath()}/{Path.GetRandomFileName()}")
        };

        public static IEnumerable<object[]> Data()
        {
            foreach (var endpoint in EndPoints)
            {
                yield return new object[] { endpoint };
            }
        }

        [Theory]
        [MemberData(nameof(Data))]
        public async void SmokeTest(EndPoint endpoint)
        {
            using var server = new EchoServer(endpoint, OutputHelper);

            var logger = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider()
                .GetService<ILoggerFactory>();

            var transport = new IoUringTransport(Options.Create(new IoUringOptions()), logger);
            var connectionFactory = new ConnectionFactory(transport);

            try
            {
                var connection = await connectionFactory.ConnectAsync(server.EndPoint);

                await SendReceiveData(connection.Transport);

                await connection.Transport.Output.CompleteAsync();
                await connection.Transport.Input.CompleteAsync();
                await connection.DisposeAsync();
            }
            finally
            {
                server.Shutdown();
                await connectionFactory.DisposeAsync();
                await transport.DisposeAsync();
            }
        }

        private async Task SendReceiveData(IDuplexPipe transport)
        {
            var sendBuffer = ArrayPool<byte>.Shared.Rent(1024);
            new Random().NextBytes(sendBuffer);

            var sendResult = await transport.Output.WriteAsync(new ReadOnlyMemory<byte>(sendBuffer, 0, 1024));
            Assert.False(sendResult.IsCompleted);
            Assert.False(sendResult.IsCanceled);

            var recvResult = await transport.Input.ReadAsync();
            Assert.False(recvResult.IsCompleted);
            Assert.False(recvResult.IsCanceled);
            var recvBuffer = recvResult.Buffer.ToArray();

            Assert.True(sendBuffer.AsSpan(0, 1024).SequenceEqual(recvBuffer.AsSpan(0, 1024)));
            ArrayPool<byte>.Shared.Return(sendBuffer);
        }
    }
}