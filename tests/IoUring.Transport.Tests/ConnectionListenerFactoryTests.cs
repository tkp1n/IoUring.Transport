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
        private static readonly MemoryPool<byte> _memoryPool = new SlabMemoryPool();
        private static readonly EndPoint[] EndPoints =
        {
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 0),
            new IPEndPoint(IPAddress.Parse("::1"), 0),
            new UnixDomainSocketEndPoint($"{Path.GetTempPath()}/{Path.GetRandomFileName()}")
        };

        private static readonly int[] Lengths =
        {
            1,
            _memoryPool.MaxBufferSize - 1,
            _memoryPool.MaxBufferSize,
            _memoryPool.MaxBufferSize + 1,
            (8 * _memoryPool.MaxBufferSize) - 1,
            (8 * _memoryPool.MaxBufferSize),
            (8 * _memoryPool.MaxBufferSize) + 1,
        };

        private static readonly PipeScheduler[] SchedulerModes =
        {
            PipeScheduler.Inline,
            PipeScheduler.ThreadPool
        };

        private static readonly int[] ThreadCount =
        {
            1,
            4
        };

        private static readonly int[] RingSize =
        {
            2,
            128
        };

        public static IEnumerable<object[]> Data()
        {
            foreach (var endpoint in EndPoints)
            foreach (var length in Lengths)
            foreach (var schedulerMode in SchedulerModes)
            foreach (var threadCount in ThreadCount)
            foreach (var ringSize in RingSize)
            {
                yield return new object[] { endpoint, length, schedulerMode, threadCount, ringSize };
            }
        }

        [Theory]
        [MemberData(nameof(Data))]
        public async Task SmokeTest(EndPoint endPoint, int length, PipeScheduler schedulerMode, int threadCount, int ringSize)
        {
            var options = Options.Create(new IoUringOptions
            {
                ThreadCount = threadCount,
                ApplicationSchedulingMode = schedulerMode,
                RingSize = ringSize
            });
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