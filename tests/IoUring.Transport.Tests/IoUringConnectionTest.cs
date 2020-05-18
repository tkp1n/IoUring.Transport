using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace IoUring.Transport.Tests
{
    public abstract class IoUringConnectionTest
    {
        private static readonly MemoryPool<byte> _memoryPool = new SlabMemoryPool();

        private static readonly Func<EndPoint>[] EndPoints =
        {
            () => new IPEndPoint(IPAddress.Parse("127.0.0.1"), 0),
            () => new IPEndPoint(IPAddress.Parse("::1"), 0),
            () => new UnixDomainSocketEndPoint($"{Path.GetTempPath()}/{Path.GetRandomFileName()}")
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

        private static readonly bool[] ThreadAffinity =
        {
            true,
            false
        };

        public static IEnumerable<object[]> Data()
        {
            foreach (var endpoint in EndPoints)
            foreach (var length in Lengths)
            foreach (var schedulerMode in SchedulerModes)
            foreach (var threadCount in ThreadCount)
            foreach (var ringSize in RingSize)
            foreach (var threadAffinity in ThreadAffinity)
            {
                yield return new object[] { endpoint, length, schedulerMode, threadCount, ringSize, threadAffinity };
            }
        }
    }
}