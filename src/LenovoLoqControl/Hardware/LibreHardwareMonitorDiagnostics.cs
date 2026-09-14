using LibreHardwareMonitor.Hardware;

namespace LenovoLoqControl.Hardware;

internal sealed record DiagnosticsHardwareSnapshot(
    double? CpuTemperature,
    double? GpuTemperature,
    double? CpuUsage,
    double? GpuUsage,
    double? CpuFanRpm,
    double? GpuFanRpm,
    double? SsdTemperature,
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
        IsMemoryEnabled = true
    };
    private readonly object _sync = new();
    private bool _opened;

    public DiagnosticsHardwareSnapshot Read()
    {
        lock (_sync)
        {
            try
            {
                if (!_opened)
                {
                    _computer.Open();
                    _opened = true;
                }

                var sensors = new List<ISensor>();
                foreach (var hardware in _computer.Hardware)
                    Collect(hardware, sensors);

                var temperatures = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is not null).ToArray();
                var loads = sensors.Where(s => s.SensorType == SensorType.Load && s.Value is not null).ToArray();
                var fans = sensors.Where(s => s.SensorType == SensorType.Fan && s.Value is not null).ToArray();
                var storageTemperatures = temperatures.Where(s => IsStorage(s.Hardware)).ToArray();

                return new DiagnosticsHardwareSnapshot(
                    Find(temperatures, HardwareType.Cpu, "Package", "Core Average"),
                    Find(temperatures, IsGpu, "Core", "GPU"),
                    Find(loads, HardwareType.Cpu, "Total", "CPU"),
                    Find(loads, IsGpu, "Core", "GPU"),
                    Find(fans, HardwareType.Cpu, "CPU"),
                    Find(fans, IsGpu, "GPU"),
                    storageTemperatures.Select(s => (double?)s.Value!.Value).DefaultIfEmpty().Max(),
                    sensors.Select(s => $"{s.Hardware.Name}: {s.Name} = {s.Value:0.#}").ToArray());
            }
            catch
            {
                return new DiagnosticsHardwareSnapshot(null, null, null, null, null, null, null, []);
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

    private static double? Find(IEnumerable<ISensor> sensors, HardwareType type, params string[] names) =>
        Find(sensors, hardware => hardware.HardwareType == type, names);

    private static double? Find(IEnumerable<ISensor> sensors, Func<IHardware, bool> hardwareFilter, params string[] names) =>
        sensors.Where(s => hardwareFilter(s.Hardware))
            .OrderByDescending(s => names.Any(n => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Select(s => (double?)s.Value)
            .FirstOrDefault();

    private static bool IsGpu(IHardware hardware) =>
        hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
}
