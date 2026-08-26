using Microsoft.Win32;
using PowerDesk.Modules.StartupPilot.Models;
using PowerDesk.Modules.StartupPilot.Services;

namespace PowerDesk.Tests.Modules;

public sealed class StartupPilotTests
{
    // ------------------------------------------------------------------ command-line parsing

    private static readonly string[] NoSearchDirs = Array.Empty<string>();
    private static bool Never(string _) => false;

    private static string Extract(string cmd, Func<string, bool>? exists = null, params string[] searchDirs) =>
        StartupPilotLogic.ExtractExecutablePath(cmd, exists ?? Never, searchDirs.Length == 0 ? NoSearchDirs : searchDirs);

    [Fact]
    public void Extract_QuotedPathWithArguments_ReturnsPathOnly()
    {
        Assert.Equal(@"C:\Program Files\Vendor\App.exe", Extract(@"""C:\Program Files\Vendor\App.exe"" --minimized /tray"));
    }

    [Fact]
    public void Extract_UnquotedPathWithSpacesAndArguments_ReturnsUpToExtension()
    {
        Assert.Equal(@"C:\Program Files\Vendor\App.exe", Extract(@"C:\Program Files\Vendor\App.exe -autostart"));
    }

    [Fact]
    public void Extract_ExtensionInsideDirectoryName_IsNotMistakenForExecutable()
    {
        // ".exe" appears inside "execute" but the real program is run.bat.
        Assert.Equal(@"C:\apps\execute\run.bat", Extract(@"C:\apps\execute\run.bat /x"));
    }

    [Fact]
    public void Extract_MultipleExtensions_PicksEarliestBoundary()
    {
        Assert.Equal(@"C:\tools\run.bat", Extract(@"C:\tools\run.bat C:\other\thing.exe"));
    }

    [Fact]
    public void Extract_UnterminatedQuote_StripsLeadingQuote()
    {
        Assert.Equal(@"C:\Program Files\Vendor\App.exe", Extract(@"""C:\Program Files\Vendor\App.exe"));
    }

    [Fact]
    public void Extract_ExpandsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("POWERDESK_TEST_DIR", @"C:\PdTest");
        try
        {
            Assert.Equal(@"C:\PdTest\bin\tool.exe", Extract(@"""%POWERDESK_TEST_DIR%\bin\tool.exe"" /q"));
            Assert.Equal(@"C:\PdTest\bin\tool.exe", Extract(@"%POWERDESK_TEST_DIR%\bin\tool.exe /q"));
        }
        finally { Environment.SetEnvironmentVariable("POWERDESK_TEST_DIR", null); }
    }

    [Theory]
    [InlineData(@"rundll32.exe ""C:\Program Files\Vendor\hook.dll"",Entry", @"C:\Program Files\Vendor\hook.dll")]
    [InlineData(@"C:\Windows\System32\rundll32.exe C:\Vendor\hook.dll,Entry", @"C:\Vendor\hook.dll")]
    [InlineData(@"""C:\Windows\System32\rundll32.exe"" C:\Vendor\hook.dll Entry", @"C:\Vendor\hook.dll")]
    public void Extract_Rundll32_ReturnsHostedDll(string command, string expected)
    {
        Assert.Equal(expected, Extract(command));
    }

    [Fact]
    public void Extract_BareHostName_IsResolvedThroughSearchDirs()
    {
        var sys32 = @"C:\Windows\System32";
        bool Exists(string p) => string.Equals(p, Path.Combine(sys32, "cmd.exe"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", Extract(@"cmd.exe /c start foo", Exists, @"C:\Nope", sys32));
        Assert.Equal(@"C:\Windows\System32\cmd.exe", Extract(@"cmd /c start foo", Exists, sys32));
    }

    [Fact]
    public void Extract_BareHostNotFound_ReturnsNameUnchanged()
    {
        Assert.Equal("unknownhost.exe", Extract("unknownhost.exe /arg", Never, @"C:\Windows\System32"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_Empty_ReturnsEmpty(string? command)
    {
        Assert.Equal(string.Empty, Extract(command!));
    }

    [Fact]
    public void SplitHead_KeepsArgumentsAfterQuotedProgram()
    {
        var (head, rest) = StartupPilotLogic.SplitHead(@"""C:\a b\x.exe""  --flag ""two words""");
        Assert.Equal(@"C:\a b\x.exe", head);
        Assert.Equal(@"--flag ""two words""", rest);
    }

    // ------------------------------------------------------------------ StartupApproved blob

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x02 }, true)]
    [InlineData(new byte[] { 0x00, 0, 0, 0 }, null)]
    [InlineData(new byte[0], null)]
    [InlineData(null, null)]
    public void ParseStartupApprovedBlob_DecodesState(byte[]? data, bool? expected)
    {
        Assert.Equal(expected, StartupPilotLogic.ParseStartupApprovedBlob(data));
    }

    [Fact]
    public void BuildStartupApprovedBlob_Enabled_Is0x02WithZeroFileTime()
    {
        var blob = StartupPilotLogic.BuildStartupApprovedBlob(enable: true);
        Assert.Equal(12, blob.Length);
        Assert.Equal(0x02, blob[0]);
        Assert.All(blob.Skip(1), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildStartupApprovedBlob_Disabled_Is0x03WithFileTime()
    {
        var when = new DateTime(2026, 8, 25, 10, 30, 0, DateTimeKind.Utc);
        var blob = StartupPilotLogic.BuildStartupApprovedBlob(enable: false, disabledAt: when);
        Assert.Equal(12, blob.Length);
        Assert.Equal(0x03, blob[0]);
        Assert.Equal(0, blob[1]); Assert.Equal(0, blob[2]); Assert.Equal(0, blob[3]);
        var fileTime = BitConverter.ToInt64(blob, 4);
        Assert.Equal(when, DateTime.FromFileTimeUtc(fileTime));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupApprovedBlob_RoundTrips(bool enable)
    {
        Assert.Equal(enable, StartupPilotLogic.ParseStartupApprovedBlob(StartupPilotLogic.BuildStartupApprovedBlob(enable)));
    }

    // ------------------------------------------------------------------ registry key mapping / locators

    [Theory]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run", "Run")]
    [InlineData(@"software\microsoft\windows\currentversion\run", "Run")]
    [InlineData(@"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", null)]
    [InlineData(@"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce", null)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run\AutorunsDisabled", null)]
    public void StartupApprovedSubkey_MapsRunKeysOnly(string keyPath, string? expected)
    {
        Assert.Equal(expected, StartupPilotLogic.StartupApprovedSubkeyForRegistryPath(keyPath));
    }

    [Fact]
    public void RegistryLocator_RoundTrips_EvenWhenValueNameContainsSeparator()
    {
        var locator = StartupPilotLogic.FormatRegistryLocator(RegistryHive.LocalMachine, StartupPilotLogic.Run32Key, "Weird|Name");
        Assert.True(StartupPilotLogic.TryParseRegistryLocator(locator, out var hive, out var key, out var name));
        Assert.Equal(RegistryHive.LocalMachine, hive);
        Assert.Equal(StartupPilotLogic.Run32Key, key);
        Assert.Equal("Weird|Name", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("CurrentUser|onlytwo")]
    [InlineData("NotAHive|Software\\Key|Value")]
    [InlineData("CurrentUser||Value")]
    [InlineData("CurrentUser|Software\\Key|")]
    public void RegistryLocator_Malformed_IsRejected(string? locator)
    {
        Assert.False(StartupPilotLogic.TryParseRegistryLocator(locator, out _, out _, out _));
    }

    [Fact]
    public void AutorunsDisabledKey_HelpersAreConsistent()
    {
        var parked = StartupPilotLogic.AutorunsDisabledKeyFor(StartupPilotLogic.RunOnceKey);
        Assert.EndsWith(@"\AutorunsDisabled", parked);
        Assert.True(StartupPilotLogic.IsAutorunsDisabledKey(parked));
        Assert.True(StartupPilotLogic.IsAutorunsDisabledKey(parked.ToLowerInvariant()));
        Assert.False(StartupPilotLogic.IsAutorunsDisabledKey(StartupPilotLogic.RunOnceKey));
        Assert.Equal(StartupPilotLogic.RunOnceKey, StartupPilotLogic.ParentOfAutorunsDisabledKey(parked));
        Assert.Equal(StartupPilotLogic.RunKey, StartupPilotLogic.ParentOfAutorunsDisabledKey(StartupPilotLogic.RunKey));
        Assert.True(StartupPilotLogic.IsRunOnceKey(StartupPilotLogic.RunOnce32Key));
        Assert.False(StartupPilotLogic.IsRunOnceKey(StartupPilotLogic.RunKey));
    }

    // ------------------------------------------------------------------ classification

    [Theory]
    [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe", "SecurityHealth", true)]
    [InlineData(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe", "Edge", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Microsoft\OneDrive\OneDrive.exe", "OneDrive", true)]
    [InlineData(@"C:\Program Files\Vendor\App.exe", "Microsoft Teams", true)]
    [InlineData(@"C:\Program Files\Vendor\App.exe", "Windows Defender helper", true)]
    [InlineData(@"C:\Program Files\Vendor\App.exe", "Vendor App", false)]
    [InlineData("", "", false)]
    public void LooksLikeMicrosoft_DetectsSystemPathsAndNames(string command, string name, bool expected)
    {
        Assert.Equal(expected, StartupPilotLogic.LooksLikeMicrosoft(command, name));
    }

    [Theory]
    [InlineData(0L, StartupImpact.Low)]
    [InlineData(10L * 1024 * 1024, StartupImpact.Low)]
    [InlineData(10L * 1024 * 1024 + 1, StartupImpact.Medium)]
    [InlineData(50L * 1024 * 1024, StartupImpact.Medium)]
    [InlineData(50L * 1024 * 1024 + 1, StartupImpact.High)]
    public void ImpactFromFileSize_UsesThresholds(long bytes, StartupImpact expected)
    {
        Assert.Equal(expected, StartupPilotLogic.ImpactFromFileSize(bytes));
    }

    [Theory]
    [InlineData(0, ServiceStartupType.Unknown)]
    [InlineData(1, ServiceStartupType.Unknown)]
    [InlineData(2, ServiceStartupType.Automatic)]
    [InlineData(3, ServiceStartupType.Manual)]
    [InlineData(4, ServiceStartupType.Disabled)]
    [InlineData(99, ServiceStartupType.Unknown)]
    public void ServiceStartupType_FromStartValue(int start, ServiceStartupType expected)
    {
        Assert.Equal(expected, StartupPilotLogic.ServiceStartupTypeFromStartValue(start));
    }

    // ------------------------------------------------------------------ CSV

    [Fact]
    public void CsvField_QuotesAndEscapesEmbeddedQuotes()
    {
        Assert.Equal("\"plain\"", StartupPilotLogic.CsvField("plain"));
        Assert.Equal("\"say \"\"hi\"\"\"", StartupPilotLogic.CsvField("say \"hi\""));
        Assert.Equal("\"\"", StartupPilotLogic.CsvField(null));
        Assert.Equal("\"a,b\"", StartupPilotLogic.CsvField("a,b"));
        Assert.Equal("\"line1\nline2\"", StartupPilotLogic.CsvField("line1\nline2"));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-x")]
    [InlineData("@SUM(A1)")]
    public void CsvField_NeutralisesLeadingFormulaCharacters(string value)
    {
        Assert.Equal("\"'" + value + "\"", StartupPilotLogic.CsvField(value));
    }

    [Fact]
    public void CsvLine_JoinsQuotedFields()
    {
        Assert.Equal("\"a\",\"b \"\"c\"\"\",\"\"", StartupPilotLogic.CsvLine("a", "b \"c\"", null));
    }

    // ------------------------------------------------------------------ history / undo bookkeeping

    private static StartupHistoryEntry Entry(DateTime ts, bool oldEnabled = true, bool newEnabled = false) =>
        new() { Timestamp = ts, ItemName = ts.Ticks.ToString(), OldEnabled = oldEnabled, NewEnabled = newEnabled };

    [Fact]
    public void ApplyRetention_Last100_KeepsNewestByTimestampRegardlessOfOrder()
    {
        var now = new DateTime(2026, 8, 25, 12, 0, 0);
        var history = new List<StartupHistoryEntry>();
        for (int i = 0; i < 130; i++) history.Add(Entry(now.AddMinutes(i)));   // oldest-first on purpose
        StartupPilotLogic.ApplyRetention(history, HistoryRetention.Last100, now);
        Assert.Equal(100, history.Count);
        Assert.All(history, h => Assert.True(h.Timestamp >= now.AddMinutes(30)));
    }

    [Fact]
    public void ApplyRetention_Last30Days_DropsOlderEntries()
    {
        var now = new DateTime(2026, 8, 25, 12, 0, 0);
        var history = new List<StartupHistoryEntry>
        {
            Entry(now), Entry(now.AddDays(-29)), Entry(now.AddDays(-31)), Entry(now.AddDays(-400)),
        };
        StartupPilotLogic.ApplyRetention(history, HistoryRetention.Last30Days, now);
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.True(h.Timestamp >= now.AddDays(-30)));
    }

    [Fact]
    public void ApplyRetention_KeepAll_LeavesHistoryAlone()
    {
        var now = DateTime.Now;
        var history = Enumerable.Range(0, 250).Select(i => Entry(now.AddDays(-i))).ToList();
        StartupPilotLogic.ApplyRetention(history, HistoryRetention.KeepAll, now);
        Assert.Equal(250, history.Count);
    }

    [Fact]
    public void IsUndoable_RequiresARealStateChange()
    {
        Assert.True(StartupPilotLogic.IsUndoable(new StartupHistoryEntry { OldEnabled = true, NewEnabled = false }));
        Assert.False(StartupPilotLogic.IsUndoable(new StartupHistoryEntry { OldEnabled = true, NewEnabled = true }));
        Assert.True(StartupPilotLogic.IsUndoable(new StartupHistoryEntry
        {
            OldServiceStartupType = ServiceStartupType.Manual, NewServiceStartupType = ServiceStartupType.Disabled,
        }));
        Assert.False(StartupPilotLogic.IsUndoable(new StartupHistoryEntry
        {
            OldEnabled = true, NewEnabled = false, // ignored when service types are present
            OldServiceStartupType = ServiceStartupType.Manual, NewServiceStartupType = ServiceStartupType.Manual,
        }));
    }

    [Fact]
    public void HistoryEntry_ActionLabels()
    {
        Assert.Equal("Disabled", new StartupHistoryEntry { OldEnabled = true, NewEnabled = false }.Action);
        Assert.Equal("Enabled", new StartupHistoryEntry { OldEnabled = false, NewEnabled = true }.Action);
        Assert.Equal("—", new StartupHistoryEntry { OldEnabled = true, NewEnabled = true }.Action);
        var svc = new StartupHistoryEntry { OldServiceStartupType = ServiceStartupType.Automatic, NewServiceStartupType = ServiceStartupType.Manual };
        Assert.Equal("Automatic → Manual", svc.Action);
        Assert.Equal("Automatic", svc.OldValueLabel);
        Assert.Equal("Manual", svc.NewValueLabel);
    }

    // ------------------------------------------------------------------ model helpers

    [Fact]
    public void StartupItem_StatusLabel_FollowsSourceKind()
    {
        var entry = new StartupItem { Source = StartupSource.Registry, Enabled = true };
        Assert.Equal("Enabled", entry.StatusLabel);
        entry.Enabled = false;
        Assert.Equal("Disabled", entry.StatusLabel);

        var svc = new StartupItem { Source = StartupSource.Service, ServiceStartupType = ServiceStartupType.Manual };
        Assert.Equal("Manual", svc.StatusLabel);
        svc.ServiceStartupType = ServiceStartupType.Disabled;
        Assert.Equal("Disabled", svc.StatusLabel);
    }

    [Fact]
    public void StartupItem_RaisesStatusLabelWhenEnabledChanges()
    {
        var item = new StartupItem { Source = StartupSource.StartupFolder, Enabled = true };
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        item.Enabled = false;
        Assert.Contains(nameof(StartupItem.Enabled), raised);
        Assert.Contains(nameof(StartupItem.StatusLabel), raised);
    }

    [Fact]
    public void NoteKey_CombinesSourceAndLocator()
    {
        Assert.Equal("Registry|CurrentUser|Software\\X|Name", StartupPilotLogic.NoteKey(StartupSource.Registry, "CurrentUser|Software\\X|Name"));
        Assert.NotEqual(StartupPilotLogic.NoteKey(StartupSource.Registry, "a"), StartupPilotLogic.NoteKey(StartupSource.Service, "a"));
    }

    // ------------------------------------------------------------------ adding startup-folder entries

    [Theory]
    [InlineData(@"C:\Apps\Tool.lnk", true)]
    [InlineData(@"C:\Apps\Site.URL", true)]
    [InlineData(@"C:\Apps\Tool.exe", false)]
    [InlineData(@"C:\Apps\run.bat", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsShortcutLike_ByExtension(string? path, bool expected)
        => Assert.Equal(expected, StartupPilotLogic.IsShortcutLike(path));

    [Theory]
    [InlineData(@"C:\Program Files\Vendor\App.exe", "App.lnk")]
    [InlineData(@"C:\Apps\Tool.lnk", "Tool.lnk")]
    [InlineData(@"C:\Apps\Site.url", "Site.url")]
    [InlineData(@"C:\Apps\run.bat", "run.lnk")]
    [InlineData(@"C:\Apps\my<tool>?.exe", "my-tool.lnk")]
    [InlineData(@"C:\Apps\a|b.exe", "a-b.lnk")]
    [InlineData(@"C:\Apps\.exe", "Startup item.lnk")]
    [InlineData("", "Startup item.lnk")]
    public void StartupEntryFileName_UsesTargetNameAndSafeCharacters(string target, string expected)
    {
        var name = StartupPilotLogic.StartupEntryFileName(target);
        Assert.Equal(expected, name);
        Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Fact]
    public void PickStartupDropTargets_FilesOnlyDedupedFoldersAndMissingCounted()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\a\app.exe", @"C:\a\tool.lnk" };
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\a" };

        var (picked, folders, missing) = StartupPilotLogic.PickStartupDropTargets(
            new[] { @"C:\a\app.exe", @"C:\A\APP.EXE", @"C:\a", @"C:\a\tool.lnk", @"C:\gone.exe", " " },
            files.Contains, dirs.Contains);

        Assert.Equal(new[] { @"C:\a\app.exe", @"C:\a\tool.lnk" }, picked);
        Assert.Equal(1, folders);
        Assert.Equal(1, missing);

        var (none, _, _) = StartupPilotLogic.PickStartupDropTargets(null, _ => true, _ => false);
        Assert.Empty(none);
    }
}
