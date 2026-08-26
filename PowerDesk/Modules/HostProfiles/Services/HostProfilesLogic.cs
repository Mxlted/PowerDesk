using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PowerDesk.Modules.HostProfiles.Services;

internal enum HostsLineKind { Blank, Comment, Entry, Invalid }

internal sealed record HostsLine(
    int LineNumber,
    HostsLineKind Kind,
    string Raw,
    string? Address,
    IReadOnlyList<string> Hostnames,
    string? Comment);

internal sealed record HostsSummary(int Entries, int Comments, int Blank, int Invalid)
{
    public int Total => Entries + Comments + Blank + Invalid;

    public string Label
    {
        get
        {
            if (Total == 0) return "Empty";
            var parts = new List<string>(3) { $"{Entries} active entr{(Entries == 1 ? "y" : "ies")}" };
            if (Comments > 0) parts.Add($"{Comments} comment line{(Comments == 1 ? string.Empty : "s")}");
            if (Invalid > 0) parts.Add($"{Invalid} invalid line{(Invalid == 1 ? string.Empty : "s")}");
            return string.Join(" - ", parts);
        }
    }
}

/// <summary>
/// Pure hosts-file logic: parsing, normalization, decoding, and profile-name rules.
/// Nothing in here touches the real hosts file, so it is fully unit-testable.
/// </summary>
internal static class HostProfilesLogic
{
    internal const int MaxProfileNameLength = 64;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>%SystemRoot%\System32\drivers\etc\hosts resolved through the system folder.</summary>
    internal static string DefaultHostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    internal static string[] SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        return content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    internal static IReadOnlyList<HostsLine> Parse(string? content)
    {
        var lines = SplitLines(content);
        var result = new List<HostsLine>(lines.Length);
        for (var i = 0; i < lines.Length; i++) result.Add(ParseLine(i + 1, lines[i]));
        return result;
    }

    internal static HostsLine ParseLine(int lineNumber, string raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return new HostsLine(lineNumber, HostsLineKind.Blank, raw ?? string.Empty, null, [], null);
        if (trimmed[0] == '#')
            return new HostsLine(lineNumber, HostsLineKind.Comment, raw!, null, [], trimmed[1..].Trim());

        string? comment = null;
        var body = trimmed;
        var hash = trimmed.IndexOf('#');
        if (hash >= 0)
        {
            comment = trimmed[(hash + 1)..].Trim();
            body = trimmed[..hash];
        }

        var tokens = body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !IsValidAddress(tokens[0]))
            return new HostsLine(lineNumber, HostsLineKind.Invalid, raw!, tokens.Length > 0 ? tokens[0] : null, tokens.Skip(1).ToList(), comment);

        var hostnames = tokens.Skip(1).ToList();
        var kind = hostnames.All(IsValidHostname) ? HostsLineKind.Entry : HostsLineKind.Invalid;
        return new HostsLine(lineNumber, kind, raw!, tokens[0], hostnames, comment);
    }

    internal static HostsSummary Summarize(string? content)
    {
        int entries = 0, comments = 0, blank = 0, invalid = 0;
        foreach (var line in Parse(content))
        {
            switch (line.Kind)
            {
                case HostsLineKind.Entry: entries++; break;
                case HostsLineKind.Comment: comments++; break;
                case HostsLineKind.Blank: blank++; break;
                default: invalid++; break;
            }
        }
        return new HostsSummary(entries, comments, blank, invalid);
    }

    /// <summary>Strict IPv4 (four dotted decimal octets) or a parseable IPv6 literal. "1" or "1.2" are rejected.</summary>
    internal static bool IsValidAddress(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (token.Contains(':'))
            return IPAddress.TryParse(token, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;

        var parts = token.Split('.');
        if (parts.Length != 4) return false;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3) return false;
            foreach (var c in part) if (c < '0' || c > '9') return false;
            if (int.Parse(part) > 255) return false;
        }
        return true;
    }

    /// <summary>DNS-style label rules; underscores are tolerated because Windows accepts them in hosts.</summary>
    internal static bool IsValidHostname(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 253) return false;
        var labels = token.TrimEnd('.').Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            foreach (var c in label)
            {
                var ok = char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
                if (!ok) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Prepares content for the hosts file: strips a stray BOM character, converts every line ending to CRLF,
    /// drops trailing blank lines, and ends with exactly one CRLF (empty content stays empty).
    /// </summary>
    internal static string NormalizeForWrite(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var text = content.TrimStart('\uFEFF');
        var lines = SplitLines(text).ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return string.Empty;
        return string.Join("\r\n", lines) + "\r\n";
    }

    internal static bool ContentEquals(string? left, string? right)
        => string.Equals(NormalizeForWrite(left), NormalizeForWrite(right), StringComparison.Ordinal);

    /// <summary>
    /// Decodes hosts bytes honoring a UTF-8/UTF-16 BOM, then strict UTF-8, then falling back to Latin-1 so
    /// legacy ANSI files never show replacement characters or throw.
    /// </summary>
    internal static string Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Utf8NoBom.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    internal static Encoding WriteEncoding => Utf8NoBom;

    /// <summary>Returns an error message, or null when the name is acceptable.</summary>
    internal static string? ValidateProfileName(string? name, IEnumerable<string> existingNames, string? currentName = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return "Profile name cannot be empty.";
        if (trimmed.Length > MaxProfileNameLength) return $"Profile name is too long (max {MaxProfileNameLength} characters).";
        foreach (var existing in existingNames)
        {
            if (currentName is not null && string.Equals(existing, currentName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(existing?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return $"A profile named '{trimmed}' already exists.";
        }
        return null;
    }

    /// <summary>"Name", then "Name (2)", "Name (3)"... until unused (case-insensitive).</summary>
    internal static string MakeUniqueName(string? baseName, IEnumerable<string> existingNames)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "Hosts profile" : baseName.Trim();
        if (name.Length > MaxProfileNameLength) name = name[..MaxProfileNameLength].TrimEnd();
        var taken = new HashSet<string>(existingNames.Select(n => n?.Trim() ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    internal static string BackupFileName(DateTime timestamp) => $"hosts-backup-{timestamp:yyyyMMdd-HHmmss}.txt";

    /// <summary>A hosts file is a few KB; anything past this is not one and is refused on import.</summary>
    internal const long MaxImportBytes = 1024 * 1024;

    private static readonly HashSet<string> StrippableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".hosts", ".bak", ".backup", ".old", ".orig", ".conf",
    };

    /// <summary>
    /// Derives a profile name from an imported file: the file name without a generic extension
    /// ("work.hosts" → "work"), "hosts" itself becomes "Imported hosts", trimmed to the name limit.
    /// </summary>
    internal static string ProfileNameFromPath(string? path)
    {
        const string fallback = "Imported hosts";
        if (string.IsNullOrWhiteSpace(path)) return fallback;
        string name;
        try { name = Path.GetFileName(path.Trim()); }
        catch { return fallback; }
        if (StrippableExtensions.Contains(Path.GetExtension(name)))
            name = Path.GetFileNameWithoutExtension(name);
        name = name.Trim();
        if (name.Length == 0 || string.Equals(name, "hosts", StringComparison.OrdinalIgnoreCase)) return fallback;
        if (name.Length > MaxProfileNameLength) name = name[..MaxProfileNameLength].TrimEnd();
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>File name for exporting a profile: invalid characters replaced, ".txt" appended.</summary>
    internal static string ExportFileName(string? profileName)
    {
        var name = (profileName ?? string.Empty).Trim();
        if (name.Length == 0) name = "hosts";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '-' : c).ToArray();
        var safe = new string(chars).Trim('-', ' ', '.');
        if (safe.Length == 0) safe = "hosts";
        return safe + ".txt";
    }
}
