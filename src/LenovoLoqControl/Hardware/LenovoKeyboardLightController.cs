using System.IO;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

public sealed class LenovoKeyboardLightController : IKeyboardLightController
{
    private readonly LenovoKeyboardLightOperations _operations;
    public LenovoKeyboardLightController()
    {
        _operations = new LenovoKeyboardLightOperations();
        IsSupported = _operations.IsSupported;
        ZoneType = _operations.ZoneType;
        ZoneDescription = ZoneType switch
        {
            KeyboardZoneType.WhiteBacklit => "White backlit keyboard detected.",
            KeyboardZoneType.FourZoneRgb => "4-zone RGB keyboard detected.",
            KeyboardZoneType.TwentyFourZoneRgb => "24-zone RGB keyboard detected.",
            KeyboardZoneType.RgbLayoutUnknown => "RGB keyboard detected; zone layout is not exposed by this firmware.",
            _ => "No supported keyboard lighting detected."
        };
        AvailabilityMessage = IsSupported ? $"Verified Lenovo keyboard lighting control is available. {ZoneDescription}" : ZoneDescription;
    }
    public bool IsSupported { get; }
    public KeyboardZoneType ZoneType { get; }
    public string ZoneDescription { get; }
    public string AvailabilityMessage { get; }
    public Task<KeyboardLightLevel?> GetCurrentLevelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported) return Task.FromResult<KeyboardLightLevel?>(null);
        return Task.Run(() => _operations.GetCurrentLevel(), cancellationToken);
    }
    public Task<FanControlResult> SetLevelAsync(KeyboardLightLevel level, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported) return Task.FromResult(FanControlResult.Unsupported(AvailabilityMessage));
        return Task.Run(() =>
        {
            try { _operations.SetLevel(level); return new FanControlResult(true, $"Keyboard lighting set to {level}."); }
            catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidOperationException or ArgumentOutOfRangeException)
            { return new FanControlResult(false, $"Keyboard lighting was not changed: {ex.Message}"); }
        }, cancellationToken);
    }
    public Task<FanControlResult> SetRgbEffectAsync(KeyboardRgbSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settings.Validate();
        if (ZoneType != KeyboardZoneType.FourZoneRgb)
            return Task.FromResult(FanControlResult.Unsupported("RGB effects require a verified 4-zone Lenovo/ITE keyboard (VID 048D, C9xx HID product family)."));
        return Task.Run(() =>
        {
            try { _operations.SetRgbEffect(settings); return new FanControlResult(true, $"RGB effect set to {settings.Effect}."); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
            { return new FanControlResult(false, $"RGB lighting was not changed: {ex.Message}"); }
        }, cancellationToken);
    }
    public void Dispose() => _operations.Dispose();
}
