using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
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

            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x09, 0x0b, 0x10);

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
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
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
            case "backend.ping":
                Send("backend.pong", GetSystemInfo());
                break;
            case "app.ready":
                SendWindowState();
                break;
        }
    }

    // ---------- C# -> JS ----------
    private void Send(string type, object? payload = null)
    {
        if (WebView.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(
            new { type, payload },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
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
