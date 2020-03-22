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
    internal abstract class IoUringConnectionContext : TransportConnection
    {
        private const int ReadCancelled = 0x1;
        private const int WriteCancelled = 0x2;
        private const int HalfClosed = 0x20;
        private const int BothClosed = 0x40;

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
        private int _flags;

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

        private object Gate => this;

        public LinuxSocket Socket { get; }

        public override MemoryPool<byte> MemoryPool { get; }

        public unsafe iovec* ReadVecs => _iovec;
        public unsafe iovec* WriteVecs => _iovec + ReadIOVecCount;

        public MemoryHandle[] ReadHandles { get; } = new MemoryHandle[ReadIOVecCount];
        public MemoryHandle[] WriteHandles { get; } = new MemoryHandle[WriteIOVecCount];

        public PipeWriter Input => Application.Output;

        public PipeReader Output => Application.Input;

        public ReadOnlySequence<byte> ReadResult { get; private set; }
        public ReadOnlySequence<byte> LastWrite { get; set; }

        public bool FlushAsync()
        {
            var awaiter = Input.FlushAsync().GetAwaiter();
            _flushResultAwaiter = awaiter;
            if (awaiter.IsCompleted)
            {
                return HandleFlushedToApp(false);
            }

            awaiter.UnsafeOnCompleted(_onOnFlushedToApp);
            return false;
        }

        private bool HandleFlushedToApp(bool async = true)
        {
            try
            {
                var result = _flushResultAwaiter.GetResult();
                if (result.IsCompleted || result.IsCanceled)
                {
                    CompleteInput(null);
                    return false;
                }
            }
            catch (Exception ex)
            {
                CompleteInput(ex);
                return false;
            }

            if (async)
            {
                Debug.WriteLine($"Flushed to app for {(int)Socket} asynchronously");
                _threadContext.ScheduleAsyncRead(Socket);
            }

            return true;
        }

        public bool ReadAsync()
        {
            var awaiter = Output.ReadAsync().GetAwaiter();
            _readResultAwaiter = awaiter;
            if (awaiter.IsCompleted)
            {
                return HandleReadFromApp(false);
            }

            awaiter.UnsafeOnCompleted(_onReadFromApp);
            return false;
        }

        private bool HandleReadFromApp(bool async = true)
        {
            try
            {
                var result = _readResultAwaiter.GetResult();
                var buffer = result.Buffer;
                ReadResult = buffer;
                if ((buffer.IsEmpty && result.IsCompleted) || result.IsCanceled)
                {
                    CompleteOutput(null);
                    return false;
                }
            }
            catch (Exception ex)
            {
                CompleteOutput(ex);
                return false;
            }

            if (async)
            {
                Debug.WriteLine($"Read from app for {(int)Socket} asynchronously");
                _threadContext.ScheduleAsyncWrite(Socket);
            }

            return true;
        }

        public void CompleteInput(Exception error)
        {
            Input.Complete(error);
            CleanupSocketEnd();
        }

        public void CompleteOutput(Exception error)
        {
            Output.Complete(error);
            CancelReadFromSocket();
            CleanupSocketEnd();
        }

        public void CleanupSocketEnd()
        {
            lock (Gate)
            {
                if ((_flags & HalfClosed) == 0)
                {
                    _flags |= HalfClosed;
                    return;
                }

                _flags |= BothClosed;
            }

            _threadContext.ScheduleAsyncClose(Socket);
        }

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

        private void CancelReadFromSocket()
        {
            lock (Gate)
            {
                if ((_flags & ReadCancelled) != 0)
                {
                    return;
                }

                _flags |= ReadCancelled;
            }

            CompleteInput(new ConnectionAbortedException());
        }

        private void CancelWriteToSocket()
        {
            lock (Gate)
            {
                if ((_flags & WriteCancelled) != 0)
                {
                    return;
                }

                _flags |= WriteCancelled;
            }

            CompleteOutput(null);
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            Output.CancelPendingRead();
            CancelWriteToSocket();
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