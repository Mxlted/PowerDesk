using PowerDesk.Modules.FileLockFinder.Models;
using PowerDesk.Modules.FileLockFinder.Services;

namespace PowerDesk.Tests.Modules;

public sealed class FileLockFinderTests
{
    private const int CurrentPid = 4242;

    private static LockingProcessInfo Proc(int pid, string app = "", string name = "proc", DateTime? startUtc = null, bool self = false, bool critical = false)
        => new()
        {
            ProcessId = pid,
            AppName = app,
            ProcessName = name,
            StartTimeUtc = startUtc,
            IsCurrentProcess = self,
            IsCriticalSystemProcess = critical,
        };

    [Fact]
    public void Classify_CurrentProcess_IsSelf()
        => Assert.Equal(ProcessRisk.Self, FileLockLogic.Classify(CurrentPid, "PowerDesk", false, CurrentPid));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Classify_KernelPids_AreCritical(int pid)
        => Assert.Equal(ProcessRisk.Critical, FileLockLogic.Classify(pid, "System", false, CurrentPid));

    [Theory]
    [InlineData("csrss")]
    [InlineData("CSRSS.EXE")]
    [InlineData("  lsass.exe ")]
    [InlineData("svchost")]
    [InlineData("winlogon")]
    public void Classify_KnownSystemProcessNames_AreCritical(string name)
        => Assert.Equal(ProcessRisk.Critical, FileLockLogic.Classify(1000, name, false, CurrentPid));

    [Fact]
    public void Classify_RestartManagerCriticalFlag_IsCritical()
        => Assert.Equal(ProcessRisk.Critical, FileLockLogic.Classify(1000, "someapp", true, CurrentPid));

    [Theory]
    [InlineData("notepad")]
    [InlineData("explorer")]
    [InlineData("chrome.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_OrdinaryProcesses_AreNotCritical(string? name)
        => Assert.Equal(ProcessRisk.None, FileLockLogic.Classify(1000, name, false, CurrentPid));

    [Theory]
    [InlineData("svchost.exe", "svchost")]
    [InlineData("SVCHOST.EXE", "SVCHOST")]
    [InlineData("  notepad ", "notepad")]
    [InlineData("exe", "exe")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeProcessName_StripsExtensionAndWhitespace(string? input, string expected)
        => Assert.Equal(expected, FileLockLogic.NormalizeProcessName(input));

    [Fact]
    public void SameStartTime_WithinTolerance_IsTrue()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(FileLockLogic.SameStartTime(t, t.AddMilliseconds(1500)));
    }

    [Fact]
    public void SameStartTime_OutsideTolerance_IsFalse()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(FileLockLogic.SameStartTime(t, t.AddSeconds(5)));
    }

    [Fact]
    public void SameStartTime_NullHandling()
    {
        var t = DateTime.UtcNow;
        Assert.True(FileLockLogic.SameStartTime(null, null));
        Assert.False(FileLockLogic.SameStartTime(t, null));
        Assert.False(FileLockLogic.SameStartTime(null, t));
    }

    [Fact]
    public void SameStartTime_ComparesAcrossKinds()
    {
        var utc = new DateTime(2026, 6, 1, 8, 30, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        Assert.True(FileLockLogic.SameStartTime(utc, local));
    }

    [Fact]
    public void FileTimeToUtc_ZeroIsNull()
        => Assert.Null(FileLockLogic.FileTimeToUtc(0, 0));

    [Fact]
    public void FileTimeToUtc_RoundTripsKnownValue()
    {
        var expected = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var ft = expected.ToFileTimeUtc();
        var high = (int)(ft >> 32);
        var low = (int)(ft & 0xFFFFFFFF);
        var actual = FileLockLogic.FileTimeToUtc(high, low);
        Assert.Equal(expected, actual);
        Assert.Equal(DateTimeKind.Utc, actual!.Value.Kind);
    }

    [Fact]
    public void FileTimeToUtc_HandlesHighBitInLowWord()
    {
        // Low DWORD with the sign bit set must not be sign-extended.
        var ft = (0x01DAL << 32) | 0x8000_0000L;
        var expected = DateTime.FromFileTimeUtc(ft);
        Assert.Equal(expected, FileLockLogic.FileTimeToUtc(0x01DA, unchecked((int)0x8000_0000)));
    }

    [Theory]
    [InlineData(0, false, true, "no files")]
    [InlineData(0, false, false, "No resources")]
    [InlineData(512, true, true, "first 512")]
    [InlineData(7, false, true, "7 file(s)")]
    [InlineData(1, false, false, "Checked 1 file")]
    public void BuildScopeLabel_DescribesScan(int count, bool limited, bool isFolder, string expectedFragment)
        => Assert.Contains(expectedFragment, FileLockLogic.BuildScopeLabel(count, limited, isFolder));

    [Fact]
    public void Sort_OrdersByDisplayNameCaseInsensitiveThenPid()
    {
        var sorted = FileLockLogic.Sort(
        [
            Proc(30, app: "zeta"),
            Proc(20, app: "Alpha"),
            Proc(10, app: "alpha"),
            Proc(5, name: "beta"),
        ]);
        Assert.Equal([10, 20, 5, 30], sorted.Select(p => p.ProcessId));
    }

    [Fact]
    public void Dedupe_CollapsesSamePidAndStartTime_KeepsReusedPid()
    {
        var t = DateTime.UtcNow;
        var list = FileLockLogic.Dedupe(
        [
            Proc(100, startUtc: t),
            Proc(100, startUtc: t.AddMilliseconds(500)),
            Proc(100, startUtc: t.AddMinutes(10)),
            Proc(200, startUtc: t),
        ]);
        Assert.Equal(3, list.Count);
        Assert.Equal(2, list.Count(p => p.ProcessId == 100));
    }

    [Fact]
    public void PickDropTarget_FirstExistingWins_CountsIgnored()
    {
        var (target, ignored) = FileLockLogic.PickDropTarget(
            ["missing", " C:\\real ", "", "C:\\other"],
            p => p.StartsWith("C:\\"));
        Assert.Equal("C:\\real", target);
        Assert.Equal(2, ignored);
    }

    [Fact]
    public void PickDropTarget_NoneExisting_ReturnsNull()
    {
        var (target, ignored) = FileLockLogic.PickDropTarget(["a", "b"], _ => false);
        Assert.Null(target);
        Assert.Equal(2, ignored);
    }

    [Fact]
    public void PickDropTarget_NullPayload_IsEmpty()
    {
        var (target, ignored) = FileLockLogic.PickDropTarget(null, _ => true);
        Assert.Null(target);
        Assert.Equal(0, ignored);
    }

    [Fact]
    public void BuildStopConfirmation_CriticalHasStrongWarning()
    {
        var text = FileLockLogic.BuildStopConfirmation(Proc(77, app: "Windows Logon"), ProcessRisk.Critical);
        Assert.Contains("WARNING", text);
        Assert.Contains("Windows Logon", text);
        Assert.Contains("77", text);
    }

    [Fact]
    public void BuildStopConfirmation_NormalMentionsProcessAndUnsavedWork()
    {
        var text = FileLockLogic.BuildStopConfirmation(Proc(12, app: "Notepad"), ProcessRisk.None);
        Assert.DoesNotContain("WARNING", text);
        Assert.Contains("Notepad (PID 12)", text);
        Assert.Contains("Unsaved work", text);
    }

    [Fact]
    public void Model_DisplayNameFallsBackToProcessName()
    {
        Assert.Equal("Notepad", Proc(1, app: "Notepad", name: "notepad").DisplayName);
        Assert.Equal("notepad", Proc(1, app: "  ", name: "notepad").DisplayName);
    }

    [Fact]
    public void Model_KindLabelAndCanStop()
    {
        Assert.Equal("PowerDesk", Proc(1, self: true).KindLabel);
        Assert.False(Proc(1, self: true).CanStop);
        Assert.Equal("System", Proc(1, critical: true).KindLabel);
        Assert.True(Proc(1, critical: true).CanStop);
        Assert.Equal(string.Empty, Proc(1).KindLabel);
        Assert.False(Proc(1).HasKindLabel);
        Assert.Equal("Service", new LockingProcessInfo { ServiceName = "Spooler" }.KindLabel);
    }
}
