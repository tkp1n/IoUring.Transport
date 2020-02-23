using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Tmds.Linux;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed unsafe class AcceptSocketContext : IAsyncDisposable
    {
        private sockaddr_storage* _addr;
        private GCHandle _addrHandle;

        private socklen_t* _addLen;
        private GCHandle _addrLenHandle;

        public AcceptSocketContext(LinuxSocket socket, IPEndPoint endPoint, ChannelWriter<ConnectionContext> acceptQueue)
        {
            EndPoint = endPoint;
            AcceptQueue = acceptQueue;
            Socket = socket;

            sockaddr_storage storage = default;
            var addrHandle = GCHandle.Alloc(storage, GCHandleType.Pinned);
            _addr = (sockaddr_storage*) addrHandle.AddrOfPinnedObject();
            _addrHandle = addrHandle;

            socklen_t addrLen = SizeOf.sockaddr_storage;
            var addrLenHandle = GCHandle.Alloc(addrLen, GCHandleType.Pinned);
            _addLen = (socklen_t*) addrLenHandle.AddrOfPinnedObject();
            _addrLenHandle = addrLenHandle;
        }

        public LinuxSocket Socket { get; }
        public IPEndPoint EndPoint { get; }
        public ChannelWriter<ConnectionContext> AcceptQueue { get; }
        public sockaddr_storage* Addr => _addr;
        public socklen_t* AddrLen => _addLen;

        public ValueTask DisposeAsync()
        {
            if (_addrHandle.IsAllocated)
                _addrHandle.Free();
            if (_addrLenHandle.IsAllocated)
                _addrLenHandle.Free();

            return default;
        }
    }
}