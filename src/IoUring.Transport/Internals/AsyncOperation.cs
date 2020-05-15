using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IoUring.Transport
{
    [Flags]
    internal enum OperationType : uint
    {
        Read                = 1 << 0,
        ReadPoll            = 1 << 1,
        Write               = 1 << 2,
        WritePoll           = 1 << 3,
        EventFdReadPoll     = 1 << 4,
        EventFdRead         = 1 << 5,
        Connect             = 1 << 6,
        AcceptPoll          = 1 << 7,
        Accept              = 1 << 8,
        CompleteInbound     = 1 << 9,
        CompleteOutbound    = 1 << 10,
        CancelGeneric       = 1 << 11,
        CancelAccept        = 1 << 12,
        Abort               = 1 << 13,
        CloseConnection     = 1 << 14,
        CloseAcceptSocket   = 1 << 15,
        Unbind              = 1 << 16,
        RecvSocketPoll      = 1 << 17,
        RecvSocket          = 1 << 18,
        Add                 = 1 << 19,
        AddAndAccept        = Add | Accept,
        AddAndConnect       = Add | Connect,
        EventFdOperation    = EventFdReadPoll | EventFdRead
    }

    [StructLayout(LayoutKind.Explicit)]
    internal readonly struct AsyncOperation
    {
        [FieldOffset(0)]
        private readonly int _socket;
        [FieldOffset(4)]
        private readonly OperationType _operation;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private AsyncOperation(int socket, OperationType operation)
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

        public static AsyncOperation ReadFrom(int fd) => new AsyncOperation(fd, OperationType.Read);
        public static AsyncOperation ReadPollFor(int fd) => new AsyncOperation(fd, OperationType.ReadPoll);
        public static AsyncOperation WriteTo(int fd) => new AsyncOperation(fd, OperationType.Write);
        public static AsyncOperation WritePollFor(int fd) => new AsyncOperation(fd, OperationType.WritePoll);
        public static AsyncOperation PollEventFd(int eventFd) => new AsyncOperation(eventFd, OperationType.EventFdReadPoll);
        public static AsyncOperation ReadEventFd(int eventFd) => new AsyncOperation(eventFd, OperationType.EventFdRead);
        public static AsyncOperation AddAndConnect(int fd) => new AsyncOperation(fd, OperationType.AddAndConnect);
        public static AsyncOperation ConnectOn(int fd) => new AsyncOperation(fd, OperationType.Connect);
        public static AsyncOperation PollAcceptFrom(int fd) => new AsyncOperation(fd, OperationType.AcceptPoll);
        public static AsyncOperation AddAndAccept(int fd) => new AsyncOperation(fd, OperationType.AddAndAccept);
        public static AsyncOperation AcceptFrom(int fd) => new AsyncOperation(fd, OperationType.Accept);
        public static AsyncOperation CompleteInboundOf(int fd) => new AsyncOperation(fd, OperationType.CompleteInbound);
        public static AsyncOperation CompleteOutboundOf(int fd) => new AsyncOperation(fd, OperationType.CompleteOutbound);
        public static AsyncOperation CancelGeneric(int fd) => new AsyncOperation(fd, OperationType.CancelGeneric);
        public static AsyncOperation CancelAccept(int fd) => new AsyncOperation(fd, OperationType.CancelAccept);
        public static AsyncOperation Abort(int fd) => new AsyncOperation(fd, OperationType.Abort);
        public static AsyncOperation UnbindFrom(int fd) => new AsyncOperation(fd, OperationType.Unbind);
        public static AsyncOperation CloseConnection(int fd) => new AsyncOperation(fd, OperationType.CloseConnection);
        public static AsyncOperation CloseAcceptSocket(int fd) => new AsyncOperation(fd, OperationType.CloseAcceptSocket);
        public static AsyncOperation RecvSocketPoll(int fd) => new AsyncOperation(fd, OperationType.RecvSocketPoll);
        public static AsyncOperation RecvSocket(int fd) => new AsyncOperation(fd, OperationType.RecvSocket);
    }
}