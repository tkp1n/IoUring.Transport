using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IoUring.Transport.Internals
{
    internal static class CpuInfo
    {
        private class LogicalCpuInfo
        {
            public int Id;
            public string SocketId;
            public string CoreId;
        }

        private static readonly LogicalCpuInfo[] CpuInfos = GetCpuInfos();

        private static LogicalCpuInfo[] GetCpuInfos()
        {
            var sysPath = "/sys/devices/system/cpu";
            var directories = Directory.GetDirectories(sysPath, "cpu*");
            var cpuInfos = new List<LogicalCpuInfo>();
            foreach (var directory in directories)
            {
                if (!int.TryParse(directory.Substring(sysPath.Length + 4), out var id)) continue;

                cpuInfos.Add(new LogicalCpuInfo
                {
                    Id = id,
                    SocketId = File.ReadAllText($"{sysPath}/cpu{id}/topology/physical_package_id").Trim(),
                    CoreId = File.ReadAllText($"{sysPath}/cpu{id}/topology/core_id").Trim()
                });
            }

            return cpuInfos.ToArray();
        }

        private static IEnumerable<string> GetSockets() => CpuInfos
            .Select(x => x.SocketId)
            .Distinct();

        private static IEnumerable<string> GetCores(string socket) => CpuInfos
            .Where(x => x.SocketId == socket)
            .Select(x => x.CoreId)
            .Distinct();

        private static IEnumerable<int> GetCpuIds(string socket, string core) => CpuInfos
            .Where(cpuInfo => cpuInfo.SocketId == socket && cpuInfo.CoreId == core)
            .Select(cpuInfo => cpuInfo.Id);

        public static List<int> GetPreferredCpuIds(int max)
        {
            var ids = new List<int>();
            bool found;
            int level = 0;
            do
            {
                found = false;
                foreach (var socket in GetSockets())
                {
                    var cores = GetCores(socket);
                    foreach (var core in cores)
                    {
                        var cpuIdIterator = GetCpuIds(socket, core).GetEnumerator();
                        int d = 0;
                        while (cpuIdIterator.MoveNext())
                        {
                            if (d++ == level)
                            {
                                ids.Add(cpuIdIterator.Current);
                                found = true;
                                break;
                            }
                        }

                        cpuIdIterator.Dispose();
                    }
                }

                level++;
            } while (found && ids.Count < max);

            return ids;
        }
    }
}