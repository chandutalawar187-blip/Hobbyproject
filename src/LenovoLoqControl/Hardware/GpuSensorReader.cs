using LibreHardwareMonitor.Hardware;

namespace LenovoLoqControl.Hardware;

internal sealed class GpuSensorReader : IDisposable
{
    private readonly object _sync = new();
    private readonly Computer _computer = new()
    {
        IsGpuEnabled = true
    };
    private bool _opened;

    public (double? Usage, double? ClockGhz) Read()
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

                var gpu = _computer.Hardware
                    .Where(hardware => hardware.HardwareType == HardwareType.GpuNvidia)
                    .FirstOrDefault()
                    ?? _computer.Hardware.FirstOrDefault(IsGpu);
                if (gpu is null)
                    return (null, null);

                gpu.Update();
                foreach (var child in gpu.SubHardware)
                    child.Update();

                var sensors = GetSensors(gpu).ToArray();
                var usage = sensors
                    .Where(sensor => sensor.SensorType == SensorType.Load)
                    .Where(sensor => sensor.Value is >= 0 and <= 100)
                    .Where(sensor => sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("3D", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    .Select(sensor => (double?)sensor.Value)
                    .Max();
                var clockMhz = sensors
                    .Where(sensor => sensor.SensorType == SensorType.Clock)
                    .Where(sensor => sensor.Value is > 0)
                    .Where(sensor => sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    .Select(sensor => (double?)sensor.Value)
                    .Max();

                return (usage, clockMhz / 1000d);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                       or DllNotFoundException
                                       or EntryPointNotFoundException
                                       or UnauthorizedAccessException)
            {
                return (null, null);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (!_opened)
                return;

            _computer.Close();
            _opened = false;
        }
    }

    private static IEnumerable<ISensor> GetSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
            yield return sensor;

        foreach (var child in hardware.SubHardware)
        foreach (var sensor in GetSensors(child))
            yield return sensor;
    }

    private static bool IsGpu(IHardware hardware) =>
        hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;
}
