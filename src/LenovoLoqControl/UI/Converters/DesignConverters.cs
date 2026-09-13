using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI.Converters;

/// <summary>Shows "Unavailable" (or ConverterParameter) when the bound value is null.</summary>
public sealed class NullToUnavailableConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return parameter as string ?? "Unavailable";

        return value switch
        {
            double d when targetType == typeof(string) || targetType == typeof(object)
                => FormatNumber(d, parameter as string),
            float f when targetType == typeof(string) || targetType == typeof(object)
                => FormatNumber(f, parameter as string),
            _ => value
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static string FormatNumber(double number, string? formatHint)
    {
        if (string.IsNullOrWhiteSpace(formatHint))
            return number.ToString("0", CultureInfo.CurrentCulture);

        // formatHint examples: "0°C", "0 RPM", "0.0 GHz", "0%"
        if (formatHint.Contains('{', StringComparison.Ordinal))
            return string.Format(CultureInfo.CurrentCulture, formatHint, number);

        var unit = formatHint.TrimStart('0', '#', '.', ',', ' ');
        var numericFormat = formatHint.StartsWith("0.0", StringComparison.Ordinal) ? "0.0" : "0";
        return string.IsNullOrEmpty(unit)
            ? number.ToString(numericFormat, CultureInfo.CurrentCulture)
            : $"{number.ToString(numericFormat, CultureInfo.CurrentCulture)} {unit}".Trim();
    }
}

/// <summary>Maps null / empty string to Collapsed; otherwise Visible.</summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        var visible = invert ? empty : !empty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Boolean to Visibility with optional Invert parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        var flag = value is true;
        if (invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Maps <see cref="ThermalState"/> to a themed status brush.</summary>
public sealed class ThermalStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ThermalState.Cool => "Brush.ThermalCool",
            ThermalState.Normal => "Brush.ThermalNormal",
            ThermalState.Warm => "Brush.ThermalWarm",
            ThermalState.Hot => "Brush.ThermalHot",
            ThermalState.Critical => "Brush.ThermalCritical",
            _ => "Brush.TextSecondary"
        };

        return Application.Current?.TryFindResource(key) as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps accepted/rejected command results to success or secondary text brushes.</summary>
public sealed class AcceptedToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "Brush.Success" : "Brush.TextSecondary";
        return Application.Current?.TryFindResource(key) as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>True → "Available" / False → "Unavailable" (or ConverterParameter pair "Yes|No").</summary>
public sealed class BoolToAvailabilityTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|');
        var yes = parts is { Length: >= 1 } ? parts[0] : "Available";
        var no = parts is { Length: >= 2 } ? parts[1] : "Unavailable";
        return value is true ? yes : no;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
