using System.IO;
using System.Text.Json;

namespace PulseCore.App.Services;

public sealed class PulseCoreSettings
{
    public string? ActiveProfile { get; set; }
    public bool WeeklyMaintenanceEnabled { get; set; }
    public DateTimeOffset? LastMaintenanceUtc { get; set; }
}

/// <summary>
/// Gestiona perfiles rápidos (Competitivo / Trabajo), Mantenimiento Semanal automático y Exportar/Importar configuración.
/// </summary>
public sealed class ProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _baseDir;
    private readonly string _settingsPath;
    private readonly object _lock = new();
    private PulseCoreSettings _settings;

    public ProfileService(string? baseDir = null)
    {
        _baseDir = string.IsNullOrWhiteSpace(baseDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseCore")
            : baseDir;
        _settingsPath = Path.Combine(_baseDir, "settings.json");
        _settings = Load();
    }

    public PulseCoreSettings GetSettings()
    {
        lock (_lock)
        {
            return new PulseCoreSettings
            {
                ActiveProfile = _settings.ActiveProfile,
                WeeklyMaintenanceEnabled = _settings.WeeklyMaintenanceEnabled,
                LastMaintenanceUtc = _settings.LastMaintenanceUtc
            };
        }
    }

    public PulseCoreSettings SetActiveProfile(string? profile)
    {
        lock (_lock) _settings.ActiveProfile = profile;
        Save();
        return GetSettings();
    }

    public PulseCoreSettings SetWeeklyMaintenance(bool enabled)
    {
        lock (_lock) _settings.WeeklyMaintenanceEnabled = enabled;
        Save();
        return GetSettings();
    }

    public async Task<(bool Ran, string? Message)> RunDueWeeklyMaintenanceAsync(CleanerService cleaner, CancellationToken ct = default)
    {
        PulseCoreSettings snap = GetSettings();
        if (!snap.WeeklyMaintenanceEnabled) return (false, null);

        var due = snap.LastMaintenanceUtc is null || (DateTimeOffset.UtcNow - snap.LastMaintenanceUtc.Value) >= TimeSpan.FromDays(7);
        if (!due) return (false, null);

        var summary = await cleaner.CleanAsync(["windows_temp", "dns_cache"], ct: ct);
        lock (_lock) _settings.LastMaintenanceUtc = DateTimeOffset.UtcNow;
        Save();
        return (true, $"Mantenimiento semanal automático: {summary.TotalFreedMb} MB liberados ({summary.TotalFiles} archivos) + DNS limpio.");
    }

    public (bool Ok, string Message) ExportBundle(string destinationPath)
    {
        try
        {
            var gamesPath = Path.Combine(_baseDir, "games.json");
            var historyPath = Path.Combine(_baseDir, "restore_history.json");

            object? ReadJson(string p) => File.Exists(p) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(p)) : null;

            var bundle = new
            {
                app = "PulseCore",
                version = "1.8.0",
                exportedAt = DateTimeOffset.Now,
                settings = GetSettings(),
                games = ReadJson(gamesPath),
                restoreHistory = ReadJson(historyPath)
            };

            File.WriteAllText(destinationPath, JsonSerializer.Serialize(bundle, JsonOpts));
            return (true, $"Configuración exportada a {Path.GetFileName(destinationPath)}.");
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo exportar la configuración: {ex.Message}");
        }
    }

    public (bool Ok, string Message) ImportBundle(string sourcePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sourcePath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (false, "El archivo seleccionado no es un respaldo válido de PulseCore.");

            Directory.CreateDirectory(_baseDir);

            if (root.TryGetProperty("settings", out var sEl) && sEl.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<PulseCoreSettings>(sEl.GetRawText(), JsonOpts);
                if (parsed is not null)
                {
                    lock (_lock) _settings = parsed;
                    Save();
                }
            }

            if (root.TryGetProperty("games", out var gEl) && gEl.ValueKind == JsonValueKind.Object)
            {
                File.WriteAllText(Path.Combine(_baseDir, "games.json"), gEl.GetRawText());
            }

            return (true, "Configuración importada correctamente. Reiniciá o refrescá las vistas para ver todos los cambios.");
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo importar el archivo: {ex.Message}");
        }
    }

    private PulseCoreSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var s = JsonSerializer.Deserialize<PulseCoreSettings>(File.ReadAllText(_settingsPath), JsonOpts);
                if (s is not null) return s;
            }
        }
        catch (Exception) { }
        return new PulseCoreSettings();
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_settings, JsonOpts);
            Directory.CreateDirectory(_baseDir);
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsPath, true);
        }
        catch (Exception) { }
    }
}
