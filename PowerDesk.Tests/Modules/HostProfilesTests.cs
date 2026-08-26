using System.Text;
using PowerDesk.Modules.HostProfiles.Services;

namespace PowerDesk.Tests.Modules;

public sealed class HostProfilesTests
{
    [Fact]
    public void ParseLine_BlankAndComment()
    {
        Assert.Equal(HostsLineKind.Blank, HostProfilesLogic.ParseLine(1, "   ").Kind);
        var comment = HostProfilesLogic.ParseLine(2, "#  Copyright (c) Microsoft");
        Assert.Equal(HostsLineKind.Comment, comment.Kind);
        Assert.Equal("Copyright (c) Microsoft", comment.Comment);
    }

    [Fact]
    public void ParseLine_EntryWithAliasesAndInlineComment()
    {
        var line = HostProfilesLogic.ParseLine(3, "127.0.0.1\tlocalhost  dev.local # my box");
        Assert.Equal(HostsLineKind.Entry, line.Kind);
        Assert.Equal("127.0.0.1", line.Address);
        Assert.Equal(["localhost", "dev.local"], line.Hostnames);
        Assert.Equal("my box", line.Comment);
        Assert.Equal(3, line.LineNumber);
    }

    [Fact]
    public void ParseLine_Ipv6Entries()
    {
        Assert.Equal(HostsLineKind.Entry, HostProfilesLogic.ParseLine(1, "::1 localhost").Kind);
        Assert.Equal(HostsLineKind.Entry, HostProfilesLogic.ParseLine(1, "fe80::1%12 router").Kind);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("1.2.3 host")]
    [InlineData("256.1.1.1 host")]
    [InlineData("host 127.0.0.1")]
    [InlineData("127.0.0.1 *.example.com")]
    public void ParseLine_InvalidLines(string raw)
        => Assert.Equal(HostsLineKind.Invalid, HostProfilesLogic.ParseLine(1, raw).Kind);

    [Fact]
    public void Summarize_CountsAndLabel()
    {
        const string content = "# header\r\n\r\n127.0.0.1 localhost\n::1 localhost\r\nbad line\r\n";
        var s = HostProfilesLogic.Summarize(content);
        Assert.Equal(2, s.Entries);
        Assert.Equal(1, s.Comments);
        Assert.Equal(1, s.Invalid);
        Assert.Equal("2 active entries - 1 comment line - 1 invalid line", s.Label);
        Assert.Equal("Empty", HostProfilesLogic.Summarize("").Label);
        Assert.Equal("1 active entry", HostProfilesLogic.Summarize("10.0.0.1 a").Label);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("abc", false)]
    [InlineData("1.2.3.x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidAddress_Strict(string? token, bool expected)
        => Assert.Equal(expected, HostProfilesLogic.IsValidAddress(token));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("my-host.example.com", true)]
    [InlineData("under_score", true)]
    [InlineData("trailing.dot.", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-bad", false)]
    [InlineData("bad-", false)]
    [InlineData("*.example.com", false)]
    [InlineData("a..b", false)]
    [InlineData("has space", false)]
    public void IsValidHostname_LabelRules(string? token, bool expected)
        => Assert.Equal(expected, HostProfilesLogic.IsValidHostname(token));

    [Fact]
    public void IsValidHostname_RejectsOverlongLabel()
        => Assert.False(HostProfilesLogic.IsValidHostname(new string('a', 64) + ".com"));

    [Theory]
    [InlineData("a\nb\nc", "a\r\nb\r\nc\r\n")]
    [InlineData("a\rb", "a\r\nb\r\n")]
    [InlineData("a\r\nb\r\n\r\n\r\n", "a\r\nb\r\n")]
    [InlineData("\uFEFF127.0.0.1 x", "127.0.0.1 x\r\n")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("\r\n\r\n   \r\n", "")]
    public void NormalizeForWrite_CrlfAndSingleTrailingNewline(string? input, string expected)
        => Assert.Equal(expected, HostProfilesLogic.NormalizeForWrite(input));

    [Fact]
    public void ContentEquals_IgnoresLineEndingDifferences()
    {
        Assert.True(HostProfilesLogic.ContentEquals("a\nb", "a\r\nb\r\n"));
        Assert.False(HostProfilesLogic.ContentEquals("a\nb", "a\nc"));
    }

    [Fact]
    public void Decode_HonorsBoms()
    {
        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("café")).ToArray();
        Assert.Equal("café", HostProfilesLogic.Decode(utf8Bom));

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wide")).ToArray();
        Assert.Equal("wide", HostProfilesLogic.Decode(utf16));

        var utf16Be = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("big")).ToArray();
        Assert.Equal("big", HostProfilesLogic.Decode(utf16Be));
    }

    [Fact]
    public void Decode_StrictUtf8ThenLatin1Fallback()
    {
        Assert.Equal("café", HostProfilesLogic.Decode(Encoding.UTF8.GetBytes("café")));
        Assert.Equal("café", HostProfilesLogic.Decode(Encoding.Latin1.GetBytes("café")));
        Assert.Equal(string.Empty, HostProfilesLogic.Decode([]));
        Assert.Equal(string.Empty, HostProfilesLogic.Decode(null));
    }

    [Fact]
    public void ValidateProfileName_Rules()
    {
        string[] existing = ["Work", "Home"];
        Assert.NotNull(HostProfilesLogic.ValidateProfileName("", existing));
        Assert.NotNull(HostProfilesLogic.ValidateProfileName("   ", existing));
        Assert.NotNull(HostProfilesLogic.ValidateProfileName(new string('x', 65), existing));
        Assert.Contains("already exists", HostProfilesLogic.ValidateProfileName(" work ", existing));
        Assert.Null(HostProfilesLogic.ValidateProfileName("Work", existing, currentName: "Work"));
        Assert.Null(HostProfilesLogic.ValidateProfileName("Lab", existing));
    }

    [Fact]
    public void MakeUniqueName_AppendsCounter()
    {
        string[] existing = ["Hosts", "hosts (2)"];
        Assert.Equal("Hosts (3)", HostProfilesLogic.MakeUniqueName("Hosts", existing));
        Assert.Equal("Fresh", HostProfilesLogic.MakeUniqueName("  Fresh ", existing));
        Assert.Equal("Hosts profile", HostProfilesLogic.MakeUniqueName("   ", existing));
    }

    [Fact]
    public void BackupFileName_IsTimestamped()
        => Assert.Equal("hosts-backup-20260825-143005.txt", HostProfilesLogic.BackupFileName(new DateTime(2026, 8, 25, 14, 30, 5)));

    [Fact]
    public void DefaultHostsPath_PointsAtDriversEtc()
        => Assert.EndsWith(Path.Combine("drivers", "etc", "hosts"), HostProfilesLogic.DefaultHostsPath, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void SplitLines_MixedEndings()
        => Assert.Equal(["a", "b", "c", ""], HostProfilesLogic.SplitLines("a\r\nb\nc\r"));

    [Fact]
    public void HostsFileService_WriteWithBackup_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var hosts = Path.Combine(dir, "hosts");
            var backups = Path.Combine(dir, "backups");
            File.WriteAllBytes(hosts, Encoding.Latin1.GetBytes("# original\r\n127.0.0.1 café\r\n"));
            File.SetAttributes(hosts, File.GetAttributes(hosts) | FileAttributes.ReadOnly);

            var svc = new HostsFileService(hosts);
            Assert.True(svc.Exists);
            Assert.Equal("# original\r\n127.0.0.1 café\r\n", svc.Read());

            var stamp = new DateTime(2026, 1, 2, 3, 4, 5);
            var backup = svc.WriteWithBackup("# new\n10.0.0.1 a\n\n", backups, stamp);

            Assert.Equal(Path.Combine(backups, "hosts-backup-20260102-030405.txt"), backup);
            Assert.Equal("# original\r\n127.0.0.1 café\r\n", HostProfilesLogic.Decode(File.ReadAllBytes(backup!)));

            var written = File.ReadAllBytes(hosts);
            Assert.Equal(Encoding.ASCII.GetBytes("# new\r\n10.0.0.1 a\r\n"), written);
            Assert.True((File.GetAttributes(hosts) & FileAttributes.ReadOnly) != 0, "read-only attribute should be restored");
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HostsFileService_MissingFile_ReadsEmptyAndNoBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var hosts = Path.Combine(dir, "hosts");
            var svc = new HostsFileService(hosts);
            Assert.False(svc.Exists);
            Assert.Equal(string.Empty, svc.Read());

            var backup = svc.WriteWithBackup("1.1.1.1 one", Path.Combine(dir, "backups"), DateTime.Now);
            Assert.Null(backup);
            Assert.Equal("1.1.1.1 one\r\n", File.ReadAllText(hosts));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- import / export naming ----------

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "Imported hosts")]
    [InlineData(@"C:\x\HOSTS", "Imported hosts")]
    [InlineData(@"C:\x\work.hosts", "work")]
    [InlineData(@"C:\x\dev machines.txt", "dev machines")]
    [InlineData(@"C:\x\hosts.bak", "Imported hosts")]
    [InlineData(@"C:\x\blocklist.conf", "blocklist")]
    [InlineData(@"C:\x\site.example.com", "site.example.com")]
    [InlineData("", "Imported hosts")]
    [InlineData(null, "Imported hosts")]
    public void ProfileNameFromPath_StripsGenericExtensionsAndFallsBack(string? path, string expected)
        => Assert.Equal(expected, HostProfilesLogic.ProfileNameFromPath(path));

    [Fact]
    public void ProfileNameFromPath_TrimsToLimit()
    {
        var name = HostProfilesLogic.ProfileNameFromPath(@"C:\x\" + new string('a', 200) + ".txt");
        Assert.Equal(HostProfilesLogic.MaxProfileNameLength, name.Length);
    }

    [Theory]
    [InlineData("Work", "Work.txt")]
    [InlineData("  Home / Lab: v2  ", "Home - Lab- v2.txt")]
    [InlineData("", "hosts.txt")]
    [InlineData(null, "hosts.txt")]
    [InlineData("...", "hosts.txt")]
    public void ExportFileName_IsSafeForTheFileSystem(string? name, string expected)
    {
        var result = HostProfilesLogic.ExportFileName(name);
        Assert.Equal(expected, result);
        Assert.DoesNotContain(result, c => Path.GetInvalidFileNameChars().Contains(c));
    }
}
