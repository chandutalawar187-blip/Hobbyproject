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
    private readonly object _lifecycleLock = new();
    private Task? _disposeTask;
    private SensorReading? _cachedReading;
    private DateTimeOffset _cachedAt;
    private int _disposed;
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
        lock (_lifecycleLock)
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_cacheLock)
        {
            if (_cachedReading is not null &&
                DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMilliseconds(250))
                return _cachedReading;
        }

        lock (_lifecycleLock)
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var lockHeld = false;
        try
        {
            await _readLock.WaitAsync(cancellationToken);
            lockHeld = true;
            lock (_lifecycleLock)
            {
                if (_disposed != 0)
                    ObjectDisposedException.ThrowIf(true, this);
            }

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
            if (lockHeld)
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

            if (cpuTemperature is null)
                cpuTemperature = ReadWindowsThermalZoneTemperature();

            gpuClock = _gpuClockReader();

            try
            {
                gpuUsage = ReadDiscreteGpuUsage();
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

    private static double? ReadDiscreteGpuUsage()
    {
        var dedicatedAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var adapterQuery = new ManagementObjectSearcher(
                   "SELECT Name, DedicatedUsage, DedicatedLimit FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
        using (var adapterRows = adapterQuery.Get())
        {
            foreach (ManagementObject row in adapterRows)
            {
                using (row)
                {
                    var name = Convert.ToString(row["Name"]);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var dedicatedLimit = double.TryParse(Convert.ToString(row["DedicatedLimit"]), out var limit)
                        ? limit
                        : 0;
                    var dedicatedUsage = double.TryParse(Convert.ToString(row["DedicatedUsage"]), out var usage)
                        ? usage
                        : 0;
                    if (dedicatedLimit <= 0 && dedicatedUsage <= 0)
                        continue;
                    var luid = ExtractGpuLuid(name);
                    if (luid is not null)
                        dedicatedAdapters.Add(luid);
                }
            }
        }

        using var engineQuery = new ManagementObjectSearcher(
            "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
        if (dedicatedAdapters.Count == 0)
            return null;

        using var engineRows = engineQuery.Get();
        var engineUsage = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagementObject row in engineRows)
        {
            using (row)
            {
                var name = Convert.ToString(row["Name"]);
                if (string.IsNullOrWhiteSpace(name) ||
                    !double.TryParse(Convert.ToString(row["UtilizationPercentage"]), out var utilization) ||
                    utilization < 0)
                    continue;

                var luid = ExtractGpuLuid(name);
                if (luid is null || !dedicatedAdapters.Contains(luid))
                    continue;

                var engineType = ExtractGpuEngineType(name) ?? "unknown";
                if (!engineUsage.TryGetValue(luid, out var byType))
                {
                    byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    engineUsage.Add(luid, byType);
                }

                byType[engineType] = Math.Max(byType.GetValueOrDefault(engineType), utilization);
            }
        }

        var selectedUsage = engineUsage.Values
            .SelectMany(byType => byType.Values)
            .DefaultIfEmpty()
            .Max();
        return engineUsage.Count == 0 ? null : Math.Clamp(selectedUsage, 0, 100);
    }

    private static string? ExtractGpuLuid(string name)
    {
        const string marker = "_luid_";
        var start = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = name.IndexOf("_phys_", start, StringComparison.OrdinalIgnoreCase);
        return end > start ? name[start..end] : null;
    }

    private static string? ExtractGpuEngineType(string name)
    {
        const string marker = "_engtype_";
        var start = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return start < 0 ? null : name[(start + marker.Length)..];
    }

    private static double? ReadLenovoFeature(ManagementObject row, uint id)
    {
        try
        {
            using var parameters = row.GetMethodParameters("GetFeatureValue");
            parameters["IDs"] = id;
            var result = row.InvokeMethod("GetFeatureValue", parameters, new InvokeMethodOptions());
            var value = Convert.ToDouble(result?["Value"] ?? -1);
            return value > 0 ? value : null;
        }
        catch (ManagementException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static double? ReadWindowsThermalZoneTemperature()
    {
        var acpiTemperature = ReadTemperatureQuery(
            @"root\WMI",
            "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        if (acpiTemperature is not null)
            return acpiTemperature;

        return ReadTemperatureQuery(
            @"root\CIMV2",
            "SELECT CurrentReading FROM Win32_TemperatureProbe");
    }

    private static double? ReadTemperatureQuery(string scope, string query)
    {
        try
        {
            using var thermalZones = new ManagementObjectSearcher(
                scope,
                query);
            using var rows = thermalZones.Get();
            var temperatures = rows.Cast<ManagementObject>()
                .Select(row =>
                {
                    using (row)
                    {
                        var property = row.Properties
                            .Cast<PropertyData>()
                            .FirstOrDefault(candidate =>
                                string.Equals(candidate.Name, "CurrentTemperature", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(candidate.Name, "CurrentReading", StringComparison.OrdinalIgnoreCase));
                        var raw = Convert.ToDouble(property?.Value ?? 0);
                        return string.Equals(property?.Name, "CurrentTemperature", StringComparison.OrdinalIgnoreCase)
                            ? raw > 0 ? (raw / 10d) - 273.15d : double.NaN
                            : raw;
                    }
                })
                .Where(value => !double.IsNaN(value) && value is >= 0 and <= 125)
                .ToArray();
            return temperatures.Length == 0 ? null : temperatures.Max();
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
        }

        lock (_lifecycleLock)
            _disposeTask ??= DrainAndDisposeAsync();
    }

    private async Task DrainAndDisposeAsync()
    {
        try
        {
            if (!await _readLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                await _readLock.WaitAsync().ConfigureAwait(false);
            _readLock.Release();
            _readLock.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
        }
    }
}
