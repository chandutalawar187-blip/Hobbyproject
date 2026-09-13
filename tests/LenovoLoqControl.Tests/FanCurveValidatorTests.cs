using LenovoLoqControl.Core;
using Xunit;

namespace LenovoLoqControl.Tests;

public class FanCurveValidatorTests
{
    [Fact]
    public void AcceptsSafeMonotonicCurve()
    {
        var curve = new FanCurve(new[]
        {
            new FanCurvePoint(45, 20), new FanCurvePoint(65, 45),
            new FanCurvePoint(85, 70), new FanCurvePoint(95, 100)
        });

        Assert.Empty(FanCurveValidator.Validate(curve));
    }

    [Fact]
    public void RejectsCoolingDropAndUnsafeHighTemperature()
    {
        var curve = new FanCurve(new[] { new FanCurvePoint(45, 50), new FanCurvePoint(85, 40) });

        var errors = FanCurveValidator.Validate(curve);

        Assert.Contains(errors, error => error.Contains("never decrease"));
        Assert.Contains(errors, error => error.Contains("85"));
    }

    [Fact]
    public void ClassifiesCriticalTemperature()
    {
        Assert.Equal(ThermalState.Critical, ThermalSafety.Classify(96));
        Assert.Equal(ThermalState.Unknown, ThermalSafety.Classify(null));
    }
}
