using System.Buffers;
using System.Net;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed class InboundConnection : IoUringConnection
    {
        public InboundConnection(LinuxSocket socket, EndPoint local, EndPoint remote, MemoryPool<byte> memoryPool, IoUringOptions options, TransportThreadScheduler scheduler)
            : base(socket, local, remote, memoryPool, options, scheduler) { }
    }
}