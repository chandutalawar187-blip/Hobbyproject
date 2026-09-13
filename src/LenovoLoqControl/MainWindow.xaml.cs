using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using Forms = System.Windows.Forms;
using Microsoft.Win32;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;
using LenovoLoqControl.UI;
using LenovoLoqControl.UI.Shell;

namespace LenovoLoqControl;

public partial class MainWindow : Window
{
    private readonly HardwareBackend _hardware = new();
    private readonly DashboardView _dashboard;
    private readonly DispatcherTimer _shellTimer;
    private readonly Button[] _navButtons;
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _allowClose;
    private bool _refreshingShell;
    private int _shellDensity = -1;
    private PageKind _currentPage = PageKind.Dashboard;

    public MainWindow()
    {
        InitializeComponent();
        Closing += HandleClosing;
        _trayIcon = CreateTrayIcon();

        _navButtons =
        [
            NavDashboard,
            NavFan,
            NavProfiles,
            NavProjects,
            NavDiagnostics,
            NavSettings
        ];

        _dashboard = new DashboardView(_hardware);
        ApplyIdentity();
        ApplyProviderStatus();
        PageTitleText.Text = "Dashboard";
        PageSubtitleText.Text = "Hardware status and live telemetry";
        SelectNav(NavDashboard);
        ContentHost.Content = _dashboard;
        StatusBarPrimary.Text = "Viewing Dashboard";

        _shellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _shellTimer.Tick += async (_, _) => await RefreshShellStatusAsync();
        Loaded += async (_, _) =>
        {
            ApplyMotionPreference();
            AppUiPreferences.Changed += OnUiPreferencesChanged;
            _shellTimer.Start();
            await RefreshShellStatusAsync();
        };
        Closed += (_, _) =>
        {
            AppUiPreferences.Changed -= OnUiPreferencesChanged;
            _shellTimer.Stop();
        };
    }

    private void OnUiPreferencesChanged() =>
        Dispatcher.Invoke(ApplyMotionPreference);

    private void ApplyMotionPreference()
    {
        ContentHost.PreferReducedMotion = AppUiPreferences.ReducedMotion;
    }

    private void ApplyIdentity()
    {
        var identity = _hardware.Monitor.Identity;
        ModelNameText.Text = string.IsNullOrWhiteSpace(identity.Model) ? "Unknown model" : identity.Model;

        var sku = TryReadMachineType();
        MachineTypeText.Text = sku is not null
            ? $"{identity.Manufacturer}  ·  {sku}"
            : $"{identity.Manufacturer}  ·  Machine type unavailable";
    }

    private static string? TryReadMachineType()
    {
        try
        {
            var sku = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS",
                "SystemSKU",
                null) as string;
            return string.IsNullOrWhiteSpace(sku) ? null : sku.Trim();
        }
        catch
        {
            return null;
        }
    }

    private void ApplyProviderStatus()
    {
        var supported = _hardware.FanController.IsSupported;
        HardwareStatus.Text = supported
            ? "Preset thermal modes available through the verified provider."
            : _hardware.FanController.AvailabilityMessage;
        ProviderBadgeText.Text = supported ? "Provider · Ready" : "Provider · Monitoring only";
        PerformanceModeBadge.Text = supported
            ? "Mode not reported this session"
            : "Modes unavailable";
        StatusBarPrimary.Text = supported
            ? "Firmware control path ready"
            : "Monitoring only — control unavailable";
    }

    private void DashboardClick(object sender, RoutedEventArgs e) =>
        Navigate(PageKind.Dashboard, "Dashboard", "Hardware status and live telemetry", _dashboard);

    private void FanClick(object sender, RoutedEventArgs e) =>
        Navigate(PageKind.Fan, "Fan Control", "Firmware modes, fixed speed, and safe curves", new FanControlView(_hardware));

    private void LightingClick(object sender, RoutedEventArgs e) =>
        Navigate(PageKind.Lighting, "Lighting", "Keyboard backlight and verified RGB effects", new LightingView(_hardware));

    private void ProfilesClick(object sender, RoutedEventArgs e) =>
        Navigate(PageKind.Profiles, "Profiles", "Workload intent without hiding firmware limits", new ProfilesView(_hardware));

    private void ProjectsClick(object sender, RoutedEventArgs e) =>
        Navigate(PageKind.Projects, "Projects", "Safe project workflows and maintenance actions", new ProjectsView(_hardware));

    private void DiagnosticsClick(object sender, RoutedEventArgs e) =>
        Navigate(PageKind.Diagnostics, "Diagnostics", "Honest compatibility and capability report", new DiagnosticsView(_hardware));

    private void SettingsClick(object sender, RoutedEventArgs e) =>
        Navigate(PageKind.Settings, "Settings", "Preferences, monitoring, and Lenovo integration", new SettingsView(_hardware));

    private void Navigate(PageKind kind, string title, string subtitle, object content)
    {
        ContentHost.TransitionDirection = kind.CompareTo(_currentPage) switch
        {
            > 0 => PageTransitionDirection.Forward,
            < 0 => PageTransitionDirection.Backward,
            _ => PageTransitionDirection.Neutral
        };
        _currentPage = kind;

        UiAnimation.AnimateShellHeader(PageTitleText, PageSubtitleText, title, subtitle);
        var navButton = kind switch
        {
            PageKind.Dashboard => NavDashboard,
            PageKind.Fan => NavFan,
            PageKind.Lighting => NavLighting,
            PageKind.Profiles => NavProfiles,
            PageKind.Projects => NavProjects,
            PageKind.Diagnostics => NavDiagnostics,
            _ => NavSettings
        };
        SelectNav(navButton);
        ContentHost.Content = content;
        StatusBarPrimary.Text = $"Viewing {title}";
    }

    private void SelectNav(Button selected)
    {
        foreach (var button in _navButtons)
            button.Tag = ReferenceEquals(button, selected) ? "Selected" : null;
        UiAnimation.PulseNavSelection(selected);
    }

    private async Task RefreshShellStatusAsync()
    {
        if (_refreshingShell) return;
        _refreshingShell = true;
        try
        {
            var reading = await _hardware.Monitor.ReadAsync(CancellationToken.None);
            var state = ThermalSafety.Classify(reading.CpuTemperature);
            ApplyHealth(state, reading);
            var mode = await _hardware.FanController.GetCurrentModeAsync(CancellationToken.None);
            PerformanceModeBadge.Text = mode is FanMode current
                ? $"Mode · {ModeLabel(current)}"
                : _hardware.FanController.IsSupported ? "Mode · Not reported" : "Modes unavailable";
            StatusBarClock.Text = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                       or TimeoutException
                                       or UnauthorizedAccessException
                                       or ManagementException)
        {
            ApplyHealth(ThermalState.Unknown, null);
            StatusBarTelemetry.Text = "Telemetry unavailable";
            StatusBarClock.Text = DateTime.Now.ToString("HH:mm:ss");
        }
        finally
        {
            _refreshingShell = false;
        }
    }

    private static string ModeLabel(FanMode mode) => mode switch
    {
        FanMode.Auto => "Automatic",
        FanMode.MaxCooling => "Max Cooling",
        _ => mode.ToString()
    };

    private void ApplyHealth(ThermalState state, SensorReading? reading)
    {
        var (label, brushKey, badgeStyle) = state switch
        {
            ThermalState.Cool => ("Cool", "Brush.Success", "Badge.Success"),
            ThermalState.Normal => ("Normal", "Brush.Information", "Badge.Info"),
            ThermalState.Warm => ("Warm", "Brush.Warning", "Badge.Warning"),
            ThermalState.Hot => ("Hot", "Brush.AccentOrange", "Badge.Warning"),
            ThermalState.Critical => ("Critical", "Brush.Critical", "Badge.Critical"),
            _ => ("Unknown", "Brush.TextSecondary", "Badge.Neutral")
        };

        if (TryFindResource(brushKey) is Brush brush)
            HealthDot.Fill = brush;

        HealthStatusText.Text = reading?.CpuTemperature is double temp
            ? $"{label}  ·  CPU {temp:0}°C"
            : $"{label}  ·  Temperature unavailable";

        HeaderHealthBadge.Style = (Style)FindResource(badgeStyle);
        HeaderHealthBadgeText.Text = reading?.CpuTemperature is double
            ? $"Health · {label}"
            : "Health · Unavailable";

        // Include accessible text, not color alone
        HeaderHealthBadge.ToolTip = reading?.CpuTemperature is double t
            ? $"System health: {label}. CPU temperature {t:0}°C."
            : "System health unknown. CPU temperature unavailable.";

        if (reading is null)
        {
            StatusBarTelemetry.Text = "Telemetry unavailable";
            return;
        }

        var parts = new List<string>();
        if (reading.CpuFanRpm is double cpuRpm) parts.Add($"CPU fan {cpuRpm:0} RPM");
        if (reading.GpuFanRpm is double gpuRpm) parts.Add($"GPU fan {gpuRpm:0} RPM");
        if (reading.CpuTemperature is double cpuTemp) parts.Add($"CPU {cpuTemp:0}°C");
        if (reading.GpuTemperature is double gpuTemp) parts.Add($"GPU {gpuTemp:0}°C");
        StatusBarTelemetry.Text = parts.Count > 0
            ? string.Join("  ·  ", parts)
            : "Live RPM/temp unavailable";
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Compact the rail and content padding on smaller widths / effective DPI layouts.
        var width = e.NewSize.Width;
        int density;
        double navWidth;
        Thickness contentPadding;
        Thickness railPadding;

        if (width < 1100)
        {
            density = 0;
            navWidth = 212;
            contentPadding = new Thickness(18, 12, 18, 8);
            railPadding = new Thickness(14, 22, 12, 18);
        }
        else if (width < 1400)
        {
            density = 1;
            navWidth = 248;
            contentPadding = new Thickness(28, 16, 32, 12);
            railPadding = new Thickness(20, 28, 16, 24);
        }
        else
        {
            density = 2;
            navWidth = 280;
            contentPadding = new Thickness(36, 18, 40, 14);
            railPadding = new Thickness(24, 32, 18, 26);
        }

        if (density == _shellDensity)
            return;

        _shellDensity = density;
        NavColumn.Width = new GridLength(navWidth);
        ResponsiveLayout.AnimateThickness(ContentHostBorder, Border.PaddingProperty, contentPadding);
        ResponsiveLayout.AnimateThickness(NavRail, Border.PaddingProperty, railPadding);
    }

    protected override void OnClosed(EventArgs e)
    {
        AppUiPreferences.Changed -= OnUiPreferencesChanged;
        _shellTimer.Stop();
        _hardware.Dispose();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open LOQ Control");
        open.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var exit = new Forms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Dispatcher.Invoke(ExitFromTray);
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        var icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "LOQ Control",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return icon;
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
        _trayIcon.ShowBalloonTip(
            1500,
            "LOQ Control is still running",
            "Hardware control remains active in the system tray.",
            Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        _trayIcon.Visible = false;
        Close();
    }

    private enum PageKind
    {
        Dashboard,
        Fan,
        Lighting,
        Profiles,
        Projects,
        Diagnostics,
        Settings
    }
}
