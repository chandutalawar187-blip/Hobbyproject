using System.Windows.Media;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI;

/// <summary>
/// Fan Control page accent colors per firmware mode (UI only).
/// </summary>
public readonly record struct FanModeUiAccent(Color Primary, Color Bright, Color Deep, double GlowStable, double GlowPeak)
{
    public static FanModeUiAccent For(FanMode mode) => mode switch
    {
        FanMode.Quiet => new(
            Color.FromRgb(0x40, 0xA9, 0xFF),
            Color.FromRgb(0x62, 0xC4, 0xFF),
            Color.FromRgb(0x1E, 0x5A, 0x8A),
            0.24,
            0.36),
        FanMode.Auto or FanMode.Balanced => new(
            Color.FromRgb(0xF0, 0xF4, 0xFA),
            Color.FromRgb(0xFF, 0xFF, 0xFF),
            Color.FromRgb(0x9A, 0xA8, 0xBA),
            0.26,
            0.38),
        FanMode.Performance => new(
            Color.FromRgb(0xE2, 0x23, 0x1A),
            Color.FromRgb(0xFF, 0x45, 0x3A),
            Color.FromRgb(0x8F, 0x17, 0x13),
            0.32,
            0.48),
        FanMode.MaxCooling => new(
            Color.FromRgb(0x00, 0xE5, 0xFF),
            Color.FromRgb(0x66, 0xFF, 0xFF),
            Color.FromRgb(0x00, 0x6B, 0x7A),
            0.38,
            0.52),
        FanMode.Custom => new(
            Color.FromRgb(0xB2, 0x4B, 0xF3),
            Color.FromRgb(0xD8, 0x96, 0xFF),
            Color.FromRgb(0x5B, 0x21, 0xB6),
            0.34,
            0.48),
        _ => For(FanMode.Auto)
    };

    public static string SelectedButtonStyleKey(FanMode mode) => mode switch
    {
        FanMode.Quiet => "Fan.ModeButtonSelectedQuiet",
        FanMode.Auto or FanMode.Balanced => "Fan.ModeButtonSelectedBalance",
        FanMode.Performance => "Fan.ModeButtonSelectedPerformance",
        FanMode.MaxCooling => "Fan.ModeButtonSelectedMaxCooling",
        FanMode.Custom => "Fan.ModeButtonSelectedCustom",
        _ => "Fan.ModeButtonSelectedBalance"
    };

    public Color InnerStroke => Color.FromArgb(0x88, Primary.R, Primary.G, Primary.B);

    public Color CoreStroke => Color.FromArgb(0x66, Deep.R, Deep.G, Deep.B);

    public Color SelectedBackgroundTint => Color.FromArgb(0x28, Primary.R, Primary.G, Primary.B);

    public Color SelectedBorder => Color.FromArgb(0x66, Primary.R, Primary.G, Primary.B);
}
