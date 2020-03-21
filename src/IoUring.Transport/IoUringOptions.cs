using System;
using System.IO.Pipelines;

namespace IoUring.Transport
{
    public class IoUringOptions
    {
        public int ThreadCount { get; set; } = Environment.ProcessorCount / 2;
        public PipeScheduler ApplicationSchedulingMode { get; set; } = PipeScheduler.ThreadPool;
        public bool TcpNoDelay { get; set; } = true;
    }
}