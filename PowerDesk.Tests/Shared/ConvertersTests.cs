using System.Globalization;
using System.Windows;
using PowerDesk.Shared.Converters;

namespace PowerDesk.Tests.Shared;

public sealed class ConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(true, false, Visibility.Visible)]
    [InlineData(false, false, Visibility.Collapsed)]
    [InlineData(true, true, Visibility.Collapsed)]
    [InlineData(false, true, Visibility.Visible)]
    public void BoolToVisibility_RespectsInvertFlag(bool value, bool invert, Visibility expected)
    {
        var conv = new BoolToVisibilityConverter { Invert = invert };
        Assert.Equal(expected, conv.Convert(value, typeof(Visibility), null!, Culture));
    }

    [Fact]
    public void BoolToVisibility_ParameterInvertAndHiddenMode()
    {
        var conv = new BoolToVisibilityConverter { Collapse = false };
        Assert.Equal(Visibility.Hidden, conv.Convert(false, typeof(Visibility), null!, Culture));
        Assert.Equal(Visibility.Hidden, conv.Convert(true, typeof(Visibility), "invert", Culture));
        Assert.Equal(Visibility.Visible, conv.Convert(false, typeof(Visibility), "INVERT", Culture));
    }

    [Fact]
    public void BoolToVisibility_NonBoolIsFalse()
    {
        var conv = new BoolToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, conv.Convert("yes", typeof(Visibility), null!, Culture));
        Assert.Equal(Visibility.Collapsed, conv.Convert(null!, typeof(Visibility), null!, Culture));
    }

    [Fact]
    public void BoolToVisibility_ConvertBack()
    {
        var conv = new BoolToVisibilityConverter();
        Assert.Equal(true, conv.ConvertBack(Visibility.Visible, typeof(bool), null!, Culture));
        Assert.Equal(false, conv.ConvertBack(Visibility.Collapsed, typeof(bool), null!, Culture));
    }

    [Theory]
    [InlineData(null, Visibility.Collapsed)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData("x", Visibility.Visible)]
    [InlineData(42, Visibility.Visible)]
    public void NullToVisibility_TreatsEmptyStringAsNull(object? value, Visibility expected)
    {
        var conv = new NullToVisibilityConverter();
        Assert.Equal(expected, conv.Convert(value, typeof(Visibility), null!, Culture));
    }

    [Fact]
    public void NullToVisibility_Invert()
    {
        var conv = new NullToVisibilityConverter();
        Assert.Equal(Visibility.Visible, conv.Convert(null, typeof(Visibility), "invert", Culture));
        Assert.Equal(Visibility.Collapsed, conv.Convert("x", typeof(Visibility), "invert", Culture));
    }

    [Fact]
    public void StringEqualsToBool_IsCaseInsensitiveAndRoundTrips()
    {
        var conv = new StringEqualsToBoolConverter();
        Assert.Equal(true, conv.Convert("Dark", typeof(bool), "dark", Culture));
        Assert.Equal(false, conv.Convert("Dark", typeof(bool), "light", Culture));
        Assert.Equal(true, conv.Convert(null!, typeof(bool), "", Culture));
        Assert.Equal("Light", conv.ConvertBack(true, typeof(string), "Light", Culture));
        Assert.Equal(System.Windows.Data.Binding.DoNothing, conv.ConvertBack(false, typeof(string), "Light", Culture));
    }

    [Fact]
    public void StringEquals_HandlesEnumsViaToString()
    {
        var conv = new StringEqualsConverter();
        Assert.Equal(true, conv.Convert(Visibility.Visible, typeof(bool), "Visible", Culture));
        Assert.Equal(false, conv.Convert(Visibility.Hidden, typeof(bool), "Visible", Culture));
    }

    [Fact]
    public void InverseBool_FlipsAndTreatsNonBoolAsFalse()
    {
        var conv = new InverseBoolConverter();
        Assert.Equal(false, conv.Convert(true, typeof(bool), null!, Culture));
        Assert.Equal(true, conv.Convert(false, typeof(bool), null!, Culture));
        Assert.Equal(true, conv.Convert(null!, typeof(bool), null!, Culture));
        Assert.Equal(true, conv.ConvertBack(false, typeof(bool), null!, Culture));
    }
}
