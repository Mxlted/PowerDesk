using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PowerDesk.Modules.PathEditor.Services;

/// <summary>
/// Pure PATH string handling (split/join/normalise/duplicates) shared by the view model and tests.
/// </summary>
internal static class PathLogic
{
    public const char Separator = ';';
    public const int MaxBackups = 20;

    /// <summary>
    /// Splits a PATH value on ';' while honouring double-quoted segments, so
    /// <c>"C:\Odd;Name";C:\Tools</c> yields two entries. Entries are trimmed and empties dropped;
    /// quotes are preserved on the entry so they round-trip through <see cref="Join"/>.
    /// </summary>
    public static List<string> Split(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(raw)) return result;

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in raw)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
                continue;
            }
            if (c == Separator && !inQuotes)
            {
                Flush();
                continue;
            }
            current.Append(c);
        }
        Flush();
        return result;

        void Flush()
        {
            var entry = current.ToString().Trim();
            if (entry.Length > 0) result.Add(entry);
            current.Clear();
        }
    }

    /// <summary>Joins entries back into a PATH string, quoting any entry that itself contains ';'.</summary>
    public static string Join(IEnumerable<string?> entries) =>
        string.Join(Separator, entries
            .Select(e => (e ?? string.Empty).Trim())
            .Where(e => e.Length > 0)
            .Select(QuoteIfNeeded));

    public static string QuoteIfNeeded(string entry)
    {
        if (!entry.Contains(Separator)) return entry;
        return IsQuoted(entry) ? entry : $"\"{entry}\"";
    }

    public static bool IsQuoted(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"';

    public static string StripQuotes(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return IsQuoted(trimmed) ? trimmed[1..^1].Trim() : trimmed;
    }

    /// <summary>
    /// Canonical form used for duplicate detection: quotes removed, %VARS% expanded, forward
    /// slashes folded, path made absolute and trailing separators dropped (drive roots keep theirs).
    /// Comparison must be case-insensitive (see <see cref="Comparer"/>).
    /// </summary>
    public static string NormalizeForCompare(string? value)
    {
        var trimmed = StripQuotes(value);
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;

        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(trimmed); }
        catch { expanded = trimmed; }
        expanded = TrimTrailingSeparators(expanded.Replace('/', '\\'));

        string full;
        try { full = Path.GetFullPath(expanded); }
        catch { full = expanded; }
        return TrimTrailingSeparators(full);
    }

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Removes trailing slashes but keeps a bare drive root ("C:\") intact.</summary>
    public static string TrimTrailingSeparators(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':') return trimmed + '\\';
        if (trimmed.Length == 0) return path[..1];
        return trimmed;
    }

    /// <summary>
    /// Flags every entry that repeats an earlier one (case-insensitive, normalised). The first
    /// occurrence is never flagged; blank entries are never flagged.
    /// </summary>
    public static bool[] FlagDuplicates(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(Comparer);
        return values
            .Select(v => NormalizeForCompare(v))
            .Select(n => n.Length > 0 && !seen.Add(n))
            .ToArray();
    }

    /// <summary>True when the entry names an existing directory after expanding %VARS%.</summary>
    public static bool EntryExists(string? value)
    {
        var trimmed = StripQuotes(value);
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        try
        {
            return Directory.Exists(Environment.ExpandEnvironmentVariables(trimmed));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Maps a drag-drop payload to folders that can become PATH entries: folders are taken as-is,
    /// files contribute their parent folder (dropping an .exe is the common "add this tool" gesture),
    /// and anything that does not exist is counted as ignored. Duplicates within the payload are
    /// collapsed (case-insensitive, normalised); first occurrence wins.
    /// </summary>
    public static (List<string> Folders, int Ignored) ResolveDropFolders(
        IEnumerable<string>? paths, Func<string, bool> directoryExists, Func<string, bool> fileExists)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(Comparer);
        var ignored = 0;
        foreach (var raw in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();
            string? folder = null;
            if (directoryExists(path)) folder = path;
            else if (fileExists(path))
            {
                try { folder = Path.GetDirectoryName(path); } catch { folder = null; }
            }

            if (string.IsNullOrWhiteSpace(folder)) { ignored++; continue; }
            folder = TrimTrailingSeparators(folder);
            if (seen.Add(NormalizeForCompare(folder))) folders.Add(folder);
        }
        return (folders, ignored);
    }

    /// <summary>
    /// Rejects entries that could never be a folder path (control characters, '|', '<', '>', or an
    /// unbalanced quote). Returns null when the entry is acceptable.
    /// </summary>
    public static string? ValidateNewEntry(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Enter a folder path first.";
        if (trimmed.Count(c => c == '"') % 2 != 0) return "The path has an unbalanced quote.";
        var inner = StripQuotes(trimmed);
        if (inner.Length == 0) return "Enter a folder path first.";
        var bad = inner.Where(c => c == '"' || c == '<' || c == '>' || c == '|' || char.IsControl(c)).ToList();
        if (bad.Count > 0) return $"The path contains characters that are not allowed: {string.Join(" ", bad.Distinct().Select(c => char.IsControl(c) ? "control" : c.ToString()))}.";
        return null;
    }
}
