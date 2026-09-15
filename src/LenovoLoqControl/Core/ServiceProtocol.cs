namespace LenovoLoqControl.Core;

public static class ServiceProtocol
{
    public const string PipeName = "LoqControlHardware";
    public const int ProtocolVersion = 1;
}

public sealed record ServiceRequest(
    int Version,
    string Command,
    FanMode? Mode = null,
    IReadOnlyList<FanCurvePoint>? Curve = null);

public sealed record ServiceResponse(
    bool Success,
    string Message,
    bool Supported = false,
    FanMode? Mode = null,
    FirmwareFanTable? Table = null,
    SensorReading? Reading = null);
