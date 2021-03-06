using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Tmds.Linux;

namespace IoUring.Transport.Internals
{
    [Flags]
    internal enum ConnectionState : byte
    {
        PollingRead     = 0x01,
        Reading         = 0x02,
        PollingWrite    = 0x04,
        Writing         = 0x08,
        ReadCancelled   = 0x10,
        WriteCancelled  = 0x20,
        HalfClosed      = 0x40,
        Closed          = 0x80
    }

    [Flags]
    internal enum ConnectionCapabilities : byte
    {
        Send     = 0x01,
        Recv     = 0x02,
        FastPoll = 0x04
    }

    internal abstract partial class IoUringConnection : TransportConnection
    {
        private const int ReadIOVecCount = 8;
        private const int WriteIOVecCount = 8;

        // Copied from LibuvTransportOptions.MaxReadBufferSize
        private const int PauseInputWriterThreshold = 1024 * 1024;
        // Copied from LibuvTransportOptions.MaxWriteBufferSize
        private const int PauseOutputWriterThreshold = 64 * 1024;

        private const int MaxBufferSize = 4096;

        private readonly Action _onOnFlushedToApp;
        private readonly Action _onReadFromApp;

        protected readonly TransportThreadScheduler _scheduler;

        private readonly byte[] _ioVecBytes;
        private readonly unsafe iovec* _iovec;

        private ValueTaskAwaiter<FlushResult> _flushResultAwaiter;
        private ValueTaskAwaiter<ReadResult> _readResultAwaiter;

        private readonly CancellationTokenSource _connectionClosedTokenSource;
        private readonly TaskCompletionSource _waitForConnectionClosedTcs;

        private ConnectionState _flags;
        private ConnectionCapabilities _capabilities;
        private byte _readIoVecsInUse;
        private byte _writeIoVecsInUse;
        private int _state;

        protected IoUringConnection(LinuxSocket socket, EndPoint local, EndPoint remote, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
        {
            Socket = socket;

            LocalEndPoint = local;
            RemoteEndPoint = remote;

            MemoryPool = memoryPool;
            Debug.Assert(MaxBufferSize == MemoryPool.MaxBufferSize);

            _scheduler = scheduler;

            _connectionClosedTokenSource = new CancellationTokenSource();
            ConnectionClosed = _connectionClosedTokenSource.Token;
            _waitForConnectionClosedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var appScheduler = options.ApplicationSchedulingMode;
            var inputOptions = new PipeOptions(memoryPool, appScheduler, PipeScheduler.Inline, PauseInputWriterThreshold, PauseInputWriterThreshold / 2, useSynchronizationContext: false);
            var outputOptions = new PipeOptions(memoryPool, PipeScheduler.Inline, appScheduler, PauseOutputWriterThreshold, PauseOutputWriterThreshold / 2, useSynchronizationContext: false);

            var pair = DuplexPipe.CreateConnectionPair(inputOptions, outputOptions);

            Transport = pair.Transport;
            Application = pair.Application;

            _onOnFlushedToApp = () => HandleFlushedToApp();
            _onReadFromApp = () => HandleReadFromApp();

            _ioVecBytes = GC.AllocateUninitializedArray<byte>(SizeOf.iovec * (ReadIOVecCount + WriteIOVecCount), pinned: true);
            unsafe
            {
                _iovec = (iovec*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_ioVecBytes);
            }
        }

        public LinuxSocket Socket { get; }

        public override MemoryPool<byte> MemoryPool { get; }

        private unsafe iovec* ReadVecs => _iovec;
        private unsafe iovec* WriteVecs => _iovec + ReadIOVecCount;

        private MemoryHandle[] ReadHandles { get; } = new MemoryHandle[ReadIOVecCount];
        private MemoryHandle[] WriteHandles { get; } = new MemoryHandle[WriteIOVecCount];

        /// <summary>
        /// Data read from the socket will be flushed to this <see cref="PipeWriter"/>
        /// </summary>
        private PipeWriter Inbound => Application.Output;

        /// <summary>
        /// Data read from this <see cref="PipeReader"/> will be written to the socket.
        /// </summary>
        private PipeReader Outbound => Application.Input;

        private ReadOnlySequence<byte> CurrentWrite { get; set; }

        public void StartSendAndReceive(Ring ring)
        {
            if (ring.Supports(RingOperation.Send))
            {
                SetCapability(ConnectionCapabilities.Send);
            }

            if (ring.Supports(RingOperation.Recv))
            {
                SetCapability(ConnectionCapabilities.Recv);
            }

            if (ring.SupportsFastPoll)
            {
                SetCapability(ConnectionCapabilities.FastPoll);
                Read(ring);
            }
            else
            {
                ReadPoll(ring);
            }

            ReadFromApp(ring);
        }

        private bool HasFlag(ConnectionState flag) => (_flags & flag) != 0;
        private void SetFlag(ConnectionState flag) => _flags |= flag;
        private void RemoveFlag(ConnectionState flag) => _flags &= ~flag;
        private static bool HasFlag(ConnectionState flag, ConnectionState test) => (flag & test) != 0;
        private static ConnectionState SetFlag(ConnectionState flag, ConnectionState newFlag) => flag | newFlag;
        private bool HasCapability(ConnectionCapabilities flag) => (_capabilities & flag) != 0;
        private void SetCapability(ConnectionCapabilities flag) => _capabilities |= flag;
    }
}