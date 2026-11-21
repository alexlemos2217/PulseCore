using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PulseCore.App.Services;

/// <summary>
/// Contador de FPS pasivo y sin inyección basado en eventos ETW de DXGI, DxgKrnl y D3D9 (solo lectura).
/// Soporta DX9, DX11, DX12, Vulkan, OpenGL y juegos UWP filtrando por el PID de la ventana en primer plano.
/// Solo escucha mientras el Mini-HUD está visible para no consumir recursos en segundo plano.
/// </summary>
public sealed class FpsMonitorService : IDisposable
{
    private const string SessionName = "PulseCoreFpsSession";

    // Providers oficiales de presentación en Windows (misma base que PresentMon)
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");      // Microsoft-Windows-DXGI
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");   // Microsoft-Windows-DxgKrnl
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");      // Microsoft-Windows-D3D9

    // Keywords de Microsoft-Windows-DxgKrnl: Base (0x40) | Performance (0x01)
    private const ulong DxgKrnlKeywords = 0x41UL;

    private static readonly HashSet<string> IgnoredShellProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "LockApp", "PulseCore", "PulseCore.App", "Taskmgr"
    };

    private readonly record struct FrameSample(double EventMs, long ArrivalTick);

    private readonly object _lock = new();
    // Colas separadas por origen para evitar doble conteo cuando DX11/DX12 emite DXGI + DxgKrnl
    private readonly Queue<FrameSample> _dxgiFrames = new();
    private readonly Queue<FrameSample> _dxgKrnlHistoryFrames = new();
    private readonly Queue<FrameSample> _dxgKrnlPresentFrames = new();
    private readonly Queue<FrameSample> _d3d9Frames = new();

    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile int _foregroundPid;
    private volatile bool _running;
    private volatile bool _disposed;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public int CurrentFps => GetMetrics().CurrentFps;
    public int OnePercentLowFps => GetMetrics().OnePercentLowFps;

    public void Start()
    {
        lock (_lock)
        {
            if (_running || _disposed) return;
            _running = true;
            ClearAllQueuesLocked();
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
            ClearAllQueuesLocked();
        }

        try { s?.Stop(); } catch (Exception) { }
        try { s?.Dispose(); } catch (Exception) { }
    }

    public (int CurrentFps, int OnePercentLowFps) GetMetrics()
    {
        if (!_running) return (0, 0);
        RefreshForegroundPid();
        if (_foregroundPid <= 4) return (0, 0);

        var nowTick = Stopwatch.GetTimestamp();
        var staleCutoff = nowTick - (long)(Stopwatch.Frequency * 2.2);

        FrameSample[]? snapshot;
        lock (_lock)
        {
            // Prioridad: DXGI -> DxgKrnl PresentHistory (Vulkan/DX12/OpenGL) -> DxgKrnl Present -> D3D9
            snapshot = TryTakeActiveSnapshotLocked(_dxgiFrames, staleCutoff)
                    ?? TryTakeActiveSnapshotLocked(_dxgKrnlHistoryFrames, staleCutoff)
                    ?? TryTakeActiveSnapshotLocked(_dxgKrnlPresentFrames, staleCutoff)
                    ?? TryTakeActiveSnapshotLocked(_d3d9Frames, staleCutoff);
        }

        if (snapshot is null || snapshot.Length < 2) return (0, 0);

        // Usar timestamps reales del kernel ETW (EventMs) para inmune a ráfagas de flush de buffer
        var latestMs = snapshot[^1].EventMs;
        var oneSecAgoMs = latestMs - 1000.0;
        var twoSecAgoMs = latestMs - 2000.0;

        var fps = 0;
        var firstInWindow = snapshot.Length - 1;
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i].EventMs >= oneSecAgoMs)
            {
                fps++;
                firstInWindow = i;
            }
            else
            {
                break;
            }
        }

        // Si hubo menos de 2 frames en el último segundo, no hay renderizado 3D continuo
        if (fps < 2) return (0, 0);

        // Si el span real dentro del segundo es < 900ms (ej. acaba de arrancar), normalizar sin disparar picos falsos
        var spanMs = latestMs - snapshot[firstInWindow].EventMs;
        if (spanMs is >= 250.0 and < 920.0 && fps >= 5)
        {
            fps = (int)Math.Round((fps - 1) * 1000.0 / spanMs);
        }

        // 1% Low FPS sobre la ventana de 2 segundos usando frametimes reales del kernel
        var deltas = new List<double>(snapshot.Length);
        for (var i = 1; i < snapshot.Length; i++)
        {
            if (snapshot[i].EventMs < twoSecAgoMs) continue;
            var dt = snapshot[i].EventMs - snapshot[i - 1].EventMs;
            if (dt is > 0.2 and < 500.0) deltas.Add(dt);
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

    private static FrameSample[]? TryTakeActiveSnapshotLocked(Queue<FrameSample> q, long staleCutoff)
    {
        if (q.Count < 2) return null;

        // Descartar muestras más viejas que 3 segundos en reloj de llegada
        var pruneCutoff = staleCutoff - Stopwatch.Frequency;
        while (q.Count > 0 && q.Peek().ArrivalTick < pruneCutoff)
            q.Dequeue();

        if (q.Count < 2) return null;

        var arr = q.ToArray();
        // Si el último frame llegó hace más de 2.2s, esta fuente está inactiva
        if (arr[^1].ArrivalTick < staleCutoff) return null;

        return arr;
    }

    private void ClearAllQueuesLocked()
    {
        _dxgiFrames.Clear();
        _dxgKrnlHistoryFrames.Clear();
        _dxgKrnlPresentFrames.Clear();
        _d3d9Frames.Clear();
    }

    private void RefreshForegroundPid()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { SetForegroundPid(0); return; }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid <= 4 || pid == Environment.ProcessId) { SetForegroundPid(0); return; }

            // Resolver proceso hijo real para juegos UWP / Xbox Game Pass alojados en ApplicationFrameHost
            if (IsProcessName((int)pid, "ApplicationFrameHost"))
            {
                var childPid = FindUwpChildPid(hwnd, pid);
                if (childPid > 4 && childPid != Environment.ProcessId)
                    pid = childPid;
                else
                {
                    SetForegroundPid(0);
                    return;
                }
            }

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

    private static bool IsProcessName(int pid, string expected)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return string.Equals(p.ProcessName, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static uint FindUwpChildPid(IntPtr parentHwnd, uint hostPid)
    {
        uint found = 0;
        EnumChildWindows(parentHwnd, (child, lParam) =>
        {
            _ = GetWindowThreadProcessId(child, out var childPid);
            if (childPid > 4 && childPid != hostPid)
            {
                found = childPid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void SetForegroundPid(int pid)
    {
        if (_foregroundPid == pid) return;
        _foregroundPid = pid;
        lock (_lock) ClearAllQueuesLocked();
    }

    private void RunEtwLoop()
    {
        try
        {
            // Limpiar posible sesión huérfana de una ejecución previa
            try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch (Exception) { }

            var session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,
                BufferSizeMB = 16
            };
            lock (_lock)
            {
                if (!_running || _disposed) { session.Dispose(); return; }
                _session = session;
            }

            session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, DxgKrnlKeywords);
            session.EnableProvider(D3d9Provider, TraceEventLevel.Informational);

            session.Source.Dynamic.All += HandleTraceEvent;
            session.Source.UnhandledEvents += HandleTraceEvent;

            session.Source.Process();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FpsMonitorService ETW stopped: {ex.Message}");
        }
    }

    private void HandleTraceEvent(TraceEvent data)
    {
        var targetPid = _foregroundPid;
        if (targetPid <= 4 || data.ProcessID != targetPid) return;

        Queue<FrameSample>? targetQueue = null;
        var id = (int)data.ID;

        if (data.ProviderGuid == DxgiProvider)
        {
            // 42 = Present_Start (DX11/DX12), 55 = PresentMultiplaneOverlay_Start (Present1 / Flip-Model)
            if (id is 42 or 55 ||
                (data.EventName.Contains("Present", StringComparison.OrdinalIgnoreCase) &&
                 data.EventName.Contains("Start", StringComparison.OrdinalIgnoreCase)))
            {
                targetQueue = _dxgiFrames;
            }
        }
        else if (data.ProviderGuid == DxgKrnlProvider)
        {
            // 215 = PresentHistory_Start, 171 = PresentHistoryDetailed_Start (DX11/DX12/Vulkan/OpenGL Flip)
            if (id is 215 or 171 ||
                (data.EventName.Contains("PresentHistory", StringComparison.OrdinalIgnoreCase) &&
                 data.EventName.Contains("Start", StringComparison.OrdinalIgnoreCase)))
            {
                targetQueue = _dxgKrnlHistoryFrames;
            }
            // 184 = Present_Start (D3DKMTPresent — DX9/OpenGL/Vulkan/Blt)
            else if (id == 184 ||
                     (data.EventName.Contains("Present", StringComparison.OrdinalIgnoreCase) &&
                      data.EventName.Contains("Start", StringComparison.OrdinalIgnoreCase) &&
                      !data.EventName.Contains("History", StringComparison.OrdinalIgnoreCase)))
            {
                targetQueue = _dxgKrnlPresentFrames;
            }
        }
        else if (data.ProviderGuid == D3d9Provider)
        {
            if (id == 1 ||
                (data.EventName.Contains("Present", StringComparison.OrdinalIgnoreCase) &&
                 data.EventName.Contains("Start", StringComparison.OrdinalIgnoreCase)))
            {
                targetQueue = _d3d9Frames;
            }
        }

        if (targetQueue is null) return;

        var sample = new FrameSample(data.TimeStampRelativeMSec, Stopwatch.GetTimestamp());
        lock (_lock)
        {
            targetQueue.Enqueue(sample);
            if (targetQueue.Count > 2400) targetQueue.Dequeue();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
