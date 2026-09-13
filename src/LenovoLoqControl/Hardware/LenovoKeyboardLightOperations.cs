using System.Management;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32.SafeHandles;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

internal sealed class LenovoKeyboardLightOperations : IDisposable
{
    private const string Scope = @"root\WMI";
    private const string LightingDataQuery =
        "SELECT * FROM LENOVO_LIGHTING_DATA " +
        "WHERE Lighting_ID = 0 AND Control_Interface = 0";
    private const string LightingMethodQuery = "SELECT * FROM LENOVO_LIGHTING_METHOD";
    private const string EnergyDriverPath = @"\\.\EnergyDrv";
    private const uint EnergyKeyboardIoctl = 0x83102144;
    private const ushort LenovoIteProductFamilyMask = 0xFF00;
    private const ushort LenovoIteProductFamily = 0xC900;
    private const ushort LenovoLoq2024ProductId = 0xC993;
    private const ushort LegacyRgbUsagePage = 0xFF89;
    private const ushort LegacyRgbUsage = 0x00CC;
    private readonly bool _useEnergyDriver;
    private readonly object _hidGate = new();
    private DateTime _lastRgbWriteUtc = DateTime.MinValue;

    public KeyboardZoneType ZoneType { get; }
    public bool IsSupported => ZoneType != KeyboardZoneType.Unsupported;

    public LenovoKeyboardLightOperations()
    {
        var rgbType = DetectRgbHidType();
        var lightingType = GetLightingType();
        _useEnergyDriver = lightingType is null && ProbeEnergyDriver();
        ZoneType = rgbType != KeyboardZoneType.Unsupported
            ? rgbType
            : lightingType == 1 || _useEnergyDriver
                ? KeyboardZoneType.WhiteBacklit
                : lightingType == 5
                    ? KeyboardZoneType.RgbLayoutUnknown
                : KeyboardZoneType.Unsupported;
    }

    public KeyboardLightLevel? GetCurrentLevel()
    {
        if (_useEnergyDriver)
        {
            var state = SendEnergyCommand(0x22);
            return state switch
            {
                0x1 => KeyboardLightLevel.Off,
                0x3 => KeyboardLightLevel.Low,
                0x5 => KeyboardLightLevel.High,
                _ => null
            };
        }

        var properties = Invoke("Get_Lighting_Current_Status",
            new Dictionary<string, object> { ["Lighting_ID"] = 0 });
        return (ReadInt32(properties, "Current_Brightness_Level") - 1) switch
        {
            0 => KeyboardLightLevel.Off,
            1 => KeyboardLightLevel.Low,
            2 => KeyboardLightLevel.High,
            _ => null
        };
    }

    public void SetLevel(KeyboardLightLevel level)
    {
        if (ZoneType != KeyboardZoneType.WhiteBacklit)
            throw new InvalidOperationException("Keyboard brightness control is unavailable for this detected keyboard.");

        var internalLevel = level switch
        {
            KeyboardLightLevel.Off => 1,
            KeyboardLightLevel.Low => 2,
            KeyboardLightLevel.High => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };

        if (_useEnergyDriver)
        {
            SendEnergyCommand(level switch
            {
                KeyboardLightLevel.Off => 0x00023u,
                KeyboardLightLevel.Low => 0x10023u,
                KeyboardLightLevel.High => 0x20023u,
                _ => throw new ArgumentOutOfRangeException(nameof(level))
            });
            return;
        }

        Invoke("Set_Lighting_Current_Status", new Dictionary<string, object>
        {
            ["Lighting_ID"] = 0,
            ["Current_State_Type"] = 0,
            ["Current_Brightness_Level"] = internalLevel
        });
    }

    public void SetRgbEffect(KeyboardRgbSettings settings)
        {
            settings.Validate();
            LogRgb($"request effect={settings.Effect} color=#{settings.Color.Red:X2}{settings.Color.Green:X2}{settings.Color.Blue:X2} speed={settings.Speed}");
            if (ZoneType != KeyboardZoneType.FourZoneRgb)
                throw new InvalidOperationException("RGB effects are unavailable for this keyboard.");

            // Lenovo Toolkit's legacy 4-zone controller uses a 33-byte
            // feature report beginning with the device protocol header CC 16.
            // The HID collection can expose a different C9xx PID from the
            // PnP keyboard identity, so match the verified ITE product family.
            lock (_hidGate)
            {
                var wait = TimeSpan.FromMilliseconds(100) - (DateTime.UtcNow - _lastRgbWriteUtc);
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                using var handle = OpenVerifiedRgbDevice();
                var packet = new byte[33];
                packet[0] = 0xCC;
                packet[1] = 0x16;
                packet[2] = settings.Effect switch
                {
                    KeyboardRgbEffect.Off => 0x00,
                    KeyboardRgbEffect.Static => 0x01,
                    KeyboardRgbEffect.Breathing => 0x03,
                    KeyboardRgbEffect.ColorCycle => 0x06,
                    KeyboardRgbEffect.Wave => 0x04,
                    _ => throw new ArgumentOutOfRangeException(nameof(settings))
                };
                packet[3] = settings.Effect is KeyboardRgbEffect.Breathing
                    or KeyboardRgbEffect.ColorCycle
                    or KeyboardRgbEffect.Wave
                    ? MapSpeed(settings.Speed)
                    : (byte)0;
                packet[4] = settings.Effect == KeyboardRgbEffect.Off
                    ? (byte)0
                    : settings.Brightness == KeyboardRgbBrightness.Low ? (byte)1 : (byte)2;
                for (var zone = 0; zone < 4; zone++)
                {
                    packet[5 + zone * 3] = settings.Color.Red;
                    packet[6 + zone * 3] = settings.Color.Green;
                    packet[7 + zone * 3] = settings.Color.Blue;
                }
                if (!HidD_SetFeature(handle, packet, packet.Length))
                {
                    LogRgb($"set-feature failed win32={Marshal.GetLastWin32Error()} packet={Convert.ToHexString(packet)}");
                    throw new InvalidOperationException(
                        $"ITE 8295 rejected the RGB request (Windows error {Marshal.GetLastWin32Error()}).");
                }
                LogRgb($"set-feature succeeded packet={Convert.ToHexString(packet)}");
                _lastRgbWriteUtc = DateTime.UtcNow;
            }
        }

    public KeyboardRgbSettings? GetCurrentRgbSettings()
    {
        if (ZoneType != KeyboardZoneType.FourZoneRgb)
            return null;

        lock (_hidGate)
        {
            using var handle = OpenVerifiedRgbDevice();
            var packet = new byte[33];
            if (!HidD_GetFeature(handle, packet, packet.Length))
            {
                LogRgb($"get-feature failed win32={Marshal.GetLastWin32Error()}");
                return null;
            }

            if (packet[0] != 0xCC || packet[1] != 0x16)
            {
                LogRgb($"get-feature ignored unexpected-header={Convert.ToHexString(packet)}");
                return null;
            }

            var effect = packet[2] switch
            {
                0x00 => KeyboardRgbEffect.Off,
                0x01 => KeyboardRgbEffect.Static,
                0x03 => KeyboardRgbEffect.Breathing,
                0x04 => KeyboardRgbEffect.Wave,
                0x06 => KeyboardRgbEffect.ColorCycle,
                _ => (KeyboardRgbEffect?)null
            };
            if (effect is null)
                return null;

            var speed = packet[3] switch
            {
                >= 1 and <= 4 => packet[3],
                _ => (byte)3
            };
            var settings = new KeyboardRgbSettings(
                effect.Value,
                new KeyboardRgbColor(packet[5], packet[6], packet[7]),
                speed,
                packet[4] == 1 ? KeyboardRgbBrightness.Low : KeyboardRgbBrightness.High);
            LogRgb($"get-feature succeeded effect={settings.Effect} color=#{settings.Color.Red:X2}{settings.Color.Green:X2}{settings.Color.Blue:X2} speed={settings.Speed}");
            return settings;
        }
    }

        private static SafeFileHandle OpenVerifiedRgbDevice()
        {
            HidD_GetHidGuid(out var hidGuid);
            var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
                throw new InvalidOperationException("The verified Lenovo RGB HID interface is unavailable.");
            var candidates = 0;
            var openFailures = 0;
            var attributeFailures = 0;
            var capsFailures = 0;
            var reportLengths = new HashSet<ushort>();
            try
            {
                for (uint index = 0; ; index++)
                {
                    var deviceInfo = new DeviceInfoData
                    {
                        Size = Marshal.SizeOf<DeviceInfoData>()
                    };
                    if (!SetupDiEnumDeviceInfo(set, index, ref deviceInfo))
                    {
                        if (Marshal.GetLastWin32Error() == 259)
                            break;
                        continue;
                    }

                    var id = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref id))
                    {
                        if (Marshal.GetLastWin32Error() == 259)
                            break;
                        continue;
                    }
                    SetupDiGetDeviceInterfaceDetail(set, ref id, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                    if (required == 0) continue;
                    var detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref id, detail, required, out _, IntPtr.Zero)) continue;
                        // The Unicode detail structure uses an 8-byte cbSize
                        // on x64, but the variable-length device path starts
                        // at byte offset 4. Using offset 8 drops the leading
                        // "\\", producing "?\\hid..." and ERROR_INVALID_NAME.
                        var path = Marshal.PtrToStringUni(detail + 4);
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        // Lenovo Toolkit opens the interface with HID data
                        // rights before querying attributes and preparsed
                        // data. This driver does not expose metadata through
                        // a zero-access handle.
                        var handle = CreateFile(path, 0x00000003, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (handle.IsInvalid)
                        {
                            openFailures++;
                            LogRgb($"open-failed index={index} win32={Marshal.GetLastWin32Error()} path={path}");
                            handle.Dispose();
                            continue;
                        }
                        var attrs = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
                        if (!HidD_GetAttributes(handle, ref attrs))
                        {
                            if (path.Contains("vid_048d&pid_c993", StringComparison.OrdinalIgnoreCase))
                                LogRgb($"c993-attributes-failed win32={Marshal.GetLastWin32Error()} path={path}");
                            attributeFailures++;
                            handle.Dispose();
                            continue;
                        }
                        var identityMatches = attrs.VendorId == 0x048D
                            && (attrs.ProductId & LenovoIteProductFamilyMask) == LenovoIteProductFamily;
                        var pathMatches = path.Contains("vid_048d&pid_c993", StringComparison.OrdinalIgnoreCase);
                        if (pathMatches)
                            LogRgb($"c993-interface pid=0x{attrs.ProductId:X4} vid=0x{attrs.VendorId:X4} path={path}");
                        if (!identityMatches && !pathMatches)
                        { attributeFailures++; handle.Dispose(); continue; }
                        if (!HidD_GetPreparsedData(handle, out var prep)) { capsFailures++; handle.Dispose(); continue; }
                        try
                        {
                            var capsStatus = HidP_GetCaps(prep, out var caps);
                            // HIDP_STATUS_SUCCESS is the NTSTATUS value
                            // 0x00110000, not zero.
                            if (capsStatus == 0x00110000)
                            {
                                    var usageMatches = caps.UsagePage == LegacyRgbUsagePage
                                        && caps.Usage == LegacyRgbUsage;
                                    candidates++;
                                    reportLengths.Add(caps.FeatureReportByteLength);
                                if (caps.FeatureReportByteLength == 33
                                    && (usageMatches || pathMatches || identityMatches))
                                {
                                    return handle;
                                }
                                else
                                    capsFailures++;
                            }
                            else
                            {
                                capsFailures++;
                                if (pathMatches)
                                    LogRgb($"c993-caps-failed status=0x{capsStatus:X8} structSize={Marshal.SizeOf<HidCaps>()} path={path}");
                            }
                        }
                        finally { HidD_FreePreparsedData(prep); }
                        handle.Dispose();
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            var lengths = reportLengths.Count == 0 ? "none" : string.Join(",", reportLengths.Order());
            LogRgb($"feature-report-not-found candidates={candidates} openFailures={openFailures} attributeRejects={attributeFailures} capsRejects={capsFailures} featureLengths={lengths}");
            throw new InvalidOperationException(
                $"The verified Lenovo LOQ RGB feature report was not found. HID candidates={candidates}, open failures={openFailures}, attribute rejects={attributeFailures}, caps rejects={capsFailures}, feature lengths={lengths}.");
        }

    private static void LogRgb(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "rgb-hid.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static byte MapSpeed(byte speed) => Math.Clamp(speed, (byte)1, (byte)4);

    private static KeyboardZoneType DetectZoneType()
    {
        var rgbType = DetectRgbHidType();
        var lightingType = GetLightingType();
        return rgbType != KeyboardZoneType.Unsupported
            ? rgbType
            : lightingType == 1 || ProbeEnergyDriver()
                ? KeyboardZoneType.WhiteBacklit
                : lightingType == 5
                    ? KeyboardZoneType.RgbLayoutUnknown
                : KeyboardZoneType.Unsupported;
    }

    private static bool ProbeEnergyDriver()
    {
        try
        {
            return EnergyDrvAccessGate.Execute(() =>
            {
                using var handle = CreateFile(EnergyDriverPath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                    return false;

                var input = 1u;
                return DeviceIoControl(handle, EnergyKeyboardIoctl, ref input, sizeof(uint),
                    out var output, sizeof(uint), out _, IntPtr.Zero)
                    && (output >> 1) == 0x2;
            });
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static uint SendEnergyCommand(uint command)
    {
        return EnergyDrvAccessGate.Execute(() =>
        {
            using var handle = CreateFile(EnergyDriverPath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new InvalidOperationException($"EnergyDrv could not be opened (Windows error {Marshal.GetLastWin32Error()}).");

            var input = command;
            if (!DeviceIoControl(handle, EnergyKeyboardIoctl, ref input, sizeof(uint),
                    out var output, sizeof(uint), out _, IntPtr.Zero))
                throw new InvalidOperationException($"EnergyDrv rejected the keyboard-light request (Windows error {Marshal.GetLastWin32Error()}).");
            return output;
        });
    }

    private static uint? GetLightingType()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, LightingDataQuery);
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    // Lenovo exposes the brightness-capable keyboard on multiple
                    // firmware revisions with different Lighting_Type values.
                    // The method contract and control interface are the verified
                    // signals; do not reject a supported device because of type 5.
                    var active = Convert.ToBoolean(row["Active"] ?? true);
                    var type = Convert.ToUInt32(row["Lighting_Type"] ?? 0);
                    if (active && type is 1 or 5)
                        return type;
                }
            }

            return null;
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static KeyboardZoneType DetectRgbHidType()
    {
        // SetupAPI/HID can hide vendor collections when Lenovo's filter driver
        // owns the interface. The exact PnP identity remains available and is
        // sufficient for capability classification without opening the device.
        var pnpType = DetectRgbPnpType();
        if (pnpType != KeyboardZoneType.Unsupported)
            return pnpType;

        HidD_GetHidGuid(out var hidGuid);
        var deviceSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (deviceSet == IntPtr.Zero || deviceSet == new IntPtr(-1))
            return KeyboardZoneType.Unsupported;

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new DeviceInterfaceData
                {
                    Size = Marshal.SizeOf<DeviceInterfaceData>()
                };
                if (!SetupDiEnumDeviceInterfaces(deviceSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    break;

                SetupDiGetDeviceInterfaceDetail(
                    deviceSet, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0)
                    continue;

                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(
                            deviceSet, ref interfaceData, detail, required, out _, IntPtr.Zero))
                        continue;

                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid)
                        continue;

                    var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes)
                        || attributes.VendorId != 0x048D
                        || (attributes.ProductId & LenovoIteProductFamilyMask) != LenovoIteProductFamily
                        || !HidD_GetPreparsedData(handle, out var preparsed))
                        continue;

                    try
                    {
                        if (HidP_GetCaps(preparsed, out var caps) != 0x00110000)
                            continue;

                        var reportType = caps.FeatureReportByteLength switch
                        {
                            // Lenovo/ITE report sizes are bytes, not report IDs:
                            // 0x21 (33) is 4-zone and 0x33 (51) is 24-zone.
                            0x21 => KeyboardZoneType.FourZoneRgb,
                            0x33 => KeyboardZoneType.TwentyFourZoneRgb,
                            _ => KeyboardZoneType.RgbLayoutUnknown
                        };
                        return reportType == KeyboardZoneType.RgbLayoutUnknown
                            && attributes.ProductId == LenovoLoq2024ProductId
                            ? KeyboardZoneType.FourZoneRgb
                            : reportType;
                    }
                    finally
                    {
                        HidD_FreePreparsedData(preparsed);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }

        return KeyboardZoneType.Unsupported;
    }

    private static KeyboardZoneType DetectRgbPnpType()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "ROOT\\CIMV2",
                "SELECT PNPDeviceID FROM Win32_PnPEntity " +
                "WHERE PNPDeviceID LIKE '%VID_048D&PID_C993%'");
            using var rows = searcher.Get();
            return rows.Cast<ManagementObject>().Any()
                ? KeyboardZoneType.FourZoneRgb
                : KeyboardZoneType.Unsupported;
        }
        catch (ManagementException) { return KeyboardZoneType.Unsupported; }
        catch (UnauthorizedAccessException) { return KeyboardZoneType.Unsupported; }
        catch (InvalidOperationException) { return KeyboardZoneType.Unsupported; }
    }

    private static IReadOnlyDictionary<string, object?> Invoke(
        string methodName, IReadOnlyDictionary<string, object> values)
    {
        using var searcher = new ManagementObjectSearcher(Scope, LightingMethodQuery);
        using var objects = searcher.Get();
        using var managementObject = objects.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException("Lenovo lighting provider is unavailable.");
        using var parameters = managementObject.GetMethodParameters(methodName);
        foreach (var value in values)
            parameters[value.Key] = value.Value;

        using var response = managementObject.InvokeMethod(
            methodName, parameters, new InvokeMethodOptions());
        var returnValue = response?["ReturnValue"];
        if (returnValue is not null && Convert.ToUInt32(returnValue) != 0)
            throw new InvalidOperationException(
                $"Lenovo lighting provider rejected the request (WMI code {returnValue}).");

        return response?.Properties.Cast<PropertyData>()
            .ToDictionary(property => property.Name, property => Unwrap(property.Value),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, object?>();
    }

    private static int ReadInt32(IReadOnlyDictionary<string, object?> properties, string name)
    {
        properties.TryGetValue(name, out var value);
        return value is null ? 0 : Convert.ToInt32(value);
    }

    private static object? Unwrap(object? value)
    {
        while (value is PropertyData property)
            value = property.Value;
        return value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int reportLength);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] report, int reportLength);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, uint index, ref DeviceInterfaceData detail);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfoData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData detail, IntPtr buffer, uint length, out uint required, IntPtr data);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle, uint code, ref uint input, int inputSize,
        out uint output, int outputSize, out int returned, IntPtr overlapped);

    public void Dispose()
    {
    }
}
