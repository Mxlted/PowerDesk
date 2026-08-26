using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PowerDesk.Modules.HashDesk.Services;

internal enum HashAlgorithmKind { Unknown, Sha256, Sha1, Md5 }

internal enum HashMatchState
{
    /// <summary>No expected value entered; nothing to compare.</summary>
    NoExpected,
    /// <summary>Expected value is not a SHA256/SHA1/MD5 hex digest.</summary>
    InvalidExpected,
    Match,
    Mismatch,
}

internal readonly record struct HashTriple(string Sha256, string Sha1, string Md5);

internal readonly record struct HashMatch(HashMatchState State, HashAlgorithmKind Algorithm);

/// <summary>
/// Pure hashing and comparison helpers so the view model only does orchestration and UI marshalling.
/// </summary>
internal static class HashDeskLogic
{
    internal const int BufferSize = 128 * 1024;

    /// <summary>Minimum change in completed fraction before another progress report is emitted.</summary>
    internal const double ProgressStep = 0.005;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Streams a file once through SHA256, SHA1 and MD5 simultaneously. Honors cancellation between
    /// buffers and reports throttled fractional progress when the total length is known.
    /// </summary>
    internal static HashTriple ComputeStream(Stream stream, long? totalLength, CancellationToken cancellationToken, IProgress<double>? progress = null, int bufferSize = BufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        cancellationToken.ThrowIfCancellationRequested();

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        var buffer = new byte[bufferSize];
        long done = 0;
        var lastReported = -1.0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha256.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            md5.AppendData(buffer, 0, read);
            done += read;

            if (progress is not null && totalLength is > 0)
            {
                var fraction = Math.Min(1.0, (double)done / totalLength.Value);
                if (fraction - lastReported >= ProgressStep)
                {
                    progress.Report(fraction);
                    lastReported = fraction;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (progress is not null && lastReported < 1.0) progress.Report(1.0);

        return new HashTriple(
            ToHex(sha256.GetHashAndReset()),
            ToHex(sha1.GetHashAndReset()),
            ToHex(md5.GetHashAndReset()));
    }

    /// <summary>Hashes text as UTF-8 without a byte-order mark, so results match `echo -n | sha256sum`.</summary>
    internal static HashTriple ComputeText(string? text)
    {
        var bytes = Utf8NoBom.GetBytes(text ?? string.Empty);
        return new HashTriple(
            ToHex(SHA256.HashData(bytes)),
            ToHex(SHA1.HashData(bytes)),
            ToHex(MD5.HashData(bytes)));
    }

    /// <summary>
    /// Normalizes a pasted digest: trims, drops internal whitespace and common separators (":" and "-"),
    /// strips a leading "0x", and lower-cases. Returns an empty string for null/blank input.
    /// </summary>
    internal static string NormalizeExpected(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c) || c == ':' || c == '-') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        var text = sb.ToString();
        if (text.StartsWith("0x", StringComparison.Ordinal)) text = text[2..];
        return text;
    }

    internal static bool IsHex(string text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    internal static HashAlgorithmKind DetectAlgorithm(string normalized)
    {
        if (!IsHex(normalized)) return HashAlgorithmKind.Unknown;
        return normalized.Length switch
        {
            64 => HashAlgorithmKind.Sha256,
            40 => HashAlgorithmKind.Sha1,
            32 => HashAlgorithmKind.Md5,
            _ => HashAlgorithmKind.Unknown,
        };
    }

    internal static HashMatch Compare(string? expectedRaw, HashTriple actual)
    {
        var expected = NormalizeExpected(expectedRaw);
        if (expected.Length == 0) return new HashMatch(HashMatchState.NoExpected, HashAlgorithmKind.Unknown);

        var algorithm = DetectAlgorithm(expected);
        if (algorithm == HashAlgorithmKind.Unknown) return new HashMatch(HashMatchState.InvalidExpected, HashAlgorithmKind.Unknown);

        var candidate = algorithm switch
        {
            HashAlgorithmKind.Sha256 => actual.Sha256,
            HashAlgorithmKind.Sha1 => actual.Sha1,
            _ => actual.Md5,
        };
        var matches = string.Equals(expected, candidate, StringComparison.OrdinalIgnoreCase);
        return new HashMatch(matches ? HashMatchState.Match : HashMatchState.Mismatch, algorithm);
    }

    internal static string DescribeMatch(HashMatch match) => match.State switch
    {
        HashMatchState.NoExpected => string.Empty,
        HashMatchState.InvalidExpected => "Expected value is not a SHA256, SHA1, or MD5 digest (needs 64, 40, or 32 hex characters).",
        HashMatchState.Match => $"Matches {AlgorithmLabel(match.Algorithm)}",
        HashMatchState.Mismatch => $"Does not match {AlgorithmLabel(match.Algorithm)}",
        _ => string.Empty,
    };

    internal static string AlgorithmLabel(HashAlgorithmKind kind) => kind switch
    {
        HashAlgorithmKind.Sha256 => "SHA256",
        HashAlgorithmKind.Sha1 => "SHA1",
        HashAlgorithmKind.Md5 => "MD5",
        _ => "unknown",
    };

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{bytes} B" : $"{value:0.##} {units[index]}";
    }

    internal static string ToHex(byte[]? bytes) => bytes is null ? string.Empty : Convert.ToHexString(bytes).ToLowerInvariant();
}
