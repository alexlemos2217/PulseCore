using System.Diagnostics;
using System.Runtime.InteropServices;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>
/// Reduce el working set de procesos accesibles (EmptyWorkingSet). No cierra ningún programa:
/// las páginas pasan a la lista en espera y Windows las recupera bajo demanda.
/// </summary>
public sealed class MemoryOptimizerService
{
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "smss", "csrss", "wininit",
        "services", "lsass", "winlogon", "Secure System"
    };

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public RamOptimizeResult Optimize(int? excludePid = null)
    {
        var sw = Stopwatch.StartNew();
        var probe = new FallbackProbe();
        var before = (probe.ReadRam()?.UsedGb ?? 0) * 1024.0;

        var trimmed = 0;
        var self = Environment.ProcessId;
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception) { all = []; }

        foreach (var p in all)
        {
            try
            {
                if (p.Id <= 4 || p.Id == self || p.Id == excludePid || Skip.Contains(p.ProcessName)) continue;

                var h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue; // protegido o sin acceso
                try
                {
                    if (EmptyWorkingSet(h)) trimmed++;
                }
                finally { CloseHandle(h); }
            }
            catch (Exception)
            {
                // Proceso terminó o es inaccesible: se ignora.
            }
            finally
            {
                p.Dispose();
            }
        }

        Thread.Sleep(450); // deja que el SO actualice contadores
        var after = (probe.ReadRam()?.UsedGb ?? 0) * 1024.0;
        sw.Stop();

        return new RamOptimizeResult(
            Math.Round(Math.Max(0, before - after), 1),
            trimmed,
            sw.ElapsedMilliseconds,
            Math.Round(before, 1),
            Math.Round(after, 1));
    }
}
