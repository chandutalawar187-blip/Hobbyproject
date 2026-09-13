using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;
using LenovoLoqControl.Services;

namespace LenovoLoqControl.UI;

public partial class DiagnosticsView : UserControl
{
    private readonly IHardwareBackend _hardware;
    private readonly List<DiagnosticRow> _rows = [];
    private string _reportText = "";

    public DiagnosticsView(IHardwareBackend? hardware = null)
    {
        InitializeComponent();
        _hardware = hardware ?? new HardwareBackend();
        Loaded += async (_, _) => await BuildReportAsync();
    }

    private async Task BuildReportAsync()
    {
        CapabilityList.Children.Clear();
        _rows.Clear();

        var identity = _hardware.Monitor.Identity;
        AddRow("Lenovo detected",
            identity.Manufacturer.Contains("Lenovo", StringComparison.OrdinalIgnoreCase),
            identity.Manufacturer.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            identity.Manufacturer);

        AddRow("LOQ model detected",
            identity.IsLenovoLoq,
            identity.IsLenovoLoq ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            identity.Model);

        SensorReading? reading = null;
        try
        {
            reading = await _hardware.Monitor.ReadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddRow("Telemetry read", false, DiagnosticKind.Fail, ex.Message);
        }

        AddSensor("CPU telemetry (utilization)", reading?.CpuUsage, "0", "%");
        AddSensor("GPU telemetry (utilization)", reading?.GpuUsage, "0", "%");
        AddSensor("CPU temperature", reading?.CpuTemperature, "0", "°C");
        AddSensor("GPU temperature", reading?.GpuTemperature, "0", "°C");
        AddSensor("CPU fan RPM", reading?.CpuFanRpm, "0", " RPM");
        AddSensor("GPU fan RPM", reading?.GpuFanRpm, "0", " RPM");

        var modes = _hardware.FanController.IsSupported;
        AddRow("Firmware mode control", modes,
            modes ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            _hardware.FanController.AvailabilityMessage);

        var currentMode = await _hardware.FanController.GetCurrentModeAsync(CancellationToken.None);
        AddRow("Current firmware mode", currentMode is not null, 
            currentMode is not null ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            currentMode is FanMode mode ? ModeLabel(mode) : "Firmware did not return a recognized mode");

        FirmwareFanTable? table = null;
        try
        {
            table = await _hardware.FanController.ReadCustomFanTableAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddRow("Custom fan table", false, DiagnosticKind.Fail, ex.Message);
        }

        if (table is not null)
            AddRow("Custom fan table", true, DiagnosticKind.Ok, $"{table.TemperaturesCelsius.Count} firmware temperature points");
        else if (_rows.All(r => r.Name != "Custom fan table"))
            AddRow("Custom fan table", false, DiagnosticKind.Warn, "No readable firmware fan table on this system");

        AddRow("Manual fan control", modes,
            modes ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            modes ? "Fixed step and curve apply paths are present" : "Not exposed by a verified interface");

        AddRow("Lenovo provider status", modes,
            modes ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            _hardware.FanController.AvailabilityMessage);

        try
        {
            var status = await new LenovoVantageDisabler().GetStatusAsync(CancellationToken.None);
            var kind = !status.Installed ? DiagnosticKind.Warn
                : status.Enabled ? DiagnosticKind.Ok
                : DiagnosticKind.Warn;
            AddRow("ImController / Vantage status", status.Installed, kind, status.Message);
        }
        catch (Exception ex)
        {
            AddRow("ImController / Vantage status", false, DiagnosticKind.Fail, ex.Message);
        }

        _reportText = BuildTextReport(identity, reading);
    }

    private static string ModeLabel(FanMode mode) => mode switch
    {
        FanMode.Auto => "Automatic",
        FanMode.MaxCooling => "Max Cooling",
        _ => mode.ToString()
    };

    private void AddSensor(string name, double? value, string format, string suffix)
    {
        if (value is double number)
            AddRow(name, true, DiagnosticKind.Ok, $"{number.ToString(format)}{suffix}");
        else
            AddRow(name, false, DiagnosticKind.Warn, "Unavailable through supported interface");
    }

    private void AddRow(string name, bool available, DiagnosticKind kind, string detail)
    {
        _rows.Add(new DiagnosticRow(name, available, kind, detail));

        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var (glyph, brushKey, statusWord) = kind switch
        {
            DiagnosticKind.Ok => ("✓", "Brush.Success", "Verified"),
            DiagnosticKind.Fail => ("✕", "Brush.Critical", "Failed"),
            _ => ("!", "Brush.Warning", "Unavailable / restricted")
        };

        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(40,
                ((SolidColorBrush)FindResource(brushKey)).Color.R,
                ((SolidColorBrush)FindResource(brushKey)).Color.G,
                ((SolidColorBrush)FindResource(brushKey)).Color.B)),
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = (Brush)FindResource(brushKey),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold
            },
            ToolTip = statusWord,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = $"{name}  ·  {statusWord}",
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Style = (Style)FindResource("Text.BodySecondary"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        // Accessible text alternative for color
        text.Children.Add(new TextBlock
        {
            Text = available ? "Availability: available" : "Availability: not available",
            Style = (Style)FindResource("Text.Caption"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        CapabilityList.Children.Add(row);
    }

    private string BuildTextReport(HardwareIdentity identity, SensorReading? reading)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LOQ Control — Diagnostic Report");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:u}");
        sb.AppendLine($"Manufacturer: {identity.Manufacturer}");
        sb.AppendLine($"Model: {identity.Model}");
        sb.AppendLine($"Processor: {identity.Processor}");
        sb.AppendLine($"IsLenovoLoq: {identity.IsLenovoLoq}");
        sb.AppendLine();
        foreach (var row in _rows)
            sb.AppendLine($"{row.Kind}\t{row.Name}\t{(row.Available ? "available" : "unavailable")}\t{row.Detail}");
        sb.AppendLine();
        if (reading is not null)
        {
            sb.AppendLine($"CPU temp: {Fmt(reading.CpuTemperature)}");
            sb.AppendLine($"GPU temp: {Fmt(reading.GpuTemperature)}");
            sb.AppendLine($"CPU RPM: {Fmt(reading.CpuFanRpm)}");
            sb.AppendLine($"GPU RPM: {Fmt(reading.GpuFanRpm)}");
            sb.AppendLine($"CPU usage: {Fmt(reading.CpuUsage)}");
            sb.AppendLine($"GPU usage: {Fmt(reading.GpuUsage)}");
        }
        return sb.ToString();
    }

    private static string Fmt(double? value) => value is double n ? n.ToString("0.###") : "null";

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"LOQ-Control-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, _reportText);
            ExportStatus.Text = $"Exported to {path}";
            ExportStatus.Foreground = (Brush)FindResource("Brush.Success");
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"Export failed: {ex.Message}";
            ExportStatus.Foreground = (Brush)FindResource("Brush.Critical");
        }
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_reportText);
            ExportStatus.Text = "Technical details copied to clipboard.";
            ExportStatus.Foreground = (Brush)FindResource("Brush.Information");
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"Copy failed: {ex.Message}";
            ExportStatus.Foreground = (Brush)FindResource("Brush.Critical");
        }
    }

    private enum DiagnosticKind { Ok, Warn, Fail }
    private sealed record DiagnosticRow(string Name, bool Available, DiagnosticKind Kind, string Detail);
}
