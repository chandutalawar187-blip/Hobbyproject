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

public interface IKeyboardLightController : IDisposable
{
    bool IsSupported { get; }
    KeyboardZoneType ZoneType { get; }
    string ZoneDescription { get; }
    string AvailabilityMessage { get; }
    Task<KeyboardLightLevel?> GetCurrentLevelAsync(CancellationToken cancellationToken);
    Task<KeyboardRgbSettings?> GetCurrentRgbSettingsAsync(CancellationToken cancellationToken);
    Task<FanControlResult> SetLevelAsync(KeyboardLightLevel level, CancellationToken cancellationToken);
    Task<FanControlResult> SetRgbEffectAsync(KeyboardRgbSettings settings, CancellationToken cancellationToken);
}
