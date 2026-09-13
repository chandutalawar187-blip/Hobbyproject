namespace LenovoLoqControl.Core;

public interface IHardwareMonitor : IDisposable
{
    HardwareIdentity Identity { get; }
    Task<SensorReading> ReadAsync(CancellationToken cancellationToken);
}

public interface IFanController : IDisposable
{
    bool IsSupported { get; }
    string AvailabilityMessage { get; }
    Task<FirmwareFanTable?> ReadCustomFanTableAsync(CancellationToken cancellationToken);
    Task<FanMode?> GetCurrentModeAsync(CancellationToken cancellationToken);
    Task<FanControlResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken);
    Task<FanControlResult> SetFanCurveAsync(FanCurve curve, CancellationToken cancellationToken);
    Task RestoreSafeStateAsync(CancellationToken cancellationToken);
}

public interface IHardwareBackend : IDisposable
{
    IHardwareMonitor Monitor { get; }
    IFanController FanController { get; }
    IGpuOverclockController GpuOverclock { get; }
}
