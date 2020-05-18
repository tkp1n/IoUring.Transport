using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal class Scheduler
    {
        public unsafe static void SetCurrentThreadAffinity(int cpuId)
        {
            cpu_set_t cpu_set;
            CPU_ZERO(&cpu_set);
            CPU_SET(cpuId, &cpu_set);

            int rv = sched_setaffinity(0, SizeOf.cpu_set_t, &cpu_set);
            if (rv != 0) ThrowHelper.ThrowNewErrnoException();
        }
    }
}