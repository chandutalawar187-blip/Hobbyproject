using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

public sealed class FallbackFanController(IFanController primary, IFanController fallback) : IFanController
{
    public bool IsSupported => primary.IsSupported || fallback.IsSupported;

    public string AvailabilityMessage =>
        primary.IsSupported
            ? primary.AvailabilityMessage
            : fallback.AvailabilityMessage;

    public Task<FirmwareFanTable?> ReadCustomFanTableAsync(CancellationToken cancellationToken) =>
        primary.IsSupported
            ? primary.ReadCustomFanTableAsync(cancellationToken)
            : fallback.ReadCustomFanTableAsync(cancellationToken);

    public Task<FanMode?> GetCurrentModeAsync(CancellationToken cancellationToken) =>
        primary.IsSupported
            ? primary.GetCurrentModeAsync(cancellationToken)
            : fallback.GetCurrentModeAsync(cancellationToken);

    public async Task<FanControlResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken)
    {
        if (primary.IsSupported)
        {
            var result = await primary.SetFanModeAsync(mode, cancellationToken);
            if (result.Accepted || !fallback.IsSupported)
                return result;
            if ((mode is FanMode.Performance or FanMode.MaxCooling) &&
                result.Message.Contains("AC power", StringComparison.OrdinalIgnoreCase))
                return new FanControlResult(false, "Performance and custom modes require AC power.");

            var fallbackResult = await fallback.SetFanModeAsync(mode, cancellationToken);
            if (fallbackResult.Accepted)
                return new FanControlResult(true, $"{mode} firmware mode applied through EnergyDrv fallback.");

            return result;
        }

        return await fallback.SetFanModeAsync(mode, cancellationToken);
    }

    public Task<FanControlResult> SetFanCurveAsync(FanCurve curve, CancellationToken cancellationToken) =>
        primary.IsSupported
            ? primary.SetFanCurveAsync(curve, cancellationToken)
            : fallback.SetFanCurveAsync(curve, cancellationToken);

    public async Task RestoreSafeStateAsync(CancellationToken cancellationToken)
    {
        if (primary.IsSupported)
            await primary.RestoreSafeStateAsync(cancellationToken);
        if (fallback.IsSupported)
            await fallback.RestoreSafeStateAsync(cancellationToken);
    }

    public void Dispose()
    {
        primary.Dispose();
        if (!ReferenceEquals(primary, fallback))
            fallback.Dispose();
    }
}
