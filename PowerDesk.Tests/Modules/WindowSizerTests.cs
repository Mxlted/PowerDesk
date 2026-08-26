using System.Text.Json;
using PowerDesk.Modules.WindowSizer.Models;
using PowerDesk.Modules.WindowSizer.Services;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;

namespace PowerDesk.Tests.Modules;

public sealed class WindowSizerTests
{
    private static RECT R(int l, int t, int r, int b) => new(l, t, r, b);

    // ---------- frame insets / geometry ----------

    [Fact]
    public void ComputeInsets_TypicalWin11Window_ReturnsInvisibleBorders()
    {
        // GetWindowRect includes 7px invisible resize borders left/right/bottom; DWM frame is the visible part.
        var window = R(93, 100, 1007, 707);
        var frame  = R(100, 100, 1000, 700);
        var insets = WindowSizerLogic.ComputeInsets(window, frame);
        Assert.Equal(new WindowSizerLogic.FrameInsets(7, 0, 7, 7), insets);
    }

    [Fact]
    public void ComputeInsets_ImplausibleDelta_FallsBackToZero()
    {
        // System-DPI-virtualised window rect vs physical DWM rect on a 200% monitor: not the same space.
        var window = R(0, 0, 800, 600);
        var frame  = R(0, 0, 1600, 1200);
        Assert.Equal(WindowSizerLogic.FrameInsets.Zero, WindowSizerLogic.ComputeInsets(window, frame));
    }

    [Fact]
    public void ComputeInsets_EmptyFrame_ReturnsZero()
    {
        Assert.Equal(WindowSizerLogic.FrameInsets.Zero, WindowSizerLogic.ComputeInsets(R(0, 0, 100, 100), default));
    }

    [Fact]
    public void ToWindowRect_ExpandsVisibleBoundsByInsets()
    {
        var insets = new WindowSizerLogic.FrameInsets(7, 0, 7, 7);
        var rect = WindowSizerLogic.ToWindowRect(100, 100, 900, 600, insets);
        Assert.Equal(R(93, 100, 1007, 707), rect);
        Assert.Equal(914, rect.Width);
        Assert.Equal(607, rect.Height);
    }

    [Fact]
    public void ToWindowRect_EnforcesMinimumVisibleSize()
    {
        var rect = WindowSizerLogic.ToWindowRect(0, 0, 1, -40, WindowSizerLogic.FrameInsets.Zero);
        Assert.Equal(WindowSizerLogic.MinWindowSize, rect.Width);
        Assert.Equal(WindowSizerLogic.MinWindowSize, rect.Height);
    }

    [Theory]
    [InlineData(WindowService.SnapEdge.Left,   0,   0, 960,  1040)]
    [InlineData(WindowService.SnapEdge.Right,  960, 0, 1920, 1040)]
    [InlineData(WindowService.SnapEdge.Top,    0,   0, 1920, 520)]
    [InlineData(WindowService.SnapEdge.Bottom, 0, 520, 1920, 1040)]
    public void SnapRect_SplitsWorkAreaInHalf(WindowService.SnapEdge edge, int l, int t, int r, int b)
    {
        var work = R(0, 0, 1920, 1040); // 1080p minus a 40px taskbar
        Assert.Equal(R(l, t, r, b), WindowSizerLogic.SnapRect(work, edge));
    }

    [Fact]
    public void SnapRect_OddWidth_HalvesTileWithoutGapOrOverlap()
    {
        var work = R(-1921, 0, 0, 1041); // secondary monitor left of primary, odd dimensions
        var left = WindowSizerLogic.SnapRect(work, WindowService.SnapEdge.Left);
        var right = WindowSizerLogic.SnapRect(work, WindowService.SnapEdge.Right);
        Assert.Equal(left.Right, right.Left);
        Assert.Equal(work.Width, left.Width + right.Width);
        var top = WindowSizerLogic.SnapRect(work, WindowService.SnapEdge.Top);
        var bottom = WindowSizerLogic.SnapRect(work, WindowService.SnapEdge.Bottom);
        Assert.Equal(top.Bottom, bottom.Top);
        Assert.Equal(work.Height, top.Height + bottom.Height);
    }

    [Fact]
    public void CenterOrigin_CentersInsideWorkAreaOfSecondaryMonitor()
    {
        var work = R(1920, 0, 3840, 1040);
        var (x, y) = WindowSizerLogic.CenterOrigin(work, 800, 600);
        Assert.Equal(1920 + 560, x);
        Assert.Equal(220, y);
    }

    [Fact]
    public void CenterOrigin_WindowLargerThanWorkArea_PinsToTopLeft()
    {
        var work = R(0, 0, 1280, 720);
        var (x, y) = WindowSizerLogic.CenterOrigin(work, 1920, 1080);
        Assert.Equal((0, 0), (x, y));
    }

    [Fact]
    public void TranslateToWorkArea_KeepsRelativeOffsetAndSize()
    {
        var from = R(0, 0, 1920, 1040);
        var to   = R(1920, 0, 3840, 1040);
        var win  = R(100, 50, 900, 650);
        var moved = WindowSizerLogic.TranslateToWorkArea(win, from, to);
        Assert.Equal(R(2020, 50, 2820, 650), moved);
    }

    [Fact]
    public void TranslateToWorkArea_ClampsIntoSmallerMonitor()
    {
        var from = R(0, 0, 2560, 1400);
        var to   = R(2560, 0, 3840, 720); // 1280x720 monitor to the right
        var win  = R(1800, 900, 2500, 1350); // 700x450 near the bottom-right corner
        var moved = WindowSizerLogic.TranslateToWorkArea(win, from, to);
        Assert.Equal(700, moved.Width);
        Assert.Equal(450, moved.Height);
        Assert.Equal(to.Right, moved.Right);
        Assert.Equal(to.Bottom, moved.Bottom);

        var huge = R(0, 0, 3000, 2000);
        var shrunk = WindowSizerLogic.TranslateToWorkArea(huge, from, to);
        Assert.Equal(to, shrunk);
    }

    // ---------- filtering helpers ----------

    [Theory]
    [InlineData(@"\\.\DISPLAY1", "Display 1")]
    [InlineData(@"\\.\DISPLAY12", "Display 12")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData(@"\\.\OTHER", "OTHER")]
    public void FriendlyMonitorName_StripsDevicePrefix(string? device, string expected)
    {
        Assert.Equal(expected, WindowSizerLogic.FriendlyMonitorName(device));
    }

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("Windows.UI.Core.CoreWindow", true)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("Notepad", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsShellClass_RecognisesDesktopAndTaskbar(string? cls, bool expected)
    {
        Assert.Equal(expected, WindowSizerLogic.IsShellClass(cls));
    }

    [Fact]
    public void IsAltTabEligible_MatchesAltTabRules()
    {
        Assert.True(WindowSizerLogic.IsAltTabEligible(0, hasOwner: false));
        Assert.False(WindowSizerLogic.IsAltTabEligible(WS_EX_TOOLWINDOW, hasOwner: false));
        Assert.True(WindowSizerLogic.IsAltTabEligible(WS_EX_TOOLWINDOW | WS_EX_APPWINDOW, hasOwner: false));
        Assert.False(WindowSizerLogic.IsAltTabEligible(0, hasOwner: true));
        Assert.True(WindowSizerLogic.IsAltTabEligible(WS_EX_APPWINDOW, hasOwner: true));
        Assert.False(WindowSizerLogic.IsAltTabEligible(WS_EX_NOACTIVATE, hasOwner: false));
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\my.app.exe", "my.app")]
    [InlineData(@"C:\Windows\notepad.exe", "notepad")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ProcessNameFromPath_MirrorsProcessName(string? path, string expected)
    {
        Assert.Equal(expected, WindowSizerLogic.ProcessNameFromPath(path));
    }

    // ---------- hotkeys ----------

    [Fact]
    public void PlanRegistrations_SkipsDisabledIncompleteAndDuplicateChords()
    {
        var a = new HotkeyBinding { Action = HotkeyAction.SnapLeft,  Modifiers = MOD_CONTROL | MOD_ALT, VirtualKey = 0x25 };
        var disabled = new HotkeyBinding { Action = HotkeyAction.SnapRight, Modifiers = MOD_CONTROL | MOD_ALT, VirtualKey = 0x27, Enabled = false };
        var noKey = new HotkeyBinding { Action = HotkeyAction.Center, Modifiers = MOD_CONTROL, VirtualKey = 0 };
        var noMods = new HotkeyBinding { Action = HotkeyAction.Center, Modifiers = 0, VirtualKey = 0x43 };
        var dup = new HotkeyBinding { Action = HotkeyAction.Maximize, Modifiers = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VirtualKey = 0x25 };
        var b = new HotkeyBinding { Action = HotkeyAction.Maximize, Modifiers = MOD_CONTROL | MOD_ALT, VirtualKey = 0x4D };

        var plan = WindowSizerLogic.PlanRegistrations(new[] { a, disabled, noKey, noMods, dup, b });

        Assert.Equal(new[] { a, b }, plan.ToRegister);
        Assert.Equal(new[] { dup }, plan.Duplicates);
    }

    [Fact]
    public void SameChord_IgnoresNoRepeatFlagAndIdentity()
    {
        var a = new HotkeyBinding { Modifiers = MOD_CONTROL | MOD_ALT, VirtualKey = 0x25 };
        var b = new HotkeyBinding { Modifiers = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VirtualKey = 0x25 };
        var c = new HotkeyBinding { Modifiers = MOD_CONTROL, VirtualKey = 0x25 };
        Assert.True(WindowSizerLogic.SameChord(a, b));
        Assert.False(WindowSizerLogic.SameChord(a, c));
        Assert.NotEqual(a.Id, b.Id);
    }

    [Theory]
    [InlineData(ERROR_HOTKEY_ALREADY_REGISTERED, "already registered by another application")]
    [InlineData(ERROR_INVALID_PARAMETER, "invalid key combination")]
    [InlineData(5, "Win32 error 5")]
    public void DescribeRegisterFailure_ExplainsCommonCodes(int code, string expected)
    {
        Assert.Equal(expected, WindowSizerLogic.DescribeRegisterFailure(code));
    }

    [Fact]
    public void HotkeyBinding_DisplayText_OrdersModifiersAndNamesKeys()
    {
        var b = new HotkeyBinding { Modifiers = MOD_SHIFT | MOD_CONTROL | MOD_WIN | MOD_ALT, VirtualKey = 0x25 };
        Assert.Equal("Ctrl+Alt+Shift+Win+Left", b.DisplayText);
        Assert.Equal("Ctrl+F5", new HotkeyBinding { Modifiers = MOD_CONTROL, VirtualKey = 0x74 }.DisplayText);
        Assert.Equal("Alt+C", new HotkeyBinding { Modifiers = MOD_ALT, VirtualKey = 0x43 }.DisplayText);
        Assert.Equal("Alt+7", new HotkeyBinding { Modifiers = MOD_ALT, VirtualKey = 0x37 }.DisplayText);
        Assert.Equal("Win+VK_0xBA", new HotkeyBinding { Modifiers = MOD_WIN, VirtualKey = 0xBA }.DisplayText);
    }

    [Fact]
    public void HotkeyBinding_ActionLabel_IncludesLayoutName()
    {
        var b = new HotkeyBinding { Action = HotkeyAction.ApplyLayoutPreset, LayoutPresetName = "Editor" };
        Assert.Equal("Apply layout: Editor", b.ActionLabel);
        Assert.Equal("Snap left", new HotkeyBinding { Action = HotkeyAction.SnapLeft }.ActionLabel);
    }

    [Fact]
    public void DefaultHotkeys_HaveNoConflictingChords()
    {
        var defaults = WindowSizerSettings.DefaultHotkeys();
        var plan = WindowSizerLogic.PlanRegistrations(defaults);
        Assert.Equal(defaults.Count, plan.ToRegister.Count);
        Assert.Empty(plan.Duplicates);
    }

    // ---------- presets / layouts ----------

    [Fact]
    public void IsDuplicateSizePreset_MatchesOnDimensionsOnly()
    {
        var presets = new[] { new SizePreset { Name = "HD", Width = 1280, Height = 720 } };
        Assert.True(WindowSizerLogic.IsDuplicateSizePreset(presets, 1280, 720));
        Assert.False(WindowSizerLogic.IsDuplicateSizePreset(presets, 720, 1280));
    }

    [Theory]
    [InlineData(null, "1280×720")]
    [InlineData("   ", "1280×720")]
    [InlineData("  Wide ", "Wide")]
    public void ResolvePresetName_DefaultsToDimensions(string? requested, string expected)
    {
        Assert.Equal(expected, WindowSizerLogic.ResolvePresetName(requested, 1280, 720));
    }

    [Fact]
    public void SizePreset_ToString_FormatsWithOptionalName()
    {
        Assert.Equal("1080p (1920 × 1080)", new SizePreset { Name = "1080p", Width = 1920, Height = 1080 }.ToString());
        Assert.Equal("800 × 600", new SizePreset { Width = 800, Height = 600 }.ToString());
    }

    [Fact]
    public void LayoutPreset_Summary_IncludesProcessWhenPresent_AndIsNotSerialised()
    {
        var p = new LayoutPreset { Name = "L", X = 10, Y = 20, Width = 800, Height = 600, TargetProcessName = "notepad" };
        Assert.Equal("800 × 600 @ 10,20 • notepad", p.Summary);
        Assert.Equal("800 × 600 @ 10,20", new LayoutPreset { Width = 800, Height = 600, X = 10, Y = 20 }.Summary);
        Assert.DoesNotContain("summary", JsonSerializer.Serialize(p), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveLayoutTarget_PrefersSelectionThenProcessMatchCaseInsensitive()
    {
        var selected = new WindowInfo { Handle = new IntPtr(1), ProcessName = "code" };
        var np = new WindowInfo { Handle = new IntPtr(2), ProcessName = "Notepad" };
        var windows = new[] { np };
        var preset = new LayoutPreset { TargetProcessName = "notepad" };

        Assert.Same(selected, WindowSizerLogic.ResolveLayoutTarget(preset, selected, windows));
        Assert.Same(np, WindowSizerLogic.ResolveLayoutTarget(preset, null, windows));
        Assert.Null(WindowSizerLogic.ResolveLayoutTarget(new LayoutPreset(), null, windows));
        Assert.Null(WindowSizerLogic.ResolveLayoutTarget(new LayoutPreset { TargetProcessName = "chrome" }, null, windows));
    }

    [Fact]
    public void FindLayoutByName_IsCaseInsensitiveAndNullSafe()
    {
        var presets = new[] { new LayoutPreset { Name = "Editor" } };
        Assert.NotNull(WindowSizerLogic.FindLayoutByName(presets, "editor"));
        Assert.Null(WindowSizerLogic.FindLayoutByName(presets, "other"));
        Assert.Null(WindowSizerLogic.FindLayoutByName(presets, null));
    }

    // ---------- settings ----------

    [Fact]
    public void Sanitize_RepairsNullListsAndDropsImpossibleEntries()
    {
        var s = new WindowSizerSettings
        {
            SizePresets = null!,
            LayoutPresets = new List<LayoutPreset>
            {
                new() { Name = "ok", Width = 800, Height = 600 },
                new() { Name = "bad", Width = 0, Height = 600 },
            },
            Hotkeys = new List<HotkeyBinding>
            {
                new() { Id = "", Modifiers = MOD_ALT, VirtualKey = 0x41 },
                new() { Modifiers = MOD_ALT, VirtualKey = 0 },
            },
            AutoRefreshSeconds = -5,
        };

        var result = WindowSizerLogic.Sanitize(s);

        Assert.Equal(WindowSizerSettings.DefaultSizePresets().Count, result.SizePresets.Count);
        Assert.Single(result.LayoutPresets);
        Assert.Equal("ok", result.LayoutPresets[0].Name);
        Assert.Single(result.Hotkeys);
        Assert.False(string.IsNullOrWhiteSpace(result.Hotkeys[0].Id));
        Assert.Equal(0, result.AutoRefreshSeconds);
        Assert.Equal(60, WindowSizerLogic.Sanitize(new WindowSizerSettings { AutoRefreshSeconds = 999 }).AutoRefreshSeconds);
    }

    [Fact]
    public void Sanitize_NullSettings_YieldsDefaults()
    {
        var result = WindowSizerLogic.Sanitize(null);
        Assert.NotEmpty(result.SizePresets);
        Assert.NotEmpty(result.Hotkeys);
        Assert.Equal(2, result.AutoRefreshSeconds);
    }

    [Fact]
    public void Settings_RoundTripThroughJson_PreservesHotkeysAndPresets()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
        var original = new WindowSizerSettings();
        original.LayoutPresets.Add(new LayoutPreset { Name = "L", X = 1, Y = 2, Width = 300, Height = 400, TargetProcessName = "x" });
        original.Hotkeys[0].Enabled = false;

        var json = JsonSerializer.Serialize(original, opts);
        var back = WindowSizerLogic.Sanitize(JsonSerializer.Deserialize<WindowSizerSettings>(json, opts));

        Assert.Equal(original.SizePresets.Count, back.SizePresets.Count);
        Assert.Single(back.LayoutPresets);
        Assert.Equal("x", back.LayoutPresets[0].TargetProcessName);
        Assert.Equal(original.Hotkeys.Count, back.Hotkeys.Count);
        Assert.False(back.Hotkeys[0].Enabled);
        Assert.Equal(original.Hotkeys[0].Id, back.Hotkeys[0].Id);
    }

    // ---------- model notifications ----------

    [Fact]
    public void WindowInfo_GeometryAndStateLabel_NotifyWhenUnderlyingValuesChange()
    {
        var w = new WindowInfo { Width = 100, Height = 100 };
        var changed = new List<string>();
        w.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        w.Width = 640;
        w.IsMaximized = true;

        Assert.Contains(nameof(WindowInfo.Geometry), changed);
        Assert.Contains(nameof(WindowInfo.StateLabel), changed);
        Assert.Equal("640 × 100 @ 0,0", w.Geometry);
        Assert.Equal("Max", w.StateLabel);
        w.IsMinimized = true;
        Assert.Equal("Min", w.StateLabel);
    }
}
