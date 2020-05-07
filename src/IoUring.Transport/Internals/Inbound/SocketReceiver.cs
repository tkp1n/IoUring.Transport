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
            Debug.WriteLine($"Adding poll to receive sockets via {socket}");
            ring.PreparePollAdd(socket, (ushort) POLLIN, AsyncOperation.RecvSocketPoll(socket).AsUlong());
        }

        public void CompleteReceivePoll(Ring ring, int result)
        {
            if (result >= 0)
            {
                Debug.WriteLine($"Completed poll to receive socket via {(int)_recipient}");
                Receive(ring);
            }
            else
            {
                var err = -result;
                if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
                {
                    Debug.WriteLine("Polled to receive socket for nothing");
                    PollReceive(ring);
                }
                else if (err == ECONNRESET && _disposed)
                {
                    Debug.WriteLine("Poll to receive socket was cancelled");
                }
                else
                {
                    throw new ErrnoException(err);
                }
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
            Debug.WriteLine($"Adding recvmsg to receive socket via {socket}");
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
            Debug.WriteLine($"Adding recvmsg to receive socket via {recipient}");

            result = recipient.RecvMsg(header, (MSG_NOSIGNAL | MSG_CMSG_CLOEXEC));
            // End of work-around for https://github.com/axboe/liburing/issues/128

            connection = default;
            if (result >= 0)
            {
                bool receivedSocket = false;
                LinuxSocket socket = default;
                for (cmsghdr* cmsg = CMSG_FIRSTHDR(header); cmsg != null; cmsg = CMSG_NXTHDR(header, cmsg))
                {
                    if (cmsg->cmsg_level == SOL_SOCKET && cmsg->cmsg_type == SCM_RIGHTS)
                    {
                        Debug.WriteLine($"Received socket from {(int) _recipient}");
                        int* fdptr = (int*)CMSG_DATA(cmsg);
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
                    else
                    {
                        Debug.WriteLine($"Socket recipient {(int) _recipient} closed");
                    }

                    return false;
                }

                connection = new InboundConnection(socket, _endPoint, null, _memoryPool, _options, _scheduler);
                return true;
            }

            int err = -result;
            if (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)
            {
                Debug.WriteLine("Recevied socket for nothing");
                Receive(ring);
            }
            else if (err == ECONNRESET && _disposed)
            {
                Debug.WriteLine("Receiving socket was cancelled");
            }
            else
            {
                throw new ErrnoException(err);
            }

            return false;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}