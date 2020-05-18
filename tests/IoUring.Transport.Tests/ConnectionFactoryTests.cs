using System;
using System.Buffers;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using IoUring.Transport.Internals;
using Microsoft.Extensions.Options;
using IoUring.Transport.Internals.Outbound;
using System.IO.Pipelines;
using Xunit.Abstractions;

namespace IoUring.Transport.Tests
{
    public class ConnectionFactoryTests : IoUringConnectionTest
    {
        public ConnectionFactoryTests(ITestOutputHelper outputHelper)
        {
            OutputHelper = outputHelper;
        }

        private ITestOutputHelper OutputHelper { get; }

        [Theory]
        [MemberData(nameof(Data))]
        public async void SmokeTest(Func<EndPoint> endpoint, int length, PipeScheduler schedulerMode, int threadCount, int ringSize, bool threadAffinity)
        {
            using var server = new EchoServer(endpoint(), OutputHelper);
            var transport = new IoUringTransport(Options.Create(new IoUringOptions
            {
                ThreadCount = threadCount,
                SetThreadAffinity = threadAffinity,
                ApplicationSchedulingMode = schedulerMode,
                RingSize = ringSize
            }));
            var connectionFactory = new ConnectionFactory(transport);

            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var connection = await connectionFactory.ConnectAsync(server.EndPoint);

                    for (int j = 0; j < 3; j++)
                    {
                        await SendReceiveData(connection.Transport, length);
                    }

                    await connection.Transport.Output.CompleteAsync();
                    await connection.Transport.Input.CompleteAsync();
                    await connection.DisposeAsync();
                }
            }
            finally
            {
                server.Shutdown();
                await connectionFactory.DisposeAsync();
                await transport.DisposeAsync();
            }
        }

        private async Task SendReceiveData(IDuplexPipe transport, int length)
        {
            var sendBuffer = ArrayPool<byte>.Shared.Rent(length);
            new Random().NextBytes(sendBuffer);

            var sendResult = await transport.Output.WriteAsync(new ReadOnlyMemory<byte>(sendBuffer, 0, length));
            Assert.False(sendResult.IsCompleted);
            Assert.False(sendResult.IsCanceled);

            int received = 0;
            var recvTotalBuffer = ArrayPool<byte>.Shared.Rent(length);
            while (received < length)
            {
                var recvResult = await transport.Input.ReadAsync();

                Assert.False(recvResult.IsCompleted);
                Assert.False(recvResult.IsCanceled);
                var recvBuffer = recvResult.Buffer;
                recvBuffer.CopyTo(recvTotalBuffer.AsSpan(received));
                received += (int) recvBuffer.Length;

                transport.Input.AdvanceTo(recvResult.Buffer.End);
            }

            Assert.True(sendBuffer.AsSpan(0, length).SequenceEqual(recvTotalBuffer.AsSpan(0, length)));
            ArrayPool<byte>.Shared.Return(sendBuffer);
            ArrayPool<byte>.Shared.Return(recvTotalBuffer);
        }
    }
}
