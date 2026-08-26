using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using PowerDesk.Modules.StartupPilot.Models;

namespace PowerDesk.Modules.StartupPilot.Services;

/// <summary>
/// Pure, side-effect-free helpers shared by the scanner, controller and view model.
/// Nothing in here touches the registry, file system or any Windows API so it can be unit tested headlessly.
/// </summary>
internal static class StartupPilotLogic
{
    public const string RunKey       = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnceKey   = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string Run32Key     = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnce32Key = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>Explorer's per-entry enabled/disabled state (what Task Manager's Startup tab reads and writes).</summary>
    public const string StartupApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>
    /// Sub-key used to park registry values that have no StartupApproved support (RunOnce).
    /// This is the convention Sysinternals Autoruns uses, so both tools understand each other's disabled entries.
    /// </summary>
    public const string AutorunsDisabledSubkey = "AutorunsDisabled";

    private static readonly string[] ExecutableExtensions =
        { ".exe", ".com", ".bat", ".cmd", ".scr", ".msi", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".ps1", ".lnk", ".url" };

    private static readonly char[] DirectorySeparators = { '\\', '/' };

    // ---------------------------------------------------------------- StartupApproved

    /// <summary>Maps a Run key path to its StartupApproved sub-key, or null when Windows keeps no approval state for it (RunOnce).</summary>
    public static string? StartupApprovedSubkeyForRegistryPath(string keyPath)
    {
        if (keyPath.Equals(RunKey, StringComparison.OrdinalIgnoreCase)) return "Run";
        if (keyPath.Equals(Run32Key, StringComparison.OrdinalIgnoreCase)) return "Run32";
        return null;
    }

    /// <summary>
    /// Decodes a StartupApproved value. The blob is 12 bytes: a state DWORD followed by a FILETIME of when the entry
    /// was disabled. Task Manager writes 0x02 (enabled) / 0x03 (disabled); Windows itself also uses 0x06 / 0x07 with
    /// the same meaning, so the low bit is what matters. Returns null for missing or unrecognised data.
    /// </summary>
    public static bool? ParseStartupApprovedBlob(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        var state = data[0];
        if (state == 0) return null;
        return (state & 1) == 0;
    }

    /// <summary>Builds the 12-byte StartupApproved blob Task Manager writes: 0x02 + zero FILETIME when enabled, 0x03 + disable time otherwise.</summary>
    public static byte[] BuildStartupApprovedBlob(bool enable, DateTime? disabledAt = null)
    {
        var data = new byte[12];
        data[0] = enable ? (byte)0x02 : (byte)0x03;
        if (!enable)
        {
            var when = disabledAt ?? DateTime.UtcNow;
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), when.ToFileTimeUtc());
        }
        return data;
    }

    // ---------------------------------------------------------------- Registry locators

    public static string FormatRegistryLocator(RegistryHive hive, string keyPath, string valueName) =>
        $"{hive}|{keyPath}|{valueName}";

    /// <summary>Parses "{Hive}|{keyPath}|{valueName}". The value name may itself contain '|'.</summary>
    public static bool TryParseRegistryLocator(string? locator, out RegistryHive hive, out string keyPath, out string valueName)
    {
        hive = RegistryHive.CurrentUser;
        keyPath = valueName = string.Empty;
        if (string.IsNullOrEmpty(locator)) return false;
        var parts = locator.Split('|', 3);
        if (parts.Length != 3) return false;
        if (!Enum.TryParse(parts[0], ignoreCase: false, out hive)) return false;
        if (parts[1].Length == 0 || parts[2].Length == 0) return false;
        keyPath = parts[1];
        valueName = parts[2];
        return true;
    }

    public static string AutorunsDisabledKeyFor(string keyPath) => keyPath + "\\" + AutorunsDisabledSubkey;

    public static bool IsAutorunsDisabledKey(string keyPath) =>
        keyPath.EndsWith("\\" + AutorunsDisabledSubkey, StringComparison.OrdinalIgnoreCase);

    public static string ParentOfAutorunsDisabledKey(string keyPath) =>
        IsAutorunsDisabledKey(keyPath) ? keyPath[..^(AutorunsDisabledSubkey.Length + 1)] : keyPath;

    public static bool IsRunOnceKey(string keyPath) =>
        keyPath.Equals(RunOnceKey, StringComparison.OrdinalIgnoreCase) ||
        keyPath.Equals(RunOnce32Key, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Command-line parsing

    /// <summary>
    /// Extracts the executable (or, for rundll32 hosts, the hosted DLL) from an autorun command line.
    /// Handles quoted paths, unquoted "Program Files" paths, environment variables, trailing arguments
    /// and bare host names (rundll32.exe, cmd.exe) which are resolved through <paramref name="searchDirs"/>.
    /// </summary>
    public static string ExtractExecutablePath(string? command, Func<string, bool> fileExists, IReadOnlyList<string> searchDirs)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        var s = Environment.ExpandEnvironmentVariables(command.Trim());
        var (head, rest) = SplitHead(s);
        if (head.Length == 0) return string.Empty;

        if (IsFileNamed(head, "rundll32.exe") || IsFileNamed(head, "rundll32"))
        {
            var dll = ParseRundll32Target(rest);
            if (dll.Length > 0) head = Environment.ExpandEnvironmentVariables(dll);
        }

        return ResolveBareExecutable(head, fileExists, searchDirs);
    }

    /// <summary>Splits a command line into the program token and the remaining arguments.</summary>
    internal static (string Head, string Args) SplitHead(string command)
    {
        var s = command.TrimStart();
        if (s.Length == 0) return (string.Empty, string.Empty);

        if (s[0] == '"')
        {
            int close = s.IndexOf('"', 1);
            if (close < 0) return (s[1..].Trim(), string.Empty);          // unterminated quote: take the rest
            return (s[1..close].Trim(), s[(close + 1)..].Trim());
        }

        // Unquoted: many entries omit quotes around paths containing spaces, so prefer the earliest
        // executable-looking prefix that ends on a token boundary (space, comma, quote or end of string).
        int bestEnd = -1;
        foreach (var ext in ExecutableExtensions)
        {
            int idx = 0;
            while ((idx = s.IndexOf(ext, idx, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                int after = idx + ext.Length;
                bool boundary = after == s.Length || char.IsWhiteSpace(s[after]) || s[after] == ',' || s[after] == '"';
                if (boundary)
                {
                    if (bestEnd < 0 || after < bestEnd) bestEnd = after;
                    break;
                }
                idx = after;
            }
        }
        if (bestEnd > 0) return (s[..bestEnd].Trim(), s[bestEnd..].TrimStart(',', ' ', '\t'));

        int sp = s.IndexOfAny(new[] { ' ', '\t' });
        return sp > 0 ? (s[..sp], s[(sp + 1)..].Trim()) : (s, string.Empty);
    }

    /// <summary>rundll32 takes the DLL as the first argument: quoted, or everything up to the first comma/space.</summary>
    internal static string ParseRundll32Target(string rest)
    {
        var r = rest.Trim();
        if (r.Length == 0) return string.Empty;
        if (r[0] == '"')
        {
            int close = r.IndexOf('"', 1);
            return (close < 0 ? r[1..] : r[1..close]).Trim();
        }
        int end = r.IndexOfAny(new[] { ',', ' ', '\t' });
        return (end < 0 ? r : r[..end]).Trim();
    }

    /// <summary>A program given without a directory ("rundll32.exe", "cmd") is looked up in the given search directories.</summary>
    internal static string ResolveBareExecutable(string head, Func<string, bool> fileExists, IReadOnlyList<string> searchDirs)
    {
        if (head.Length == 0 || head.IndexOfAny(DirectorySeparators) >= 0) return head;
        var candidates = Path.HasExtension(head) ? new[] { head } : new[] { head + ".exe", head };
        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var c in candidates)
            {
                string full;
                try { full = Path.Combine(dir.Trim(), c); } catch { continue; }
                if (fileExists(full)) return full;
            }
        }
        return head;
    }

    private static bool IsFileNamed(string path, string fileName)
    {
        var name = path.AsSpan();
        int cut = path.LastIndexOfAny(DirectorySeparators);
        if (cut >= 0) name = name[(cut + 1)..];
        return name.Equals(fileName, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- Classification

    public static bool LooksLikeMicrosoft(string? commandOrPath, string? name)
    {
        var s = (commandOrPath ?? string.Empty).ToLowerInvariant();
        if (s.Contains(@"\windows\system32") || s.Contains(@"\windows\syswow64") || s.Contains(@"\windowsapps\microsoft")
            || s.Contains(@"\microsoft\edge") || s.Contains(@"\microsoft\onedrive"))
            return true;
        var lname = (name ?? string.Empty).ToLowerInvariant();
        return lname.StartsWith("microsoft ") || lname.StartsWith("windows ");
    }

    public static StartupImpact ImpactFromFileSize(long bytes) => bytes switch
    {
        > 50L * 1024 * 1024 => StartupImpact.High,
        > 10L * 1024 * 1024 => StartupImpact.Medium,
        _                   => StartupImpact.Low,
    };

    /// <summary>Maps the SCM "Start" registry DWORD to a startup type. Boot/system drivers (0/1) are reported as Unknown.</summary>
    public static ServiceStartupType ServiceStartupTypeFromStartValue(int start) => start switch
    {
        2 => ServiceStartupType.Automatic,
        3 => ServiceStartupType.Manual,
        4 => ServiceStartupType.Disabled,
        _ => ServiceStartupType.Unknown,
    };

    public static string NoteKey(StartupSource source, string locator) => $"{source}|{locator}";

    // ---------------------------------------------------------------- Adding startup-folder entries

    /// <summary>
    /// Files Explorer can launch directly from the Startup folder without wrapping them in a shortcut.
    /// Everything else gets a .lnk pointing at it.
    /// </summary>
    public static bool IsShortcutLike(string? path)
    {
        var ext = Path.GetExtension(path ?? string.Empty);
        return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// File name to create in the Startup folder for a target: shortcuts keep their own name and
    /// extension, anything else becomes "&lt;name&gt;.lnk". Characters Windows forbids in names are replaced.
    /// </summary>
    public static string StartupEntryFileName(string targetPath)
    {
        var name = Path.GetFileNameWithoutExtension(targetPath ?? string.Empty).Trim();
        if (name.Length == 0) name = "Startup item";
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim('.', ' ', '-');
        if (name.Length == 0) name = "Startup item";
        var ext = IsShortcutLike(targetPath) ? Path.GetExtension(targetPath!) : ".lnk";
        return name + ext;
    }

    /// <summary>
    /// Filters a drag-drop payload down to files that can become startup entries. Folders cannot
    /// be launched at sign-in and are counted separately from paths that do not exist.
    /// </summary>
    public static (List<string> Files, int Folders, int Missing) PickStartupDropTargets(
        IEnumerable<string>? paths, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int folders = 0, missing = 0;
        foreach (var raw in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();
            if (fileExists(path)) { if (seen.Add(path)) files.Add(path); }
            else if (directoryExists(path)) folders++;
            else missing++;
        }
        return (files, folders, missing);
    }

    // ---------------------------------------------------------------- History

    /// <summary>Trims history in place. Entries are kept newest-first; retention keeps the most recent entries regardless of list order.</summary>
    public static void ApplyRetention(IList<StartupHistoryEntry> history, HistoryRetention retention, DateTime now)
    {
        switch (retention)
        {
            case HistoryRetention.Last100:
                if (history.Count <= 100) return;
                var keep = new HashSet<StartupHistoryEntry>(history.OrderByDescending(h => h.Timestamp).Take(100));
                for (int i = history.Count - 1; i >= 0; i--)
                    if (!keep.Contains(history[i])) history.RemoveAt(i);
                break;
            case HistoryRetention.Last30Days:
                var cutoff = now.AddDays(-30);
                for (int i = history.Count - 1; i >= 0; i--)
                    if (history[i].Timestamp < cutoff) history.RemoveAt(i);
                break;
        }
    }

    /// <summary>True when the entry records an actual state change that can be undone.</summary>
    public static bool IsUndoable(StartupHistoryEntry entry)
    {
        if (entry.OldServiceStartupType.HasValue && entry.NewServiceStartupType.HasValue)
            return entry.OldServiceStartupType.Value != entry.NewServiceStartupType.Value;
        return entry.OldEnabled != entry.NewEnabled;
    }

    // ---------------------------------------------------------------- CSV

    /// <summary>Quotes a CSV field, doubles embedded quotes, and neutralises leading formula characters so spreadsheets don't execute them.</summary>
    public static string CsvField(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public static string CsvLine(params string?[] fields) => string.Join(",", fields.Select(CsvField));
}
