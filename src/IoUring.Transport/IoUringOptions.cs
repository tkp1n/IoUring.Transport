using System;
using System.IO.Pipelines;

namespace IoUring.Transport
{
    public sealed class IoUringOptions
    {
        public int ThreadCount { get; set; } = Environment.ProcessorCount / 2;
        public PipeScheduler ApplicationSchedulingMode { get; set; } = PipeScheduler.ThreadPool;
        public bool TcpNoDelay { get; set; } = true;
        public int RingSize { get; set; } = 4096;
        public int ListenBacklog { get; set; } = 128;
    }
}