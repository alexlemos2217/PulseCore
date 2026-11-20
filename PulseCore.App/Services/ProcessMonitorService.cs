using System.Diagnostics;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

public sealed class ProcessMonitorService
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "csrss", "lsass", "PulseCore.App", "PulseCore",
        "Registry", "Memory Compression", "smss", "wininit", "services", "winlogon"
    };

    public IReadOnlyList<ProcessMemInfo> GetTopByMemory(int count = 5)
    {
        var list = new List<ProcessMemInfo>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception) { return list; }

        foreach (var p in all)
        {
            try
            {
                if (p.Id <= 4 || Excluded.Contains(p.ProcessName)) continue;
                list.Add(new ProcessMemInfo(p.ProcessName, p.Id, Math.Round(p.WorkingSet64 / 1048576.0, 1)));
            }
            catch (Exception) { /* proceso inaccesible o ya terminado */ }
            finally { p.Dispose(); }
        }

        return list.OrderByDescending(x => x.MemoryMb).Take(count).ToList();
    }
}
