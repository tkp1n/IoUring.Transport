using System.Runtime.InteropServices;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal enum Resource : int
    {
        RLIMIT_NOFILE = 7
    }

    internal static class Limits
    {
        private const string LibC = "libc.so.6";

        [StructLayout(LayoutKind.Sequential)]
        struct rlimit
        {
            public long rlim_cur;
            public long rlim_max;
        }

        [DllImport(LibC, SetLastError = true)]
        private static extern unsafe int getrlimit(int resource, rlimit* rlp);

        [DllImport(LibC, SetLastError = true)]
        private static extern unsafe int setrlimit(int resource, rlimit* rlp);

        public static unsafe void SetToMax(Resource resource)
        {
            rlimit rlp = default;
            if (getrlimit((int) resource, &rlp) != 0) throw new ErrnoException(errno);
            rlp.rlim_cur = rlp.rlim_max;
            if (setrlimit((int) resource, &rlp) != 0) throw new ErrnoException(errno);
        }
    }
}