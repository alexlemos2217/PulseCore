using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>
/// Módulo competitivo de Fortnite. Solo toca GameUserSettings.ini y las carpetas Saved\Demos, Crashes y Logs.
/// Nunca modifica ejecutables ni la memoria del juego.
/// </summary>
public sealed class FortniteService
{
    private const string GameProcess = "FortniteClient-Win64-Shipping";
    private const string GameSection = "/Script/FortniteGame.FortGameUserSettings";
    private const string ScalabilitySection = "ScalabilityGroups";

    private static readonly EnumerationOptions EnumOpts = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private static readonly (string Key, string Value)[] Competitive =
    [
        ("bUseVSync", "False"), ("bMotionBlur", "False"), ("bShowGrass", "False"),
        ("sg.ShadowQuality", "0"), ("sg.EffectsQuality", "0"), ("sg.PostProcessQuality", "0"),
        ("sg.AntiAliasingQuality", "0"), ("sg.ViewDistanceQuality", "2")
    ];

    private static readonly (string Key, string Value)[] Quality =
    [
        ("bUseVSync", "False"), ("bMotionBlur", "False"), ("bShowGrass", "True"),
        ("sg.ShadowQuality", "2"), ("sg.EffectsQuality", "2"), ("sg.PostProcessQuality", "2"),
        ("sg.AntiAliasingQuality", "2"), ("sg.ViewDistanceQuality", "3")
    ];

    private readonly object _lock = new();

    // ---------- Rutas ----------
    private static string LocalApp => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string SavedDir => Path.Combine(LocalApp, "FortniteGame", "Saved");
    private static string ConfigPath => Path.Combine(SavedDir, "Config", "WindowsClient", "GameUserSettings.ini");
    private static string DemosDir => Path.Combine(SavedDir, "Demos");
    private static string CrashesDir => Path.Combine(SavedDir, "Crashes");
    private static string LogsDir => Path.Combine(SavedDir, "Logs");
    private static string BackupDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseCore", "Backups");
    private static string BackupPath => Path.Combine(BackupDir, "GameUserSettings.original.ini");

    // ---------- Estado ----------
    public FortniteStatus GetStatus(string? message = null)
    {
        var empty = new FolderStats("", false, 0, 0);
        try
        {
            var path = ConfigPath;
            var detected = File.Exists(path);
            FortniteSettings? settings = null;
            var ro = false;
            if (detected)
            {
                try { settings = ReadSettings(path); } catch (Exception) { }
                try { ro = IsReadOnly(path); } catch (Exception) { }
            }

            var hasBackup = File.Exists(BackupPath);
            DateTimeOffset? backupAt = null;
            if (hasBackup)
            {
                try { backupAt = new DateTimeOffset(File.GetCreationTime(BackupPath)); } catch (Exception) { }
            }

            var demos = Measure([DemosDir]);
            var logs = Measure([CrashesDir, LogsDir]);

            return new FortniteStatus(detected, path, hasBackup, hasBackup ? BackupPath : null, backupAt,
                settings, ro, IsGameRunning(), demos, logs,
                message ?? (detected ? null : "No se encontró GameUserSettings.ini. ¿Fortnite está instalado y se abrió al menos una vez?"));
        }
        catch (Exception ex)
        {
            return new FortniteStatus(false, "", false, null, null, null, false, false, empty, empty,
                $"No se pudo leer el estado de Fortnite: {ex.Message}");
        }
    }

    // ---------- Presets ----------
    public FortnitePresetResult ApplyPreset(string? preset, int fpsLimit, bool setReadOnly)
    {
        lock (_lock)
        {
            preset = (preset ?? "").Trim().ToLowerInvariant();
            FortnitePresetResult Fail(string msg, bool backupCreated = false) =>
                new(false, msg, preset!, fpsLimit, setReadOnly, backupCreated, IsGameRunning(), 0, GetStatus());

            if (preset is not ("competitive" or "quality")) return Fail("Preset desconocido.");
            if (fpsLimit != 0 && (fpsLimit < 30 || fpsLimit > 1000))
                return Fail("Límite de FPS inválido (usá 0 para ilimitado o un valor entre 30 y 1000).");

            var path = ConfigPath;
            if (!File.Exists(path)) return Fail("No se encontró GameUserSettings.ini; no se modificó nada.");

            // 1. Backup original (solo la primera vez). Sin backup no se toca el archivo.
            var backupCreated = false;
            try { backupCreated = EnsureBackup(); }
            catch (Exception ex) { return Fail($"No se pudo crear el backup original, así que no se modificó nada: {ex.Message}"); }

            // 2. Escribir cambios
            var values = new List<(string Key, string Value)>(preset == "competitive" ? Competitive : Quality)
            {
                ("FrameRateLimit", fpsLimit == 0 ? "0.000000" : fpsLimit.ToString(CultureInfo.InvariantCulture) + ".000000")
            };

            var attrs = File.GetAttributes(path);
            var wasReadOnly = attrs.HasFlag(FileAttributes.ReadOnly);
            var tmp = path + ".pulsecore.tmp";
            int changed;
            try
            {
                var (text, enc, preamble) = ReadText(path);
                var newText = ApplyKeys(text, values, out changed);

                if (wasReadOnly) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

                var body = enc.GetBytes(newText);
                var bytes = new byte[preamble.Length + body.Length];
                Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
                Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);

                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                try { if (wasReadOnly) File.SetAttributes(path, attrs | FileAttributes.ReadOnly); } catch (Exception) { }
                return Fail($"No se pudo escribir el archivo de configuración: {ex.Message}", backupCreated);
            }

            // 3. Estado final de solo lectura
            try
            {
                var cur = File.GetAttributes(path);
                File.SetAttributes(path, setReadOnly ? cur | FileAttributes.ReadOnly : cur & ~FileAttributes.ReadOnly);
            }
            catch (Exception ex)
            {
                return new FortnitePresetResult(true,
                    $"Preset aplicado, pero no se pudo ajustar el modo solo lectura: {ex.Message}",
                    preset, fpsLimit, setReadOnly, backupCreated, IsGameRunning(), changed, GetStatus());
            }

            var name = preset == "competitive" ? "Competitivo FPS" : "Calidad Visual";
            var fpsText = fpsLimit == 0 ? "FPS ilimitados" : $"límite {fpsLimit} FPS";
            var msg = $"Preset “{name}” aplicado ({fpsText}, {changed} valores modificados).";
            if (IsGameRunning())
                msg += " Fortnite está abierto: cerralo y volvé a abrirlo, o puede sobrescribir los cambios al salir.";
            return new FortnitePresetResult(true, msg, preset, fpsLimit, setReadOnly, backupCreated,
                IsGameRunning(), changed, GetStatus());
        }
    }

    public FortniteRestoreResult RestoreBackup()
    {
        lock (_lock)
        {
            FortniteRestoreResult Fail(string msg) => new(false, msg, IsGameRunning(), GetStatus());

            if (!File.Exists(BackupPath)) return Fail("Todavía no existe un backup original para restaurar.");
            var path = ConfigPath;
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) return Fail("No se encontró la carpeta de configuración de Fortnite.");

            try
            {
                if (File.Exists(path))
                {
                    var a = File.GetAttributes(path);
                    if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
                }
                File.Copy(BackupPath, path, true);
            }
            catch (Exception ex)
            {
                return Fail($"No se pudo restaurar el backup: {ex.Message}");
            }

            var msg = "GameUserSettings.ini restaurado exactamente desde el backup original.";
            if (IsGameRunning()) msg += " Fortnite está abierto: reinicialo para que tome la configuración.";
            return new FortniteRestoreResult(true, msg, IsGameRunning(), GetStatus());
        }
    }

    // ---------- Limpieza ----------
    public FortniteCleanResult CleanFortniteFiles(bool cleanDemos, bool cleanLogs)
    {
        lock (_lock)
        {
            var sw = Stopwatch.StartNew();
            if (!cleanDemos && !cleanLogs)
                return new FortniteCleanResult(false, "No seleccionaste nada para limpiar.", 0, 0, 0, 0, GetStatus());

            var roots = new List<string>();
            if (cleanDemos) roots.Add(DemosDir);
            if (cleanLogs) { roots.Add(CrashesDir); roots.Add(LogsDir); }

            long freed = 0;
            int deleted = 0, skipped = 0;
            foreach (var root in roots)
            {
                if (!IsInsideSaved(root) || !Directory.Exists(root)) continue;
                var (b, d, s) = CleanRoot(root);
                freed += b; deleted += d; skipped += s;
            }
            sw.Stop();

            var mb = Math.Round(freed / 1048576.0, 2);
            var msg = skipped > 0
                ? $"{deleted} archivos eliminados ({mb} MB). {skipped} omitidos por estar en uso."
                : $"{deleted} archivos eliminados ({mb} MB).";
            return new FortniteCleanResult(true, msg, mb, deleted, skipped, sw.ElapsedMilliseconds, GetStatus());
        }
    }

    // ---------- Lectura/escritura INI ----------
    private static bool IsGameRunning()
    {
        Process[] procs = [];
        try
        {
            procs = Process.GetProcessesByName(GameProcess);
            return procs.Length > 0;
        }
        catch (Exception) { return false; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    private static bool IsReadOnly(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

    private static bool EnsureBackup()
    {
        if (File.Exists(BackupPath)) return false; // nunca sobrescribir el original
        Directory.CreateDirectory(BackupDir);
        File.Copy(ConfigPath, BackupPath, false);
        return true;
    }

    private static (string Text, Encoding Enc, byte[] Preamble) ReadText(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return (Encoding.UTF8.GetString(raw, 3, raw.Length - 3), new UTF8Encoding(false), [0xEF, 0xBB, 0xBF]);
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return (Encoding.Unicode.GetString(raw, 2, raw.Length - 2), new UnicodeEncoding(false, false), [0xFF, 0xFE]);
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2), new UnicodeEncoding(true, false), [0xFE, 0xFF]);
        return (Encoding.UTF8.GetString(raw), new UTF8Encoding(false), []);
    }

    private static FortniteSettings ReadSettings(string path)
    {
        var (text, _, _) = ReadText(path);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '[') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (!map.ContainsKey(key)) map[key] = line[(eq + 1)..].Trim();
        }

        double? D(string k) => map.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
        bool? B(string k) => map.TryGetValue(k, out var v) && bool.TryParse(v, out var r) ? r : null;
        int? I(string k) => map.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;

        return new FortniteSettings(D("FrameRateLimit"), B("bUseVSync"), B("bMotionBlur"), B("bShowGrass"),
            I("sg.ShadowQuality"), I("sg.EffectsQuality"), I("sg.PostProcessQuality"),
            I("sg.AntiAliasingQuality"), I("sg.ViewDistanceQuality"));
    }

    /// <summary>Actualiza claves existentes (en cualquier sección) o las agrega en su sección; conserva el resto del archivo intacto.</summary>
    private static string ApplyKeys(string text, IReadOnlyList<(string Key, string Value)> values, out int changed)
    {
        var crlf = text.Contains("\r\n");
        var cr = crlf ? "\r" : "";
        var lines = text.Split('\n').ToList();
        var trailingEmpty = lines.Count > 0 && lines[^1].Length == 0;
        if (trailingEmpty) lines.RemoveAt(lines.Count - 1);
        changed = 0;

        foreach (var (key, value) in values)
        {
            var found = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || t[0] is ';' or '[') continue;
                var eq = t.IndexOf('=');
                if (eq <= 0 || !t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                found = true;
                var origKey = t[..eq].Trim();
                var current = t[(eq + 1)..].Trim();
                if (!current.Equals(value, StringComparison.Ordinal))
                {
                    lines[i] = $"{origKey}={value}{cr}";
                    changed++;
                }
            }
            if (found) continue;

            var section = key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase) ? ScalabilitySection : GameSection;
            var header = $"[{section}]";
            var hi = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (hi < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length != 0) lines.Add(cr);
                lines.Add(header + cr);
                lines.Add($"{key}={value}{cr}");
            }
            else
            {
                var end = hi + 1;
                while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
                var insertAt = end;
                while (insertAt - 1 > hi && lines[insertAt - 1].Trim().Length == 0) insertAt--;
                lines.Insert(insertAt, $"{key}={value}{cr}");
            }
            changed++;
        }

        var result = string.Join("\n", lines);
        return trailingEmpty ? result + "\n" : result;
    }

    // ---------- Carpetas ----------
    private static bool IsInsideSaved(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var saved = Path.GetFullPath(SavedDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(saved, StringComparison.OrdinalIgnoreCase) && full.Length > saved.Length;
        }
        catch (Exception) { return false; }
    }

    private static FolderStats Measure(string[] roots)
    {
        long bytes = 0;
        var files = 0;
        var exists = false;
        foreach (var root in roots)
        {
            if (!IsInsideSaved(root) || !Directory.Exists(root)) continue;
            exists = true;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", EnumOpts))
                {
                    try { bytes += new FileInfo(f).Length; files++; }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
        return new FolderStats(string.Join(" ; ", roots), exists, Math.Round(bytes / 1048576.0, 2), files);
    }

    private static (long Freed, int Deleted, int Skipped) CleanRoot(string root)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", EnumOpts))
            {
                try
                {
                    var fi = new FileInfo(f);
                    var len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    freed += len;
                    deleted++;
                }
                catch (Exception) { skipped++; }
            }

            foreach (var d in Directory.EnumerateDirectories(root, "*", EnumOpts).OrderByDescending(x => x.Length).ToList())
            {
                try { Directory.Delete(d, false); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return (freed, deleted, skipped);
    }
}
