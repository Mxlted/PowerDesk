using System.Text;
using System.Threading;
using PowerDesk.Modules.HashDesk.Models;
using PowerDesk.Modules.HashDesk.Services;

namespace PowerDesk.Tests.Modules;

public sealed class HashDeskTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string AbcSha1 = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string AbcMd5 = "900150983cd24fb0d6963f7d28e17f72";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string EmptySha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
    private const string EmptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";

    [Fact]
    public void ComputeText_KnownVectors()
    {
        var t = HashDeskLogic.ComputeText("abc");
        Assert.Equal(AbcSha256, t.Sha256);
        Assert.Equal(AbcSha1, t.Sha1);
        Assert.Equal(AbcMd5, t.Md5);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ComputeText_EmptyHasNoBom(string? text)
    {
        var t = HashDeskLogic.ComputeText(text);
        Assert.Equal(EmptySha256, t.Sha256);
        Assert.Equal(EmptySha1, t.Sha1);
        Assert.Equal(EmptyMd5, t.Md5);
    }

    [Fact]
    public void ComputeText_NonAscii_MatchesUtf8Bytes()
    {
        var bytes = new UTF8Encoding(false).GetBytes("héllo wörld");
        using var stream = new MemoryStream(bytes);
        Assert.Equal(HashDeskLogic.ComputeStream(stream, bytes.Length, CancellationToken.None), HashDeskLogic.ComputeText("héllo wörld"));
    }

    [Fact]
    public void ComputeStream_MultipleChunks_MatchesSinglePass()
    {
        var bytes = Encoding.ASCII.GetBytes("abc");
        using var stream = new MemoryStream(bytes);
        var t = HashDeskLogic.ComputeStream(stream, bytes.Length, CancellationToken.None, bufferSize: 1);
        Assert.Equal(AbcSha256, t.Sha256);
        Assert.Equal(AbcSha1, t.Sha1);
        Assert.Equal(AbcMd5, t.Md5);
    }

    [Fact]
    public void ComputeStream_ReportsMonotonicProgressEndingAtOne()
    {
        var bytes = new byte[10_000];
        new Random(1).NextBytes(bytes);
        var reports = new List<double>();
        using var stream = new MemoryStream(bytes);
        var progress = new SyncProgress(reports.Add);

        // 1,000 reads of 10 bytes: each read is 0.1%, below the 0.5% throttle step.
        HashDeskLogic.ComputeStream(stream, bytes.Length, CancellationToken.None, progress, bufferSize: 10);

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1]);
        for (var i = 1; i < reports.Count; i++) Assert.True(reports[i] >= reports[i - 1]);
        Assert.InRange(reports.Count, 2, 1.0 / HashDeskLogic.ProgressStep + 1);
    }

    [Fact]
    public void ComputeStream_EmptyStream_ReportsCompletion()
    {
        var reports = new List<double>();
        using var stream = new MemoryStream();
        var t = HashDeskLogic.ComputeStream(stream, 0, CancellationToken.None, new SyncProgress(reports.Add));
        Assert.Equal(EmptySha256, t.Sha256);
        Assert.Equal([1.0], reports);
    }

    [Fact]
    public void ComputeStream_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stream = new MemoryStream(new byte[1024]);
        Assert.Throws<OperationCanceledException>(() => HashDeskLogic.ComputeStream(stream, 1024, cts.Token));
    }

    [Theory]
    [InlineData("  0xAB:CD-ef 12 ", "abcdef12")]
    [InlineData("0XDEADBEEF", "deadbeef")]
    [InlineData("ab cd\tef\r\n01", "abcdef01")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeExpected_StripsNoiseAndLowercases(string? raw, string expected)
        => Assert.Equal(expected, HashDeskLogic.NormalizeExpected(raw));

    [Theory]
    [InlineData(AbcSha256, "Sha256")]
    [InlineData(AbcSha1, "Sha1")]
    [InlineData(AbcMd5, "Md5")]
    [InlineData("abc", "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Unknown")]
    public void DetectAlgorithm_ByLengthAndHex(string input, string expected)
        => Assert.Equal(Enum.Parse<HashAlgorithmKind>(expected), HashDeskLogic.DetectAlgorithm(input));

    [Fact]
    public void Compare_MatchesCaseInsensitivelyAgainstCorrectAlgorithm()
    {
        var actual = new HashTriple(AbcSha256, AbcSha1, AbcMd5);
        Assert.Equal(new HashMatch(HashMatchState.Match, HashAlgorithmKind.Sha256), HashDeskLogic.Compare(AbcSha256.ToUpperInvariant(), actual));
        Assert.Equal(new HashMatch(HashMatchState.Match, HashAlgorithmKind.Sha1), HashDeskLogic.Compare(" 0x" + AbcSha1, actual));
        Assert.Equal(new HashMatch(HashMatchState.Match, HashAlgorithmKind.Md5), HashDeskLogic.Compare(AbcMd5, actual));
    }

    [Fact]
    public void Compare_MismatchInvalidAndEmpty()
    {
        var actual = new HashTriple(AbcSha256, AbcSha1, AbcMd5);
        Assert.Equal(HashMatchState.Mismatch, HashDeskLogic.Compare(EmptySha256, actual).State);
        Assert.Equal(HashMatchState.InvalidExpected, HashDeskLogic.Compare("not-a-hash", actual).State);
        Assert.Equal(HashMatchState.NoExpected, HashDeskLogic.Compare("   ", actual).State);
        Assert.Equal(HashMatchState.NoExpected, HashDeskLogic.Compare(null, actual).State);
    }

    [Fact]
    public void DescribeMatch_Wording()
    {
        Assert.Equal("Matches SHA1", HashDeskLogic.DescribeMatch(new HashMatch(HashMatchState.Match, HashAlgorithmKind.Sha1)));
        Assert.Equal("Does not match MD5", HashDeskLogic.DescribeMatch(new HashMatch(HashMatchState.Mismatch, HashAlgorithmKind.Md5)));
        Assert.Equal(string.Empty, HashDeskLogic.DescribeMatch(new HashMatch(HashMatchState.NoExpected, HashAlgorithmKind.Unknown)));
        Assert.Contains("64, 40, or 32", HashDeskLogic.DescribeMatch(new HashMatch(HashMatchState.InvalidExpected, HashAlgorithmKind.Unknown)));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    [InlineData(-5, "0 B")]
    public void FormatBytes_HumanReadable(long bytes, string expected)
        => Assert.Equal(expected, HashDeskLogic.FormatBytes(bytes));

    [Fact]
    public void ToHex_LowercaseAndNullSafe()
    {
        Assert.Equal("00ff10", HashDeskLogic.ToHex([0x00, 0xFF, 0x10]));
        Assert.Equal(string.Empty, HashDeskLogic.ToHex(null));
    }

    [Fact]
    public void HashResult_DerivedLabels()
    {
        var r = new HashResult { FilePath = @"C:\tmp\file.bin", SizeBytes = 2048 };
        Assert.Equal("file.bin", r.FileName);
        Assert.Equal("2 KB", r.SizeLabel);
    }

    [Fact]
    public void HashMatchIndicator_AppliesAndClears()
    {
        var indicator = new HashMatchIndicator();
        indicator.Apply(new HashMatch(HashMatchState.Mismatch, HashAlgorithmKind.Sha256));
        Assert.True(indicator.IsMismatch);
        Assert.False(indicator.IsMatch);
        Assert.Equal("Does not match SHA256", indicator.Label);

        indicator.Apply(new HashMatch(HashMatchState.InvalidExpected, HashAlgorithmKind.Unknown));
        Assert.True(indicator.IsInvalid);
        Assert.False(indicator.IsMismatch);

        indicator.Clear();
        Assert.False(indicator.IsInvalid);
        Assert.Equal(string.Empty, indicator.Label);
    }

    /// <summary>Synchronous IProgress so tests do not depend on a SynchronizationContext.</summary>
    private sealed class SyncProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
