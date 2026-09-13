namespace LenovoLoqControl.Core;

public interface ILenovoWmiOperations : IDisposable
{
    bool IsSmartFanSupported { get; }
    FirmwareFanTable? ReadCustomFanTable();
    uint? GetSmartFanMode();
    void SetSmartFanMode(uint mode);
    void SetFullSpeed(bool enabled);
    void SetFanTable(IReadOnlyList<int> steps);
}

public interface IGpuOverclockController : IDisposable
{
    bool IsSupported { get; }
    string AvailabilityMessage { get; }
    Task<FanControlResult> ApplyAsync(int coreOffsetMhz, int memoryOffsetMhz, CancellationToken cancellationToken);
    Task<FanControlResult> ResetAsync(CancellationToken cancellationToken);
}
