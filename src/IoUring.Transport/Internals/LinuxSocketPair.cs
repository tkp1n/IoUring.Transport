using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal readonly struct LinuxSocketPair
    {
        public unsafe LinuxSocketPair(int domain, int type, int protocol, bool blocking)
        {
            int* sv = stackalloc int[2];

            type |= SOCK_CLOEXEC;

            if (!blocking)
            {
                type |= SOCK_NONBLOCK;
            }

            int rv = socketpair(domain, type, protocol, sv);
            if (rv != 0)
            {
                throw new ErrnoException(errno);
            }

            Socket1 = sv[0];
            Socket2 = sv[1];
        }

        public LinuxSocket Socket1 { get; }
        public LinuxSocket Socket2 { get; }

        public void Close()
        {
            Socket1.Close();
            Socket2.Close();
        }
    }
}