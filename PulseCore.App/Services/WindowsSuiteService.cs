using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>
/// Suite de sistema Windows: Debloat seguro reversible, Gestor de Inicio y Reparación SFC / DISM en vivo.
/// </summary>
public sealed class WindowsSuiteService
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record RegTarget(bool IsMachine, string SubKey, string ValueName, int AppliedValue, int DefaultValue);
    private sealed record DebloatDef(string Id, string Name, string Description, string Level, RegTarget[] Targets);
    private sealed record DisabledStartupEntry(string Name, string Command, string Location);

    private static readonly DebloatDef[] DebloatDefs =
    [
        new("telemetry",
            "Desactivar Telemetría de Windows",
            "Reduce al mínimo el envío de datos de diagnóstico de Windows en segundo plano.",
            "Esencial",
            [new(true, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, 1)]),

        new("start_ads",
            "Quitar anuncios y sugerencias del Menú Inicio",
            "Elimina recomendaciones de apps patrocinadas y sugerencias en el menú Inicio.",
            "Esencial",
            [
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0, 1),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0, 1)
            ]),

        new("bing_search",
            "Desactivar búsqueda web de Bing en Inicio",
            "Hace que la barra de búsqueda de Windows busque solo archivos y apps locales al instante.",
            "Esencial",
            [new(false, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, 0)]),

        new("windows_tips",
            "Desactivar consejos y notificaciones de Windows",
            "Evita avisos promocionales, tips emergentes y sugerencias de configuración del sistema.",
            "Recomendado",
            [
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0, 1),
                new(false, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", 0, 1)
            ]),

        new("xbox_dvr",
            "Desactivar Xbox Game DVR en segundo plano",
            "Detiene la grabación en segundo plano de Windows para liberar recursos y reducir stuttering.",
            "Recomendado",
            [new(false, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1)])
    ];

    private readonly SystemCheckupService _checkup;
    private readonly string _disabledStartupPath;
    private readonly object _startupLock = new();
    private readonly SemaphoreSlim _repairGate = new(1, 1);

    public WindowsSuiteService(SystemCheckupService checkup, string? disabledStartupPath = null)
    {
        _checkup = checkup;
        _disabledStartupPath = string.IsNullOrWhiteSpace(disabledStartupPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseCore", "disabled_startup.json")
            : disabledStartupPath;
    }

    // ==================== 1. DEBLOAT SEGURO ====================
    public IReadOnlyList<DebloatItem> GetDebloatItems() =>
        DebloatDefs.Select(d => new DebloatItem(d.Id, d.Name, d.Description, d.Level, IsDebloatApplied(d))).ToList();

    public DebloatToggleResult ToggleDebloatItem(string? id, bool enable)
    {
        var def = DebloatDefs.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (def is null)
            return new DebloatToggleResult(false, "Ajuste de Debloat no encontrado.", GetDebloatItems(), _checkup.GetHistory());

        try
        {
            var prevSnapshot = new Dictionary<string, int?>();
            foreach (var t in def.Targets)
                prevSnapshot[$"{(t.IsMachine ? "HKLM" : "HKCU")}\\{t.SubKey}\\{t.ValueName}"] = ReadDword(t.IsMachine, t.SubKey, t.ValueName);

            foreach (var t in def.Targets)
                WriteDword(t.IsMachine, t.SubKey, t.ValueName, enable ? t.AppliedValue : t.DefaultValue);

            var prevJson = JsonSerializer.Serialize(prevSnapshot);
            _checkup.RecordExternalChange($"debloat:{def.Id}", $"Debloat: {def.Name}", prevJson, enable ? "Aplicado" : "Restaurado");

            var msg = enable ? $"“{def.Name}” aplicado correctamente." : $"“{def.Name}” restaurado a su valor por defecto.";
            return new DebloatToggleResult(true, msg, GetDebloatItems(), _checkup.GetHistory());
        }
        catch (Exception ex)
        {
            return new DebloatToggleResult(false, $"No se pudo modificar el Registro: {ex.Message}", GetDebloatItems(), _checkup.GetHistory());
        }
    }

    public (bool Ok, string Message) RevertDebloatRecord(RestoreChangeRecord rec)
    {
        var id = rec.ItemId.Replace("debloat:", "", StringComparison.OrdinalIgnoreCase);
        var def = DebloatDefs.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (def is null) return (false, "Ajuste de Debloat desconocido.");

        try
        {
            var prevMap = JsonSerializer.Deserialize<Dictionary<string, int?>>(rec.PreviousValue);
            foreach (var t in def.Targets)
            {
                var key = $"{(t.IsMachine ? "HKLM" : "HKCU")}\\{t.SubKey}\\{t.ValueName}";
                if (prevMap is not null && prevMap.TryGetValue(key, out var val))
                {
                    if (val is { } v) WriteDword(t.IsMachine, t.SubKey, t.ValueName, v);
                    else DeleteValue(t.IsMachine, t.SubKey, t.ValueName);
                }
                else
                {
                    WriteDword(t.IsMachine, t.SubKey, t.ValueName, t.DefaultValue);
                }
            }
            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool IsDebloatApplied(DebloatDef def) =>
        def.Targets.All(t => ReadDword(t.IsMachine, t.SubKey, t.ValueName) == t.AppliedValue);

    private static RegistryKey RootOf(bool isMachine) => isMachine ? Registry.LocalMachine : Registry.CurrentUser;

    private static int? ReadDword(bool isMachine, string subKey, string name)
    {
        try
        {
            using var k = RootOf(isMachine).OpenSubKey(subKey, false);
            return k?.GetValue(name) is int i ? i : null;
        }
        catch (Exception) { return null; }
    }

    private static void WriteDword(bool isMachine, string subKey, string name, int value)
    {
        using var k = RootOf(isMachine).CreateSubKey(subKey, true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void DeleteValue(bool isMachine, string subKey, string name)
    {
        using var k = RootOf(isMachine).OpenSubKey(subKey, true);
        k?.DeleteValue(name, false);
    }

    // ==================== 2. GESTOR DE INICIO (STARTUP MANAGER) ====================
    public IReadOnlyList<StartupAppItem> GetStartupApps()
    {
        var items = new List<StartupAppItem>();
        ReadRunKey(false, "Usuario", items);
        ReadRunKey(true, "Sistema", items);

        lock (_startupLock)
        {
            foreach (var dis in LoadDisabledStartup())
            {
                if (items.Any(i => i.Location == dis.Location && i.Name.Equals(dis.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                items.Add(new StartupAppItem($"{dis.Location}|{dis.Name}", dis.Name, dis.Command, dis.Location, false));
            }
        }

        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public StartupToggleResult ToggleStartupApp(string? name, string? location, bool enable)
    {
        name = (name ?? "").Trim();
        location = (location ?? "").Trim();
        if (string.IsNullOrEmpty(name) || location is not ("Usuario" or "Sistema"))
            return new StartupToggleResult(false, "Aplicación de inicio inválida.", GetStartupApps());

        var isMachine = location == "Sistema";
        try
        {
            lock (_startupLock)
            {
                var disabled = LoadDisabledStartup();
                if (!enable)
                {
                    using var k = RootOf(isMachine).OpenSubKey(RunSubKey, true);
                    var cmd = k?.GetValue(name)?.ToString();
                    if (cmd is null)
                        return new StartupToggleResult(false, $"“{name}” ya estaba desactivado o no existe.", GetStartupApps());

                    disabled.RemoveAll(d => d.Location == location && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    disabled.Add(new DisabledStartupEntry(name, cmd, location));
                    SaveDisabledStartup(disabled);
                    k!.DeleteValue(name, false);
                    return new StartupToggleResult(true, $"“{name}” desactivado del inicio de Windows (respaldado).", GetStartupApps());
                }
                else
                {
                    var entry = disabled.FirstOrDefault(d => d.Location == location && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                        return new StartupToggleResult(false, $"No se encontró respaldo para reactivar “{name}”.", GetStartupApps());

                    using var k = RootOf(isMachine).CreateSubKey(RunSubKey, true);
                    k.SetValue(entry.Name, entry.Command, RegistryValueKind.String);
                    disabled.Remove(entry);
                    SaveDisabledStartup(disabled);
                    return new StartupToggleResult(true, $"“{name}” reactivado en el inicio de Windows.", GetStartupApps());
                }
            }
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, $"No se pudo modificar el inicio de Windows: {ex.Message}", GetStartupApps());
        }
    }

    private static void ReadRunKey(bool isMachine, string location, List<StartupAppItem> list)
    {
        try
        {
            using var k = RootOf(isMachine).OpenSubKey(RunSubKey, false);
            if (k is null) return;
            foreach (var name in k.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var cmd = k.GetValue(name)?.ToString() ?? "";
                list.Add(new StartupAppItem($"{location}|{name}", name, cmd, location, true));
            }
        }
        catch (Exception) { }
    }

    private List<DisabledStartupEntry> LoadDisabledStartup()
    {
        try
        {
            if (File.Exists(_disabledStartupPath))
            {
                var list = JsonSerializer.Deserialize<List<DisabledStartupEntry>>(File.ReadAllText(_disabledStartupPath), JsonOpts);
                if (list is not null) return list;
            }
        }
        catch (Exception) { }
        return new List<DisabledStartupEntry>();
    }

    private void SaveDisabledStartup(List<DisabledStartupEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_disabledStartupPath)!);
        var tmp = _disabledStartupPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
        File.Move(tmp, _disabledStartupPath, true);
    }

    // ==================== 3. REPARACIÓN DE WINDOWS (SFC / DISM) ====================
    public async Task RunRepairToolAsync(
        string? tool,
        Action<RepairOutputEvent> onOutput,
        Action<RepairCompletedEvent> onCompleted,
        CancellationToken ct = default)
    {
        tool = (tool ?? "").Trim().ToLowerInvariant();
        if (tool is not ("sfc" or "dism"))
        {
            onCompleted(new RepairCompletedEvent(false, tool, -1, "Herramienta de reparación no reconocida."));
            return;
        }

        if (!await _repairGate.WaitAsync(0, ct))
        {
            onCompleted(new RepairCompletedEvent(false, tool, -1, "Ya hay una reparación de Windows en curso. Esperá a que termine."));
            return;
        }

        try
        {
            var isSfc = tool == "sfc";
            var exe = isSfc ? "sfc.exe" : "DISM.exe";
            var args = isSfc ? "/scannow" : "/Online /Cleanup-Image /RestoreHealth";
            var enc = isSfc ? Encoding.Unicode : ResolveOemEncoding();

            onOutput(new RepairOutputEvent(tool, $"Ejecutando {exe} {args}… (puede tardar varios minutos)", "info"));

            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {exe}.");

            var pOut = PumpAsync(proc.StandardOutput, tool, "out", onOutput, ct);
            var pErr = PumpAsync(proc.StandardError, tool, "err", onOutput, ct);
            await Task.WhenAll(pOut, pErr, proc.WaitForExitAsync(ct));

            var ok = proc.ExitCode == 0;
            var msg = ok
                ? (isSfc ? "Escaneo SFC completado correctamente." : "Reparación DISM completada correctamente.")
                : $"{exe} finalizó con código {proc.ExitCode}. Revisá la salida en la consola.";
            onOutput(new RepairOutputEvent(tool, ok ? $"✓ {msg}" : $"! {msg}", ok ? "ok" : "warn"));
            onCompleted(new RepairCompletedEvent(ok, tool, proc.ExitCode, msg));
        }
        catch (OperationCanceledException)
        {
            onCompleted(new RepairCompletedEvent(false, tool, -1, "Reparación cancelada."));
        }
        catch (Exception ex)
        {
            onOutput(new RepairOutputEvent(tool, $"Error: {ex.Message}", "err"));
            onCompleted(new RepairCompletedEvent(false, tool, -1, ex.Message));
        }
        finally
        {
            _repairGate.Release();
        }
    }

    private static async Task PumpAsync(StreamReader reader, string tool, string stream, Action<RepairOutputEvent> onOutput, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var raw = await reader.ReadLineAsync(ct);
            if (raw is null) break;
            var clean = raw.Replace("\0", "").Trim();
            if (!string.IsNullOrWhiteSpace(clean))
                onOutput(new RepairOutputEvent(tool, clean, stream));
        }
    }

    private static Encoding ResolveOemEncoding()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch (Exception) { return Encoding.UTF8; }
    }
}
