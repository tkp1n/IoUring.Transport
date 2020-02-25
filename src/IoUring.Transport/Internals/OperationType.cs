using System;

namespace IoUring.Transport.Internals
{
    [Flags]
    internal enum OperationType : uint
    {
        Read        = 1,
        Write       = 1 << 1,
        EventFdPoll = 1 << 2,
        Connect     = 1 << 3,
        Accept      = 1 << 4
    }
}