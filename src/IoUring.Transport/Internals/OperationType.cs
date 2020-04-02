using System;

namespace IoUring.Transport.Internals
{
    [Flags]
    internal enum OperationType : uint
    {
        Read             = 1,
        ReadPoll         = 1 << 1,
        Write            = 1 << 2,
        WritePoll        = 1 << 3,
        EventFdReadPoll  = 1 << 4,
        EventFdRead      = 1 << 5,
        Connect          = 1 << 6,
        Accept           = 1 << 7,
        CompleteInbound  = 1 << 8,
        CompleteOutbound = 1 << 9,
        Cancel           = 1 << 10,
        Abort            = 1 << 11
    }
}