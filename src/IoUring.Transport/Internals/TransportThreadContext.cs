using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal sealed class TransportThreadContext
    {
        private const int True = 1;
        private const int False = 0;

        private readonly int _eventFd;
        private readonly ConcurrentQueue<ulong> _asyncOperationQueue;
        private readonly ConcurrentDictionary<int, object> _asyncOperationStates;
        private int _blockingMode;

        public TransportThreadContext(IoUringOptions options, MemoryPool<byte> memoryPool, int eventFd, ConcurrentQueue<ulong> asyncOperationQueue, ConcurrentDictionary<int, object> asyncOperationStates)
        {
            Options = options;
            MemoryPool = memoryPool;
            _eventFd = eventFd;
            _asyncOperationQueue = asyncOperationQueue;
            _asyncOperationStates = asyncOperationStates;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlockingMode(bool blocking)
            => Volatile.Write(ref _blockingMode, blocking ? True : False);

        public IoUringOptions Options { get; }

        public MemoryPool<byte> MemoryPool { get; }

        public void ScheduleAsyncAccept(int socket, object state)
        {
            _asyncOperationStates[socket] = state;
            _asyncOperationQueue.Enqueue(TransportThread.Mask(socket, TransportThread.AcceptMask));
            Notify();
        }

        public void ScheduleAsyncConnect(int socket, object state)
        {
            _asyncOperationStates[socket] = state;
            _asyncOperationQueue.Enqueue(TransportThread.Mask(socket, TransportThread.ConnectMask));
            Notify();
        }

        public void ScheduleAsyncRead(int socket)
        {
            _asyncOperationQueue.Enqueue(TransportThread.Mask(socket, TransportThread.ReadPollMask));
            Notify();
        }

        public void ScheduleAsyncWrite(int socket)
        {
            _asyncOperationQueue.Enqueue(TransportThread.Mask(socket, TransportThread.WritePollMask));
            Notify();
        }

        public void ScheduleAsyncClose(int socket, bool onTransportThread)
        {
            _asyncOperationQueue.Enqueue(TransportThread.Mask(socket, TransportThread.CloseMask));
            if (!onTransportThread)
                Notify();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ShouldUnblock()
            => Interlocked.CompareExchange(ref _blockingMode, False, True) == True;

        private unsafe void Notify()
        {
            if (!ShouldUnblock())
            {
                // If the transport thread is not (yet) in blocking mode, we have the guarantee, that it will read
                // from the queues one more time before actually blocking. Therefore, it is safe not to notify now.
                return;
            }

            // The transport thread reported it is (probably still) blocking. We therefore must notify it by writing
            // to the eventfd established for that purpose.

            Debug.WriteLine("Attempting to unblock thread");

            byte* val = stackalloc byte[sizeof(ulong)];
            Unsafe.WriteUnaligned(val, 1UL);
            int rv;
            do
            {
                rv = (int) write(_eventFd, val, sizeof(ulong));
            } while (rv == -1 && errno == EINTR);
        }
    }
}