using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Binding = System.Windows.Data.Binding;
using Application = System.Windows.Application;
using PowerDesk.Modules.StartupPilot.Models;

namespace PowerDesk.Shared.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public bool Collapse { get; set; } = true;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool bb && bb;
        if (Invert) b = !b;
        if (parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : (Collapse ? Visibility.Collapsed : Visibility.Hidden);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        // "has" is true when the bound value is meaningful: a non-null object, or a non-empty string.
        bool has = value is string s ? !string.IsNullOrEmpty(s) : value is not null;
        bool invert = parameter is string p && string.Equals(p, "invert", StringComparison.OrdinalIgnoreCase);
        if (invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StringEqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString() ?? string.Empty, parameter?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? parameter?.ToString() ?? string.Empty : Binding.DoNothing;
}

public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString() ?? string.Empty, parameter?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.Green;
    public Brush FalseBrush { get; set; } = Brushes.Gray;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? TrueBrush : FalseBrush;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);
}

/// <summary>
/// Maps a <see cref="StartupImpact"/> to a themed brush so the impact pill in StartupPilot's grid
/// is scannable at a glance. Falls back to <c>SurfaceActiveBrush</c> when the theme lookup fails.
/// </summary>
public sealed class ImpactToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StartupImpact.High   => "DangerBrush",
            StartupImpact.Medium => "WarningBrush",
            StartupImpact.Low    => "SuccessBrush",
            _                    => "SurfaceActiveBrush",
        };
        try
        {
            var app = Application.Current;
            if (app?.TryFindResource(key) is Brush b) return b;
        }
        catch { }
        return Brushes.Gray;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Returns a soft "tinted" background derived from the same impact axis. Used as the pill's
/// fill so the colored text reads well against the chip.
/// </summary>
public sealed class ImpactToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Soft variants exist on the accent only; for impact we synthesize a low-alpha
        // tint by darkening the surface — but to keep WPF themability we just return
        // SurfaceActiveBrush for Unknown and a translucent fallback otherwise.
        var key = value switch
        {
            StartupImpact.High   => "DangerBrush",
            StartupImpact.Medium => "WarningBrush",
            StartupImpact.Low    => "SuccessBrush",
            _                    => "SurfaceActiveBrush",
        };
        try
        {
            var app = Application.Current;
            if (app?.TryFindResource(key) is System.Windows.Media.SolidColorBrush scb)
            {
                var c = scb.Color;
                var soft = System.Windows.Media.Color.FromArgb(48, c.R, c.G, c.B);
                var b = new System.Windows.Media.SolidColorBrush(soft);
                b.Freeze();
                return b;
            }
        }
        catch { }
        return Brushes.Transparent;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
