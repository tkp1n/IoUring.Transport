using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using IoUring.Transport.Internals;
using IoUring.Transport.Internals.Inbound;
using Microsoft.Extensions.Options;
using Xunit;

namespace IoUring.Transport.Tests
{
    public class ConnectionListenerFactoryTests
    {
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
        public async Task SmokeTest(EndPoint endPoint)
        {
            var options = Options.Create(new IoUringOptions());
            var transport = new IoUringTransport(options);

            try
            {
                var listenerFactory = new ConnectionListenerFactory(transport, options);
                var listener = await listenerFactory.BindAsync(endPoint);

                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var client = new EchoClient(listener.EndPoint);
                        var connection = await listener.AcceptAsync();

                        for (int j = 0; j < 3; j++)
                        {
                            var exchange = client.ExchangeData();
                            await LoopBack(connection.Transport);
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

        private async Task LoopBack(IDuplexPipe transport)
        {
            var read = await transport.Input.ReadAsync();
            await transport.Output.WriteAsync(read.Buffer.ToArray().AsMemory());
            transport.Input.AdvanceTo(read.Buffer.End);
        }
    }
}