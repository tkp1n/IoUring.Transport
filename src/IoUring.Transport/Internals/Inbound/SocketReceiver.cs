using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed unsafe class SocketReceiver : IDisposable
    {
        private readonly LinuxSocket _recipient;
        private readonly EndPoint _endPoint;
        private readonly MemoryPool<byte> _memoryPool;
        private readonly IoUringOptions _options;
        private readonly TransportThreadScheduler _scheduler;
        private readonly byte[] _dummyBuffer = GC.AllocateUninitializedArray<byte>(1, pinned: true);
        private readonly byte[] _iov = GC.AllocateUninitializedArray<byte>(SizeOf.iovec, pinned: true);
        private readonly byte[] _control = GC.AllocateUninitializedArray<byte>(CMSG_SPACE(sizeof(int)), pinned: true);
        private readonly byte[] _header = GC.AllocateUninitializedArray<byte>(SizeOf.msghdr, pinned: true);
        private volatile bool _disposed;

        public SocketReceiver(LinuxSocket recipient, ChannelWriter<ConnectionContext> acceptQueue, EndPoint endPoint, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
        {
            _recipient = recipient;
            AcceptQueue = acceptQueue;
            _endPoint = endPoint;
            _memoryPool = memoryPool;
            _options = options;
            _scheduler = scheduler;
        }

        public ChannelWriter<ConnectionContext> AcceptQueue { get; }

        public void PollReceive(Ring ring)
        {
            int socket = _recipient;
#if TRACE_IO_URING
            Trace.WriteLine($"Adding poll to receive sockets via {socket}");
#endif
            ring.PreparePollAdd(socket, (ushort) POLLIN, AsyncOperation.RecvSocketPoll(socket).AsUlong());
        }

        public void CompleteReceivePoll(Ring ring, int result)
        {
            if (result < 0)
            {
                HandleCompleteReceivePollError(ring, result);
                return;
            }

#if TRACE_IO_URING
                Trace.WriteLine($"Completed poll to receive socket via {(int)_recipient}");
#endif
            Receive(ring);
        }

        private void HandleCompleteReceivePollError(Ring ring, int result)
        {
            var err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
#if TRACE_IO_URING
                Trace.WriteLine("Polled to receive socket for nothing");
#endif
                PollReceive(ring);
            }
            else if (err == ECONNRESET && _disposed)
            {
#if TRACE_IO_URING
                Trace.WriteLine("Poll to receive socket was cancelled");
#endif
            }
            else
            {
                ThrowHelper.ThrowNewErrnoException(err);
            }
        }

        public void Receive(Ring ring)
        {
            // Start work-around for https://github.com/axboe/liburing/issues/128
            /*
            _iov.AsSpan().Clear();
            _dummyBuffer.AsSpan().Clear();
            _control.AsSpan().Clear();
            _header.AsSpan().Clear();

            iovec* iov = (iovec*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_iov);
            iov->iov_base = (byte*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_dummyBuffer);
            iov->iov_len = 1;

            byte* control = (byte*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_control);

            msghdr* header = (msghdr*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_header);
            header->msg_iov = iov;
            header->msg_iovlen = 1;
            header->msg_control = control;
            header->msg_controllen = _control.Length;

            int socket = _recipient;
#if TRACE_IO_URING
            Trace.WriteLine($"Adding recvmsg to receive socket via {socket}");
#endif
            ring.PrepareRecvMsg(socket, header, (uint)(MSG_NOSIGNAL | MSG_CMSG_CLOEXEC), AsyncOperation.RecvSocket(socket).AsUlong());
            */
            ring.PrepareNop(AsyncOperation.RecvSocket(_recipient).AsUlong());
            // End work-around for https://github.com/axboe/liburing/issues/128
        }

        public bool TryCompleteReceive(Ring ring, int result, [NotNullWhen(true)] out InboundConnection connection)
        {
            // Start work-around for https://github.com/axboe/liburing/issues/128
            _iov.AsSpan().Clear();
            _dummyBuffer.AsSpan().Clear();
            _control.AsSpan().Clear();
            _header.AsSpan().Clear();

            iovec* iov = (iovec*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_iov);
            iov->iov_base = (byte*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_dummyBuffer);
            iov->iov_len = 1;

            byte* control = (byte*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_control);

            msghdr* header = (msghdr*) MemoryHelper.UnsafeGetAddressOfPinnedArrayData(_header);
            header->msg_iov = iov;
            header->msg_iovlen = 1;
            header->msg_control = control;
            header->msg_controllen = _control.Length;

            LinuxSocket recipient = _recipient;
#if TRACE_IO_URING
            Trace.WriteLine($"Adding recvmsg to receive socket via {recipient}");
#endif

            result = recipient.RecvMsg(header, (MSG_NOSIGNAL | MSG_CMSG_CLOEXEC));
            // End of work-around for https://github.com/axboe/liburing/issues/128

            connection = default;
            if (result < 0)
            {
                HandleCompleteReceiveError(ring, result);
                return false;
            }

            bool receivedSocket = false;
            LinuxSocket socket = default;
            for (cmsghdr* cmsg = CMSG_FIRSTHDR(header); cmsg != null; cmsg = CMSG_NXTHDR(header, cmsg))
            {
                if (cmsg->cmsg_level == SOL_SOCKET && cmsg->cmsg_type == SCM_RIGHTS)
                {
#if TRACE_IO_URING
                        Trace.WriteLine($"Received socket from {(int) _recipient}");
#endif
                    int* fdptr = (int*) CMSG_DATA(cmsg);
                    socket = *fdptr;
                    socket.SetFlag(O_NONBLOCK);

                    receivedSocket = true;
                    break;
                }
            }

            if (!receivedSocket)
            {
                if (result != 0)
                {
                    PollReceive(ring);
                }
#if TRACE_IO_URING
                else
                {
                    Trace.WriteLine($"Socket recipient {(int) _recipient} closed");
                }
#endif

                return false;
            }

            connection = new InboundConnection(socket, _endPoint, null, _memoryPool, _options, _scheduler);
            return true;
        }

        private void HandleCompleteReceiveError(Ring ring, int result)
        {
            int err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
#if TRACE_IO_URING
                Trace.WriteLine("Recevied socket for nothing");
#endif
                Receive(ring);
            }
            else if (err == ECONNRESET && _disposed)
            {
#if TRACE_IO_URING
                Trace.WriteLine("Receiving socket was cancelled");
#endif
            }
            else
            {
                ThrowHelper.ThrowNewErrnoException(err);
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}