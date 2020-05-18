using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;
using IoUring.Transport.Internals;
using IoUring.Transport.Internals.Inbound;
using Microsoft.Extensions.Options;
using Xunit;

namespace IoUring.Transport.Tests
{
    public class ConnectionListenerFactoryTests : IoUringConnectionTest
    {
        [Theory]
        [MemberData(nameof(Data))]
        public async Task SmokeTest(Func<EndPoint> endPoint, int length, PipeScheduler schedulerMode, int threadCount, int ringSize, bool threadAffinity)
        {
            var options = Options.Create(new IoUringOptions
            {
                ThreadCount = threadCount,
                SetThreadAffinity = threadAffinity,
                ApplicationSchedulingMode = schedulerMode,
                RingSize = ringSize
            });
            var transport = new IoUringTransport(options);

            try
            {
                var listenerFactory = new ConnectionListenerFactory(transport, options);
                var listener = await listenerFactory.BindAsync(endPoint());

                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var client = new EchoClient(listener.EndPoint);
                        var connection = await listener.AcceptAsync();

                        for (int j = 0; j < 3; j++)
                        {
                            var exchange = client.ExchangeData(length);
                            await LoopBack(connection.Transport, length);
                            await exchange;
                        }

                        await connection.Transport.Output.CompleteAsync();
                        await connection.Transport.Input.CompleteAsync();
                        await connection.DisposeAsync();
                        client.Close();
                    }
                }
                finally
                {
                    await listener.UnbindAsync();
                    await listener.DisposeAsync();
                }
            }
            finally
            {
                await transport.DisposeAsync();
            }
        }

        private async Task LoopBack(IDuplexPipe transport, int bytes)
        {
            int looped = 0;
            while (looped < bytes)
            {
                var read = await transport.Input.ReadAsync();
                var readMemory = read.Buffer.ToArray().AsMemory();
                await transport.Output.WriteAsync(readMemory);
                transport.Input.AdvanceTo(read.Buffer.End);

                looped += readMemory.Length;
            }
        }
    }
}