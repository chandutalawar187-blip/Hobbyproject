using System.Management;
using System.Runtime.InteropServices;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

internal sealed class LenovoWmiOperations : ILenovoWmiOperations
{
    private const string Scope = @"root\WMI";
    private const string GameZoneQuery = "SELECT * FROM LENOVO_GAMEZONE_DATA";
    private const uint FanFullSpeedFeatureId = 0x04020000;

    public bool IsSmartFanSupported { get; }

    public LenovoWmiOperations()
    {
        IsSmartFanSupported = Probe();
    }

    public void SetSmartFanMode(uint mode)
    {
        // Lenovo LOQ firmware exposes 1=Quiet, 2=Balanced, 3=Performance,
        // 224=Max Cooling, and 255=Custom on the verified five-state path.
        if (mode is not (1u or 2u or 3u or 224u or 255u))
            throw new ArgumentOutOfRangeException(nameof(mode), "The firmware mode is not in the verified Lenovo allowlist.");

        InvokeMethod(GameZoneQuery, "SetSmartFanMode",
            new Dictionary<string, object> { ["Data"] = mode });
    }

    public uint? GetSmartFanMode()
    {
        try
        {
            var result = InvokeMethod(GameZoneQuery, "GetSmartFanMode",
                new Dictionary<string, object>());
            var mode = ReadInt32(result, "Data");
            return mode is 1 or 2 or 3 or 224 or 255 ? (uint)mode : null;
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException
            or InvalidCastException or FormatException or UnauthorizedAccessException
            or COMException)
        {
            return null;
        }
    }

    public void SetFullSpeed(bool enabled)
    {
        using var searcher = new ManagementObjectSearcher(
            Scope, "SELECT * FROM LENOVO_OTHER_METHOD");
        using var rows = searcher.Get();
        using var method = rows.Cast<ManagementObject>()
            .FirstOrDefault(row => Convert.ToBoolean(row["Active"] ?? true))
            ?? throw new InvalidOperationException("Lenovo Other Mode is unavailable.");

        var expected = enabled ? 1u : 0u;
        var current = ReadFeatureValue(method);
        if (current == expected)
            return;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var parameters = method.GetMethodParameters("SetFeatureValue");
            parameters["IDs"] = FanFullSpeedFeatureId;
            parameters["value"] = expected;
            using var result = method.InvokeMethod("SetFeatureValue", parameters, null);
            ValidateFeatureResult(result);

            var verified = ReadFeatureValue(method);
            if (verified == expected)
                return;

            if (attempt < 3)
                Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            $"Lenovo full-speed feature 0x{FanFullSpeedFeatureId:X8} did not verify value {expected}.");
    }

    private static uint ReadFeatureValue(ManagementObject method)
    {
        using var parameters = method.GetMethodParameters("GetFeatureValue");
        parameters["IDs"] = FanFullSpeedFeatureId;
        using var result = method.InvokeMethod("GetFeatureValue", parameters, null);
        if (result?.Properties["value"]?.Value is null)
            throw new InvalidOperationException("Lenovo full-speed feature returned no value.");
        return Convert.ToUInt32(result["value"]);
    }

    private static void ValidateFeatureResult(ManagementBaseObject? result)
    {
        if (result?.Properties["ReturnValue"]?.Value is null)
            return;

        var status = Convert.ToUInt32(result["ReturnValue"]);
        if (status is not 0 and not 1)
            throw new InvalidOperationException($"Lenovo Other Mode rejected the feature request (status {status}).");
    }

    public FirmwareFanTable? ReadCustomFanTable()
    {
        using var searcher = new ManagementObjectSearcher(Scope,
            "SELECT SensorTable_Data, FanTable_Data FROM LENOVO_FAN_TABLE_DATA " +
            "WHERE Active = True AND Mode = 255 AND Fan_Id = 1 AND Sensor_ID = 4");
        using var rows = searcher.Get();
        using var row = rows.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException("Custom fan table is unavailable.");
        if (row["SensorTable_Data"] is not Array temperatureData ||
            row["FanTable_Data"] is not Array fanData)
            throw new InvalidOperationException("The Lenovo fan table returned invalid data.");

        var temperatures = temperatureData.Cast<object>().Select(Convert.ToInt32).ToArray();
        var speeds = fanData.Cast<object>().Select(Convert.ToInt32).ToArray();
        if (temperatures.Length != 10 || speeds.Length != 10)
            throw new InvalidOperationException("The Lenovo fan table has an unsupported shape.");
        return new FirmwareFanTable(temperatures, speeds, [1, 1, 1, 1, 1, 1, 1, 1, 3, 5]);
    }

    public void SetFanTable(IReadOnlyList<int> steps)
    {
        if (steps.Count != 10 || steps.Any(step => step is < 0 or > 10))
            throw new ArgumentOutOfRangeException(nameof(steps), "A Lenovo fan table must contain ten steps from 0 through 10.");

        var bytes = new List<byte>(64) { 1, 0, 0, 0, 0, 0 };
        foreach (var step in steps)
            bytes.AddRange(BitConverter.GetBytes((ushort)step));
        while (bytes.Count < 64)
            bytes.Add(0);

        InvokeMethod("SELECT * FROM LENOVO_FAN_METHOD", "Fan_Set_Table",
            new Dictionary<string, object> { ["FanTable"] = bytes.ToArray() });
    }

    private static bool Probe()
    {
        try
        {
            var result = InvokeMethod(GameZoneQuery, "IsSupportSmartFan",
                new Dictionary<string, object>());
            return ReadInt32(result, "Data") > 0;
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException
            or InvalidCastException or FormatException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static int ReadInt32(PropertyDataCollection? properties, string name)
    {
        var value = properties?[name]?.Value;
        while (value is PropertyData property)
            value = property.Value;
        return value is null ? 0 : Convert.ToInt32(value);
    }

    private static PropertyDataCollection? InvokeMethod(string query, string methodName,
        Dictionary<string, object> parameters)
    {
        using var searcher = new ManagementObjectSearcher(Scope, query);
        using var objects = searcher.Get();
        using var managementObject = objects.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException($"No WMI object found for {query}.");
        using var methodParameters = managementObject.GetMethodParameters(methodName);
        foreach (var parameter in parameters)
            methodParameters[parameter.Key] = parameter.Value;
        return managementObject.InvokeMethod(methodName, methodParameters, new InvokeMethodOptions())?.Properties;
    }

    public void Dispose()
    {
    }
}
