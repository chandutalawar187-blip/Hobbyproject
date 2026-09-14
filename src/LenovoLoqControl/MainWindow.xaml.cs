using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
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
    private bool _navExpanded = true;
    private double _expandedNavWidth = 292;
    private PageKind _currentPage = PageKind.Dashboard;

    private const double NavExpandedMinWidth = 280;
    // Must fit Fan icon (height × 1.4) + rail padding + button padding.
    private const double NavCollapsedWidth = 108;

    public MainWindow()
    {
        InitializeComponent();
        Icon = LoadAdaptiveWindowIcon();
        Closing += HandleClosing;
        _trayIcon = CreateTrayIcon();

        _navButtons =
        [
            NavDashboard,
            NavFan,
            NavLighting,
            NavProfiles,
            NavProjects,
            NavDiagnostics,
            NavSettings
        ];

        _dashboard = new DashboardView(_hardware);
        ApplyIdentity();
        ApplyProviderStatus();
        HeaderVersionText.Text = $"v{Services.UpdateService.DisplayVersion}";
        StatusBarVersion.Text = $"v{Services.UpdateService.DisplayVersion}";
        PageTitleText.Text = "Dashboard";
        PageSubtitleText.Text = "Hardware status and live telemetry";
        SelectNav(NavDashboard);
        ContentHost.Content = _dashboard;
        StatusBarPrimary.Text = "Viewing Dashboard";
        ApplyNavExpandedState(animate: false);

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

    private void NavToggleClick(object sender, RoutedEventArgs e)
    {
        _navExpanded = !_navExpanded;
        ApplyNavExpandedState(animate: true);
    }

    private void ApplyNavExpandedState(bool animate)
    {
        var labelVisibility = _navExpanded ? Visibility.Visible : Visibility.Collapsed;
        var detailVisibility = _navExpanded ? Visibility.Visible : Visibility.Collapsed;

        BrandPanel.Visibility = detailVisibility;
        BrandSubLabel.Visibility = detailVisibility;
        IdentityCard.Visibility = detailVisibility;
        HealthCard.Visibility = detailVisibility;

        NavDashboardLabel.Visibility = labelVisibility;
        NavFanLabel.Visibility = labelVisibility;
        NavLightingLabel.Visibility = labelVisibility;
        NavProfilesLabel.Visibility = labelVisibility;
        NavProjects.Visibility = detailVisibility;
        NavDiagnostics.Visibility = detailVisibility;
        NavSettings.Visibility = detailVisibility;

        NavDashboard.Visibility = Visibility.Visible;

        NavDashboardIcon.LabelVisible = _navExpanded;
        NavFanIcon.LabelVisible = _navExpanded;
        NavLightingIcon.LabelVisible = _navExpanded;
        NavProfilesIcon.LabelVisible = _navExpanded;

        // Collapsed rail: size icons to fit column width (Fan is wide at AspectRatio 1.4).
        // Keep ZoomScale at 1 — values >1 crop the animation inside the view.
        if (_navExpanded)
        {
            NavDashboardIcon.IconSize = 40d;
            NavFanIcon.IconSize = 44d;
            NavLightingIcon.IconSize = 40d;
            NavProfilesIcon.IconSize = 40d;
        }
        else
        {
            // Available ≈ NavCollapsedWidth - railPad*2 - buttonPad*2
            // 108 - 16 - 8 = 84 → Fan height = 84 / 1.4 ≈ 60, clamp for balance
            NavDashboardIcon.IconSize = 44d;
            NavFanIcon.IconSize = 48d;
            NavLightingIcon.IconSize = 44d;
            NavProfilesIcon.IconSize = 44d;
        }

        NavFanIcon.ZoomScale = 1d;
        NavLightingIcon.ZoomScale = 1d;

        foreach (var button in new[] { NavFan, NavLighting, NavProfiles })
        {
            button.HorizontalContentAlignment = _navExpanded
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Center;
            button.Padding = _navExpanded
                ? new Thickness(14, 10, 14, 10)
                : new Thickness(4, 10, 4, 10);
            button.MinHeight = _navExpanded ? 64 : 68;
            button.MaxWidth = _navExpanded ? double.PositiveInfinity : 92;
        }

        NavToggleButton.ToolTip = _navExpanded
            ? "Collapse navigation"
            : "Expand navigation";

        var railPadding = _navExpanded
            ? new Thickness(18, 24, 16, 22)
            : new Thickness(8, 18, 8, 16);
        var navWidth = _navExpanded ? _expandedNavWidth : NavCollapsedWidth;

        NavColumn.MinWidth = _navExpanded ? NavExpandedMinWidth : NavCollapsedWidth;
        NavColumn.MaxWidth = _navExpanded ? 340 : NavCollapsedWidth;

        if (animate)
        {
            ResponsiveLayout.AnimateThickness(NavRail, Border.PaddingProperty, railPadding);
            AnimateNavWidth(navWidth);
        }
        else
        {
            NavRail.Padding = railPadding;
            NavColumn.Width = new GridLength(navWidth);
        }
    }

    private void AnimateNavWidth(double to)
    {
        NavColumn.Width = new GridLength(to);
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
            navWidth = NavExpandedMinWidth;
            contentPadding = new Thickness(18, 12, 18, 8);
            railPadding = new Thickness(14, 20, 12, 16);
        }
        else if (width < 1400)
        {
            density = 1;
            navWidth = 292;
            contentPadding = new Thickness(28, 16, 32, 12);
            railPadding = new Thickness(18, 24, 16, 22);
        }
        else
        {
            density = 2;
            navWidth = 320;
            contentPadding = new Thickness(36, 18, 40, 14);
            railPadding = new Thickness(22, 28, 18, 24);
        }

        _expandedNavWidth = navWidth;

        if (density == _shellDensity)
            return;

        _shellDensity = density;
        ResponsiveLayout.AnimateThickness(ContentHostBorder, Border.PaddingProperty, contentPadding);

        if (_navExpanded)
        {
            ResponsiveLayout.AnimateThickness(NavRail, Border.PaddingProperty, railPadding);
            NavColumn.Width = new GridLength(_expandedNavWidth);
        }
        else
        {
            NavColumn.Width = new GridLength(NavCollapsedWidth);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        AppUiPreferences.Changed -= OnUiPreferencesChanged;
        _shellTimer?.Stop();
        _hardware.Dispose();
        _trayIcon?.Dispose();
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
            Icon = LoadApplicationIcon(),
            Text = "LOQ Control",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return icon;
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var resourceName = UseLightLogo() ? "app_icon_white.ico" : "app_icon_black.ico";
        var resource = Application.GetResourceStream(
            new Uri($"pack://application:,,,/Assets/{resourceName}"));
        if (resource is null)
            return System.Drawing.SystemIcons.Application;

        using var stream = resource.Stream;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        return new System.Drawing.Icon(buffer);
    }

    private static BitmapImage LoadAdaptiveWindowIcon()
    {
        var resourceName = UseLightLogo() ? "app_icon_white.ico" : "app_icon_black.ico";
        var resource = Application.GetResourceStream(
            new Uri($"pack://application:,,,/Assets/{resourceName}"))
            ?? throw new IOException($"The adaptive icon resource '{resourceName}' is unavailable.");
        using var stream = resource.Stream;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(buffer.ToArray());
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool UseLightLogo()
    {
        return Application.Current.TryFindResource("Color.Background") is System.Windows.Media.Color color
            && (color.R * 299 + color.G * 587 + color.B * 114) / 1000 < 150;
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

    internal void ShowFromAnotherInstance() => Dispatcher.Invoke(ShowFromTray);

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

    internal void CloseForUpdate()
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
