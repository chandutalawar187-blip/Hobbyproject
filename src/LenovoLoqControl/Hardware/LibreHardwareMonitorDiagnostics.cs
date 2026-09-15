using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32.SafeHandles;
using DiskInfoToolkit;

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
                var storageTemperatures = temperatures
                    .Where(s => IsStorage(s.Hardware))
                    .Where(s => s.Value is > 0 and < 150)
                    .ToArray();
                var ssdTemperature = storageTemperatures.Length > 0
                    ? storageTemperatures.Max(s => (double?)s.Value!.Value)
                    : null;
                Log($"ssdTemperatureBeforeFallback={ssdTemperature?.ToString() ?? "null"}");
                if (ssdTemperature is null)
                    ssdTemperature = ReadDiskInfoToolkitTemperature();
                Log($"ssdTemperatureAfterFallback={ssdTemperature?.ToString() ?? "null"}");
                Log($"read sensors={sensors.Count} temperatures={temperatures.Length} storageTemperatures={storageTemperatures.Length}");
                foreach (var sensor in temperatures)
                    Log($"temperature hardwareType={sensor.Hardware.HardwareType} hardware={sensor.Hardware.Name} sensor={sensor.Name} value={sensor.Value}");
                var batterySensors = sensors.Where(s => s.Hardware.HardwareType == HardwareType.Battery
                    && s.Value is not null).ToArray();

                return new DiagnosticsHardwareSnapshot(
                    Find(temperatures, HardwareType.Cpu, "Package", "Core Average"),
                    Find(temperatures, IsGpu, "Core", "GPU"),
                    Find(loads, HardwareType.Cpu, "Total", "CPU"),
                    Find(loads, IsGpu, "Core", "GPU"),
                    Find(fans, HardwareType.Cpu, "CPU"),
                    Find(fans, IsGpu, "GPU"),
                    ssdTemperature,
                    powerStatus is not null || batterySensors.Length > 0,
                    FormatBattery(batterySensors, powerStatus),
                    sensors.Select(s => $"{s.Hardware.Name}: {s.Name} = {s.Value:0.#}").ToArray());
            }
            catch (Exception ex)
            {
                Log($"LibreHardwareMonitor read failed: {ex}");
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
        || hardware.Name.Contains("NVMe", StringComparison.OrdinalIgnoreCase)
        || hardware.Name.Contains("NVM", StringComparison.OrdinalIgnoreCase)
        || hardware.Name.Contains("SAMSUNG", StringComparison.OrdinalIgnoreCase)
        || hardware.Name.Contains("MZAL", StringComparison.OrdinalIgnoreCase)
        || hardware.Name.Contains("MZVL", StringComparison.OrdinalIgnoreCase);

    private static double? ReadNvmeSmartTemperature()
    {
        for (var diskNumber = 0; diskNumber < 32; diskNumber++)
        {
            using var handle = CreateFile(
                $@"\\.\PhysicalDrive{diskNumber}",
                0,
                FileShare.ReadWrite,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
                continue;

            var query = new StoragePropertyQuery
            {
                PropertyId = StoragePropertyId.StorageDeviceProtocolSpecificProperty,
                QueryType = StorageQueryType.StandardQuery,
                Protocol = new StorageProtocolSpecificData
                {
                    ProtocolType = StorageProtocolType.Nvme,
                    DataType = StorageProtocolDataType.LogPage,
                    RequestValue = 0x02,
                    RequestSubValue = 0,
                    DataOffset = (uint)(Marshal.SizeOf<StorageProtocolDataDescriptor>()
                        + Marshal.SizeOf<StorageProtocolSpecificData>()),
                    DataLength = 512
                }
            };
            var buffer = new byte[4096];
            var querySize = Marshal.SizeOf<StoragePropertyQuery>();
            var queryHandle = GCHandle.Alloc(query, GCHandleType.Pinned);
            try
            {
                Marshal.Copy(queryHandle.AddrOfPinnedObject(), buffer, 0, querySize);
            }
            finally
            {
                queryHandle.Free();
            }

            if (!DeviceIoControl(
                    handle,
                    IoctlStorageQueryProperty,
                    buffer,
                    buffer.Length,
                    buffer,
                    buffer.Length,
                    out var returned,
                    IntPtr.Zero)
                || returned < query.Protocol.DataOffset + 2)
                continue;

            var temperatureKelvin = BitConverter.ToUInt16(buffer, (int)query.Protocol.DataOffset);
            var temperatureCelsius = temperatureKelvin - 273.15;
            if (temperatureCelsius is > 0 and < 150)
            {
                Log($"NVMe SMART temperature physicalDrive={diskNumber} value={temperatureCelsius:0.#}");
                return temperatureCelsius;
            }
        }

        Log("NVMe SMART temperature unavailable.");
        return null;
    }

    private static double? ReadDiskInfoToolkitTemperature()
    {
        try
        {
            double? temperature = null;
            StorageManager.ReloadStorages();
            Log($"DiskInfoToolkit storageCount={StorageManager.Storages.Count}");
            foreach (var disk in StorageManager.Storages)
            {
                try
                {
                    disk.Update();
                    var diskTemperature = disk.Smart?.Temperature;
                    Log($"DiskInfoToolkit disk={disk.Model} bus={disk.BusType} nvme={disk.IsNVMe} temperature={diskTemperature}");
                    if (diskTemperature is > 0 and < 150)
                        temperature = temperature is double current
                            ? Math.Max(current, diskTemperature.Value)
                            : diskTemperature.Value;

                    foreach (var attribute in disk.Smart?.SmartAttributes ?? [])
                        Log($"DiskInfoToolkit smart disk={disk.Model} id={attribute.Info.ID} name={attribute.Info.Name} value={attribute.Attribute}");
                }
                catch (Exception ex)
                {
                    Log($"DiskInfoToolkit disk update failed disk={disk.Model}: {ex}");
                }
            }

            return temperature;
        }
        catch (Exception ex)
        {
            Log($"DiskInfoToolkit read failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public StoragePropertyId PropertyId;
        public StorageQueryType QueryType;
        public StorageProtocolSpecificData Protocol;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageProtocolSpecificData
    {
        public StorageProtocolType ProtocolType;
        public StorageProtocolDataType DataType;
        public uint RequestValue;
        public uint RequestSubValue;
        public uint ProtocolDataOffset;
        public uint ProtocolDataLength;

        public uint DataOffset
        {
            readonly get => ProtocolDataOffset;
            set => ProtocolDataOffset = value;
        }

        public uint DataLength
        {
            readonly get => ProtocolDataLength;
            set => ProtocolDataLength = value;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageProtocolDataDescriptor
    {
        public uint Version;
        public uint Size;
        public StorageProtocolSpecificData Protocol;
    }

    private enum StoragePropertyId : uint
    {
        StorageDeviceProtocolSpecificProperty = 50
    }

    private enum StorageQueryType : uint
    {
        StandardQuery = 0
    }

    private enum StorageProtocolType : uint
    {
        Nvme = 3
    }

    private enum StorageProtocolDataType : uint
    {
        LogPage = 2
    }

    private const uint IoctlStorageQueryProperty = 0x002D1400;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LOQ Control", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "diagnostics-hardware.log"),
                $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

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
