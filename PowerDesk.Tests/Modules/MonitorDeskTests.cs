using PowerDesk.Modules.MonitorDesk.Models;
using PowerDesk.Modules.MonitorDesk.Services;

namespace PowerDesk.Tests.Modules;

public sealed class MonitorDeskTests
{
    private static MonitorInfo Monitor(string device, int x, int y, int w = 1920, int h = 1080, bool primary = false)
        => new()
        {
            DisplayNumber = MonitorLayoutLogic.DisplayNumberOrIndex(device, 0),
            DeviceName = device,
            IsPrimary = primary,
            X = x,
            Y = y,
            Width = w,
            Height = h,
        };

    private static MonitorLayoutDisplay Display(string device, int x, int y, int w = 1920, int h = 1080, bool primary = false, int number = 0)
        => new()
        {
            DisplayNumber = number > 0 ? number : MonitorLayoutLogic.ParseDisplayNumber(device),
            DeviceName = device,
            IsPrimary = primary,
            X = x,
            Y = y,
            Width = w,
            Height = h,
        };

    private static MonitorLayoutPreset Preset(params MonitorLayoutDisplay[] displays)
        => new() { Name = "test", Displays = displays.ToList() };

    // ---------- display numbering ----------

    [Theory]
    [InlineData(@"\\.\DISPLAY1", 1)]
    [InlineData(@"\\.\DISPLAY12", 12)]
    [InlineData(@"\\.\display3", 3)]
    [InlineData(@"\\.\DISPLAY", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData(@"\\.\DISPLAY0", 0)]
    public void ParseDisplayNumber_ReadsTrailingDigits(string? device, int expected)
        => Assert.Equal(expected, MonitorLayoutLogic.ParseDisplayNumber(device));

    [Fact]
    public void DisplayNumberOrIndex_FallsBackToOneBasedIndex()
    {
        Assert.Equal(3, MonitorLayoutLogic.DisplayNumberOrIndex("weird-name", 2));
        Assert.Equal(7, MonitorLayoutLogic.DisplayNumberOrIndex(@"\\.\DISPLAY7", 2));
    }

    [Fact]
    public void NormalizeDisplayNumbers_FillsLegacyPresetsFromDeviceName()
    {
        var preset = Preset(
            new MonitorLayoutDisplay { DeviceName = @"\\.\DISPLAY4" },
            new MonitorLayoutDisplay { DeviceName = "custom" },
            new MonitorLayoutDisplay { DeviceName = @"\\.\DISPLAY9", DisplayNumber = 2 });

        MonitorLayoutLogic.NormalizeDisplayNumbers(preset);

        Assert.Equal(4, preset.Displays[0].DisplayNumber);
        Assert.Equal(2, preset.Displays[1].DisplayNumber);
        Assert.Equal(2, preset.Displays[2].DisplayNumber); // explicit numbers are preserved
    }

    // ---------- preset matching ----------

    [Fact]
    public void MatchPreset_PrefersDeviceNameOverNumber()
    {
        // Saved with numbers that no longer line up with the device names: names must win.
        var preset = Preset(
            Display(@"\\.\DISPLAY1", 0, 0, primary: true, number: 2),
            Display(@"\\.\DISPLAY2", 1920, 0, number: 1));
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, primary: true),
            Monitor(@"\\.\DISPLAY2", -1920, 0),
        };

        var pairs = MonitorLayoutLogic.MatchPresetToMonitors(preset, monitors, requireAll: true);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.Equal(p.Saved.DeviceName, p.Monitor.DeviceName));
    }

    [Fact]
    public void MatchPreset_FallsBackToNumberOnlyForUnclaimedMonitors()
    {
        var preset = Preset(
            Display(@"\\.\DISPLAY1", 0, 0, primary: true),
            Display("OLD-DEVICE", 1920, 0, number: 2));
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, primary: true),
            Monitor(@"\\.\DISPLAY2", 1920, 0),
        };

        var pairs = MonitorLayoutLogic.MatchPresetToMonitors(preset, monitors, requireAll: true);

        Assert.Equal(2, pairs.Count);
        Assert.Equal(@"\\.\DISPLAY2", pairs[1].Monitor.DeviceName);
    }

    [Fact]
    public void MatchPreset_NumberFallbackNeverStealsAMonitorClaimedByName()
    {
        // Saved display "OLD" has number 1, but DISPLAY1 is already claimed by name.
        var preset = Preset(
            Display(@"\\.\DISPLAY1", 0, 0, primary: true),
            Display("OLD", 1920, 0, number: 1));
        var monitors = new[] { Monitor(@"\\.\DISPLAY1", 0, 0, primary: true) };

        Assert.Empty(MonitorLayoutLogic.MatchPresetToMonitors(preset, monitors, requireAll: true));
        var partial = MonitorLayoutLogic.MatchPresetToMonitors(preset, monitors, requireAll: false);
        Assert.Single(partial);
        Assert.Equal(@"\\.\DISPLAY1", partial[0].Monitor.DeviceName);
    }

    [Fact]
    public void MatchPreset_DifferentMonitorSet_IsNotMisapplied()
    {
        var preset = Preset(
            Display(@"\\.\DISPLAY5", 0, 0, primary: true),
            Display(@"\\.\DISPLAY6", 1920, 0));
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, primary: true),
            Monitor(@"\\.\DISPLAY2", 1920, 0),
        };

        Assert.Empty(MonitorLayoutLogic.MatchPresetToMonitors(preset, monitors, requireAll: false));
        Assert.False(MonitorLayoutLogic.LayoutMatches(preset, monitors));
    }

    [Fact]
    public void LayoutMatches_RequiresSamePositionSizeAndPrimary()
    {
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, primary: true),
            Monitor(@"\\.\DISPLAY2", 1920, 0),
        };

        var exact = Preset(Display(@"\\.\DISPLAY1", 0, 0, primary: true), Display(@"\\.\DISPLAY2", 1920, 0));
        var moved = Preset(Display(@"\\.\DISPLAY1", 0, 0, primary: true), Display(@"\\.\DISPLAY2", -1920, 0));
        var resized = Preset(Display(@"\\.\DISPLAY1", 0, 0, primary: true), Display(@"\\.\DISPLAY2", 1920, 0, w: 2560, h: 1440));
        var otherPrimary = Preset(Display(@"\\.\DISPLAY1", 0, 0), Display(@"\\.\DISPLAY2", 1920, 0, primary: true));
        var fewer = Preset(Display(@"\\.\DISPLAY1", 0, 0, primary: true));

        Assert.True(MonitorLayoutLogic.LayoutMatches(exact, monitors));
        Assert.False(MonitorLayoutLogic.LayoutMatches(moved, monitors));
        Assert.False(MonitorLayoutLogic.LayoutMatches(resized, monitors));
        Assert.False(MonitorLayoutLogic.LayoutMatches(otherPrimary, monitors));
        Assert.False(MonitorLayoutLogic.LayoutMatches(fewer, monitors));
    }

    // ---------- target layout construction ----------

    [Fact]
    public void BuildTargetLayout_OverridesPositionAndKeepsCurrentSize()
    {
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, primary: true),
            Monitor(@"\\.\DISPLAY2", 1920, 0, w: 2560, h: 1440),
        };

        var target = MonitorLayoutLogic.BuildTargetLayout(monitors,
            [Display(@"\\.\DISPLAY2", -2560, 0, w: 1, h: 1)]);

        Assert.Equal(2, target.Count);
        var moved = target.Single(t => t.DeviceName == @"\\.\DISPLAY2");
        Assert.Equal(-2560, moved.X);
        Assert.Equal(2560, moved.Width);
        Assert.Equal(1440, moved.Height);
        Assert.True(target.Single(t => t.DeviceName == @"\\.\DISPLAY1").IsPrimary);
    }

    [Fact]
    public void BuildTargetLayout_NewPrimaryClearsOldPrimary()
    {
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, primary: true),
            Monitor(@"\\.\DISPLAY2", 1920, 0),
        };

        var target = MonitorLayoutLogic.BuildTargetLayout(monitors,
            [Display(@"\\.\DISPLAY1", -1920, 0), Display(@"\\.\DISPLAY2", 0, 0, primary: true)]);

        Assert.Single(target, t => t.IsPrimary);
        Assert.True(target.Single(t => t.DeviceName == @"\\.\DISPLAY2").IsPrimary);
    }

    // ---------- validation ----------

    [Fact]
    public void Validate_AcceptsSideBySideAndStackedArrangements()
    {
        var sideBySide = new[]
        {
            Display(@"\\.\DISPLAY1", 0, 0, primary: true),
            Display(@"\\.\DISPLAY2", 1920, 0, w: 2560, h: 1440),
            Display(@"\\.\DISPLAY3", -1920, 200),
        };
        var stacked = new[]
        {
            Display(@"\\.\DISPLAY1", 0, 0, primary: true),
            Display(@"\\.\DISPLAY2", 400, -1080),
        };

        Assert.Empty(MonitorLayoutLogic.Validate(sideBySide));
        Assert.Empty(MonitorLayoutLogic.Validate(stacked));
    }

    [Fact]
    public void Validate_RejectsOverlap()
    {
        var layout = new[]
        {
            Display(@"\\.\DISPLAY1", 0, 0, primary: true),
            Display(@"\\.\DISPLAY2", 1000, 0),
        };

        var problems = MonitorLayoutLogic.Validate(layout);

        Assert.Contains(problems, p => p.Contains("overlaps", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsGapsAndCornerOnlyContact()
    {
        var gap = new[]
        {
            Display(@"\\.\DISPLAY1", 0, 0, primary: true),
            Display(@"\\.\DISPLAY2", 2000, 0),
        };
        var corner = new[]
        {
            Display(@"\\.\DISPLAY1", 0, 0, primary: true),
            Display(@"\\.\DISPLAY2", 1920, 1080),
        };

        Assert.Contains(MonitorLayoutLogic.Validate(gap), p => p.Contains("does not touch", StringComparison.Ordinal));
        Assert.Contains(MonitorLayoutLogic.Validate(corner), p => p.Contains("does not touch", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequiresExactlyOnePrimaryAtOrigin()
    {
        var none = new[] { Display(@"\\.\DISPLAY1", 0, 0), Display(@"\\.\DISPLAY2", 1920, 0) };
        var two = new[] { Display(@"\\.\DISPLAY1", 0, 0, primary: true), Display(@"\\.\DISPLAY2", 1920, 0, primary: true) };
        var offOrigin = new[] { Display(@"\\.\DISPLAY1", 100, 0, primary: true), Display(@"\\.\DISPLAY2", 2020, 0) };

        Assert.Contains(MonitorLayoutLogic.Validate(none), p => p.Contains("No display is marked as primary", StringComparison.Ordinal));
        Assert.Contains(MonitorLayoutLogic.Validate(two), p => p.Contains("More than one", StringComparison.Ordinal));
        Assert.Contains(MonitorLayoutLogic.Validate(offOrigin), p => p.Contains("must stay at 0,0", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsEmptyAndInvalidSizes()
    {
        Assert.Single(MonitorLayoutLogic.Validate(Array.Empty<MonitorLayoutDisplay>()));
        var bad = new[] { Display(@"\\.\DISPLAY1", 0, 0, w: 0, h: 1080, primary: true) };
        Assert.Contains(MonitorLayoutLogic.Validate(bad), p => p.Contains("invalid size", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SingleDisplayAtOriginIsValid()
        => Assert.Empty(MonitorLayoutLogic.Validate([Display(@"\\.\DISPLAY1", 0, 0, primary: true)]));

    // ---------- geometry helpers ----------

    [Fact]
    public void VirtualBounds_SpansNegativeCoordinates()
    {
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY1", 0, 0, primary: true),
            Monitor(@"\\.\DISPLAY2", -2560, -360, w: 2560, h: 1440),
        };

        var (x, y, w, h) = MonitorLayoutLogic.VirtualBounds(monitors);

        Assert.Equal(-2560, x);
        Assert.Equal(-360, y);
        Assert.Equal(4480, w);
        Assert.Equal(1440, h);
        Assert.Equal((0, 0, 0, 0), MonitorLayoutLogic.VirtualBounds(Array.Empty<MonitorInfo>()));
    }

    // ---------- naming ----------

    [Fact]
    public void UniqueLayoutName_TrimsAndDeduplicatesCaseInsensitively()
    {
        var existing = new[] { "Desk", "Desk (2)" };
        var now = new DateTime(2026, 8, 25, 14, 5, 0);

        Assert.Equal("desk (3)", MonitorLayoutLogic.UniqueLayoutName("  desk ", existing, now));
        Assert.Equal("Home", MonitorLayoutLogic.UniqueLayoutName("Home", existing, now));
        Assert.Equal("Layout 2026-08-25 14-05", MonitorLayoutLogic.UniqueLayoutName("   ", existing, now));
    }

    [Fact]
    public void UniqueLayoutName_CapsLength()
    {
        var name = MonitorLayoutLogic.UniqueLayoutName(new string('x', 200), Array.Empty<string>(), DateTime.Now);
        Assert.Equal(MonitorLayoutLogic.MaxLayoutNameLength, name.Length);
    }

    [Fact]
    public void DisplayChangeMessage_MapsWin32Codes()
    {
        Assert.Contains("restart", DisplayLayoutService.DisplayChangeMessage(1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("driver failed", DisplayLayoutService.DisplayChangeMessage(-1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not supported", DisplayLayoutService.DisplayChangeMessage(-2), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry", DisplayLayoutService.DisplayChangeMessage(-3), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad parameters", DisplayLayoutService.DisplayChangeMessage(-5), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error 42", DisplayLayoutService.DisplayChangeMessage(42), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresetSummary_UsesMonitorNumbersAndBounds()
    {
        var preset = Preset(Display(@"\\.\DISPLAY1", 0, 0, primary: true), Display(@"\\.\DISPLAY2", 1920, 0));
        Assert.Equal("Monitor 1 1920 x 1080 @ 0,0; Monitor 2 1920 x 1080 @ 1920,0", preset.Summary);
        Assert.Equal("2 displays", preset.DisplayCountLabel);
        Assert.Equal("No displays", new MonitorLayoutPreset().Summary);
    }
}
