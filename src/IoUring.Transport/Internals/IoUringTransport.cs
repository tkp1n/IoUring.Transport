using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IoUring.Transport.Internals.Inbound;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IoUring.Transport.Internals
{
    internal sealed class IoUringTransport : IAsyncDisposable
    {
        private const int Disposed = -1;

        private object _lock = new object();
        private TransportThread[] _transportThreads;
        private AcceptThread _acceptThread;
        private int _refCount;

        public IoUringTransport(IOptions<IoUringOptions> options, ILoggerFactory loggerFactory)
        {
            Options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            Limits.SetToMax(Resource.RLIMIT_NOFILE);
        }

        public IoUringOptions Options { get; }
        public ILoggerFactory LoggerFactory { get; }
        public TransportThread[] TransportThreads => LazyInitializer.EnsureInitialized(ref _transportThreads, ref _lock, () => CreateTransportThreads());

        private TransportThread[] CreateTransportThreads()
        {
            Debug.Assert(Monitor.IsEntered(_lock));
            if (_refCount == Disposed) throw new ObjectDisposedException(nameof(IoUringTransport));

            var threads = new TransportThread[Options.ThreadCount];
            for (int i = 0; i < threads.Length; i++)
            {
                var thread = new TransportThread(Options);
                thread.Run();
                threads[i] = thread;
            }

            return threads;
        }

        public AcceptThread AcceptThread => LazyInitializer.EnsureInitialized(ref _acceptThread, ref _lock, () => CreateAcceptThread());

        private AcceptThread CreateAcceptThread()
        {
            Debug.Assert(Monitor.IsEntered(_lock));
            if (_refCount == Disposed) throw new ObjectDisposedException(nameof(IoUringTransport));

            var schedulers = TransportThreads.Select(t => t.Scheduler).ToArray();

            var thread = new AcceptThread(Options, schedulers);
            thread.Run();
            return thread;
        }

        public void IncrementThreadRefCount()
        {
            lock (_lock)
            {
                if (_refCount == Disposed) throw new ObjectDisposedException(nameof(IoUringTransport));
                _refCount++;
            }
        }

        public void DecrementThreadRefCount()
        {
            lock (_lock)
            {
                if (_refCount == Disposed) throw new ObjectDisposedException(nameof(IoUringTransport));
                _refCount--;
            }
        }

        public async ValueTask DisposeAsync()
        {
            TransportThread[] transportThreads;
            AcceptThread acceptThread;

            lock (_lock)
            {
                if (_refCount == Disposed) return;
                if ( _refCount != 0) throw new InvalidOperationException();
                _refCount = Disposed;
                transportThreads = _transportThreads;
                acceptThread = _acceptThread;
            }

            if (transportThreads != null)
            {
                foreach (var transportThread in transportThreads)
                {
                    await transportThread.DisposeAsync();
                }
            }

            if (acceptThread != null)
                await _acceptThread.DisposeAsync();
        }
    }
}