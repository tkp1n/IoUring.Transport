using System;
using System.IO.Pipelines;

namespace IoUring.Transport
{
    public sealed class IoUringOptions
    {
        private static readonly int CpuThreadCount = Environment.ProcessorCount;
        private static readonly int CpuCoreCountEstimate = Environment.ProcessorCount / 2;

        private int _threadCount = CpuCoreCountEstimate;
        private bool _setThreadAffinity;
        private bool _receiveOnIncomingCpu;

        public int ThreadCount
        {
            get => _threadCount;
            set
            {
                if (value != CpuThreadCount)
                {
                    _receiveOnIncomingCpu = false;
                }

                _threadCount = value;
            }
        }

        public bool SetThreadAffinity
        {
            get => _setThreadAffinity;
            set
            {
                if (!value)
                {
                    _receiveOnIncomingCpu = false;
                }

                _setThreadAffinity = value;
            }
        }

        public bool ReceiveOnIncomingCpu
        {
            get => _receiveOnIncomingCpu;
            set
            {
                _receiveOnIncomingCpu = value;

                if (value)
                {
                    _threadCount = CpuThreadCount;
                    _setThreadAffinity = true;
                }
            }
        }

        public PipeScheduler ApplicationSchedulingMode { get; set; } = PipeScheduler.ThreadPool;
        public bool TcpNoDelay { get; set; } = true;
        public int RingSize { get; set; } = 128;
        public int ListenBacklog { get; set; } = 128;
    }
}