using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Binding = System.Windows.Data.Binding;

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
        bool invert = parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
        bool has = value is not null && value is not string str || (value is string ss && !string.IsNullOrEmpty(ss));
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
