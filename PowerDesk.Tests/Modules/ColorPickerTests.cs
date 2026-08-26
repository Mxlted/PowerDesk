using PowerDesk.Core.Logging;
using PowerDesk.Core.Services;
using PowerDesk.Modules.ColorPicker.Services;
using PowerDesk.Modules.ColorPicker.ViewModels;

namespace PowerDesk.Tests.Modules;

public sealed class ColorPickerTests
{
    private sealed class NullLogger : ILogger
    {
        public string LogFilePath => string.Empty;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    // ---------- hex parsing ----------

    [Theory]
    [InlineData("#3b82f6", 59, 130, 246)]
    [InlineData("3B82F6", 59, 130, 246)]
    [InlineData("  #3b82f6  ", 59, 130, 246)]
    [InlineData("#fff", 255, 255, 255)]
    [InlineData("abc", 170, 187, 204)]
    [InlineData("#abcd", 170, 187, 204)]          // #RGBA, alpha ignored
    [InlineData("#3b82f680", 59, 130, 246)]       // #RRGGBBAA, alpha ignored
    [InlineData("0x3b82f6", 59, 130, 246)]
    [InlineData("#000000", 0, 0, 0)]
    public void TryParseHex_AcceptsSupportedForms(string input, int r, int g, int b)
    {
        Assert.True(ColorLogic.TryParseHex(input, out var pr, out var pg, out var pb));
        Assert.Equal((r, g, b), (pr, pg, pb));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#")]
    [InlineData("#3b")]
    [InlineData("#3b82f")]
    [InlineData("#3b82f6a")]
    [InlineData("#gggggg")]
    [InlineData("#3b82f6aa0")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("# 3b82f6")]
    public void TryParseHex_RejectsPartialOrInvalidInput(string? input)
        => Assert.False(ColorLogic.TryParseHex(input, out _, out _, out _));

    [Fact]
    public void FormatHex_IsLowercaseAndClamped()
    {
        Assert.Equal("#3b82f6", ColorLogic.FormatHex(59, 130, 246));
        Assert.Equal("#ff0000", ColorLogic.FormatHex(300, -5, 0));
        Assert.Equal("rgb(255, 0, 0)", ColorLogic.FormatRgb(300, -5, 0));
    }

    // ---------- HSL ----------

    [Theory]
    [InlineData(59, 130, 246, 217, 91, 60)]
    [InlineData(255, 0, 0, 0, 100, 50)]
    [InlineData(0, 255, 0, 120, 100, 50)]
    [InlineData(0, 0, 255, 240, 100, 50)]
    [InlineData(255, 255, 255, 0, 0, 100)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(128, 128, 128, 0, 0, 50)]
    [InlineData(255, 255, 0, 60, 100, 50)]
    [InlineData(0, 255, 255, 180, 100, 50)]
    [InlineData(255, 0, 255, 300, 100, 50)]
    public void ToHsl_MatchesCssReferenceValues(int r, int g, int b, int h, int s, int l)
        => Assert.Equal((h, s, l), ColorLogic.ToHsl(r, g, b));

    [Fact]
    public void ToHsl_HueNear360WrapsToZero()
    {
        // Nearly pure red with a hint of blue: hue is 359.76 degrees, which must round to 0 not 360.
        var (h, _, _) = ColorLogic.ToHsl(255, 0, 1);
        Assert.Equal(0, h);
    }

    [Fact]
    public void ToHsl_RoundsHalfAwayFromZero()
    {
        // 128,128,128 -> lightness 50.196 -> 50; 64,64,64 -> 25.098 -> 25; 191,191,191 -> 74.9 -> 75.
        Assert.Equal(25, ColorLogic.ToHsl(64, 64, 64).Lightness);
        Assert.Equal(75, ColorLogic.ToHsl(191, 191, 191).Lightness);
        Assert.Equal("hsl(0, 0%, 75%)", ColorLogic.FormatHsl(191, 191, 191));
    }

    // ---------- COLORREF ----------

    [Fact]
    public void TryDecodeColorRef_UsesBgrByteOrderAndRejectsInvalid()
    {
        Assert.True(ColorLogic.TryDecodeColorRef(0x00F6823B, out var r, out var g, out var b));
        Assert.Equal((59, 130, 246), (r, g, b));
        Assert.False(ColorLogic.TryDecodeColorRef(-1, out _, out _, out _));
    }

    // ---------- history ----------

    [Fact]
    public void PushHistory_MovesDuplicatesToFrontAndCapsLength()
    {
        var history = new List<string> { "#000001", "#000002", "#000003" };

        Assert.True(ColorLogic.PushHistory(history, "#000003", limit: 3));
        Assert.Equal(new[] { "#000003", "#000001", "#000002" }, history);

        Assert.False(ColorLogic.PushHistory(history, "#000003", limit: 3)); // already at front
        Assert.True(ColorLogic.PushHistory(history, "#000004", limit: 3));
        Assert.Equal(new[] { "#000004", "#000003", "#000001" }, history);
    }

    [Fact]
    public void PushHistory_IsCaseInsensitiveAndIgnoresBlank()
    {
        var history = new List<string> { "#ABCDEF" };
        Assert.False(ColorLogic.PushHistory(history, "#abcdef"));
        Assert.False(ColorLogic.PushHistory(history, "  "));
        Assert.Single(history);
    }

    // ---------- view model (headless) ----------

    private static ColorPickerViewModel CreateVm() => new(new NullLogger(), new StatusService());

    [Fact]
    public void TypingHex_UpdatesChannelsWithoutRewritingText()
    {
        var vm = CreateVm();

        vm.Hex = "#fff";
        Assert.Equal("#fff", vm.Hex);
        Assert.Equal((255, 255, 255), (vm.Red, vm.Green, vm.Blue));
        Assert.True(vm.IsHexValid);
        Assert.Equal("#ffffff", vm.CanonicalHex);

        vm.Hex = "#ff0";
        Assert.Equal((255, 255, 0), (vm.Red, vm.Green, vm.Blue));

        vm.Hex = "#ff0a";  // #RGBA -> ff ff 00, alpha ignored
        Assert.Equal((255, 255, 0), (vm.Red, vm.Green, vm.Blue));

        vm.Hex = "#12";
        Assert.False(vm.IsHexValid);
        Assert.Equal((255, 255, 0), (vm.Red, vm.Green, vm.Blue)); // channels keep last valid color
    }

    [Fact]
    public void ChangingChannel_RewritesHexAndDerivedStrings()
    {
        var vm = CreateVm();

        vm.Red = 255;
        vm.Green = 0;
        vm.Blue = 0;

        Assert.Equal("#ff0000", vm.Hex);
        Assert.Equal("rgb(255, 0, 0)", vm.Rgb);
        Assert.Equal("hsl(0, 100%, 50%)", vm.Hsl);
        Assert.True(vm.IsHexValid);
    }

    [Fact]
    public void ChannelValues_AreClamped()
    {
        var vm = CreateVm();
        vm.Red = 999;
        vm.Blue = -20;
        Assert.Equal(255, vm.Red);
        Assert.Equal(0, vm.Blue);
    }

    [Fact]
    public void CommitHex_NormalizesTextAndRecordsHistory()
    {
        var vm = CreateVm();
        Assert.False(vm.HasHistory);

        vm.Hex = "ABC";
        vm.CommitHexCommand.Execute(null);

        Assert.Equal("#aabbcc", vm.Hex);
        Assert.True(vm.HasHistory);
        Assert.Equal("#aabbcc", vm.History[0].Hex);

        vm.CommitHexCommand.Execute(null);
        Assert.Single(vm.History); // no duplicate entry for the same color
    }

    [Fact]
    public void SelectHistory_RestoresColorAndMovesItToFront()
    {
        var vm = CreateVm();
        vm.Hex = "#111111";
        vm.CommitHexCommand.Execute(null);
        vm.Hex = "#222222";
        vm.CommitHexCommand.Execute(null);

        var older = vm.History.Single(h => h.Hex == "#111111");
        vm.SelectHistoryCommand.Execute(older);

        Assert.Equal("#111111", vm.Hex);
        Assert.Equal("#111111", vm.History[0].Hex);
        Assert.Equal(2, vm.History.Count);

        vm.ClearHistoryCommand.Execute(null);
        Assert.False(vm.HasHistory);
    }
}
