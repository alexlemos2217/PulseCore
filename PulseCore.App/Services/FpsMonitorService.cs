using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PulseCore.App.Services;

/// <summary>
/// Contador de FPS pasivo y sin inyección basado en eventos ETW de DXGI y D3D9 (solo lectura).
/// Solo escucha mientras el Mini-HUD está visible para no consumir recursos en segundo plano.
/// </summary>
public sealed class FpsMonitorService : IDisposable
{
    private const string SessionName = "PulseCoreFpsSession";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private static readonly HashSet<string> IgnoredShellProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "ApplicationFrameHost", "LockApp", "PulseCore", "PulseCore.App"
    };

    private readonly object _lock = new();
    private readonly Queue<long> _frameTicks = new();

    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile int _foregroundPid;
    private volatile bool _running;
    private volatile bool _disposed;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public int CurrentFps => GetMetrics().CurrentFps;
    public int OnePercentLowFps => GetMetrics().OnePercentLowFps;

    public void Start()
    {
        lock (_lock)
        {
            if (_running || _disposed) return;
            _running = true;
            _frameTicks.Clear();
        }

        RefreshForegroundPid();
        _thread = new Thread(RunEtwLoop)
        {
            IsBackground = true,
            Name = "PulseCore-ETW-FPS"
        };
        _thread.Start();
    }

    public void Stop()
    {
        TraceEventSession? s;
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            s = _session;
            _session = null;
            _frameTicks.Clear();
        }

        try { s?.Stop(); } catch (Exception) { }
        try { s?.Dispose(); } catch (Exception) { }
    }

    public (int CurrentFps, int OnePercentLowFps) GetMetrics()
    {
        if (!_running) return (0, 0);
        RefreshForegroundPid();
        if (_foregroundPid <= 4) return (0, 0);

        var now = Stopwatch.GetTimestamp();
        var oneSecAgo = now - Stopwatch.Frequency;
        var twoSecAgo = now - Stopwatch.Frequency * 2;

        long[] snapshot;
        lock (_lock)
        {
            while (_frameTicks.Count > 0 && _frameTicks.Peek() < twoSecAgo)
                _frameTicks.Dequeue();
            if (_frameTicks.Count < 2) return (0, 0);
            snapshot = _frameTicks.ToArray();
        }

        var fps = 0;
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i] >= oneSecAgo) fps++;
            else break;
        }
        if (fps < 2) return (0, 0);

        var deltas = new List<double>(snapshot.Length);
        var tickToMs = 1000.0 / Stopwatch.Frequency;
        for (var i = 1; i < snapshot.Length; i++)
        {
            var ms = (snapshot[i] - snapshot[i - 1]) * tickToMs;
            if (ms is > 0.2 and < 500.0) deltas.Add(ms);
        }

        if (deltas.Count < 10) return (fps, 0);
        deltas.Sort((a, b) => b.CompareTo(a));
        var take = Math.Max(1, (int)Math.Ceiling(deltas.Count * 0.01));
        double sum = 0;
        for (var i = 0; i < take; i++) sum += deltas[i];
        var avgSlowMs = sum / take;
        var onePct = avgSlowMs > 0 ? (int)Math.Round(1000.0 / avgSlowMs) : 0;
        if (onePct > fps) onePct = fps;
        return (fps, onePct);
    }

    public int GetCurrentFps() => GetMetrics().CurrentFps;

    private void RefreshForegroundPid()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { SetForegroundPid(0); return; }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid <= 4 || pid == Environment.ProcessId) { SetForegroundPid(0); return; }

            if ((int)pid == _foregroundPid) return;

            using var proc = Process.GetProcessById((int)pid);
            if (IgnoredShellProcs.Contains(proc.ProcessName))
            {
                SetForegroundPid(0);
                return;
            }
            SetForegroundPid((int)pid);
        }
        catch (Exception)
        {
            SetForegroundPid(0);
        }
    }

    private void SetForegroundPid(int pid)
    {
        if (_foregroundPid == pid) return;
        _foregroundPid = pid;
        lock (_lock) _frameTicks.Clear();
    }

    private void RunEtwLoop()
    {
        try
        {
            // Limpiar posible sesión huérfana de una ejecución previa
            try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch (Exception) { }

            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            lock (_lock)
            {
                if (!_running || _disposed) { session.Dispose(); return; }
                _session = session;
            }

            session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            session.EnableProvider(D3d9Provider, TraceEventLevel.Informational);

            session.Source.Dynamic.All += data =>
            {
                var targetPid = _foregroundPid;
                if (targetPid <= 4 || data.ProcessID != targetPid) return;

                var isPresent =
                    (data.ProviderGuid == DxgiProvider && ((int)data.ID == 42 ||
                        (data.EventName.Contains("Present", StringComparison.OrdinalIgnoreCase) &&
                         data.EventName.Contains("Start", StringComparison.OrdinalIgnoreCase)))) ||
                    (data.ProviderGuid == D3d9Provider && ((int)data.ID == 1 ||
                        data.EventName.Contains("Present", StringComparison.OrdinalIgnoreCase)));

                if (!isPresent) return;

                var tick = Stopwatch.GetTimestamp();
                lock (_lock)
                {
                    _frameTicks.Enqueue(tick);
                    if (_frameTicks.Count > 2000) _frameTicks.Dequeue();
                }
            };

            session.Source.Process();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FpsMonitorService ETW stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
