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
                var client = new EchoClient(listener.EndPoint);

                try
                {
                    var connection = await listener.AcceptAsync();

                    var exchange = client.ExchangeData();
                    await LoopBack(connection.Transport);
                    await exchange;
                }
                finally
                {
                    client.Close();
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
        }
    }
}