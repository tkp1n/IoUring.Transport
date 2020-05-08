using System;
using System.Buffers;
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
    internal enum ConnectionState
    {
        PollingRead     = 1 << 0,
        Reading         = 1 << 1,
        PollingWrite    = 1 << 2,
        Writing         = 1 << 3,
        ReadCancelled   = 1 << 4,
        WriteCancelled  = 1 << 5,
        HalfClosed      = 1 << 6,
        Closed          = 1 << 7
    }

    internal abstract partial class IoUringConnection : TransportConnection
    {
        private const int ReadIOVecCount = 1;
        private const int WriteIOVecCount = 8;

        // Copied from LibuvTransportOptions.MaxReadBufferSize
        private const int PauseInputWriterThreshold = 1024 * 1024;
        // Copied from LibuvTransportOptions.MaxWriteBufferSize
        private const int PauseOutputWriterThreshold = 64 * 1024;

        private readonly Action _onOnFlushedToApp;
        private readonly Action _onReadFromApp;

        private readonly TransportThreadScheduler _scheduler;

        private readonly byte[] _ioVecBytes;
        private readonly unsafe iovec* _iovec;

        private ValueTaskAwaiter<FlushResult> _flushResultAwaiter;
        private ValueTaskAwaiter<ReadResult> _readResultAwaiter;

        private readonly CancellationTokenSource _connectionClosedTokenSource;
        private readonly TaskCompletionSource<object> _waitForConnectionClosedTcs;

        protected IoUringConnection(LinuxSocket socket, EndPoint local, EndPoint remote, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
        {
            Socket = socket;

            LocalEndPoint = local;
            RemoteEndPoint = remote;

            MemoryPool = memoryPool;
            _scheduler = scheduler;

            _connectionClosedTokenSource = new CancellationTokenSource();
            ConnectionClosed = _connectionClosedTokenSource.Token;
            _waitForConnectionClosedTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            var appScheduler = options.ApplicationSchedulingMode;
            var inputOptions = new PipeOptions(MemoryPool, appScheduler, PipeScheduler.Inline, PauseInputWriterThreshold, PauseInputWriterThreshold / 2, useSynchronizationContext: false);
            var outputOptions = new PipeOptions(MemoryPool, PipeScheduler.Inline, appScheduler, PauseOutputWriterThreshold, PauseOutputWriterThreshold / 2, useSynchronizationContext: false);

            var pair = DuplexPipe.CreateConnectionPair(inputOptions, outputOptions);

            Transport = pair.Transport;
            Application = pair.Application;

            _onOnFlushedToApp = () => HandleFlushedToApp();
            _onReadFromApp = () => HandleReadFromApp();

            _ioVecBytes = GC.AllocateUninitializedArray<byte>(SizeOf.iovec * (ReadIOVecCount + WriteIOVecCount));
            unsafe
            {
                _iovec = (iovec*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_ioVecBytes);
            }
        }

        public LinuxSocket Socket { get; }

        public override MemoryPool<byte> MemoryPool { get; }

        private ConnectionState Flags { get; set; }

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

        private ReadOnlySequence<byte> ReadResult { get; set; }
        private ReadOnlySequence<byte> LastWrite { get; set; }

        private bool HasFlag(ConnectionState flag) => (Flags & flag) != 0;
        private void SetFlag(ConnectionState flag) => Flags |= flag;
        private void RemoveFlag(ConnectionState flag) => Flags &= ~flag;
        private static bool HasFlag(ConnectionState flag, ConnectionState test) => (flag & test) != 0;
        private static ConnectionState SetFlag(ConnectionState flag, ConnectionState newFlag) => flag | newFlag;
    }
}