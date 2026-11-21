using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>
/// Vigila procesos de juegos configurados. Al iniciarse uno: guarda el plan de energía, activa Alto Rendimiento,
/// sube la prioridad del juego y libera RAM. Al cerrarse: restaura el plan original. Persistencia en %APPDATA%\PulseCore\games.json.
/// </summary>
public sealed class GameModeService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex ExeRx = new(@"^[A-Za-z0-9_\-. ]{1,100}\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Session(
        string name, string process, int pid, DateTimeOffset startedAt,
        string? previousPlanGuid, string? previousPlanName, string? newPlanName, bool planChanged)
    {
        public string Name { get; } = name;
        public string Process { get; } = process;
        public int Pid { get; } = pid;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string? PreviousPlanGuid { get; } = previousPlanGuid;
        public string? PreviousPlanName { get; } = previousPlanName;
        public string? NewPlanName { get; } = newPlanName;
        public bool PlanChanged { get; } = planChanged;
        public double? MaxGpuTemp { get; set; }
        public double MaxCpuLoad { get; set; }
        public double MaxRamGb { get; set; }
    }

    private const int MaxStoredSessions = 30;

    public event Action<string, object>? Event;

    private readonly PowerPlanService _power;
    private readonly MemoryOptimizerService _memory;
    private readonly GameDiscoveryService? _discovery;
    private readonly string _path;
    private readonly string _sessionsPath;
    private readonly string _iconCacheDir;
    private readonly Dictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InstalledGameInfo>? _discoveredCache;
    private readonly object _lock = new();   // protege config + sesión + historial
    private readonly object _opLock = new(); // serializa operaciones (tick / fin de sesión)

    private GameModeConfig _config;
    private List<GameSessionRecord> _sessions;
    private Session? _session;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _disposed;

    public GameModeService(
        PowerPlanService power,
        MemoryOptimizerService memory,
        string? configPath = null,
        GameDiscoveryService? discovery = null)
    {
        _power = power;
        _memory = memory;
        _discovery = discovery;
        _path = string.IsNullOrWhiteSpace(configPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseCore", "games.json")
            : configPath;
        var baseDir = Path.GetDirectoryName(_path)!;
        _sessionsPath = Path.Combine(baseDir, "sessions.json");
        _iconCacheDir = Path.Combine(baseDir, "icon_cache");
        _config = Load();
        _sessions = LoadSessions();
    }

    // ---------- Telemetría e Historial de Sesiones ----------
    public void RecordTelemetrySample(double cpuLoad, double? gpuTemp, double ramUsedGb)
    {
        lock (_lock)
        {
            if (_session is null) return;
            if (!double.IsNaN(cpuLoad) && cpuLoad > _session.MaxCpuLoad)
                _session.MaxCpuLoad = Math.Round(cpuLoad, 1);
            if (gpuTemp is { } t && !double.IsNaN(t) && t > 0 && (_session.MaxGpuTemp is null || t > _session.MaxGpuTemp.Value))
                _session.MaxGpuTemp = Math.Round(t, 1);
            if (!double.IsNaN(ramUsedGb) && ramUsedGb > _session.MaxRamGb)
                _session.MaxRamGb = Math.Round(ramUsedGb, 2);
        }
    }

    public IReadOnlyList<GameSessionRecord> GetSessions()
    {
        lock (_lock) return _sessions.ToList();
    }

    public bool ClearSessions()
    {
        lock (_lock) _sessions.Clear();
        return SaveSessions();
    }

    // ---------- Estado ----------
    public GameModeState GetState()
    {
        List<GameProfile> rawGames;
        ActiveGameInfo? active;
        bool enabled, running;
        lock (_lock)
        {
            enabled = _config.IsEnabled;
            rawGames = _config.Games.ToList();
            running = _loop is not null;
            active = _session is null ? null : new ActiveGameInfo(
                _session.Name, _session.Process, _session.Pid, _session.StartedAt,
                _session.PreviousPlanName, _session.NewPlanName, _session.PlanChanged);
        }

        var enriched = rawGames.Select(g => g with { IconDataUrl = ResolveGameIcon(g) }).ToList();
        return new GameModeState(enabled, enriched, active, running, _path);
    }

    private string? ResolveGameIcon(GameProfile game)
    {
        var key = game.Process.ToLowerInvariant();
        lock (_iconCache)
        {
            if (_iconCache.TryGetValue(key, out var cached)) return cached;
        }

        // 1. Caché en disco (%APPDATA%\PulseCore\icon_cache\)
        var diskFile = Path.Combine(_iconCacheDir, Path.GetFileNameWithoutExtension(key) + ".png");
        if (File.Exists(diskFile))
        {
            try
            {
                var url = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(diskFile));
                CacheIconMemory(key, url);
                return url;
            }
            catch (Exception) { }
        }

        // 2. Cruce contra biblioteca detectada (Steam / Epic)
        if (_discovery is not null)
        {
            _discoveredCache ??= _discovery.ScanInstalledGames();
            var stem = Path.GetFileNameWithoutExtension(game.Process);
            var match = _discoveredCache.FirstOrDefault(d =>
                d.Name.Equals(game.Name, StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains(game.Name, StringComparison.OrdinalIgnoreCase) ||
                game.Name.Contains(d.Name, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(d.InstallPath) && stem.Length >= 4 &&
                 d.InstallPath.Contains(stem, StringComparison.OrdinalIgnoreCase)));
            if (match?.IconDataUrl is not null)
            {
                CacheIconMemory(key, match.IconDataUrl);
                return match.IconDataUrl;
            }
        }

        // 3. Proceso en ejecución actualmente
        var runningPath = TryGetRunningExePath(game.Process);
        if (runningPath is not null)
        {
            var url = GameDiscoveryService.ExtractExeIconDataUrl(runningPath);
            if (url is not null)
            {
                SaveIconCache(key, diskFile, url);
                return url;
            }
        }

        // 4. Rutas estándar conocidas (System32 / Riot / Epic / Steam)
        var fallbackExe = FindKnownExePath(game.Process);
        if (fallbackExe is not null)
        {
            var url = GameDiscoveryService.ExtractExeIconDataUrl(fallbackExe);
            if (url is not null)
            {
                SaveIconCache(key, diskFile, url);
                return url;
            }
        }

        return null;
    }

    private void CacheIconMemory(string key, string dataUrl)
    {
        lock (_iconCache) _iconCache[key] = dataUrl;
    }

    private void SaveIconCache(string key, string diskFile, string dataUrl)
    {
        CacheIconMemory(key, dataUrl);
        try
        {
            const string prefix = "data:image/png;base64,";
            if (!dataUrl.StartsWith(prefix, StringComparison.Ordinal)) return;
            Directory.CreateDirectory(_iconCacheDir);
            File.WriteAllBytes(diskFile, Convert.FromBase64String(dataUrl[prefix.Length..]));
        }
        catch (Exception) { }
    }

    private static string? TryGetRunningExePath(string exe)
    {
        Process[] procs = [];
        try
        {
            procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            foreach (var p in procs)
            {
                try { return p.MainModule?.FileName; }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        finally { foreach (var p in procs) p.Dispose(); }
        return null;
    }

    private static string? FindKnownExePath(string exe)
    {
        var sys32 = Path.Combine(Environment.SystemDirectory, exe);
        if (File.Exists(sys32)) return sys32;

        var winDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
        if (File.Exists(winDir)) return winDir;

        var lower = exe.ToLowerInvariant();
        string[] candidates = lower switch
        {
            "valorant-win64-shipping.exe" or "valorant.exe" =>
            [
                @"C:\Riot Games\VALORANT\live\VALORANT.exe",
                @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"
            ],
            "fortniteclient-win64-shipping.exe" =>
            [
                @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"
            ],
            "cyberpunk2077.exe" =>
            [
                @"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
                @"C:\Program Files\Epic Games\Cyberpunk2077\bin\x64\Cyberpunk2077.exe"
            ],
            _ => []
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public (bool Ok, string Message) SetEnabled(bool enabled)
    {
        bool hadSession;
        lock (_lock)
        {
            _config.IsEnabled = enabled;
            hadSession = _session is not null;
        }
        var saved = Save();
        if (!enabled && hadSession) EndSession("Modo Juego desactivado manualmente");
        return (saved, saved
            ? (enabled ? "Vigilancia automática activada." : "Vigilancia automática desactivada.")
            : "Se aplicó, pero no se pudo guardar la configuración en disco.");
    }

    public (bool Ok, string Message) AddGame(string? name, string? process)
    {
        name = (name ?? "").Trim();
        process = (process ?? "").Trim();
        if (name.Length is < 1 or > 40) return (false, "El nombre debe tener entre 1 y 40 caracteres.");
        if (process.Length == 0) return (false, "Indicá el ejecutable del juego (por ejemplo Juego.exe).");
        if (!process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process += ".exe";
        if (!ExeRx.IsMatch(process)) return (false, "Ejecutable inválido: usá solo el nombre del archivo, sin rutas (ej. Juego.exe).");

        lock (_lock)
        {
            if (_config.Games.Any(g => g.Process.Equals(process, StringComparison.OrdinalIgnoreCase)))
                return (false, $"{process} ya está en la lista.");
            _config.Games.Add(new GameProfile(name, process));
        }
        return Save() ? (true, $"“{name}” agregado.") : (true, $"“{name}” agregado, pero no se pudo guardar en disco.");
    }

    public (bool Ok, string Message) RemoveGame(string? process)
    {
        process = (process ?? "").Trim();
        string removedName;
        lock (_lock)
        {
            var g = _config.Games.FirstOrDefault(x => x.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
            if (g is null) return (false, "Ese juego no está en la lista.");
            _config.Games.Remove(g);
            removedName = g.Name;
        }
        return Save() ? (true, $"“{removedName}” eliminado.") : (true, $"“{removedName}” eliminado, pero no se pudo guardar en disco.");
    }

    // ---------- Watcher ----------
    public void Start()
    {
        lock (_lock)
        {
            if (_loop is not null || _disposed) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => LoopAsync(ct));
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            do
            {
                try { Tick(); }
                catch (Exception ex) { Debug.WriteLine($"GameMode tick: {ex.Message}"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private void Tick()
    {
        lock (_opLock)
        {
            Session? current;
            bool enabled;
            List<GameProfile> games;
            lock (_lock)
            {
                current = _session;
                enabled = _config.IsEnabled;
                games = _config.Games.ToList();
            }

            if (current is not null)
            {
                if (!IsAlive(current)) EndSession("El juego se cerró");
                return;
            }
            if (!enabled) return;

            foreach (var g in games)
            {
                var pid = FindRunning(g.Process);
                if (pid is not null)
                {
                    StartSession(g, pid.Value);
                    break;
                }
            }
        }
    }

    private static int? FindRunning(string exe)
    {
        var procs = Array.Empty<Process>();
        try
        {
            procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            foreach (var p in procs)
            {
                try { if (!p.HasExited) return p.Id; }
                catch (Exception) { }
            }
            return null;
        }
        catch (Exception) { return null; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    private static bool IsAlive(Session s)
    {
        try
        {
            using var p = Process.GetProcessById(s.Pid);
            return !p.HasExited &&
                   p.ProcessName.Equals(Path.GetFileNameWithoutExtension(s.Process), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private void StartSession(GameProfile game, int pid)
    {
        // 1. Guardar plan actual
        var plans = _power.GetPlans();
        var prev = plans.FirstOrDefault(p => p.IsActive);
        var target = plans.FirstOrDefault(p => p.Kind == "high") ?? plans.FirstOrDefault(p => p.Kind == "ultimate");

        // 2. Cambiar a Alto Rendimiento
        var changed = false;
        var newName = prev?.Name;
        string planMsg;
        if (target is null) planMsg = "Alto Rendimiento no está disponible en este equipo; el plan no se modificó.";
        else if (target.IsActive) { planMsg = $"{target.Name} ya estaba activo."; newName = target.Name; }
        else
        {
            var r = _power.SetPowerPlan(target.Guid);
            planMsg = r.Message;
            if (r.Success) { changed = true; newName = r.ActiveName ?? target.Name; }
        }

        // 3. Prioridad High (puede fallar por anti-cheat o permisos)
        var priorityOk = false;
        string priorityMsg;
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.PriorityClass = ProcessPriorityClass.High;
            priorityOk = true;
            priorityMsg = "Prioridad del juego elevada a Alto (High).";
        }
        catch (Exception ex)
        {
            priorityMsg = $"No se pudo elevar la prioridad (posible protección anti-cheat o permisos): {ex.Message}";
        }

        // 4. Limpieza rápida de RAM (sin tocar el proceso del juego)
        var ram = _memory.Optimize(pid);

        // 5. Registrar sesión + notificar
        var started = DateTimeOffset.Now;
        var session = new Session(game.Name, game.Process, pid, started, prev?.Guid, prev?.Name, newName, changed);
        lock (_lock) _session = session;

        Raise("game_mode_event", new
        {
            @event = "started",
            game = game.Name,
            process = game.Process,
            pid,
            startedAt = started,
            previousPlanName = prev?.Name,
            planName = newName,
            planChanged = changed,
            planMessage = planMsg,
            priorityRaised = priorityOk,
            priorityMessage = priorityMsg,
            ramFreedMb = ram.FreedMb,
            ramProcessesTrimmed = ram.ProcessesTrimmed,
            state = GetState()
        });
    }

    private void EndSession(string reason)
    {
        lock (_opLock)
        {
            Session? s;
            lock (_lock)
            {
                s = _session;
                _session = null;
            }
            if (s is null) return;

            var reverted = false;
            string? restoredName = null;
            string planMsg = "El plan de energía no había sido modificado por Modo Juego.";
            if (s.PlanChanged && s.PreviousPlanGuid is not null)
            {
                var r = _power.SetPowerPlan(s.PreviousPlanGuid);
                reverted = r.Success;
                restoredName = r.ActiveName ?? s.PreviousPlanName;
                planMsg = r.Success ? $"Plan de energía restaurado: {restoredName}." : $"No se pudo restaurar el plan: {r.Message}";
            }

            var ended = DateTimeOffset.Now;
            var durationSeconds = Math.Max(0L, (long)(ended - s.StartedAt).TotalSeconds);
            var record = new GameSessionRecord(
                s.Name,
                s.Process,
                s.StartedAt,
                ended,
                durationSeconds,
                s.MaxGpuTemp,
                Math.Round(s.MaxCpuLoad, 1),
                Math.Round(s.MaxRamGb, 2));

            lock (_lock)
            {
                _sessions.Insert(0, record);
                if (_sessions.Count > MaxStoredSessions)
                    _sessions.RemoveRange(MaxStoredSessions, _sessions.Count - MaxStoredSessions);
            }
            SaveSessions();

            Raise("game_session_ended", new
            {
                game = s.Name,
                process = s.Process,
                startedAt = s.StartedAt,
                endedAt = ended,
                durationSeconds,
                maxGpuTemp = record.MaxGpuTemp,
                maxCpuLoad = record.MaxCpuLoad,
                maxRamGb = record.MaxRamGb,
                session = record,
                sessions = GetSessions(),
                planReverted = reverted,
                restoredPlanName = restoredName,
                planMessage = planMsg,
                reason,
                state = GetState()
            });
        }
    }

    private void Raise(string type, object payload)
    {
        if (_disposed) return;
        try { Event?.Invoke(type, payload); }
        catch (Exception) { }
    }

    // ---------- Persistencia ----------
    private GameModeConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var cfg = JsonSerializer.Deserialize<GameModeConfig>(File.ReadAllText(_path), JsonOpts);
                if (cfg is not null)
                {
                    cfg.Games = (cfg.Games ?? new()).Where(g => !string.IsNullOrWhiteSpace(g.Process)).ToList();
                    return cfg;
                }
            }
        }
        catch (Exception) { /* archivo corrupto: se recrea con valores por defecto */ }

        var def = new GameModeConfig
        {
            IsEnabled = false,
            Games =
            [
                new("Fortnite", "FortniteClient-Win64-Shipping.exe"),
                new("Cyberpunk 2077", "Cyberpunk2077.exe"),
                new("Valorant", "Valorant-Win64-Shipping.exe")
            ]
        };
        _config = def;
        Save();
        return def;
    }

    private bool Save()
    {
        try
        {
            string json;
            lock (_lock)
            {
                var clean = new GameModeConfig
                {
                    IsEnabled = _config.IsEnabled,
                    Games = _config.Games.Select(g => new GameProfile(g.Name, g.Process)).ToList()
                };
                json = JsonSerializer.Serialize(clean, JsonOpts);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, true);
            return true;
        }
        catch (Exception) { return false; }
    }

    private List<GameSessionRecord> LoadSessions()
    {
        try
        {
            if (File.Exists(_sessionsPath))
            {
                var list = JsonSerializer.Deserialize<List<GameSessionRecord>>(File.ReadAllText(_sessionsPath), JsonOpts);
                if (list is not null) return list.Take(MaxStoredSessions).ToList();
            }
        }
        catch (Exception) { }
        return new List<GameSessionRecord>();
    }

    private bool SaveSessions()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_sessions, JsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(_sessionsPath)!);
            var tmp = _sessionsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _sessionsPath, true);
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch (Exception) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }

        // Si la app se cierra durante una partida, dejamos el plan de energía como estaba.
        Session? s;
        lock (_lock) { s = _session; _session = null; }
        if (s is { PlanChanged: true, PreviousPlanGuid: not null })
        {
            try { _power.SetPowerPlan(s.PreviousPlanGuid); } catch (Exception) { }
        }
        _cts?.Dispose();
    }
}
