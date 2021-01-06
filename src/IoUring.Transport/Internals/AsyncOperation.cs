using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IoUring.Transport
{
    [Flags]
    internal enum OperationType : uint
    {
        Read                   = 1 << 0,
        ReadPoll               = 1 << 1,
        Write                  = 1 << 2,
        WritePoll              = 1 << 3,
        EventFdReadPoll        = 1 << 4,
        EventFdRead            = 1 << 5,
        EventFdOperation       = EventFdReadPoll | EventFdRead,
        Connect                = 1 << 6,
        WritePollDuringConnect = 1 << 7,
        AcceptPoll             = 1 << 8,
        Accept                 = 1 << 9,
        CompleteInbound        = 1 << 10,
        CompleteOutbound       = 1 << 11,
        CancelGeneric          = 1 << 12,
        CancelAccept           = CancelGeneric | Accept,
        CancelRead             = CancelGeneric | Read,
        CancelReadPoll         = CancelGeneric | ReadPoll,
        CancelWrite            = CancelGeneric | Write,
        CancelWritePoll        = CancelGeneric | WritePoll,
        Abort                  = 1 << 13,
        CloseConnection        = 1 << 14,
        CloseAcceptSocket      = 1 << 15,
        Unbind                 = 1 << 16,
        RecvSocketPoll         = 1 << 17,
        RecvSocket             = 1 << 18,
        Add                    = 1 << 19,
        AddAndAcceptPoll       = Add | AcceptPoll,
        AddAndConnect          = Add | Connect,
    }

    [StructLayout(LayoutKind.Explicit)]
    internal readonly struct AsyncOperation
    {
        [FieldOffset(0)]
        private readonly int _socket;
        [FieldOffset(4)]
        private readonly OperationType _operation;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AsyncOperation(int socket, OperationType operation)
        {
            _socket = socket;
            _operation = operation;
        }

        public void Deconstruct(out int socket, out OperationType operation)
        {
            socket = _socket;
            operation = _operation;
        }

        public int Socket => -_socket;
        public OperationType Operation => _operation;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong AsUlong() => Unsafe.As<AsyncOperation, ulong>(ref Unsafe.AsRef(this));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AsyncOperation FromUlong(ulong value)
            => Unsafe.As<ulong, AsyncOperation>(ref Unsafe.AsRef(value));

        public static AsyncOperation ReadFrom(int fd) => new(fd, OperationType.Read);
        public static AsyncOperation ReadPollFor(int fd) => new(fd, OperationType.ReadPoll);
        public static AsyncOperation WriteTo(int fd) => new(fd, OperationType.Write);
        public static AsyncOperation WritePollFor(int fd) => new(fd, OperationType.WritePoll);
        public static AsyncOperation PollEventFd(int eventFd) => new(eventFd, OperationType.EventFdReadPoll);
        public static AsyncOperation ReadEventFd(int eventFd) => new(eventFd, OperationType.EventFdRead);
        public static AsyncOperation AddAndConnect(int fd) => new(fd, OperationType.AddAndConnect);
        public static AsyncOperation ConnectOn(int fd) => new(fd, OperationType.Connect);
        public static AsyncOperation WritePollDuringConnect(int fd) => new(fd, OperationType.WritePollDuringConnect);
        public static AsyncOperation PollAcceptFrom(int fd) => new(fd, OperationType.AcceptPoll);
        public static AsyncOperation AddAndAcceptPoll(int fd) => new(fd, OperationType.AddAndAcceptPoll);
        public static AsyncOperation AcceptFrom(int fd) => new(fd, OperationType.Accept);
        public static AsyncOperation CompleteInboundOf(int fd) => new(fd, OperationType.CompleteInbound);
        public static AsyncOperation CompleteOutboundOf(int fd) => new(fd, OperationType.CompleteOutbound);
        public static AsyncOperation CancelOperation(OperationType op, int fd) => new(fd, OperationType.CancelGeneric | op);
        public static AsyncOperation CancelAccept(int fd) => new(fd, OperationType.CancelAccept);
        public static AsyncOperation Abort(int fd) => new(fd, OperationType.Abort);
        public static AsyncOperation UnbindFrom(int fd) => new(fd, OperationType.Unbind);
        public static AsyncOperation CloseConnection(int fd) => new(fd, OperationType.CloseConnection);
        public static AsyncOperation CloseAcceptSocket(int fd) => new(fd, OperationType.CloseAcceptSocket);
        public static AsyncOperation RecvSocketPoll(int fd) => new(fd, OperationType.RecvSocketPoll);
        public static AsyncOperation RecvSocket(int fd) => new(fd, OperationType.RecvSocket);
    }
}
