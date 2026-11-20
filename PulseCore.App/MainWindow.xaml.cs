using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace PulseCore.App;

public partial class MainWindow : Window
{
    private const string VirtualHost = "pulsecore.app";

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => OnWindowStateChanged();
        Loaded += async (_, _) => await InitWebViewAsync();
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
