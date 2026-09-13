namespace LenovoLoqControl.Core;

public enum FanMode { Auto, Quiet, Balanced, Performance, MaxCooling, Custom }
public enum KeyboardLightLevel { Off, Low, High }
public enum KeyboardRgbEffect { Off, Static, Breathing, ColorCycle, Wave }
public readonly record struct KeyboardRgbColor(byte Red, byte Green, byte Blue)
{
    public static KeyboardRgbColor White => new(255, 255, 255);
}
public sealed record KeyboardRgbSettings(KeyboardRgbEffect Effect, KeyboardRgbColor Color, byte Speed)
{
    public static KeyboardRgbSettings Default => new(KeyboardRgbEffect.Static, KeyboardRgbColor.White, 3);
    public void Validate()
    {
        if (!Enum.IsDefined(Effect)) throw new ArgumentOutOfRangeException(nameof(Effect));
        if (Speed > 10) throw new ArgumentOutOfRangeException(nameof(Speed), "Speed must be between 0 and 10.");
    }
}
public enum KeyboardZoneType
{
    Unsupported,
    WhiteBacklit,
    FourZoneRgb,
    TwentyFourZoneRgb,
    RgbLayoutUnknown
}
public enum PowerLightColor { Blue, White, Red, Purple, Unknown }
public enum ThermalState { Cool, Normal, Warm, Hot, Critical, Unknown }

public sealed record SensorReading(double? CpuTemperature, double? GpuTemperature, double? CpuUsage, double? GpuUsage,
    double? CpuClock, double? GpuClock, double? CpuFanRpm, double? GpuFanRpm, double? MemoryUsage,
    double? BatteryPercent, bool? IsCharging, DateTimeOffset Timestamp);

public sealed record HardwareIdentity(string Manufacturer, string Model, string Processor, bool IsLenovoLoq);
public sealed record FanControlResult(bool Accepted, string Message)
{
    public static FanControlResult Unsupported(string reason) => new(false, reason);
}

public static class FanModeDetails
{
    public static PowerLightColor ExpectedLight(FanMode mode) => mode switch
    {
        FanMode.Quiet => PowerLightColor.Blue,
        FanMode.Auto or FanMode.Balanced => PowerLightColor.White,
        FanMode.Performance => PowerLightColor.Red,
        FanMode.Custom => PowerLightColor.Purple,
        _ => PowerLightColor.Unknown
    };

    public static string LightLabel(PowerLightColor color) => color switch
    {
        PowerLightColor.Blue => "blue",
        PowerLightColor.White => "white",
        PowerLightColor.Red => "red",
        PowerLightColor.Purple => "purple",
        _ => "the firmware-defined color"
    };
}

public sealed record FanCurvePoint(double TemperatureCelsius, double FanPercent);
public sealed class FanCurve
{
    public IReadOnlyList<FanCurvePoint> Points { get; }
    public FanCurve(IEnumerable<FanCurvePoint> points) => Points = points.OrderBy(p => p.TemperatureCelsius).ToArray();
}

public sealed record FirmwareFanTable(
    IReadOnlyList<int> TemperaturesCelsius,
    IReadOnlyList<int> FanSpeedsRpm,
    IReadOnlyList<int> MinimumSteps);
