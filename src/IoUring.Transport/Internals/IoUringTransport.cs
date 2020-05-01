using System;
using System.Diagnostics;
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
        private AcceptThread _acceptThread;
        private int _refCount;
        private readonly IoUringOptions _options;
        private readonly ILoggerFactory _loggerFactory;

        public IoUringTransport(IOptions<IoUringOptions> options, ILoggerFactory loggerFactory)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            Limits.SetToMax(Resource.RLIMIT_NOFILE);

            var threads = new TransportThread[_options.ThreadCount];
            for (int i = 0; i < threads.Length; i++)
            {
                var thread = new TransportThread(_options);
                thread.Run();
                threads[i] = thread;
            }

            TransportThreads = threads;
        }

        public TransportThread[] TransportThreads { get; private set; }

        public AcceptThread AcceptThread => LazyInitializer.EnsureInitialized(ref _acceptThread, ref _lock, () => CreateAcceptThread());

        private AcceptThread CreateAcceptThread()
        {
            Debug.Assert(Monitor.IsEntered(_lock));
            if (_refCount == Disposed) throw new ObjectDisposedException(nameof(IoUringTransport));

            var thread = new AcceptThread(_options, TransportThreads);
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
                transportThreads = TransportThreads;
                TransportThreads = null;
                acceptThread = _acceptThread;
                _acceptThread = null;
            }

            Debug.WriteLine("Disposing IoUringTransport");

            if (transportThreads != null)
            {
                foreach (var transportThread in transportThreads)
                {
                    await transportThread.DisposeAsync();
                }
            }

            if (acceptThread != null)
                await acceptThread.DisposeAsync();
        }
    }
}