using System.Collections.Generic;
using System.IO;

namespace IoUring.Transport.Internals
{
    internal class CpuInfo
    {
        private class LogicalCpuInfo
        {
            public int Id;
            public string SocketId;
            public string CoreId;
        }

        private readonly static LogicalCpuInfo[] _cpuInfos = GetCpuInfos();

        private static LogicalCpuInfo[] GetCpuInfos()
        {
            var sysPath = "/sys/devices/system/cpu";
            var directories = Directory.GetDirectories(sysPath, "cpu*");
            var cpuInfos = new List<LogicalCpuInfo>();
            foreach (var directory in directories)
            {
                int id;
                if (int.TryParse(directory.Substring(sysPath.Length + 4), out id))
                {
                    var cpuInfo = new LogicalCpuInfo
                    {
                        Id = id,
                        SocketId = File.ReadAllText($"{sysPath}/cpu{id}/topology/physical_package_id").Trim(),
                        CoreId = File.ReadAllText($"{sysPath}/cpu{id}/topology/core_id").Trim()
                    };
                    cpuInfos.Add(cpuInfo);
                }
            }

            return cpuInfos.ToArray();
        }

        private static IEnumerable<string> GetSockets()
        {
            for (int i = 0; i < _cpuInfos.Length; i++)
            {
                var socket = _cpuInfos[i].SocketId;
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                {
                    if (socket == _cpuInfos[j].SocketId)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    yield return socket;
                }
            }
        }

        private static IEnumerable<string> GetCores(string socket)
        {
            for (int i = 0; i < _cpuInfos.Length; i++)
            {
                var cpuInfo = _cpuInfos[i];
                if (cpuInfo.SocketId != socket)
                {
                    continue;
                }
                var core = _cpuInfos[i].CoreId;
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                {
                    if (_cpuInfos[j].SocketId != socket)
                    {
                        continue;
                    }
                    if (core == _cpuInfos[j].CoreId)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    yield return core;
                }
            }
        }

        private static IEnumerable<int> GetCpuIds(string socket, string core)
        {
            for (int i = 0; i < _cpuInfos.Length; i++)
            {
                var cpuInfo = _cpuInfos[i];
                if (cpuInfo.SocketId != socket || cpuInfo.CoreId != core)
                {
                    continue;
                }
                yield return _cpuInfos[i].Id;
            }
        }

        public static List<int> GetPreferredCpuIds(int max)
        {
            var ids = new List<int>();
            bool found = true;
            int level = 0;
            do
            {
                found = false;
                foreach (var socket in CpuInfo.GetSockets())
                {
                    var cores = CpuInfo.GetCores(socket);
                    foreach (var core in cores)
                    {
                        var cpuIdIterator = CpuInfo.GetCpuIds(socket, core).GetEnumerator();
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
                    }
                }
                level++;
            } while (found && ids.Count < max);
            return ids;
        }
    }
}