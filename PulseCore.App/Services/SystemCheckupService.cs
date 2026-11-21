using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>
/// Audita 5 ajustes clave de Windows para gaming, aplica correcciones reversibles con historial en JSON
/// y permite crear puntos de restauración nativos de Windows.
/// </summary>
public sealed class SystemCheckupService
{
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly PowerPlanService _power;
    private readonly string _historyPath;
    private readonly object _lock = new();
    private List<RestoreChangeRecord> _history;

    public Func<RestoreChangeRecord, (bool Ok, string Message)>? ExternalReverter { get; set; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    public SystemCheckupService(PowerPlanService power, string? historyPath = null)
    {
        _power = power;
        _historyPath = string.IsNullOrWhiteSpace(historyPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseCore", "restore_history.json")
            : historyPath;
        _history = LoadHistory();
    }

    // ---------- Auditoría ----------
    public CheckupReport RunCheckup()
    {
        var items = new List<CheckupItem>
        {
            CheckPowerPlan(),
            CheckWindowsGameMode(),
            CheckMouseAcceleration(),
            CheckStickyKeys(),
            CheckStorageAndRam()
        };

        var optimalCount = items.Count(i => i.Status == "Optimal");
        var score = (int)Math.Round(optimalCount * 100.0 / items.Count);
        return new CheckupReport(items, score, DateTimeOffset.Now);
    }

    private CheckupItem CheckPowerPlan()
    {
        var plans = _power.GetPlans();
        var active = plans.FirstOrDefault(p => p.IsActive);
        var isOptimal = active is not null && active.Kind is "high" or "ultimate";
        var hasHigh = plans.Any(p => p.Kind is "high" or "ultimate");
        return new CheckupItem(
            "power_plan",
            "Plan de Energía de Windows",
            "Alto Rendimiento o Máximo Rendimiento evita caídas de frecuencia y micro-stuttering en CPU.",
            isOptimal ? "Optimal" : "Warning",
            active?.Name ?? "Desconocido",
            !isOptimal && hasHigh);
    }

    private static CheckupItem CheckWindowsGameMode()
    {
        var val = ReadDword(@"Software\Microsoft\GameBar", "AllowAutoGameMode");
        // En Windows 10/11 el valor ausente equivale a activado (1).
        var enabled = val is null or 1;
        return new CheckupItem(
            "windows_game_mode",
            "Modo Juego de Windows",
            "Prioriza los hilos del juego activo y pausa tareas secundarias de Windows Update durante la partida.",
            enabled ? "Optimal" : "Warning",
            enabled ? "Activado (1)" : "Desactivado (0)",
            !enabled);
    }

    private static CheckupItem CheckMouseAcceleration()
    {
        var speed = ReadString(@"Control Panel\Mouse", "MouseSpeed") ?? "1";
        var disabled = speed == "0";
        return new CheckupItem(
            "mouse_accel",
            "Aceleración de Mouse (Precisión del puntero)",
            "Desactivar la aceleración garantiza puntería 1:1 consistente en shooters competitivos.",
            disabled ? "Optimal" : "Warning",
            disabled ? "Desactivada (0)" : $"Activada ({speed})",
            !disabled);
    }

    private static CheckupItem CheckStickyKeys()
    {
        var raw = ReadString(@"Control Panel\Accessibility\StickyKeys", "Flags") ?? "510";
        var flags = int.TryParse(raw, out var n) ? n : 510;
        // Bit 0x04 controla el atajo de presionar Shift 5 veces.
        var hotkeyOff = (flags & 4) == 0;
        return new CheckupItem(
            "sticky_keys",
            "Teclas Especiales (Sticky Keys)",
            "Desactiva la ventana emergente al presionar Shift 5 veces seguidas en medio de una partida.",
            hotkeyOff ? "Optimal" : "Warning",
            hotkeyOff ? $"Atajo desactivado (Flags={flags})" : $"Atajo activo (Flags={flags})",
            !hotkeyOff);
    }

    private static CheckupItem CheckStorageAndRam()
    {
        double freePct = 0, freeGb = 0, totalGb = 0;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var d = new DriveInfo(root);
            const double gb = 1024.0 * 1024 * 1024;
            totalGb = d.TotalSize / gb;
            freeGb = d.TotalFreeSpace / gb;
            freePct = totalGb > 0 ? freeGb / totalGb * 100.0 : 0;
        }
        catch (Exception) { }

        var ramDesc = ReadRamModulesSummary();
        var ok = freePct >= 15.0;
        var current = $"C: {freeGb:F0} GB libres ({freePct:F0}%) · {ramDesc}";

        return new CheckupItem(
            "storage_ram",
            "Espacio en Disco C: y Memoria RAM",
            "Mantener al menos 15% libre en el SSD/disco del sistema evita degradación de caché y paginación.",
            ok ? "Optimal" : "Warning",
            current,
            false);
    }

    private static string ReadRamModulesSummary()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            var modules = new List<(double Gb, uint Speed)>();
            foreach (var obj in searcher.Get())
            {
                var cap = obj["Capacity"] is ulong u ? u / (1024.0 * 1024 * 1024) : 0;
                uint spd = 0;
                if (obj["ConfiguredClockSpeed"] is uint cs && cs > 0) spd = cs;
                else if (obj["Speed"] is uint sp && sp > 0) spd = sp;
                if (cap > 0) modules.Add((Math.Round(cap, 0), spd));
            }
            if (modules.Count > 0)
            {
                var total = modules.Sum(m => m.Gb);
                var maxSpd = modules.Max(m => m.Speed);
                return maxSpd > 0
                    ? $"RAM: {total:F0} GB ({modules.Count} mód. @ {maxSpd} MHz)"
                    : $"RAM: {total:F0} GB ({modules.Count} mód.)";
            }
        }
        catch (Exception) { }

        var fb = new FallbackProbe().ReadRam();
        return fb is not null ? $"RAM: {fb.TotalGb:F1} GB física" : "RAM: N/D";
    }

    // ---------- Corrección reversible ----------
    public CheckupActionResult FixItem(string? id)
    {
        id = (id ?? "").Trim().ToLowerInvariant();
        try
        {
            switch (id)
            {
                case "power_plan":
                {
                    var plans = _power.GetPlans();
                    var prev = plans.FirstOrDefault(p => p.IsActive);
                    var target = plans.FirstOrDefault(p => p.Kind == "high") ?? plans.FirstOrDefault(p => p.Kind == "ultimate");
                    if (target is null)
                        return BuildResult(false, "Alto Rendimiento no está disponible en este equipo.");
                    if (target.IsActive)
                        return BuildResult(true, $"“{target.Name}” ya estaba activo.");

                    var res = _power.SetPowerPlan(target.Guid);
                    if (!res.Success) return BuildResult(false, res.Message);

                    RecordChange("power_plan", "Plan de Energía",
                        prev is not null ? $"{prev.Guid}|{prev.Name}" : "",
                        $"{target.Guid}|{target.Name}");
                    return BuildResult(true, $"Plan de energía cambiado a {target.Name}.");
                }

                case "windows_game_mode":
                {
                    var prev = ReadDword(@"Software\Microsoft\GameBar", "AllowAutoGameMode");
                    WriteDword(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1);
                    RecordChange("windows_game_mode", "Modo Juego de Windows",
                        prev?.ToString() ?? "0", "1");
                    return BuildResult(true, "Modo Juego de Windows activado.");
                }

                case "mouse_accel":
                {
                    var prevSpeed = ReadString(@"Control Panel\Mouse", "MouseSpeed") ?? "1";
                    var prevT1 = ReadString(@"Control Panel\Mouse", "MouseThreshold1") ?? "6";
                    var prevT2 = ReadString(@"Control Panel\Mouse", "MouseThreshold2") ?? "10";
                    WriteString(@"Control Panel\Mouse", "MouseSpeed", "0");
                    WriteString(@"Control Panel\Mouse", "MouseThreshold1", "0");
                    WriteString(@"Control Panel\Mouse", "MouseThreshold2", "0");
                    ApplyMouseSpi(0, 0, 0);

                    RecordChange("mouse_accel", "Aceleración de Mouse",
                        $"{prevSpeed}|{prevT1}|{prevT2}", "0|0|0");
                    return BuildResult(true, "Aceleración de mouse desactivada (puntería 1:1 activa).");
                }

                case "sticky_keys":
                {
                    var prevRaw = ReadString(@"Control Panel\Accessibility\StickyKeys", "Flags") ?? "510";
                    var flags = int.TryParse(prevRaw, out var n) ? n : 510;
                    var next = (flags & ~4).ToString();
                    WriteString(@"Control Panel\Accessibility\StickyKeys", "Flags", next);

                    RecordChange("sticky_keys", "Teclas Especiales (Sticky Keys)", prevRaw, next);
                    return BuildResult(true, "Aviso de Sticky Keys (Shift ×5) desactivado.");
                }

                default:
                    return BuildResult(false, "Este ítem es solo de diagnóstico o no existe.");
            }
        }
        catch (Exception ex)
        {
            return BuildResult(false, $"No se pudo aplicar el ajuste: {ex.Message}");
        }
    }

    // ---------- Centro de Restauración ----------
    public IReadOnlyList<RestoreChangeRecord> GetHistory()
    {
        lock (_lock) return _history.ToList();
    }

    public CheckupActionResult RevertChange(string? changeId)
    {
        RestoreChangeRecord? rec;
        lock (_lock) rec = _history.FirstOrDefault(x => x.ChangeId == changeId);
        if (rec is null) return BuildResult(false, "No se encontró ese cambio en el historial.");

        var (ok, msg) = ApplyRevert(rec);
        if (!ok) return BuildResult(false, msg);

        lock (_lock) _history.RemoveAll(x => x.ChangeId == rec.ChangeId);
        SaveHistory();
        return BuildResult(true, $"Cambio revertido: {rec.SettingName}.");
    }

    public CheckupActionResult RevertAllChanges()
    {
        List<RestoreChangeRecord> snapshot;
        lock (_lock) snapshot = _history.ToList();
        if (snapshot.Count == 0) return BuildResult(false, "No hay cambios pendientes para revertir.");

        var reverted = 0;
        var failed = new List<string>();
        foreach (var rec in snapshot)
        {
            var (ok, _) = ApplyRevert(rec);
            if (ok)
            {
                reverted++;
                lock (_lock) _history.RemoveAll(x => x.ChangeId == rec.ChangeId);
            }
            else failed.Add(rec.SettingName);
        }
        SaveHistory();

        var msg = failed.Count == 0
            ? $"Se revirtieron {reverted} ajustes a su estado original."
            : $"Se revirtieron {reverted} ajustes; no se pudo revertir: {string.Join(", ", failed)}.";
        return BuildResult(failed.Count == 0, msg);
    }

    public void RecordExternalChange(string itemId, string settingName, string previousValue, string newValue) =>
        RecordChange(itemId, settingName, previousValue, newValue);

    private (bool Ok, string Message) ApplyRevert(RestoreChangeRecord rec)
    {
        if (rec.ItemId.StartsWith("debloat:", StringComparison.OrdinalIgnoreCase) && ExternalReverter is not null)
            return ExternalReverter(rec);
        try
        {
            switch (rec.ItemId)
            {
                case "power_plan":
                {
                    var guid = rec.PreviousValue.Split('|')[0];
                    if (string.IsNullOrWhiteSpace(guid)) return (false, "GUID previo no registrado.");
                    var r = _power.SetPowerPlan(guid);
                    return (r.Success, r.Message);
                }
                case "windows_game_mode":
                {
                    var val = int.TryParse(rec.PreviousValue, out var n) ? n : 0;
                    WriteDword(@"Software\Microsoft\GameBar", "AllowAutoGameMode", val);
                    return (true, "OK");
                }
                case "mouse_accel":
                {
                    var parts = rec.PreviousValue.Split('|');
                    var spd = parts.Length > 0 ? parts[0] : "1";
                    var t1 = parts.Length > 1 ? parts[1] : "6";
                    var t2 = parts.Length > 2 ? parts[2] : "10";
                    WriteString(@"Control Panel\Mouse", "MouseSpeed", spd);
                    WriteString(@"Control Panel\Mouse", "MouseThreshold1", t1);
                    WriteString(@"Control Panel\Mouse", "MouseThreshold2", t2);
                    ApplyMouseSpi(
                        int.TryParse(t1, out var i1) ? i1 : 6,
                        int.TryParse(t2, out var i2) ? i2 : 10,
                        int.TryParse(spd, out var ispd) ? ispd : 1);
                    return (true, "OK");
                }
                case "sticky_keys":
                {
                    WriteString(@"Control Panel\Accessibility\StickyKeys", "Flags", rec.PreviousValue);
                    return (true, "OK");
                }
                default:
                    return (false, "Tipo de cambio desconocido.");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------- Punto de Restauración de Windows ----------
    public RestorePointResult CreateRestorePoint()
    {
        var sw = Stopwatch.StartNew();
        var cmd = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'PulseCore Backup' -RestorePointType 'MODIFY_SETTINGS'\"";
        var r = ProcessRunner.Run("powershell.exe", cmd, timeoutMs: 45000);
        sw.Stop();

        var combined = $"{r.Output}\n{r.Error}".Trim();
        if (r.Ok && !combined.Contains("1058", StringComparison.OrdinalIgnoreCase)
            && !combined.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new RestorePointResult(true, "Punto de restauración “PulseCore Backup” creado correctamente.", sw.ElapsedMilliseconds);
        }

        if (r.TimedOut)
            return new RestorePointResult(false, "La creación del punto de restauración superó el tiempo de espera.", sw.ElapsedMilliseconds);

        if (combined.Contains("frequency", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("frecuencia", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("24", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("1440", StringComparison.OrdinalIgnoreCase))
        {
            return new RestorePointResult(false, "Windows limita la creación a 1 punto de restauración cada 24 horas (ya existe uno reciente).", sw.ElapsedMilliseconds);
        }

        if (combined.Contains("disabled", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("deshabilit", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("1058", StringComparison.OrdinalIgnoreCase))
        {
            return new RestorePointResult(false, "La Protección del Sistema está deshabilitada en Windows. Activala en Propiedades del Sistema → Protección del sistema.", sw.ElapsedMilliseconds);
        }

        var detail = string.IsNullOrWhiteSpace(combined) ? "Ejecutá PulseCore como Administrador." : combined.Split('\n')[0].Trim();
        return new RestorePointResult(false, $"No se pudo crear el punto de restauración: {detail}", sw.ElapsedMilliseconds);
    }

    // ---------- Helpers ----------
    private CheckupActionResult BuildResult(bool ok, string message) =>
        new(ok, message, RunCheckup(), GetHistory());

    private void RecordChange(string itemId, string settingName, string prev, string next)
    {
        var rec = new RestoreChangeRecord(
            Guid.NewGuid().ToString("N")[..10], itemId, settingName, prev, next, DateTimeOffset.Now);
        lock (_lock)
        {
            _history.Insert(0, rec);
            if (_history.Count > 50) _history.RemoveRange(50, _history.Count - 50);
        }
        SaveHistory();
    }

    private static void ApplyMouseSpi(int t1, int t2, int speed)
    {
        try { SystemParametersInfo(SPI_SETMOUSE, 0, [t1, t2, speed], SPIF_UPDATEINIFILE | SPIF_SENDCHANGE); }
        catch (Exception) { }
    }

    private static int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(subKey, false);
            return k?.GetValue(valueName) is int i ? i : null;
        }
        catch (Exception) { return null; }
    }

    private static void WriteDword(string subKey, string valueName, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(subKey, true);
        k.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private static string? ReadString(string subKey, string valueName)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(subKey, false);
            return k?.GetValue(valueName)?.ToString();
        }
        catch (Exception) { return null; }
    }

    private static void WriteString(string subKey, string valueName, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(subKey, true);
        k.SetValue(valueName, value, RegistryValueKind.String);
    }

    private List<RestoreChangeRecord> LoadHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var list = JsonSerializer.Deserialize<List<RestoreChangeRecord>>(File.ReadAllText(_historyPath), JsonOpts);
                if (list is not null) return list;
            }
        }
        catch (Exception) { }
        return new List<RestoreChangeRecord>();
    }

    private void SaveHistory()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_history, JsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            var tmp = _historyPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _historyPath, true);
        }
        catch (Exception) { }
    }
}
