using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using PulseCore.App.Models;
using PulseCore.App.Services;

namespace PulseCore.App;

public partial class MainWindow : Window
{
    private const string VirtualHost = "pulsecore.app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly CancellationTokenSource _cts = new();
    private TelemetryService? _telemetry;
    private Task? _telemetryLoop;

    private readonly PowerPlanService _power = new();
    private readonly MemoryOptimizerService _memory = new();
    private readonly CleanerService _cleaner = new();
    private readonly ProcessMonitorService _processes = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => OnWindowStateChanged();
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += (_, _) => _cts.Cancel();
        Closed += OnClosedCleanup;
    }

    // ---------- Loop de telemetría ----------
    private void StartTelemetryLoop()
    {
        if (_telemetryLoop is not null) return;
        _telemetryLoop = Task.Run(() => TelemetryLoopAsync(_cts.Token));
    }

    /// <summary>Envía un mensaje a JS. SIEMPRE se ejecuta en el hilo de UI.</summary>
    private Task PostToUiAsync(string json, CancellationToken ct) =>
        Dispatcher.InvokeAsync(() => WebView.CoreWebView2?.PostWebMessageAsJson(json),
            System.Windows.Threading.DispatcherPriority.Normal, ct).Task;

    private Task ReportErrorAsync(string stage, Exception ex, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new { type = "telemetry_error", message = $"[{stage}] {ex}" }, JsonOptions);
        return PostToUiAsync(json, ct);
    }

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        try
        {
            // Computer.Open() es lento: se hace fuera del hilo de UI.
            try { _telemetry = new TelemetryService(); }
            catch (Exception ex) { await ReportErrorAsync("init", ex, ct); return; }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000));
            do
            {
                try
                {
                    var snapshot = _telemetry.GetSnapshot();
                    var json = JsonSerializer.Serialize(new { type = "telemetry", data = snapshot }, JsonOptions);
                    await PostToUiAsync(json, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    try { await ReportErrorAsync("tick", ex, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* sin canal para reportar */ }
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // El loop arranca sólo cuando la página (y su listener de mensajes) ya cargó.
        if (e.IsSuccess) StartTelemetryLoop();
        else MessageBox.Show($"Falló la carga de la UI: {e.WebErrorStatus}", "PulseCore");
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        _cts.Cancel();
        try { _telemetryLoop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _telemetry?.Dispose();
        _cts.Dispose();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Datos de usuario de WebView2 en %LOCALAPPDATA% (carpeta escribible).
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PulseCore", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await WebView.EnsureCoreWebView2Async(env);

            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x06, 0x08, 0x0d);

            var settings = WebView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
#if !DEBUG
            settings.AreDevToolsEnabled = false;
#endif

            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.Navigate($"https://{VirtualHost}/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "No se pudo inicializar WebView2. ¿Está instalado el WebView2 Runtime?\n\n" + ex.Message,
                "PulseCore", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    // ---------- JS -> C# ----------
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string type;
        JsonElement? payload = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (doc.RootElement.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object)
                payload = p.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        switch (type)
        {
            case "window.drag":
                if (Mouse.LeftButton == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch (InvalidOperationException) { }
                }
                break;
            case "window.minimize":
                WindowState = WindowState.Minimized;
                break;
            case "window.toggleMaximize":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "window.close":
                Close();
                break;
            case "get_optimizer_state":
                _ = RunActionAsync("get_optimizer_state", "optimizer_state", false,
                    () => Task.FromResult<object>(GetOptimizerState()));
                break;
            case "set_power_plan":
            {
                var guid = PayloadString(payload, "guid") ?? "";
                _ = RunActionAsync("set_power_plan", "power_plan_result", true,
                    () => Task.FromResult<object>(_power.SetPowerPlan(guid)));
                break;
            }
            case "optimize_ram":
                _ = RunActionAsync("optimize_ram", "ram_result", true,
                    () => Task.FromResult<object>(_memory.Optimize()));
                break;
            case "scan_cleanup":
                _ = RunActionAsync("scan_cleanup", "scan_result", true,
                    async () => await _cleaner.ScanAsync());
                break;
            case "run_cleanup":
            {
                var cats = PayloadStrings(payload, "categories");
                var progress = new Progress<CleanItemResult>(item => Send("cleanup_progress", item));
                _ = RunActionAsync("run_cleanup", "cleanup_result", true,
                    async () => await _cleaner.CleanAsync(cats, progress));
                break;
            }
            case "run_quick_boost":
                _ = RunActionAsync("run_quick_boost", "boost_result", true,
                    () => Task.FromResult<object>(QuickBoost()));
                break;
            case "backend.ping":
                Send("backend.pong", GetSystemInfo());
                break;
            case "app.ready":
                SendWindowState();
                Send("system.info", GetSystemInfo());
                break;
        }
    }

    // ---------- Acciones del Optimizador / Limpieza ----------
    private static string? PayloadString(JsonElement? p, string name) =>
        p is { ValueKind: JsonValueKind.Object } el
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string[] PayloadStrings(JsonElement? p, string name)
    {
        if (p is { ValueKind: JsonValueKind.Object } el
            && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            return v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToArray();
        }
        return [];
    }

    /// <summary>Ejecuta trabajo pesado fuera del hilo de UI y responde por el puente. Las acciones exclusivas no se solapan.</summary>
    private async Task RunActionAsync(string action, string replyType, bool exclusive, Func<Task<object>> work)
    {
        var gated = false;
        if (exclusive)
        {
            if (!await _actionGate.WaitAsync(0))
            {
                Send("action_error", new { action, message = "Hay otra operación en curso. Esperá a que termine." });
                return;
            }
            gated = true;
        }

        try
        {
            var result = await Task.Run(work);
            Send(replyType, result);
        }
        catch (Exception ex)
        {
            Send("action_error", new { action, message = ex.Message });
        }
        finally
        {
            if (gated) _actionGate.Release();
        }
    }

    private object GetOptimizerState()
    {
        var plans = _power.GetPlans();
        var active = plans.FirstOrDefault(p => p.IsActive);
        return new
        {
            plans,
            activePlan = active,
            topProcesses = _processes.GetTopByMemory(5)
        };
    }

    /// <summary>Cadena: Alto Rendimiento (si existe) + liberar RAM + vaciar DNS. Todo reversible (el plan previo se informa).</summary>
    private QuickBoostResult QuickBoost()
    {
        var sw = Stopwatch.StartNew();
        var plans = _power.GetPlans();
        var prev = plans.FirstOrDefault(p => p.IsActive);
        var target = plans.FirstOrDefault(p => p.Kind == "high") ?? plans.FirstOrDefault(p => p.Kind == "ultimate");

        var available = target is not null;
        var changed = false;
        var planName = prev?.Name ?? "Desconocido";
        string planMsg;

        if (target is null)
        {
            planMsg = "Alto Rendimiento no está disponible en este equipo; el plan no se modificó.";
        }
        else if (target.IsActive)
        {
            planName = target.Name;
            planMsg = $"{target.Name} ya estaba activo.";
        }
        else
        {
            var r = _power.SetPowerPlan(target.Guid);
            planMsg = r.Message;
            if (r.Success)
            {
                changed = true;
                planName = r.ActiveName ?? target.Name;
            }
        }

        var ram = _memory.Optimize();
        var (dnsOk, dnsMsg) = _cleaner.FlushDns();
        sw.Stop();

        return new QuickBoostResult(available, changed, planName, prev?.Guid, prev?.Name,
            planMsg, ram, dnsOk, dnsMsg, sw.ElapsedMilliseconds);
    }

    // ---------- C# -> JS ----------
    private void Send(string type, object? payload = null)
    {
        if (WebView.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new { type, payload }, JsonOptions);
        WebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static object GetSystemInfo() => new
    {
        machineName = Environment.MachineName,
        windowsVersion = RuntimeInformation.OSDescription,
        osVersion = Environment.OSVersion.Version.ToString(),
        architecture = RuntimeInformation.OSArchitecture.ToString(),
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        logicalCores = Environment.ProcessorCount
    };

    private void OnWindowStateChanged()
    {
        // Con WindowStyle=None + WindowChrome, al maximizar la ventana se desborda
        // por el grosor del borde de redimensionado; lo compensamos con un margen.
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(
                SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.FixedFrameVerticalBorderWidth,
                SystemParameters.WindowResizeBorderThickness.Top + SystemParameters.FixedFrameHorizontalBorderHeight,
                SystemParameters.WindowResizeBorderThickness.Right + SystemParameters.FixedFrameVerticalBorderWidth,
                SystemParameters.WindowResizeBorderThickness.Bottom + SystemParameters.FixedFrameHorizontalBorderHeight)
            : new Thickness(0);
        SendWindowState();
    }

    private void SendWindowState() =>
        Send("window.state", new { maximized = WindowState == WindowState.Maximized });
}
