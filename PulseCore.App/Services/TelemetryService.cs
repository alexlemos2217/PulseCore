using System.IO;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>Encapsula LibreHardwareMonitor y expone snapshots tipados. No es thread-safe: usar desde un solo loop.</summary>
public sealed class TelemetryService : IDisposable
{
    private readonly Computer? _computer;
    private readonly bool _isElevated;
    private readonly string? _initError;
    private readonly FallbackProbe _fallback = new();
    private bool _disposed;

    public TelemetryService()
    {
        _isElevated = DetectElevation();
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true
            };
            _computer.Open();
        }
        catch (Exception ex)
        {
            // LibreHardwareMonitor no pudo abrir drivers: seguimos con métricas nativas de Windows.
            _initError = "LibreHardwareMonitor falló al iniciar: " + ex.Message;
            try { _computer?.Close(); } catch (Exception) { }
            _computer = null;
        }
    }

    public TelemetrySnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Siempre se mide el fallback para mantener el delta de CPU actualizado.
        var fbCpu = _fallback.ReadCpuUsage();
        _fbCpuUsage = fbCpu;

        CpuInfo cpu;
        GpuInfo gpu = new("GPU no detectada", null, null, null, null);
        RamInfo ram;
        var notices = new List<string>();
        if (_initError is not null) notices.Add(_initError);

        if (_computer is null)
        {
            cpu = new CpuInfo(FallbackProbe.CpuName, Math.Round(fbCpu ?? 0, 1), null, null);
            ram = _fallback.ReadRam() ?? new RamInfo(0, 0, 0);
        }
        else
        {
            try
            {
                foreach (var hw in _computer.Hardware) SafeUpdate(hw);
                var cpuHw = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                var memHw = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
                cpu = ReadCpu(cpuHw);
                gpu = ReadGpu(PickGpu());
                ram = ReadRam(memHw);
            }
            catch (Exception ex)
            {
                notices.Add("Lectura de sensores falló, usando fallback: " + ex.Message);
                cpu = new CpuInfo(FallbackProbe.CpuName, Math.Round(fbCpu ?? 0, 1), null, null);
                ram = _fallback.ReadRam() ?? new RamInfo(0, 0, 0);
            }
        }

        return new TelemetrySnapshot(
            cpu, gpu, ram, ReadStorage(), DateTimeOffset.Now, _isElevated,
            _computer is null ? "Fallback (Windows nativo)" : "LibreHardwareMonitor",
            notices.Count > 0 ? string.Join(" | ", notices) : null);
    }

    private double? _fbCpuUsage;

    // ---------- CPU ----------
    private CpuInfo ReadCpu(IHardware? hw)
    {
        if (hw is null) return new CpuInfo(FallbackProbe.CpuName, Math.Round(_fbCpuUsage ?? 0, 1), null, null);
        var sensors = AllSensors(hw).ToList();

        var load = Value(sensors, SensorType.Load, "CPU Total")
                   ?? Average(sensors.Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core")))
                   ?? (float?)_fbCpuUsage;

        var temp = Value(sensors, SensorType.Temperature, "CPU Package")
                   ?? Value(sensors, SensorType.Temperature, "Core (Tctl/Tdie)")
                   ?? Value(sensors, SensorType.Temperature, "Core Max")
                   ?? Value(sensors, SensorType.Temperature, "CPU Total")
                   ?? Average(sensors.Where(s => s.SensorType == SensorType.Temperature && s.Name.StartsWith("Core")));

        var freq = Average(sensors.Where(s =>
            s.SensorType == SensorType.Clock && s.Name.StartsWith("Core") && !s.Name.Contains("Bus")));

        return new CpuInfo(hw.Name, Round(load) ?? 0, Round(temp), Round(freq, 0));
    }

    // ---------- GPU ----------
    private IHardware? PickGpu()
    {
        var gpus = _computer!.Hardware.Where(h =>
            h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        // Preferimos GPU dedicada sobre la integrada de Intel.
        return gpus.FirstOrDefault(g => g.HardwareType != HardwareType.GpuIntel) ?? gpus.FirstOrDefault();
    }

    private static GpuInfo ReadGpu(IHardware? hw)
    {
        if (hw is null) return new GpuInfo("GPU no detectada", null, null, null, null);
        var sensors = AllSensors(hw).ToList();

        var load = Value(sensors, SensorType.Load, "GPU Core");
        var temp = Value(sensors, SensorType.Temperature, "GPU Core")
                   ?? Value(sensors, SensorType.Temperature, "GPU Hot Spot");
        var used = Value(sensors, SensorType.SmallData, "GPU Memory Used")
                   ?? Value(sensors, SensorType.SmallData, "D3D Dedicated Memory Used");
        var total = Value(sensors, SensorType.SmallData, "GPU Memory Total")
                    ?? Value(sensors, SensorType.SmallData, "D3D Dedicated Memory Total");

        return new GpuInfo(hw.Name, Round(load), Round(temp), Round(used, 0), Round(total, 0));
    }

    // ---------- RAM ----------
    private RamInfo ReadRam(IHardware? hw)
    {
        if (hw is null) return _fallback.ReadRam() ?? new RamInfo(0, 0, 0);
        var sensors = AllSensors(hw).ToList();

        var used = Value(sensors, SensorType.Data, "Memory Used");
        var avail = Value(sensors, SensorType.Data, "Memory Available");
        var pct = Value(sensors, SensorType.Load, "Memory");

        if (used is null) return _fallback.ReadRam() ?? new RamInfo(0, 0, Round(pct) ?? 0);
        var total = avail is null ? (pct is > 0 ? used.Value * 100 / pct.Value : used.Value) : used.Value + avail.Value;
        pct ??= total > 0 ? used.Value / total * 100 : 0;
        return new RamInfo(Round(used, 2) ?? 0, Round(total, 2) ?? 0, Round(pct) ?? 0);
    }

    // ---------- Storage ----------
    private static StorageInfo ReadStorage()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var d = new DriveInfo(root);
            const double gb = 1024.0 * 1024 * 1024;
            var total = d.TotalSize / gb;
            var used = (d.TotalSize - d.TotalFreeSpace) / gb;
            return new StorageInfo(root.TrimEnd('\\'), Math.Round(used, 1), Math.Round(total, 1),
                total > 0 ? Math.Round(used / total * 100, 1) : 0);
        }
        catch (Exception)
        {
            return new StorageInfo("C:", 0, 0, 0);
        }
    }

    // ---------- Helpers ----------
    private static void SafeUpdate(IHardware hw)
    {
        try
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) SafeUpdate(sub);
        }
        catch (Exception) { /* un sensor que falla no debe tumbar el loop */ }
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hw) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(AllSensors));

    private static float? Value(IEnumerable<ISensor> sensors, SensorType type, string name)
    {
        var v = sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)?.Value;
        return Valid(v);
    }

    private static float? Average(IEnumerable<ISensor> sensors)
    {
        var vals = sensors.Select(s => Valid(s.Value)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return vals.Count > 0 ? vals.Average() : null;
    }

    private static float? Valid(float? v) => v is { } f && !float.IsNaN(f) && !float.IsInfinity(f) ? f : null;

    private static double? Round(float? v, int digits = 1) => v is null ? null : Math.Round((double)v.Value, digits);

    private static bool DetectElevation()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _computer?.Close(); } catch (Exception) { }
    }
}
