using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

public sealed class UnsupportedFanController : IFanController
{
    public bool IsSupported => false;
    public string AvailabilityMessage => "Manual fan control is not exposed by a verified, supported Windows interface on this system.";
    public Task<FirmwareFanTable?> ReadCustomFanTableAsync(CancellationToken cancellationToken) =>
        Task.FromResult<FirmwareFanTable?>(null);
    public Task<FanMode?> GetCurrentModeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<FanMode?>(null);
    public Task<FanControlResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken) =>
        Task.FromResult(FanControlResult.Unsupported(AvailabilityMessage));
    public Task<FanControlResult> SetFanCurveAsync(FanCurve curve, CancellationToken cancellationToken) =>
        Task.FromResult(FanControlResult.Unsupported(AvailabilityMessage));
    public Task RestoreSafeStateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() { }
}
