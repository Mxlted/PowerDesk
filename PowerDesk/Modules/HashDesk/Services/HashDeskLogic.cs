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

    // ---------------------------------------------------------------- drag-drop payloads

    /// <summary>Upper bound on files taken from a single dropped folder so a drop of C:\ cannot run away.</summary>
    internal const int MaxFolderFiles = 500;

    /// <summary>Checksum sidecar extensions whose content is an expected digest rather than data to hash.</summary>
    private static readonly HashSet<string> ChecksumExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sha256", ".sha1", ".md5", ".sha256sum", ".sha1sum", ".md5sum", ".checksum", ".hash", ".digest",
    };

    internal static bool IsChecksumFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && ChecksumExtensions.Contains(Path.GetExtension(path));

    internal sealed record DropExpansion(List<string> Files, List<string> ChecksumFiles, int FoldersExpanded, bool Truncated, int Ignored);

    /// <summary>
    /// Turns a raw drop payload into hashable files. Folders contribute their top-level files (not
    /// recursive, capped at <see cref="MaxFolderFiles"/> per folder); checksum sidecars are split out
    /// so the caller can load them as the expected digest instead of hashing them; anything that does
    /// not exist is counted as ignored. Duplicates are removed case-insensitively, first occurrence wins.
    /// </summary>
    internal static DropExpansion ExpandDropPaths(
        IEnumerable<string>? paths,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateFiles,
        int maxFolderFiles = MaxFolderFiles)
    {
        var files = new List<string>();
        var checksums = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = 0;
        var truncated = false;
        var ignored = 0;

        foreach (var raw in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();
            if (fileExists(path))
            {
                Add(path);
            }
            else if (directoryExists(path))
            {
                folders++;
                var taken = 0;
                IEnumerable<string> children;
                try { children = enumerateFiles(path); }
                catch { ignored++; continue; }
                foreach (var child in children)
                {
                    if (taken >= maxFolderFiles) { truncated = true; break; }
                    if (string.IsNullOrWhiteSpace(child)) continue;
                    if (Add(child)) taken++;
                }
            }
            else
            {
                ignored++;
            }
        }

        return new DropExpansion(files, checksums, folders, truncated, ignored);

        bool Add(string file)
        {
            if (!seen.Add(file)) return false;
            if (IsChecksumFile(file)) checksums.Add(file);
            else files.Add(file);
            return true;
        }
    }

    internal sealed record ChecksumEntry(string Digest, string? FileName);

    /// <summary>
    /// Reads the first digest from a checksum file in the common formats:
    /// <c>DIGEST  file</c>, <c>DIGEST *file</c> (sha256sum), <c>file: DIGEST</c>, or a bare digest.
    /// Only 32/40/64-character hex digests are accepted; comment and blank lines are skipped.
    /// </summary>
    internal static ChecksumEntry? ParseChecksumFile(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

            // "file: DIGEST" / "SHA256 (file) = DIGEST" style: digest is the last token.
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            var first = NormalizeExpected(tokens[0]);
            if (DetectAlgorithm(first) != HashAlgorithmKind.Unknown)
            {
                var name = tokens.Length > 1 ? line[tokens[0].Length..].Trim().TrimStart('*').Trim() : null;
                return new ChecksumEntry(first, string.IsNullOrWhiteSpace(name) ? null : name);
            }

            var last = NormalizeExpected(tokens[^1]);
            if (DetectAlgorithm(last) != HashAlgorithmKind.Unknown)
            {
                var head = line[..^tokens[^1].Length].Trim().TrimEnd('=', ':').Trim();
                if (head.StartsWith("SHA", StringComparison.OrdinalIgnoreCase) || head.StartsWith("MD5", StringComparison.OrdinalIgnoreCase))
                {
                    var open = head.IndexOf('(');
                    var close = head.LastIndexOf(')');
                    head = open >= 0 && close > open ? head[(open + 1)..close].Trim() : string.Empty;
                }
                return new ChecksumEntry(last, string.IsNullOrWhiteSpace(head) ? null : head);
            }
        }
        return null;
    }
}
