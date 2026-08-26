using PowerDesk.Core.Services;

namespace PowerDesk.Tests.Core;

public sealed class IconServiceTests
{
    private static string SystemExe(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    [Fact]
    public void NormalizeExePath_StripsQuotesAroundExecutable()
    {
        var exe = SystemExe("notepad.exe");
        Assert.Equal(exe, IconService.NormalizeExePath($"\"{exe}\" /A \"some file.txt\""));
    }

    [Fact]
    public void NormalizeExePath_TakesHeadBeforeArgumentsWhenUnquoted()
    {
        var exe = SystemExe("notepad.exe");
        Assert.Equal(exe, IconService.NormalizeExePath($"{exe} /A"));
    }

    [Fact]
    public void NormalizeExePath_ExpandsEnvironmentVariables()
    {
        var raw = @"%SystemRoot%\System32\notepad.exe";
        var expanded = Environment.ExpandEnvironmentVariables(raw);
        Assert.Equal(expanded, IconService.NormalizeExePath(raw));
        Assert.Equal(expanded, IconService.NormalizeExePath(raw + " -flag"));
    }

    [Fact]
    public void NormalizeExePath_ReturnsInputWhenNothingResolves()
    {
        Assert.Equal(@"Z:\does\not\exist.exe --x", IconService.NormalizeExePath(@"  Z:\does\not\exist.exe --x  "));
        Assert.Equal(string.Empty, IconService.NormalizeExePath("   "));
    }

    [Fact]
    public void GetIcon_NullOrMissingPath_ReturnsNullWithoutThrowing()
    {
        var svc = new IconService(new NullLogger());
        Assert.Null(svc.GetIcon(null));
        Assert.Null(svc.GetIcon(""));
        Assert.Null(svc.GetIcon(@"Z:\nope\missing.exe"));
    }

    [Fact]
    public void GetIconForProcess_InvalidPid_ReturnsNull()
    {
        var svc = new IconService(new NullLogger());
        Assert.Null(svc.GetIconForProcess(-1));
    }

    private sealed class NullLogger : PowerDesk.Core.Logging.ILogger
    {
        public string LogFilePath => string.Empty;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
