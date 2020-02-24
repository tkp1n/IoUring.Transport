using System;
using System.Threading;
using System.Threading.Tasks;
using IoUring.Transport.Internals.Inbound;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IoUring.Transport.Internals
{
    internal sealed class IoUringTransport : IAsyncDisposable
    {
        private object _lock;
        private TransportThread[] _transportThreads;
        private AcceptThread _acceptThread;

        public IoUringTransport(IOptions<IoUringOptions> options, ILoggerFactory loggerFactory)
        {
            Options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            Limits.SetToMax(Resource.RLIMIT_NOFILE);
        }

        public IoUringOptions Options { get; }
        public ILoggerFactory LoggerFactory { get; }
        public TransportThread[] TransportThreads => LazyInitializer.EnsureInitialized(ref _transportThreads, ref _lock, () => CreateTransportThreads());

        public AcceptThread AcceptThread => LazyInitializer.EnsureInitialized(ref _acceptThread, ref _lock, () => CreateAcceptThread());

        private TransportThread[] CreateTransportThreads()
        {
            var threads = new TransportThread[Options.ThreadCount];
            for (int i = 0; i < threads.Length; i++)
            {
                var thread = new TransportThread(Options);
                thread.Run();
                threads[i] = thread;
            }

            return threads;
        }

        private static AcceptThread CreateAcceptThread()
        {
            var thread = new AcceptThread();
            thread.Run();
            return thread;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var transportThread in _transportThreads)
            {
                await transportThread.DisposeAsync();
            }
        }
    }
}