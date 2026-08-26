using PowerDesk.Core.Models;

namespace PowerDesk.Tests.Core;

public sealed class WindowPlacementTests
{
    // A 1920x1080 primary plus a 1920x1080 secondary to the left → virtual screen starts at -1920.
    private const double ScreenLeft = -1920, ScreenTop = 0, ScreenWidth = 3840, ScreenHeight = 1080;
    private const double MinW = 1120, MinH = 680;

    private static bool Usable(double l, double t, double w, double h) =>
        new WindowPlacement { Left = l, Top = t, Width = w, Height = h }
            .IsUsable(ScreenLeft, ScreenTop, ScreenWidth, ScreenHeight, MinW, MinH);

    [Fact]
    public void FullyOnPrimaryMonitor_IsUsable() => Assert.True(Usable(100, 50, 1600, 900));

    [Fact]
    public void OnLeftMonitorWithNegativeCoordinates_IsUsable() => Assert.True(Usable(-1800, 40, 1500, 900));

    [Fact]
    public void EntirelyOffScreenRight_IsNotUsable() => Assert.False(Usable(4000, 0, 1600, 900));

    [Fact]
    public void EntirelyOffScreenAbove_IsNotUsable() => Assert.False(Usable(100, -2000, 1600, 900));

    [Fact]
    public void MostlyOffScreenButTitleBarVisible_IsUsable() => Assert.True(Usable(3840 - 1920 - 200, 990, 1600, 900));

    [Fact]
    public void OnlyTinySliverVisible_IsNotUsable() => Assert.False(Usable(1920 - 40, 0, 1600, 900));

    [Fact]
    public void SmallerThanMinimum_IsNotUsable()
    {
        Assert.False(Usable(0, 0, MinW - 1, 900));
        Assert.False(Usable(0, 0, 1600, MinH - 1));
    }

    [Fact]
    public void NaNValues_AreNotUsable()
    {
        Assert.False(Usable(double.NaN, 0, 1600, 900));
        Assert.False(Usable(0, 0, double.NaN, 900));
    }

    [Fact]
    public void MonitorUnplugged_PlacementOnMissingMonitorIsRejected()
    {
        // Same placement as the left-monitor test, but now only the primary monitor exists.
        var p = new WindowPlacement { Left = -1800, Top = 40, Width = 1500, Height = 900 };
        Assert.False(p.IsUsable(0, 0, 1920, 1080, MinW, MinH));
    }

    [Fact]
    public void AppSettings_WindowDefaultsToNull() => Assert.Null(new AppSettings().Window);
}
