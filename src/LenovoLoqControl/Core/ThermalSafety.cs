namespace LenovoLoqControl.Core;

public static class ThermalSafety
{
    public static ThermalState Classify(double? temperature) => temperature switch
    {
        null => ThermalState.Unknown,
        < 60 => ThermalState.Cool,
        < 75 => ThermalState.Normal,
        < 85 => ThermalState.Warm,
        < 95 => ThermalState.Hot,
        _ => ThermalState.Critical
    };
}
