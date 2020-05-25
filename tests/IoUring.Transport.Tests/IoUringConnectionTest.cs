using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace IoUring.Transport.Tests
{
    public abstract class IoUringConnectionTest
    {
        private const int MaxBufferSize = 4096;
        private static readonly Func<EndPoint>[] EndPoints;

        static IoUringConnectionTest()
        {
            var endPoints = new List<Func<EndPoint>>();
            if (Socket.OSSupportsIPv4) endPoints.Add(() => new IPEndPoint(IPAddress.Parse("127.0.0.1"), 0));
            if (Socket.OSSupportsIPv6) endPoints.Add(() => new IPEndPoint(IPAddress.Parse("::1"), 0));
            if (Socket.OSSupportsUnixDomainSockets) endPoints.Add(() => new UnixDomainSocketEndPoint($"{Path.GetTempPath()}/{Path.GetRandomFileName()}"));

            EndPoints = endPoints.ToArray();
        }

        private static readonly int[] Lengths =
        {
            1,
            MaxBufferSize - 1,
            MaxBufferSize,
            MaxBufferSize + 1,
            (8 * MaxBufferSize) - 1,
            (8 * MaxBufferSize),
            (8 * MaxBufferSize) + 1,
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
            4,
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