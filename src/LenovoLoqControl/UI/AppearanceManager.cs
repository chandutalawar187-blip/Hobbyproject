using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace LenovoLoqControl.UI;

public enum AppearanceMode
{
    System,
    Dark,
    Light
}

public static class AppearanceManager
{
    public static AppearanceMode Mode { get; private set; } = AppearanceMode.System;

    public static bool IsDark =>
        Mode switch
        {
            AppearanceMode.Dark => true,
            AppearanceMode.Light => false,
            _ => ReadSystemDarkMode()
        };

    public static void Apply(AppearanceMode mode)
    {
        Mode = mode;
        var resources = Application.Current.Resources;
        var palette = IsDark ? DarkPalette : LightPalette;
        foreach (var pair in palette)
        {
            resources[pair.Key] = pair.Value;
            if (pair.Key.StartsWith("Color.", StringComparison.Ordinal)
                && resources[$"Brush.{pair.Key[6..]}"] is SolidColorBrush)
            {
                resources[$"Brush.{pair.Key[6..]}"] = new SolidColorBrush(pair.Value);
            }
        }

        resources["Brush.BackgroundGradient"] = CreateGradient(
            IsDark ? Color.FromRgb(10, 13, 18) : Color.FromRgb(247, 248, 250),
            IsDark ? Color.FromRgb(10, 16, 24) : Color.FromRgb(232, 235, 240));
        resources["Brush.NavigationGradient"] = CreateGradient(
            IsDark ? Color.FromRgb(18, 24, 33) : Color.FromRgb(255, 255, 255),
            IsDark ? Color.FromRgb(16, 21, 29) : Color.FromRgb(235, 238, 243));
    }

    private static bool ReadSystemDarkMode()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is not int lightTheme || lightTheme == 0;
        }
        catch
        {
            return true;
        }
    }

    private static LinearGradientBrush CreateGradient(Color start, Color end) =>
        new(start, end, 45);

    private static readonly IReadOnlyDictionary<string, Color> DarkPalette =
        new Dictionary<string, Color>
        {
            ["Color.Background"] = Color.FromRgb(10, 13, 18),
            ["Color.Navigation"] = Color.FromRgb(16, 21, 29),
            ["Color.Card"] = Color.FromRgb(21, 27, 36),
            ["Color.Elevated"] = Color.FromRgb(27, 34, 45),
            ["Color.TextPrimary"] = Color.FromRgb(244, 247, 251),
            ["Color.TextSecondary"] = Color.FromRgb(154, 168, 186),
            ["Color.TextTertiary"] = Color.FromRgb(107, 122, 141),
            ["Color.TextDisabled"] = Color.FromRgb(85, 98, 115),
            ["Color.Border"] = Color.FromRgb(42, 51, 64),
            ["Color.BorderSubtle"] = Color.FromRgb(30, 38, 48)
        };

    private static readonly IReadOnlyDictionary<string, Color> LightPalette =
        new Dictionary<string, Color>
        {
            ["Color.Background"] = Color.FromRgb(247, 248, 250),
            ["Color.Navigation"] = Color.FromRgb(255, 255, 255),
            ["Color.Card"] = Color.FromRgb(255, 255, 255),
            ["Color.Elevated"] = Color.FromRgb(238, 241, 245),
            ["Color.TextPrimary"] = Color.FromRgb(20, 24, 30),
            ["Color.TextSecondary"] = Color.FromRgb(75, 84, 96),
            ["Color.TextTertiary"] = Color.FromRgb(105, 115, 128),
            ["Color.TextDisabled"] = Color.FromRgb(145, 153, 163),
            ["Color.Border"] = Color.FromRgb(208, 214, 222),
            ["Color.BorderSubtle"] = Color.FromRgb(225, 229, 235)
        };
}
