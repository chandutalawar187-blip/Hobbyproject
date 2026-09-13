namespace LenovoLoqControl.Core;

public static class FanCurveValidator
{
    public static IReadOnlyList<string> Validate(FanCurve curve)
    {
        var errors = new List<string>();
        if (curve.Points.Count < 2) errors.Add("A curve requires at least two points.");
        if (curve.Points.Any(p => p.TemperatureCelsius is < 35 or > 105))
            errors.Add("Temperature points must be between 35°C and 105°C.");
        if (curve.Points.Any(p => p.FanPercent is < 0 or > 100))
            errors.Add("Fan output must be between 0% and 100%.");
        if (curve.Points.Zip(curve.Points.Skip(1)).Any(pair => pair.Second.FanPercent < pair.First.FanPercent))
            errors.Add("Fan output must never decrease as temperature rises.");
        var highTemperature = curve.Points.Where(p => p.TemperatureCelsius >= 85).ToArray();
        if (highTemperature.Length > 0 && highTemperature.Min(p => p.FanPercent) < 60)
            errors.Add("At least 60% cooling is required at or above 85°C.");
        if (curve.Points.Any(p => p.TemperatureCelsius >= 95 && p.FanPercent < 100))
            errors.Add("100% cooling is required at or above 95°C.");
        return errors;
    }
}
