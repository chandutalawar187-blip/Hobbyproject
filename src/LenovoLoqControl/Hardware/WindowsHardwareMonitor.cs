using LenovoLoqControl.Core;
using Microsoft.Win32;
using System.Management;

namespace LenovoLoqControl.Hardware;

public sealed class WindowsHardwareMonitor : IHardwareMonitor
{
    private const string LenovoOtherMethodQuery = "SELECT * FROM LENOVO_OTHER_METHOD";
    private const uint CpuFanSpeedId = 0x04030001;
    private const uint GpuFanSpeedId = 0x04030002;
    private const uint CpuTemperatureId = 0x05040000;
    private const uint GpuTemperatureId = 0x05050000;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly Func<double?> _gpuClockReader;
    private readonly object _cacheLock = new();
    private SensorReading? _cachedReading;
    private DateTimeOffset _cachedAt;
    public HardwareIdentity Identity { get; }

    public WindowsHardwareMonitor(Func<double?>? gpuClockReader = null)
    {
        _gpuClockReader = gpuClockReader ?? (() => null);
        var manufacturer = (string?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer", null) ?? "Unknown";
        var model = (string?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName", null) ?? "Unknown";
        var family = (string?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemFamily", null);
        var version = (string?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemVersion", null);
        var sku = (string?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemSKU", null);
        var descriptiveModel = new[] { family, version, sku }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) &&
                value.Contains("LOQ", StringComparison.OrdinalIgnoreCase)) ?? model;
        var processor = ReadProcessorName()
            ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
            ?? "Unknown processor";
        Identity = new HardwareIdentity(manufacturer, model, processor,
            manufacturer.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) &&
            (descriptiveModel.Contains("LOQ", StringComparison.OrdinalIgnoreCase) ||
             sku?.Contains("LOQ", StringComparison.OrdinalIgnoreCase) == true));
        if (Identity.IsLenovoLoq && !string.Equals(model, descriptiveModel, StringComparison.OrdinalIgnoreCase))
            Identity = Identity with { Model = descriptiveModel };

        _ = ReadAsync(CancellationToken.None);
    }

    private static string? ReadProcessorName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Processor");
            using var rows = searcher.Get();
            using var row = rows.Cast<ManagementObject>()
                .FirstOrDefault(processor => !string.IsNullOrWhiteSpace(processor["Name"] as string));
            return row?["Name"] as string;
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cachedReading is not null &&
                DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMilliseconds(250))
                return _cachedReading;
        }

        await _readLock.WaitAsync(cancellationToken);
        try
        {
            lock (_cacheLock)
            {
                if (_cachedReading is not null &&
                    DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMilliseconds(250))
                    return _cachedReading;
            }

            var reading = await Task.Run(() => ReadCore(cancellationToken), cancellationToken);
            lock (_cacheLock)
            {
                _cachedReading = reading;
                _cachedAt = DateTimeOffset.UtcNow;
            }

            return reading;
        }
        finally
        {
            _readLock.Release();
        }
    }

    private SensorReading ReadCore(CancellationToken cancellationToken)
    {
            cancellationToken.ThrowIfCancellationRequested();
            double? cpuUsage = null;
            double? cpuClock = null;
            double? memoryUsage = null;
            double? battery = null;
            bool? charging = null;
            double? cpuTemperature = null;
            double? gpuTemperature = null;
            double? gpuUsage = null;
            double? gpuClock = null;
            double? cpuFanRpm = null;
            double? gpuFanRpm = null;

            try
            {
                using var other = new ManagementObjectSearcher(
                    @"root\WMI", LenovoOtherMethodQuery);
                using var rows = other.Get();
                using var row = rows.Cast<ManagementObject>().FirstOrDefault();
                if (row is not null)
                {
                    cpuTemperature = ReadLenovoFeature(row, CpuTemperatureId);
                    gpuTemperature = ReadLenovoFeature(row, GpuTemperatureId);
                    cpuFanRpm = ReadLenovoFeature(row, CpuFanSpeedId);
                    gpuFanRpm = ReadLenovoFeature(row, GpuFanSpeedId);
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }

            gpuClock = _gpuClockReader();

            try
            {
                using var gpu = new ManagementObjectSearcher(
                    "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
                using var rows = gpu.Get();
                var values = rows.Cast<ManagementObject>()
                    .Select(row =>
                    {
                        using (row)
                            return row["UtilizationPercentage"] is null ? 0d : Convert.ToDouble(row["UtilizationPercentage"]);
                    })
                    .Where(value => value >= 0)
                    .ToArray();
                gpuUsage = values.Length == 0 ? null : Math.Clamp(values.Max(), 0, 100);
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }

            try
            {
                using var processor = new ManagementObjectSearcher("SELECT LoadPercentage, CurrentClockSpeed FROM Win32_Processor");
                using var rows = processor.Get();
                var processorRows = rows.Cast<ManagementObject>().ToArray();
                try
                {
                    if (processorRows.Length > 0)
                    {
                        cpuUsage = processorRows.Average(row => Convert.ToDouble(row["LoadPercentage"] ?? 0));
                        cpuClock = processorRows.Average(row => Convert.ToDouble(row["CurrentClockSpeed"] ?? 0)) / 1000d;
                    }
                }
                finally
                {
                    foreach (var processorRow in processorRows)
                        processorRow.Dispose();
                }
            }
            catch (ManagementException) { }

            try
            {
                using var os = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                using var rows = os.Get();
                using var row = rows.Cast<ManagementObject>().FirstOrDefault();
                if (row is not null)
                {
                    var total = Convert.ToDouble(row["TotalVisibleMemorySize"]);
                    var free = Convert.ToDouble(row["FreePhysicalMemory"]);
                    memoryUsage = total <= 0 ? null : (total - free) / total * 100d;
                }
            }
            catch (ManagementException) { }

            try
            {
                using var batteries = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
                using var rows = batteries.Get();
                using var row = rows.Cast<ManagementObject>().FirstOrDefault();
                if (row is not null)
                {
                    battery = Convert.ToDouble(row["EstimatedChargeRemaining"]);
                    charging = Convert.ToInt32(row["BatteryStatus"]) is 2 or 6 or 7 or 8 or 9;
                }
            }
            catch (ManagementException) { }

            return new SensorReading(cpuTemperature, gpuTemperature, cpuUsage, gpuUsage, cpuClock, gpuClock,
                cpuFanRpm, gpuFanRpm,
                memoryUsage, battery, charging, DateTimeOffset.Now);
    }

    private static double? ReadLenovoFeature(ManagementObject row, uint id)
    {
        try
        {
            using var parameters = row.GetMethodParameters("GetFeatureValue");
            parameters["IDs"] = id;
            var result = row.InvokeMethod("GetFeatureValue", parameters, new InvokeMethodOptions());
            var value = Convert.ToDouble(result?["Value"] ?? -1);
            return value >= 0 ? value : null;
        }
        catch (ManagementException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _readLock.Dispose();
}
