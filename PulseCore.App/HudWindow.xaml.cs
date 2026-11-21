using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PulseCore.App.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PulseCore.App;

/// <summary>Mini-HUD flotante ligero en XAML nativo (sin WebView2 ni inyección en juegos).</summary>
public partial class HudWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly Brush CyanBrush = Freeze(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Brush EmeraldBrush = Freeze(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly Brush AmberBrush = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush RedBrush = Freeze(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush MutedBrush = Freeze(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush BorderNormal = Freeze(Color.FromArgb(0x77, 0x38, 0xBD, 0xF8));
    private static readonly Brush BorderLocked = Freeze(Color.FromArgb(0x88, 0x10, 0xB9, 0x81));

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public bool IsClickThrough { get; private set; }
    public event Action? StateChangedNotify;

    public HudWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowStyles();
    }

    public void UpdateMetrics(TelemetrySnapshot snap, long? pingMs, int fps)
    {
        if (fps > 0)
        {
            TxtFps.Text = $"{fps} FPS";
            TxtFps.Foreground = fps >= 60 ? EmeraldBrush : fps >= 30 ? AmberBrush : RedBrush;
        }
        else
        {
            TxtFps.Text = "-- FPS";
            TxtFps.Foreground = MutedBrush;
        }

        var cpu = Math.Round(snap.Cpu.UsagePercent);
        TxtCpu.Text = $"{cpu:F0}%";
        TxtCpu.Foreground = cpu >= 85 ? RedBrush : cpu >= 65 ? AmberBrush : CyanBrush;

        if (snap.Gpu.TemperatureC is { } gt && gt > 0)
        {
            TxtGpu.Text = $"{Math.Round(gt):F0}°C";
            TxtGpu.Foreground = gt >= 80 ? RedBrush : gt >= 68 ? AmberBrush : EmeraldBrush;
        }
        else
        {
            TxtGpu.Text = "N/D";
            TxtGpu.Foreground = MutedBrush;
        }

        TxtRam.Text = $"{snap.Ram.UsedGb:F1} GB";

        if (pingMs is { } pm)
        {
            TxtPing.Text = $"{pm}ms";
            TxtPing.Foreground = pm > 90 ? RedBrush : pm >= 45 ? AmberBrush : EmeraldBrush;
        }
        else
        {
            TxtPing.Text = "--";
            TxtPing.Foreground = MutedBrush;
        }
    }

    public void SetClickThrough(bool enabled)
    {
        IsClickThrough = enabled;
        ApplyWindowStyles();
        BtnLock.Content = enabled ? "🔒" : "🔓";
        BtnLock.Foreground = enabled ? EmeraldBrush : MutedBrush;
        PillBorder.BorderBrush = enabled ? BorderLocked : BorderNormal;
        StateChangedNotify?.Invoke();
    }

    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        ex = IsClickThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsClickThrough || e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        SetClickThrough(!IsClickThrough);
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        Hide();
        StateChangedNotify?.Invoke();
    }

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
