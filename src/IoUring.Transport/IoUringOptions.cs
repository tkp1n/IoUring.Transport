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

        /// <summary>
        /// Sets the number of transport threads to be used to process I/O.
        /// This assumes SMT/HT and defaults to the number of physical cores of the CPU.
        /// <br/>
        /// Default: <code>Environment.ProcessorCount / 2</code>
        /// </summary>
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

        /// <summary>
        /// If set to true, this sets thread affinity of all I/O threads.
        /// <br/>
        /// Default: <code>false</code>
        /// </summary>
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

        internal bool ReceiveOnIncomingCpu
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

        /// <summary>
        /// Sets whether application logic is scheduled to run on the thread pool or inline of the transport threads.
        /// <br/>
        /// Default: <code>PipeScheduler.ThreadPool</code>
        /// </summary>
        public PipeScheduler ApplicationSchedulingMode { get; set; } = PipeScheduler.ThreadPool;

        /// <summary>
        /// Enables/Disables Nagle's algorithm.
        /// <br/>
        /// Default: <code>true</code>
        /// </summary>
        public bool TcpNoDelay { get; set; } = true;

        /// <summary>
        /// Sets the number of entries in the <code>io_uring</code> used by each transport thread.
        /// <br/>
        /// Default: 128
        /// </summary>
        public int RingSize { get; set; } = 128;

        /// <summary>
        /// Sets the size of the TCP listen backlog.
        /// <br/>
        /// Default: 128
        /// </summary>
        public int ListenBacklog { get; set; } = 128;
    }
}