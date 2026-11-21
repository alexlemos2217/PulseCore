using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using PulseCore.App.Models;
using PulseCore.App.Services;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

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
    private readonly NetworkPingService _ping = new();
    private readonly NetworkOptimizerService _netOpt = new();
    private readonly FortniteService _fortnite = new();
    private readonly GameDiscoveryService _discovery = new();
    private readonly WingetService _winget = new();
    private readonly UpdateService _updater = new();
    private readonly ProfileService _profiles = new();
    private readonly FpsMonitorService _fps = new();
    private readonly SystemCheckupService _checkup;
    private readonly WindowsSuiteService _winSuite;
    private readonly GameModeService _gameMode;
    private volatile TelemetrySnapshot? _lastSnapshot;
    private Forms.NotifyIcon? _trayIcon;
    private HudWindow? _hud;
    private long? _lastHudPingMs;
    private long _lastHudPingTick;
    private bool _forceExit;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/pulsecore.ico", UriKind.Absolute));
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        InitTrayIcon();
        _checkup = new SystemCheckupService(_power);
        _winSuite = new WindowsSuiteService(_checkup);
        _checkup.ExternalReverter = _winSuite.RevertDebloatRecord;
        _gameMode = new GameModeService(_power, _memory, discovery: _discovery);
        _gameMode.Event += (type, payload) => Dispatcher.InvokeAsync(() => Send(type, payload));
        StateChanged += (_, _) => OnWindowStateChanged();
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += OnWindowClosing;
        Closed += OnClosedCleanup;
    }

    // ---------- Bandeja del Sistema (System Tray) ----------
    private void InitTrayIcon()
    {
        System.Drawing.Icon trayIco;
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/pulsecore.ico", UriKind.Absolute));
            trayIco = res is not null ? new System.Drawing.Icon(res.Stream) : System.Drawing.SystemIcons.Application;
        }
        catch (Exception)
        {
            trayIco = System.Drawing.SystemIcons.Application;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir PulseCore", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Mostrar/Ocultar Mini-HUD", null, (_, _) => Dispatcher.Invoke(ToggleHud));
        menu.Items.Add("Desbloquear/Bloquear Mini-HUD", null, (_, _) => Dispatcher.Invoke(ToggleHudLock));
        menu.Items.Add("⚡ Boost Rápido", null, (_, _) => _ = RunTrayQuickBoostAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = trayIco,
            Text = "PulseCore — Monitor & Optimizador",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        _forceExit = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_forceExit)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon?.ShowBalloonTip(
                    3500,
                    "PulseCore sigue activo",
                    "Se minimizó a la bandeja del sistema para seguir vigilando tus juegos.",
                    Forms.ToolTipIcon.Info);
            }
            return;
        }
        _cts.Cancel();
    }

    private async Task RunTrayQuickBoostAsync()
    {
        if (!await _actionGate.WaitAsync(0))
        {
            _trayIcon?.ShowBalloonTip(3000, "PulseCore", "Hay otra operación en curso. Esperá un momento.", Forms.ToolTipIcon.Warning);
            return;
        }
        try
        {
            var r = await Task.Run(QuickBoost);
            var ramText = r.Ram.FreedMb >= 1024
                ? $"+{r.Ram.FreedMb / 1024.0:F2} GB RAM"
                : r.Ram.FreedMb > 0 ? $"+{r.Ram.FreedMb:F0} MB RAM" : "RAM optimizada";
            var planText = r.PlanChanged ? $"{r.PlanName} activo" : r.PlanName;
            var dnsText = r.DnsFlushed ? "DNS limpio" : "DNS sin cambios";

            _trayIcon?.ShowBalloonTip(4500, "PulseCore — Boost Rápido", $"{ramText} · {planText} · {dnsText}", Forms.ToolTipIcon.Info);
            await Dispatcher.InvokeAsync(() => Send("boost_result", r));
        }
        catch (Exception ex)
        {
            _trayIcon?.ShowBalloonTip(4000, "PulseCore — Error", ex.Message, Forms.ToolTipIcon.Error);
        }
        finally
        {
            _actionGate.Release();
        }
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
                    _lastSnapshot = snapshot;
                    _gameMode.RecordTelemetrySample(snapshot.Cpu.UsagePercent, snapshot.Gpu.TemperatureC, snapshot.Ram.UsedGb);

                    var hudVisible = false;
                    var (fps, onePctLow) = _fps.GetMetrics();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        hudVisible = _hud is { IsVisible: true };
                        if (hudVisible) _hud!.UpdateMetrics(snapshot, _lastHudPingMs, fps, onePctLow);
                    }, System.Windows.Threading.DispatcherPriority.Normal, ct);

                    if (hudVisible && Environment.TickCount64 - _lastHudPingTick >= 5000)
                    {
                        _lastHudPingTick = Environment.TickCount64;
                        _ = Task.Run(ProbeHudPingAsync, ct);
                    }

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
        if (e.IsSuccess)
        {
            StartTelemetryLoop();
            _gameMode.Start();
            _ = CheckForUpdatesAsync();
            _ = RunDueMaintenanceAsync();
        }
        else MessageBox.Show($"Falló la carga de la UI: {e.WebErrorStatus}", "PulseCore");
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        _fps.Dispose();
        if (_hud is not null)
        {
            try { _hud.Close(); } catch (Exception) { }
            _hud = null;
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _cts.Cancel();
        try { _telemetryLoop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _gameMode.Dispose();
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
            case "get_fortnite_status":
                _ = RunActionAsync("get_fortnite_status", "fortnite_status", false,
                    () => Task.FromResult<object>(_fortnite.GetStatus()));
                break;
            case "apply_fortnite_preset":
            {
                var preset = PayloadString(payload, "preset");
                var fps = PayloadInt(payload, "fpsLimit");
                var ro = PayloadBool(payload, "setReadOnly");
                _ = RunActionAsync("apply_fortnite_preset", "fortnite_preset_result", true,
                    () => Task.FromResult<object>(_fortnite.ApplyPreset(preset, fps, ro)));
                break;
            }
            case "restore_fortnite_backup":
                _ = RunActionAsync("restore_fortnite_backup", "fortnite_restore_result", true,
                    () => Task.FromResult<object>(_fortnite.RestoreBackup()));
                break;
            case "clean_fortnite_files":
            {
                var demos = PayloadBool(payload, "cleanDemos");
                var logs = PayloadBool(payload, "cleanLogs");
                _ = RunActionAsync("clean_fortnite_files", "fortnite_clean_result", true,
                    () => Task.FromResult<object>(_fortnite.CleanFortniteFiles(demos, logs)));
                break;
            }
            case "run_speedtest":
                _ = Task.Run(async () =>
                {
                    var res = await _netOpt.RunSpeedTestAsync(
                        prog => Dispatcher.InvokeAsync(() => Send("speedtest_progress", prog)),
                        _cts.Token);
                    await Dispatcher.InvokeAsync(() => Send("speedtest_result", res));
                });
                break;
            case "search_winget_apps":
            {
                var q = PayloadString(payload, "query");
                _ = RunActionAsync("search_winget_apps", "winget_search_result", false,
                    async () => await _winget.SearchPackagesAsync(q, _cts.Token));
                break;
            }
            case "get_profile_settings":
                _ = RunActionAsync("get_profile_settings", "profile_settings", false,
                    () => Task.FromResult<object>(_profiles.GetSettings()));
                break;
            case "apply_quick_profile":
            {
                var profile = PayloadString(payload, "profile") ?? "";
                _ = ApplyQuickProfileAsync(profile);
                break;
            }
            case "toggle_weekly_maintenance":
            {
                var enabled = PayloadBool(payload, "enabled");
                _ = RunActionAsync("toggle_weekly_maintenance", "profile_settings", false,
                    () => Task.FromResult<object>(_profiles.SetWeeklyMaintenance(enabled)));
                break;
            }
            case "export_config":
                ExportConfigDialog();
                break;
            case "import_config":
                ImportConfigDialog();
                break;
            case "get_dns_state":
                _ = RunActionAsync("get_dns_state", "dns_state", false,
                    () => Task.FromResult<object>(_netOpt.GetDnsState()));
                break;
            case "benchmark_dns":
                _ = RunActionAsync("benchmark_dns", "dns_state", false,
                    async () => await _netOpt.BenchmarkDnsAsync(_cts.Token));
                break;
            case "apply_dns":
            {
                var prov = PayloadString(payload, "providerId");
                _ = RunActionAsync("apply_dns", "dns_apply_result", true,
                    () => Task.FromResult<object>(_netOpt.ApplyDns(prov)));
                break;
            }
            case "run_packet_loss":
            {
                var host = PayloadString(payload, "host");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var res = await _netOpt.RunPacketLossTestAsync(
                            host,
                            prog => Dispatcher.InvokeAsync(() => Send("packet_loss_progress", prog)),
                            _cts.Token);
                        await Dispatcher.InvokeAsync(() => Send("packet_loss_result", res));
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.InvokeAsync(() => Send("action_error", new { action = "run_packet_loss", message = ex.Message }));
                    }
                });
                break;
            }
            case "get_debloat_items":
                _ = RunActionAsync("get_debloat_items", "debloat_items", false,
                    () => Task.FromResult<object>(new { items = _winSuite.GetDebloatItems() }));
                break;
            case "toggle_debloat_item":
            {
                var id = PayloadString(payload, "id");
                var enable = PayloadBool(payload, "enable");
                _ = RunActionAsync("toggle_debloat_item", "debloat_result", true,
                    () => Task.FromResult<object>(_winSuite.ToggleDebloatItem(id, enable)));
                break;
            }
            case "get_startup_apps":
                _ = RunActionAsync("get_startup_apps", "startup_apps", false,
                    () => Task.FromResult<object>(new { apps = _winSuite.GetStartupApps() }));
                break;
            case "toggle_startup_app":
            {
                var name = PayloadString(payload, "name");
                var location = PayloadString(payload, "location");
                var enable = PayloadBool(payload, "enable");
                _ = RunActionAsync("toggle_startup_app", "startup_result", true,
                    () => Task.FromResult<object>(_winSuite.ToggleStartupApp(name, location, enable)));
                break;
            }
            case "run_windows_repair":
            {
                var tool = PayloadString(payload, "tool");
                _ = Task.Run(() => _winSuite.RunRepairToolAsync(
                    tool,
                    ev => Dispatcher.InvokeAsync(() => Send("repair_output", ev)),
                    done => Dispatcher.InvokeAsync(() => Send("repair_completed", done)),
                    _cts.Token));
                break;
            }
            case "get_winget_catalog":
                _ = RunActionAsync("get_winget_catalog", "winget_catalog", false,
                    () => Task.FromResult<object>(_winget.GetCatalog()));
                break;
            case "install_winget_apps":
            {
                var ids = PayloadStrings(payload, "packageIds").ToList();
                _ = Task.Run(() => _winget.InstallPackagesAsync(
                    ids,
                    ev => Dispatcher.InvokeAsync(() => Send("winget_output", ev)),
                    done => Dispatcher.InvokeAsync(() => Send("winget_completed", done)),
                    _cts.Token));
                break;
            }
            case "upgrade_all_winget_apps":
                _ = Task.Run(() => _winget.UpgradeAllAsync(
                    ev => Dispatcher.InvokeAsync(() => Send("winget_output", ev)),
                    done => Dispatcher.InvokeAsync(() => Send("winget_completed", done)),
                    _cts.Token));
                break;
            case "run_system_checkup":
                _ = RunActionAsync("run_system_checkup", "checkup_report", false,
                    () => Task.FromResult<object>(new { report = _checkup.RunCheckup(), history = _checkup.GetHistory() }));
                break;
            case "fix_checkup_item":
            {
                var id = PayloadString(payload, "id");
                _ = RunActionAsync("fix_checkup_item", "checkup_action_result", true,
                    () => Task.FromResult<object>(_checkup.FixItem(id)));
                break;
            }
            case "create_restore_point":
                _ = RunActionAsync("create_restore_point", "restore_point_result", true,
                    () => Task.FromResult<object>(_checkup.CreateRestorePoint()));
                break;
            case "get_restore_history":
                _ = RunActionAsync("get_restore_history", "restore_history", false,
                    () => Task.FromResult<object>(new { history = _checkup.GetHistory() }));
                break;
            case "revert_change":
            {
                var cid = PayloadString(payload, "changeId");
                _ = RunActionAsync("revert_change", "checkup_action_result", true,
                    () => Task.FromResult<object>(_checkup.RevertChange(cid)));
                break;
            }
            case "revert_all_changes":
                _ = RunActionAsync("revert_all_changes", "checkup_action_result", true,
                    () => Task.FromResult<object>(_checkup.RevertAllChanges()));
                break;
            case "scan_installed_games":
                _ = RunActionAsync("scan_installed_games", "installed_games", false, () =>
                {
                    var games = _discovery.ScanInstalledGames();
                    return Task.FromResult<object>(new
                    {
                        games,
                        steamCount = games.Count(g => g.Platform == "Steam"),
                        epicCount = games.Count(g => g.Platform == "Epic")
                    });
                });
                break;
            case "launch_game_boosted":
            {
                var name = PayloadString(payload, "name") ?? "Juego";
                var platform = PayloadString(payload, "platform") ?? "";
                var uri = PayloadString(payload, "launchUri") ?? "";
                _ = RunActionAsync("launch_game_boosted", "launch_game_result", true,
                    () => Task.FromResult<object>(LaunchGameBoosted(name, platform, uri)));
                break;
            }
            case "get_game_sessions":
                _ = RunActionAsync("get_game_sessions", "game_sessions", false,
                    () => Task.FromResult<object>(new { sessions = _gameMode.GetSessions(), cleared = false }));
                break;
            case "clear_game_sessions":
                _ = RunActionAsync("clear_game_sessions", "game_sessions", false, () =>
                {
                    _gameMode.ClearSessions();
                    return Task.FromResult<object>(new { sessions = _gameMode.GetSessions(), cleared = true });
                });
                break;
            case "get_game_mode_state":
                _ = RunActionAsync("get_game_mode_state", "game_mode_state", false,
                    () => Task.FromResult<object>(GameReply(true, null)));
                break;
            case "toggle_game_mode":
            {
                var enabled = PayloadBool(payload, "enabled");
                _ = RunActionAsync("toggle_game_mode", "game_mode_state", false, () =>
                {
                    var (ok, msg) = _gameMode.SetEnabled(enabled);
                    return Task.FromResult<object>(GameReply(ok, msg));
                });
                break;
            }
            case "add_game_profile":
            {
                var name = PayloadString(payload, "name");
                var proc = PayloadString(payload, "process");
                _ = RunActionAsync("add_game_profile", "game_mode_state", false, () =>
                {
                    var (ok, msg) = _gameMode.AddGame(name, proc);
                    return Task.FromResult<object>(GameReply(ok, msg));
                });
                break;
            }
            case "remove_game_profile":
            {
                var proc = PayloadString(payload, "process");
                _ = RunActionAsync("remove_game_profile", "game_mode_state", false, () =>
                {
                    var (ok, msg) = _gameMode.RemoveGame(proc);
                    return Task.FromResult<object>(GameReply(ok, msg));
                });
                break;
            }
            case "run_ping_test":
                _ = RunActionAsync("run_ping_test", "ping_result", false,
                    async () => await _ping.RunBurstAsync());
                break;
            case "export_logs":
            {
                var logsPayload = payload;
                _ = RunActionAsync("export_logs", "export_result", false,
                    () => Task.FromResult<object>(ExportDiagnostics(logsPayload)));
                break;
            }
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
            case "open_external_url":
            {
                var url = PayloadString(payload, "url") ?? "";
                if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception) { }
                }
                break;
            }
            case "toggle_hud":
                ToggleHud();
                break;
            case "toggle_hud_lock":
                ToggleHudLock();
                break;
            case "app.ready":
                SendWindowState();
                SendHudState();
                Send("system.info", GetSystemInfo());
                break;
        }
    }

    private LaunchBoostedResult LaunchGameBoosted(string name, string platform, string launchUri)
    {
        if (string.IsNullOrWhiteSpace(launchUri) ||
            (!launchUri.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase) &&
             !launchUri.StartsWith("com.epicgames.launcher://apps/", StringComparison.OrdinalIgnoreCase)))
        {
            return new LaunchBoostedResult(false, name, platform, launchUri, "URI de lanzamiento no permitida.", null);
        }

        var boost = QuickBoost();
        try
        {
            Process.Start(new ProcessStartInfo(launchUri) { UseShellExecute = true });
            return new LaunchBoostedResult(true, name, platform, launchUri,
                $"Boost aplicado y lanzando {name} ({platform}).", boost);
        }
        catch (Exception ex)
        {
            return new LaunchBoostedResult(false, name, platform, launchUri,
                $"Boost aplicado, pero no se pudo abrir {name}: {ex.Message}", boost);
        }
    }

    // ---------- Modo Juego / Exportación ----------
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static bool PayloadBool(JsonElement? p, string name) =>
        p is { ValueKind: JsonValueKind.Object } el
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int PayloadInt(JsonElement? p, string name) =>
        p is { ValueKind: JsonValueKind.Object } el
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : -1;

    private object GameReply(bool ok, string? message) =>
        new { success = ok, message, state = _gameMode.GetState() };

    private object ExportDiagnostics(JsonElement? p)
    {
        var logs = new List<object>();
        if (p is { ValueKind: JsonValueKind.Object } el
            && el.TryGetProperty("logs", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray().Take(2000))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string Get(string n) => item.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
                logs.Add(new { ts = Get("ts"), level = Get("level"), message = Get("message") });
            }
        }

        var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var fileName = $"PulseCore_Diagnostico_{stamp}.json";
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path))
        {
            fileName = $"PulseCore_Diagnostico_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            path = Path.Combine(dir, fileName);
        }

        var doc = new
        {
            app = "PulseCore",
            generatedAt = DateTimeOffset.Now,
            system = GetSystemInfo(),
            telemetry = _lastSnapshot,
            gameMode = _gameMode.GetState(),
            logCount = logs.Count,
            logs
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, ExportOptions), new System.Text.UTF8Encoding(false));
        return new { path, fileName, directory = dir, logCount = logs.Count };
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
        // WM_GETMINMAXINFO ya ajusta el tamaño maximizado a rcWork (sin tapar la barra de tareas).
        RootGrid.Margin = new Thickness(0);
        SendWindowState();
    }

    private void SendWindowState() =>
        Send("window.state", new { maximized = WindowState == WindowState.Maximized });

    private async Task RunDueMaintenanceAsync()
    {
        try
        {
            var (ran, msg) = await _profiles.RunDueWeeklyMaintenanceAsync(_cleaner, _cts.Token);
            if (ran && msg is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    Send("weekly_maintenance_ran", new { message = msg, settings = _profiles.GetSettings() });
                    _trayIcon?.ShowBalloonTip(4500, "PulseCore — Mantenimiento Semanal", msg, Forms.ToolTipIcon.Info);
                });
            }
        }
        catch (Exception) { }
    }

    private async Task ApplyQuickProfileAsync(string profile)
    {
        profile = profile.Trim().ToLowerInvariant();
        if (profile == "competitive")
        {
            var boost = await Task.Run(QuickBoost);
            var settings = _profiles.SetActiveProfile("competitive");
            await Dispatcher.InvokeAsync(() =>
            {
                if (_hud is null || !_hud.IsVisible) ToggleHud();
                Send("quick_profile_applied", new { profile = "competitive", message = "Perfil Competitivo activado: Alto Rendimiento + Boost + Mini-HUD.", boost, settings });
                Send("optimizer_state", GetOptimizerState());
            });
        }
        else if (profile == "work")
        {
            var plan = await Task.Run(() => _power.SetPowerPlan(PowerPlanService.BalancedGuid));
            var settings = _profiles.SetActiveProfile("work");
            await Dispatcher.InvokeAsync(() =>
            {
                if (_hud is { IsVisible: true }) ToggleHud();
                Send("quick_profile_applied", new { profile = "work", message = $"Perfil Trabajo/Estudio activado: {plan.ActiveName ?? "Equilibrado"} + Mini-HUD oculto.", settings });
                Send("optimizer_state", GetOptimizerState());
            });
        }
    }

    private void ExportConfigDialog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar configuración de PulseCore",
            Filter = "JSON (*.json)|*.json",
            FileName = $"PulseCore_Config_{DateTime.Now:yyyyMMdd}.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        var (ok, msg) = _profiles.ExportBundle(dlg.FileName);
        Send("config_io_result", new { success = ok, action = "export", message = msg });
    }

    private void ImportConfigDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar configuración de PulseCore",
            Filter = "JSON (*.json)|*.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        var (ok, msg) = _profiles.ImportBundle(dlg.FileName);
        Send("config_io_result", new { success = ok, action = "import", message = msg, settings = _profiles.GetSettings() });
    }

    private async Task CheckForUpdatesAsync()
    {
        var info = await _updater.CheckForUpdateAsync(_cts.Token);
        if (info is not null)
            await Dispatcher.InvokeAsync(() => Send("update_available", info));
    }

    // ---------- Mini-HUD Flotante ----------
    private void ToggleHud()
    {
        if (_hud is null)
        {
            _hud = new HudWindow();
            _hud.StateChangedNotify += () => Dispatcher.InvokeAsync(() =>
            {
                if (_hud is not { IsVisible: true }) _fps.Stop();
                SendHudState();
            });
        }

        if (_hud.IsVisible)
        {
            _hud.Hide();
            _fps.Stop();
        }
        else
        {
            _fps.Start();
            _hud.Show();
            if (_lastSnapshot is { } snap) _hud.UpdateMetrics(snap, _lastHudPingMs, _fps.CurrentFps);
            _lastHudPingTick = 0; // dispara medición de ping inmediata
        }
        SendHudState();
    }

    private void ToggleHudLock()
    {
        if (_hud is null || !_hud.IsVisible) return;
        _hud.SetClickThrough(!_hud.IsClickThrough);
    }

    private void SendHudState() =>
        Send("hud_state", new { visible = _hud is { IsVisible: true }, clickThrough = _hud?.IsClickThrough ?? false });

    private async Task ProbeHudPingAsync()
    {
        try
        {
            using var p = new Ping();
            var reply = await p.SendPingAsync("1.1.1.1", 1200);
            _lastHudPingMs = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception) { _lastHudPingMs = null; }
    }

    // ---------- WM_GETMINMAXINFO (Fix maximizado respetando barra de tareas en cualquier monitor) ----------
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                var area = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(work.left - area.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - area.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
            }
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
