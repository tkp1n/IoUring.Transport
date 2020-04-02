using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    internal abstract class IoUringConnectionContext : TransportConnection
    {
        private const int ReadIOVecCount = 1;
        private const int WriteIOVecCount = 8;

        // Copied from LibuvTransportOptions.MaxReadBufferSize
        private const int PauseInputWriterThreshold = 1024 * 1024;
        // Copied from LibuvTransportOptions.MaxWriteBufferSize
        private const int PauseOutputWriterThreshold = 64 * 1024;

        private readonly TransportThreadContext _threadContext;
        private readonly Action _onOnFlushedToApp;
        private readonly Action _onReadFromApp;

        private readonly unsafe iovec* _iovec;
        private GCHandle _iovecHandle;

        private ValueTaskAwaiter<FlushResult> _flushResultAwaiter;
        private ValueTaskAwaiter<ReadResult> _readResultAwaiter;

        private readonly CancellationTokenSource _connectionClosedTokenSource;
        private readonly TaskCompletionSource<object> _waitForConnectionClosedTcs;

        protected IoUringConnectionContext(LinuxSocket socket, EndPoint local, EndPoint remote, TransportThreadContext threadContext)
        {
            Socket = socket;

            LocalEndPoint = local;
            RemoteEndPoint = remote;

            MemoryPool = threadContext.MemoryPool;
            _threadContext = threadContext;

            _connectionClosedTokenSource = new CancellationTokenSource();
            ConnectionClosed = _connectionClosedTokenSource.Token;
            _waitForConnectionClosedTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            var appScheduler = threadContext.Options.ApplicationSchedulingMode;
            var inputOptions = new PipeOptions(MemoryPool, appScheduler, PipeScheduler.Inline, PauseInputWriterThreshold, PauseInputWriterThreshold / 2, useSynchronizationContext: false);
            var outputOptions = new PipeOptions(MemoryPool, PipeScheduler.Inline, appScheduler, PauseOutputWriterThreshold, PauseOutputWriterThreshold / 2, useSynchronizationContext: false);

            var pair = DuplexPipe.CreateConnectionPair(inputOptions, outputOptions);

            Transport = pair.Transport;
            Application = pair.Application;

            _onOnFlushedToApp = () => HandleFlushedToApp();
            _onReadFromApp = () => HandleReadFromApp();

            iovec[] vecs = new iovec[ReadIOVecCount + WriteIOVecCount];
            var vecsHandle = GCHandle.Alloc(vecs, GCHandleType.Pinned);
            unsafe { _iovec = (iovec*) vecsHandle.AddrOfPinnedObject(); }
            _iovecHandle = vecsHandle;
        }

        public ConnectionState Flags { get; set; }

        public LinuxSocket Socket { get; }

        public override MemoryPool<byte> MemoryPool { get; }

        public unsafe iovec* ReadVecs => _iovec;
        public unsafe iovec* WriteVecs => _iovec + ReadIOVecCount;

        public MemoryHandle[] ReadHandles { get; } = new MemoryHandle[ReadIOVecCount];
        public MemoryHandle[] WriteHandles { get; } = new MemoryHandle[WriteIOVecCount];

        /// <summary>
        /// Data read from the socket will be flushed to this <see cref="PipeWriter"/>
        /// </summary>
        public PipeWriter Inbound => Application.Output;

        /// <summary>
        /// Data read from this <see cref="PipeReader"/> will be written to the socket.
        /// </summary>
        public PipeReader Outbound => Application.Input;

        public ReadOnlySequence<byte> ReadResult { get; private set; }
        public ReadOnlySequence<byte> LastWrite { get; set; }

        public AsyncOperationResult FlushAsync()
        {
            var awaiter = Inbound.FlushAsync().GetAwaiter();
            _flushResultAwaiter = awaiter;
            if (awaiter.IsCompleted)
            {
                return HandleFlushedToApp(true);
            }

            awaiter.UnsafeOnCompleted(_onOnFlushedToApp);
            return default;
        }

        private AsyncOperationResult HandleFlushedToApp(bool onTransportThread = false)
        {
            Exception error = null;
            try
            {
                var flushResult = _flushResultAwaiter.GetResult();
                if (flushResult.IsCompleted || flushResult.IsCanceled)
                {
                    error = AsyncOperationResult.CompleteWithoutErrorSentinel;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }

            var result = new AsyncOperationResult(onTransportThread, error);
            if (onTransportThread)
            {
                return result;
            }

            if (error != null)
            {
                _threadContext.ScheduleAsyncInboundCompletion(Socket, result.GetError());
            }
            else
            {
                Debug.WriteLine($"Flushed to app for {(int) Socket} asynchronously");
                _threadContext.ScheduleAsyncRead(Socket);
            }

            return result;
        }

        public AsyncOperationResult ReadAsync()
        {
            var awaiter = Outbound.ReadAsync().GetAwaiter();
            _readResultAwaiter = awaiter;
            if (awaiter.IsCompleted)
            {
                return HandleReadFromApp(true);
            }

            awaiter.UnsafeOnCompleted(_onReadFromApp);
            return default;
        }

        private AsyncOperationResult HandleReadFromApp(bool onTransportThread = false)
        {
            Exception error = null;
            try
            {
                var readResult = _readResultAwaiter.GetResult();
                var buffer = readResult.Buffer;
                ReadResult = buffer;
                if ((buffer.IsEmpty && readResult.IsCompleted) || readResult.IsCanceled)
                {
                    error = AsyncOperationResult.CompleteWithoutErrorSentinel;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }

            var result = new AsyncOperationResult(onTransportThread, error);
            if (onTransportThread)
            {
                return result;
            }

            if (error != null)
            {
                _threadContext.ScheduleAsyncOutboundCompletion(Socket, result.GetError());
            }
            else
            {
                Debug.WriteLine($"Read from app for {(int)Socket} asynchronously");
                _threadContext.ScheduleAsyncWrite(Socket);
            }

            return result;
        }

        public bool HasFlag(ConnectionState flag) => (Flags & flag) != 0;
        public void SetFlag(ConnectionState flag) => Flags |= flag;
        public void RemoveFlag(ConnectionState flag) => Flags &= ~flag;

        // Invoked when socket was closed by transport thread
        public void NotifyClosed()
        {
            ThreadPool.UnsafeQueueUserWorkItem(state => ((IoUringConnectionContext)state).CancelConnectionClosedToken(), this);
        }

        // Invoked on thread pool to notify application that the connection is closed
        private void CancelConnectionClosedToken()
        {
            _connectionClosedTokenSource.Cancel();
            _waitForConnectionClosedTcs.SetResult(null);
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            _threadContext.ScheduleAsyncAbort(Socket, abortReason);
        }

        public override async ValueTask DisposeAsync()
        {
            Transport.Input.Complete();
            Transport.Output.Complete();

            Abort();

            await _waitForConnectionClosedTcs.Task;
            _connectionClosedTokenSource.Dispose();

            if (_iovecHandle.IsAllocated)
                _iovecHandle.Free();
        }

        internal class DuplexPipe : IDuplexPipe
        {
            public DuplexPipe(PipeReader reader, PipeWriter writer)
            {
                Input = reader;
                Output = writer;
            }

            public PipeReader Input { get; }

            public PipeWriter Output { get; }

            public static DuplexPipePair CreateConnectionPair(PipeOptions inputOptions, PipeOptions outputOptions)
            {
                var input = new Pipe(inputOptions);
                var output = new Pipe(outputOptions);

                var transportToApplication = new DuplexPipe(output.Reader, input.Writer);
                var applicationToTransport = new DuplexPipe(input.Reader, output.Writer);

                return new DuplexPipePair(applicationToTransport, transportToApplication);
            }

            // This class exists to work around issues with value tuple on .NET Framework
            public readonly struct DuplexPipePair
            {
                public IDuplexPipe Transport { get; }
                public IDuplexPipe Application { get; }

                public DuplexPipePair(IDuplexPipe transport, IDuplexPipe application)
                {
                    Transport = transport;
                    Application = application;
                }
            }
        }
    }
}