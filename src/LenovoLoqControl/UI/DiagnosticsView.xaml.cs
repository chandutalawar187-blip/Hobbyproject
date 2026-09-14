using System.IO;
using System.Text;
using System.Management;
using Microsoft.Win32;
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
    private bool _actionsStacked;

    public DiagnosticsView(IHardwareBackend? hardware = null)
    {
        InitializeComponent();
        _hardware = hardware ?? new HardwareBackend();
        SizeChanged += DiagnosticsViewSizeChanged;
        Loaded += async (_, _) =>
        {
            AdaptDiagnosticsActions(ActualWidth);
            await BuildReportAsync();
        };
    }

    private void DiagnosticsViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && e.NewSize.Width > 0)
            AdaptDiagnosticsActions(e.NewSize.Width);
    }

    private void AdaptDiagnosticsActions(double width)
    {
        if (DiagnosticsActions is null)
            return;

        var stack = width < ResponsiveLayout.BreakpointMedium;
        if (stack == _actionsStacked)
            return;

        _actionsStacked = stack;
        ResponsiveLayout.Transition(DiagnosticsActions, () =>
        {
            DiagnosticsActions.Orientation = stack ? Orientation.Vertical : Orientation.Horizontal;
            foreach (UIElement child in DiagnosticsActions.Children)
            {
                if (child is FrameworkElement fe)
                    fe.Margin = stack ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 10, 0);
            }

            // Last button should not keep trailing margin in horizontal mode.
            if (!stack && DiagnosticsActions.Children.Count > 0
                && DiagnosticsActions.Children[^1] is FrameworkElement last)
            {
                last.Margin = new Thickness(0);
            }
        }, DiagnosticsActions);
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

        var inventory = ReadSystemInventory();
        AddRow("Memory inventory", inventory.MemoryAvailable, inventory.MemoryKind, inventory.MemorySummary);
        AddRow("Storage inventory", inventory.StorageAvailable, inventory.StorageKind, inventory.StorageSummary);
        AddRow("SSD temperature", inventory.SsdTemperature is not null, inventory.SsdTemperature is not null ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            inventory.SsdTemperature is double ssd ? $"{ssd:0.#} °C" : "Unavailable through supported Windows storage interface");
        AddRow("Battery information", inventory.BatteryAvailable, inventory.BatteryKind, inventory.BatterySummary);
        AddRow("Graphics adapters", inventory.GraphicsAvailable, inventory.GraphicsKind, inventory.GraphicsSummary);
        AddRow("Display information", inventory.DisplayAvailable, inventory.DisplayKind, inventory.DisplaySummary);
        AddRow("Windows information", true, DiagnosticKind.Ok, inventory.WindowsSummary);

        _reportText = BuildTextReport(identity, reading, inventory);
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

    private string BuildTextReport(HardwareIdentity identity, SensorReading? reading, SystemInventory inventory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LOQ CONTROL - DIAGNOSTIC REPORT");
        sb.AppendLine(new string('=', 92));
        sb.AppendLine($"Generated    : {DateTimeOffset.Now:u}");
        sb.AppendLine($"Manufacturer : {identity.Manufacturer}");
        sb.AppendLine($"Model        : {identity.Model}");
        sb.AppendLine($"Processor    : {identity.Processor}");
        sb.AppendLine($"Lenovo LOQ   : {(identity.IsLenovoLoq ? "Yes" : "No")}");
        sb.AppendLine();
        sb.AppendLine("CAPABILITY CHECKS");
        sb.AppendLine(new string('-', 92));
        sb.AppendLine($"{"STATUS",-8} {"CAPABILITY",-34} {"AVAILABILITY",-15} DETAILS");
        sb.AppendLine(new string('-', 92));
        foreach (var row in _rows)
        {
            var status = row.Kind switch
            {
                DiagnosticKind.Ok => "OK",
                DiagnosticKind.Warn => "WARN",
                _ => "FAIL"
            };
            var availability = row.Available ? "Available" : "Unavailable";
            sb.AppendLine($"{status,-8} {TrimForColumn(row.Name, 34),-34} {availability,-15} {row.Detail}");
        }

        sb.AppendLine();
        sb.AppendLine("CURRENT TELEMETRY");
        sb.AppendLine(new string('-', 92));
        if (reading is not null)
        {
            sb.AppendLine($"{"CPU temperature",-24} {Fmt(reading.CpuTemperature),12}");
            sb.AppendLine($"{"GPU temperature",-24} {Fmt(reading.GpuTemperature),12}");
            sb.AppendLine($"{"CPU fan RPM",-24} {Fmt(reading.CpuFanRpm),12}");
            sb.AppendLine($"{"GPU fan RPM",-24} {Fmt(reading.GpuFanRpm),12}");
            sb.AppendLine($"{"CPU utilization",-24} {Fmt(reading.CpuUsage),12}");
            sb.AppendLine($"{"GPU utilization",-24} {Fmt(reading.GpuUsage),12}");
        }
        else
            sb.AppendLine("Telemetry unavailable through the supported interface.");

        sb.AppendLine();
        sb.AppendLine("SYSTEM INVENTORY");
        sb.AppendLine(new string('-', 92));
        sb.AppendLine($"{"Memory",-24} {inventory.MemorySummary}");
        sb.AppendLine($"{"Storage",-24} {inventory.StorageSummary}");
        sb.AppendLine($"{"SSD temperature",-24} {Fmt(inventory.SsdTemperature)} °C");
        sb.AppendLine($"{"Battery",-24} {inventory.BatterySummary}");
        sb.AppendLine($"{"Graphics",-24} {inventory.GraphicsSummary}");
        sb.AppendLine($"{"Display",-24} {inventory.DisplaySummary}");
        sb.AppendLine($"{"Windows",-24} {inventory.WindowsSummary}");

        return sb.ToString();
    }

    private static string Fmt(double? value) => value is double n ? n.ToString("0.###") : "Unavailable";

    private static string TrimForColumn(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    private static SystemInventory ReadSystemInventory()
    {
        var memoryParts = new List<string>();
        var storageParts = new List<string>();
        double? ssdTemperature = null;
        var memoryAvailable = false;
        var storageAvailable = false;
        var batteryAvailable = false;
        var batteryKind = DiagnosticKind.Warn;
        var batterySummary = "Battery information unavailable";

        try
        {
            using var memory = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            foreach (ManagementObject row in memory.Get())
            {
                var capacity = Convert.ToDouble(row["Capacity"] ?? 0) / (1024d * 1024d * 1024d);
                var speed = row["Speed"]?.ToString();
                var manufacturer = row["Manufacturer"]?.ToString()?.Trim();
                var part = row["PartNumber"]?.ToString()?.Trim();
                memoryParts.Add($"{capacity:0.#} GB {speed} MHz {manufacturer} {part}".Trim());
            }
            memoryAvailable = memoryParts.Count > 0;
        }
        catch
        {
            memoryParts.Add("Unable to query physical memory");
        }

        try
        {
            using var disks = new ManagementObjectSearcher(
                "SELECT DeviceID, Model, Size, MediaType, InterfaceType FROM Win32_DiskDrive");
            foreach (ManagementObject row in disks.Get())
            {
                var size = Convert.ToDouble(row["Size"] ?? 0) / (1024d * 1024d * 1024d);
                var model = row["Model"]?.ToString()?.Trim() ?? "Unknown drive";
                var media = row["MediaType"]?.ToString()?.Trim();
                var bus = row["InterfaceType"]?.ToString()?.Trim();
                storageParts.Add($"{model} · {size:0.#} GB · {media} · {bus}".Trim());

                var smartTemperature = SmartmontoolsTemperatureReader.ReadTemperature(
                    row["DeviceID"]?.ToString() ?? "",
                    model);
                if (smartTemperature is double temperature)
                    ssdTemperature = ssdTemperature is double current
                        ? Math.Max(current, temperature)
                        : temperature;
            }
            storageAvailable = storageParts.Count > 0;
        }
        catch
        {
            storageParts.Add("Unable to query physical storage");
        }

        try
        {
            using var physicalDisks = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT * FROM MSFT_PhysicalDisk");
            foreach (ManagementObject disk in physicalDisks.Get())
            {
                var value = disk.Properties["Temperature"]?.Value;
                if (value is not null && double.TryParse(value.ToString(), out var temperature)
                    && temperature is > 0 and < 150)
                    ssdTemperature = ssdTemperature is double current
                        ? Math.Max(current, temperature)
                        : temperature;
            }

            using var reliability = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT Temperature FROM MSFT_StorageReliabilityCounter");
            foreach (ManagementObject counter in reliability.Get())
            {
                if (double.TryParse(counter["Temperature"]?.ToString(), out var temperature)
                    && temperature is > 0 and < 150)
                    ssdTemperature = ssdTemperature is double current
                        ? Math.Max(current, temperature)
                        : temperature;
            }
        }
        catch
        {
            // SSD temperature is optional and often requires vendor storage support.
        }

        try
        {
            using var batteries = new ManagementObjectSearcher(
                "SELECT Name, EstimatedChargeRemaining, BatteryStatus, DesignCapacity, FullChargeCapacity, Chemistry, Status FROM Win32_Battery");
            var batteryRows = batteries.Get().Cast<ManagementObject>().ToArray();
            if (batteryRows.Length > 0)
            {
                var battery = batteryRows[0];
                batteryAvailable = true;
                batteryKind = DiagnosticKind.Ok;
                var charge = battery["EstimatedChargeRemaining"]?.ToString() ?? "Unavailable";
                var status = battery["BatteryStatus"]?.ToString() ?? "Unavailable";
                var design = battery["DesignCapacity"]?.ToString() ?? "Unavailable";
                var full = battery["FullChargeCapacity"]?.ToString() ?? "Unavailable";
                var chemistry = battery["Chemistry"]?.ToString() ?? "Unavailable";
                batterySummary = $"{battery["Name"] ?? "Battery"} · Charge {charge}% · Status {status} · Design {design} mWh · Full {full} mWh · Chemistry {chemistry}";
            }
        }
        catch
        {
            batterySummary = "Unable to query Windows battery information";
        }

        var graphicsParts = new List<string>();
        var displayParts = new List<string>();
        try
        {
            using var graphics = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, AdapterRAM, VideoModeDescription FROM Win32_VideoController");
            foreach (ManagementObject adapter in graphics.Get())
            {
                var memory = adapter["AdapterRAM"] is object rawMemory
                    && ulong.TryParse(rawMemory.ToString(), out var bytes)
                    ? $"{bytes / (1024d * 1024d * 1024d):0.#} GB"
                    : "VRAM unavailable";
                graphicsParts.Add($"{adapter["Name"] ?? "Unknown adapter"} · Driver {adapter["DriverVersion"] ?? "Unavailable"} · {memory} · {adapter["VideoModeDescription"] ?? "Mode unavailable"}");
            }
        }
        catch
        {
            graphicsParts.Add("Unable to query graphics adapters");
        }

        try
        {
            using var displays = new ManagementObjectSearcher(
                "SELECT Name, MonitorManufacturer, ScreenWidth, ScreenHeight, PNPDeviceID FROM Win32_DesktopMonitor");
            foreach (ManagementObject display in displays.Get())
            {
                displayParts.Add($"{display["Name"] ?? "Unknown display"} · {display["MonitorManufacturer"] ?? "Manufacturer unavailable"} · {display["ScreenWidth"] ?? "?"}x{display["ScreenHeight"] ?? "?"} · {display["PNPDeviceID"] ?? "ID unavailable"}");
            }
        }
        catch
        {
            displayParts.Add("Unable to query display information");
        }

        string displayVersion;
        string build;
        try
        {
            displayVersion = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "DisplayVersion",
                null)?.ToString() ?? "Unavailable";
            build = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "CurrentBuild",
                null)?.ToString() ?? Environment.OSVersion.Version.Build.ToString();
        }
        catch
        {
            displayVersion = "Unavailable";
            build = Environment.OSVersion.Version.Build.ToString();
        }

        var architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
        var windowsSummary = $"{Environment.OSVersion.VersionString} · Version {displayVersion} · Build {build} · {architecture}";

        return new SystemInventory(
            memoryAvailable,
            memoryAvailable ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            memoryParts.Count == 0 ? "Unavailable" : string.Join(" | ", memoryParts),
            storageAvailable,
            storageAvailable ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            storageParts.Count == 0 ? "Unavailable" : string.Join(" | ", storageParts),
            ssdTemperature,
            batteryAvailable,
            batteryKind,
            batterySummary,
            graphicsParts.Count > 0,
            graphicsParts.Count > 0 ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            graphicsParts.Count == 0 ? "Unavailable" : string.Join(" | ", graphicsParts),
            displayParts.Count > 0,
            displayParts.Count > 0 ? DiagnosticKind.Ok : DiagnosticKind.Warn,
            displayParts.Count == 0 ? "Unavailable" : string.Join(" | ", displayParts),
            windowsSummary);
    }

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
    private sealed record SystemInventory(
        bool MemoryAvailable,
        DiagnosticKind MemoryKind,
        string MemorySummary,
        bool StorageAvailable,
        DiagnosticKind StorageKind,
        string StorageSummary,
        double? SsdTemperature,
        bool BatteryAvailable,
        DiagnosticKind BatteryKind,
        string BatterySummary,
        bool GraphicsAvailable,
        DiagnosticKind GraphicsKind,
        string GraphicsSummary,
        bool DisplayAvailable,
        DiagnosticKind DisplayKind,
        string DisplaySummary,
        string WindowsSummary);
}
