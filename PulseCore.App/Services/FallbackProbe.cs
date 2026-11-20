using System.Runtime.InteropServices;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>Métricas nativas de Windows (sin drivers) para CPU% y RAM cuando LibreHardwareMonitor no responde.</summary>
internal sealed class FallbackProbe
{
    public static string CpuName =>
        Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrev;

    public double? ReadCpuUsage()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
            var i = ToU64(idle); var k = ToU64(kernel); var u = ToU64(user);
            double? result = null;
            if (_hasPrev)
            {
                var dIdle = i - _prevIdle;
                var dTotal = (k - _prevKernel) + (u - _prevUser); // kernel incluye idle
                if (dTotal > 0) result = Math.Clamp((1.0 - (double)dIdle / dTotal) * 100.0, 0, 100);
            }
            (_prevIdle, _prevKernel, _prevUser, _hasPrev) = (i, k, u, true);
            return result;
        }
        catch (Exception) { return null; }
    }

    public RamInfo? ReadRam()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m)) return null;
            const double gb = 1024.0 * 1024 * 1024;
            var total = m.ullTotalPhys / gb;
            var used = (m.ullTotalPhys - m.ullAvailPhys) / gb;
            return new RamInfo(Math.Round(used, 2), Math.Round(total, 2),
                total > 0 ? Math.Round(used / total * 100, 1) : 0);
        }
        catch (Exception) { return null; }
    }

    private static ulong ToU64(FILETIME f) => ((ulong)f.dwHighDateTime << 32) | f.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
