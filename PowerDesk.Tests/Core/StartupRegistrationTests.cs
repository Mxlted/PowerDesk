using PowerDesk.Core.Services;

namespace PowerDesk.Tests.Core;

public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Tools\PowerDesk\PowerDesk.exe";

    [Fact]
    public void BuildCommand_QuotesThePath()
    {
        Assert.Equal("\"" + Exe + "\"", StartupRegistration.BuildCommand(Exe));
    }

    [Theory]
    [InlineData("\"C:\\Tools\\PowerDesk\\PowerDesk.exe\"", true)]
    [InlineData("\"c:\\tools\\powerdesk\\POWERDESK.EXE\"", true)]
    [InlineData("C:\\Tools\\PowerDesk\\PowerDesk.exe", true)]
    [InlineData("  \"C:\\Tools\\PowerDesk\\PowerDesk.exe\"  ", true)]
    [InlineData("\"C:\\Tools\\PowerDesk\\PowerDesk.exe\" --relaunched", true)]
    [InlineData("\"C:\\Old\\PowerDesk.exe\"", false)]
    [InlineData("C:\\Old\\PowerDesk.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CommandMatchesExe_ComparesTheProgramToken(string? command, bool expected)
    {
        Assert.Equal(expected, StartupRegistration.CommandMatchesExe(command, Exe));
    }

    [Fact]
    public void CommandMatchesExe_RejectsMissingExePath()
    {
        Assert.False(StartupRegistration.CommandMatchesExe("\"" + Exe + "\"", null));
        Assert.False(StartupRegistration.CommandMatchesExe("\"" + Exe + "\"", " "));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 0x07 }, false)]
    public void ParseApprovedBlob_ReadsTaskManagerState(byte[] blob, bool enabled)
    {
        Assert.Equal(enabled, StartupRegistration.ParseApprovedBlob(blob));
    }

    [Fact]
    public void ParseApprovedBlob_NullForMissingOrZeroState()
    {
        Assert.Null(StartupRegistration.ParseApprovedBlob(null));
        Assert.Null(StartupRegistration.ParseApprovedBlob(Array.Empty<byte>()));
        Assert.Null(StartupRegistration.ParseApprovedBlob(new byte[] { 0x00, 0, 0, 0 }));
    }
}
