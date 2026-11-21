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

    public int CurrentFps => GetCurrentFps();

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

    public int GetCurrentFps()
    {
        if (!_running) return 0;
        RefreshForegroundPid();
        if (_foregroundPid <= 4) return 0;

        var now = Stopwatch.GetTimestamp();
        var oneSecAgo = now - Stopwatch.Frequency;

        lock (_lock)
        {
            while (_frameTicks.Count > 0 && _frameTicks.Peek() < oneSecAgo)
                _frameTicks.Dequeue();

            // Si hay menos de 2 frames en el último segundo, asumimos ventana estática/escritorio.
            return _frameTicks.Count >= 2 ? _frameTicks.Count : 0;
        }
    }

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
