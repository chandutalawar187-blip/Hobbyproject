using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.Text.Json;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;

namespace LenovoLoqControl.UI;

public partial class DashboardView : UserControl
{
    private readonly IHardwareBackend _hardware;
    private readonly DispatcherTimer _timer;
    private bool _refreshing;

    public DashboardView(IHardwareBackend? hardware = null)
    {
        InitializeComponent();
        _hardware = hardware ?? new HardwareBackend();

        ApplyStaticIdentity();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await RefreshAsync();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void ApplyStaticIdentity()
    {
        var identity = _hardware.Monitor.Identity;
        OverviewModelText.Text = $"{identity.Manufacturer}  ·  {identity.Model}";
        var supportedDevice = identity.IsLenovoLoq;
        UnsupportedDeviceBanner.Visibility = supportedDevice ? Visibility.Collapsed : Visibility.Visible;
        UnsupportedDeviceText.Text = "Your device is unsupported. Telemetry is available, but Lenovo LOQ controls and profiles are disabled.";
        var supported = _hardware.FanController.IsSupported;
        OverviewProviderText.Text = !supportedDevice
            ? "Unsupported device · telemetry only"
            : supported
            ? "Firmware provider ready · preset thermal modes available"
            : "No verified fan-control interface · monitoring only";
        OverviewModeText.Text = supported ? "Mode · Loading" : "Modes unavailable";
        ProviderStatusText.Text = supported ? "Ready" : "Unavailable";
        ProviderDetailText.Text = _hardware.FanController.AvailabilityMessage;
        FanAvailabilityText.Text = supported
            ? "Control · Firmware modes available"
            : "Control · Unavailable";
        FanModeText.Text = supported
            ? "Fan mode · Loading"
            : "Fan mode · Unavailable";
        PowerText.Text = "Unavailable";
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var reading = await _hardware.Monitor.ReadAsync(CancellationToken.None);
            ApplyReading(reading);
            try
            {
                using var modeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ApplyModeAsync(modeTimeout.Token);
            }
            catch (Exception ex) when (ex is IOException
                                       or JsonException
                                       or InvalidOperationException
                                       or OperationCanceledException
                                       or TimeoutException
                                       or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
            {
                OverviewModeText.Text = "Mode · Unavailable";
                FanModeText.Text = "Fan mode · Unavailable";
            }
            LastUpdatedText.Text = $"Last updated  {reading.Timestamp.ToLocalTime():HH:mm:ss}";
        }
        catch (Exception ex) when (ex is ManagementException or TimeoutException or UnauthorizedAccessException)
        {
            ApplyTelemetryError();
            LastUpdatedText.Text = "Telemetry error — values not displayed";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ApplyModeAsync(CancellationToken cancellationToken)
    {
        var mode = await _hardware.FanController.GetCurrentModeAsync(cancellationToken);
        var text = mode is FanMode current ? current switch
        {
            FanMode.Custom => "Custom",
            FanMode.Auto => "Automatic",
            FanMode.MaxCooling => "Max Cooling",
            _ => current.ToString()
        } : "Not reported by firmware";
        OverviewModeText.Text = $"Mode · {text}";
        FanModeText.Text = $"Fan mode · {text}";
    }

    private void ApplyReading(SensorReading reading)
    {
        var cpuState = ThermalSafety.Classify(reading.CpuTemperature);
        var gpuState = ThermalSafety.Classify(reading.GpuTemperature);

        SetTemperature(CpuTempText, reading.CpuTemperature);
        SetTemperature(GpuTempText, reading.GpuTemperature);
        CpuRing.Value = reading.CpuTemperature;
        CpuRing.ThermalState = cpuState;
        GpuRing.Value = reading.GpuTemperature;
        GpuRing.ThermalState = gpuState;

        CpuUtilText.Text = FormatMetric("Utilization", reading.CpuUsage, "0", "%");
        GpuUtilText.Text = FormatMetric("Utilization", reading.GpuUsage, "0", "%");
        CpuClockText.Text = FormatMetric("Clock", reading.CpuClock, "0.0", " GHz");
        GpuClockText.Text = FormatMetric("Clock", reading.GpuClock, "0.0", " GHz");
        CpuThermalText.Text = $"Thermal · {StateLabel(cpuState, reading.CpuTemperature)}";
        GpuThermalText.Text = $"Thermal · {StateLabel(gpuState, reading.GpuTemperature)}";

        CpuFanText.Text = FormatMetric("CPU fan", reading.CpuFanRpm, "0", " RPM");
        GpuFanText.Text = FormatMetric("GPU fan", reading.GpuFanRpm, "0", " RPM");
        CoolingHeadline.Text = reading.CpuFanRpm is double || reading.GpuFanRpm is double
            ? "Live"
            : "Unavailable";
        CoolingHeadline.Foreground = (Brush)FindResource(
            reading.CpuFanRpm is double || reading.GpuFanRpm is double
                ? "Brush.TextPrimary"
                : "Brush.TextSecondary");

        MemoryText.Text = reading.MemoryUsage is double mem ? $"{mem:0}%" : "Unavailable";
        if (reading.BatteryPercent is double bat)
        {
            BatteryText.Text = $"{bat:0}%";
            ChargingText.Text = reading.IsCharging switch
            {
                true => "Charge state · Charging",
                false => "Charge state · On battery",
                _ => "Charge state · Unavailable"
            };
        }
        else
        {
            BatteryText.Text = "Unavailable";
            ChargingText.Text = "Charge state · Unavailable";
        }

        ApplyHealthBadge(cpuState, reading.CpuTemperature);
    }

    private void ApplyTelemetryError()
    {
        CpuTempText.Text = "Error";
        GpuTempText.Text = "Error";
        CpuRing.Value = null;
        GpuRing.Value = null;
        CpuUtilText.Text = "Utilization · Error";
        GpuUtilText.Text = "Utilization · Error";
        CpuClockText.Text = "Clock · Error";
        GpuClockText.Text = "Clock · Error";
        CpuThermalText.Text = "Thermal · Error";
        GpuThermalText.Text = "Thermal · Error";
        CpuFanText.Text = "CPU fan · Error";
        GpuFanText.Text = "GPU fan · Error";
        CoolingHeadline.Text = "Error";
        MemoryText.Text = "Error";
        BatteryText.Text = "Error";
        ChargingText.Text = "Charge state · Error";
        OverviewHealthBadge.Style = (Style)FindResource("Badge.Critical");
        OverviewHealthText.Text = "Health · Error";
    }

    private void ApplyHealthBadge(ThermalState state, double? cpuTemp)
    {
        var (style, label) = state switch
        {
            ThermalState.Cool => ("Badge.Success", "Cool"),
            ThermalState.Normal => ("Badge.Info", "Normal"),
            ThermalState.Warm => ("Badge.Warning", "Warm"),
            ThermalState.Hot => ("Badge.Warning", "Hot"),
            ThermalState.Critical => ("Badge.Critical", "Critical"),
            _ => ("Badge.Neutral", "Unavailable")
        };
        OverviewHealthBadge.Style = (Style)FindResource(style);
        OverviewHealthText.Text = cpuTemp is double t
            ? $"Health · {label} · CPU {t:0}°C"
            : $"Health · {label}";
        OverviewHealthBadge.ToolTip = OverviewHealthText.Text;
    }

    private static void SetTemperature(TextBlock target, double? value)
    {
        target.Text = value is double t ? $"{t:0}°C" : "Unavailable";
        target.Foreground = (Brush)Application.Current.FindResource(
            value is double ? "Brush.TextPrimary" : "Brush.TextSecondary");
    }

    private static string FormatMetric(string label, double? value, string numericFormat, string suffix) =>
        value is double number
            ? $"{label} · {number.ToString(numericFormat)}{suffix}"
            : $"{label} · Unavailable";

    private static string StateLabel(ThermalState state, double? temperature) =>
        temperature is null ? "Unavailable" : state.ToString();
}
