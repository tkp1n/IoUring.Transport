using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IoUring.Transport
{
    internal enum OperationType : uint
    {
        Read,
        ReadPoll,
        Write,
        WritePoll,
        EventFdReadPoll,
        EventFdRead,
        Connect,
        Bind,
        Accept,
        CompleteInbound,
        CompleteOutbound,
        CancelGeneric,
        CancelAccept,
        Abort,
        CloseConnection,
        CloseAcceptSocket,
        Unbind,
        Transfer
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
        public static AsyncOperation ConnectOn(int fd) => new AsyncOperation(fd, OperationType.Connect);
        public static AsyncOperation BindTo(int fd) => new AsyncOperation(fd, OperationType.Bind);
        public static AsyncOperation AcceptFrom(int fd) => new AsyncOperation(fd, OperationType.Accept);
        public static AsyncOperation CompleteInboundOf(int fd) => new AsyncOperation(fd, OperationType.CompleteInbound);
        public static AsyncOperation CompleteOutboundOf(int fd) => new AsyncOperation(fd, OperationType.CompleteOutbound);
        public static AsyncOperation CancelGeneric(int fd) => new AsyncOperation(fd, OperationType.CancelGeneric);
        public static AsyncOperation CancelAccept(int fd) => new AsyncOperation(fd, OperationType.CancelAccept);
        public static AsyncOperation Abort(int fd) => new AsyncOperation(fd, OperationType.Abort);
        public static AsyncOperation UnbindFrom(int fd) => new AsyncOperation(fd, OperationType.Unbind);
        public static AsyncOperation CloseConnection(int fd) => new AsyncOperation(fd, OperationType.CloseConnection);
        public static AsyncOperation CloseAcceptSocket(int fd) => new AsyncOperation(fd, OperationType.CloseAcceptSocket);
        public static AsyncOperation Transfer() => new AsyncOperation(0, OperationType.Transfer);
    }
}