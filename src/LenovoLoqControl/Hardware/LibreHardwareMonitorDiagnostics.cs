using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace LenovoLoqControl.Hardware;

internal sealed record DiagnosticsHardwareSnapshot(
    double? CpuTemperature,
    double? GpuTemperature,
    double? CpuUsage,
    double? GpuUsage,
    double? CpuFanRpm,
    double? GpuFanRpm,
    double? SsdTemperature,
    bool BatteryAvailable,
    string BatterySummary,
    IReadOnlyList<string> Sensors);

internal sealed class LibreHardwareMonitorDiagnostics : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsStorageEnabled = true,
        IsMemoryEnabled = true,
        IsBatteryEnabled = true
    };
    private readonly object _sync = new();
    private bool _opened;

    public DiagnosticsHardwareSnapshot Read()
    {
        lock (_sync)
        {
            var powerStatus = GetSystemPowerStatus();
            try
            {
                if (!_opened)
                {
                    _computer.Open();
                    _opened = true;
                }

                var sensors = new List<ISensor>();
                foreach (var hardware in _computer.Hardware)
                {
                    try
                    {
                        Collect(hardware, sensors);
                    }
                    catch
                    {
                        // One provider must not hide sensors returned by the other providers.
                    }
                }

                var temperatures = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is not null).ToArray();
                var loads = sensors.Where(s => s.SensorType == SensorType.Load && s.Value is not null).ToArray();
                var fans = sensors.Where(s => s.SensorType == SensorType.Fan && s.Value is not null).ToArray();
                var storageTemperatures = temperatures.Where(s => IsStorage(s.Hardware)).ToArray();
                var batterySensors = sensors.Where(s => s.Hardware.HardwareType == HardwareType.Battery
                    && s.Value is not null).ToArray();

                return new DiagnosticsHardwareSnapshot(
                    Find(temperatures, HardwareType.Cpu, "Package", "Core Average"),
                    Find(temperatures, IsGpu, "Core", "GPU"),
                    Find(loads, HardwareType.Cpu, "Total", "CPU"),
                    Find(loads, IsGpu, "Core", "GPU"),
                    Find(fans, HardwareType.Cpu, "CPU"),
                    Find(fans, IsGpu, "GPU"),
                    storageTemperatures.Select(s => (double?)s.Value!.Value).DefaultIfEmpty().Max(),
                    powerStatus is not null || batterySensors.Length > 0,
                    FormatBattery(batterySensors, powerStatus),
                    sensors.Select(s => $"{s.Hardware.Name}: {s.Name} = {s.Value:0.#}").ToArray());
            }
            catch
            {
                return new DiagnosticsHardwareSnapshot(null, null, null, null, null, null, null,
                    powerStatus is not null,
                    FormatBattery([], powerStatus), []);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_opened)
                _computer.Close();
            _opened = false;
        }
    }

    private static void Collect(IHardware hardware, ICollection<ISensor> sensors)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
            sensors.Add(sensor);
        foreach (var child in hardware.SubHardware)
            Collect(child, sensors);
    }

    private static bool IsStorage(IHardware hardware) =>
        hardware.HardwareType == HardwareType.Storage
        || hardware.Name.Contains("SSD", StringComparison.OrdinalIgnoreCase)
        || hardware.Name.Contains("NVMe", StringComparison.OrdinalIgnoreCase);

    private static string FormatBattery(IEnumerable<ISensor> sensors, BatteryPowerStatus? powerStatus)
    {
        var batterySensors = sensors.ToArray();
        var rate = batterySensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Power
            && s.Name.Contains("Rate", StringComparison.OrdinalIgnoreCase));

        var lhmCharging = rate?.Name.Contains("Charge Rate", StringComparison.OrdinalIgnoreCase) == true
            && !rate.Name.Contains("Discharge", StringComparison.OrdinalIgnoreCase);
        var lhmDischarging = rate?.Name.Contains("Discharge Rate", StringComparison.OrdinalIgnoreCase) == true;
        var lhmIdle = rate?.Name.Contains("Charge/Discharge Rate", StringComparison.OrdinalIgnoreCase) == true
            && rate.Value is float rateValue
            && Math.Abs(rateValue) < 0.001;
        // Windows AC state is authoritative for direction. LHM can briefly retain or
        // relabel the previous rate while the adapter state is changing.
        var isCharging = powerStatus is { IsOnBattery: false, IsCharging: true }
            || (powerStatus is null && lhmCharging);
        var isDischarging = powerStatus is { IsOnBattery: true }
            || (powerStatus is null && !isCharging && lhmDischarging);
        var state = isCharging
            ? "Status: Charging"
            : isDischarging
                ? "Status: Discharging"
                : powerStatus is not null || lhmIdle
                    ? "Status: Not charging or discharging"
                    : "Status: Unavailable";

        var current = batterySensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Current
            && ((isCharging && s.Name.Contains("Charge Current", StringComparison.OrdinalIgnoreCase)
                    && !s.Name.Contains("Discharge", StringComparison.OrdinalIgnoreCase))
                || (isDischarging && s.Name.Contains("Discharge Current", StringComparison.OrdinalIgnoreCase))));

        var values = batterySensors
            .Where(s => s.SensorType is not SensorType.Power and not SensorType.Current)
            .OrderBy(s => s.SensorType)
            .ThenBy(s => s.Name)
            .Select(s => $"{s.Name}: {s.Value!.Value:0.###} {BatteryUnit(s.SensorType)}".Trim())
            .ToList();

        if (isCharging || isDischarging)
        {
            if (rate is not null)
                values.Add($"{rate.Name}: {rate.Value!.Value:0.###} {BatteryUnit(rate.SensorType)}");
            if (current is not null)
                values.Add($"{current.Name}: {current.Value!.Value:0.###} {BatteryUnit(current.SensorType)}");
        }

        var mode = ReadLenovoBatteryMode();
        if (mode is not null)
            values.Add(mode);

        return string.Join(" · ", new[] { state }.Concat(values));
    }

    private static string? ReadLenovoBatteryMode()
    {
        try
        {
            const string path = @"HKEY_CURRENT_USER\Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin";
            var value = Registry.GetValue(path, "BatteryChargeMode", null)?.ToString();
            return value switch
            {
                "Storage" => "Charging mode: Conservation (80% limit)",
                "Normal" => "Charging mode: Normal",
                "Quick" => "Charging mode: Rapid charge",
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static BatteryPowerStatus? GetSystemPowerStatus()
    {
        try
        {
            if (!NativeGetSystemPowerStatus(out var status)
                || (status.BatteryFlag & 0x80) != 0)
                return null;

            var isOnBattery = status.ACLineStatus == 0;
            var isCharging = !isOnBattery && (status.BatteryFlag & 0x08) != 0;
            return new BatteryPowerStatus(isCharging, isOnBattery);
        }
        catch
        {
            return null;
        }
    }

    private static string BatteryUnit(SensorType type) => type switch
    {
        SensorType.Level => "%",
        SensorType.Temperature => "°C",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Energy => "mWh",
        SensorType.TimeSpan => "seconds",
        SensorType.Data => "GB",
        _ => string.Empty
    };

    private static double? Find(IEnumerable<ISensor> sensors, HardwareType type, params string[] names) =>
        Find(sensors, hardware => hardware.HardwareType == type, names);

    private static double? Find(IEnumerable<ISensor> sensors, Func<IHardware, bool> hardwareFilter, params string[] names) =>
        sensors.Where(s => hardwareFilter(s.Hardware))
            .OrderByDescending(s => names.Any(n => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Select(s => (double?)s.Value)
            .FirstOrDefault();

    private static bool IsGpu(IHardware hardware) =>
        hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private readonly record struct BatteryPowerStatus(bool IsCharging, bool IsOnBattery);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
