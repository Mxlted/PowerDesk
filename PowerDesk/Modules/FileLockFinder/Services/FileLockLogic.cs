using System;
using System.Collections.Generic;
using System.Linq;
using PowerDesk.Modules.FileLockFinder.Models;

namespace PowerDesk.Modules.FileLockFinder.Services;

/// <summary>How dangerous it is to terminate a lock owner.</summary>
internal enum ProcessRisk
{
    /// <summary>Ordinary user process; a normal confirmation is enough.</summary>
    None,
    /// <summary>Windows system process; terminating it can crash or lock the session.</summary>
    Critical,
    /// <summary>The PowerDesk process itself; never terminated from here.</summary>
    Self,
}

/// <summary>
/// Pure helpers behind <see cref="RestartManagerService"/> and the view model so the
/// decision logic (risk classification, PID-reuse checks, labels) can be unit tested
/// without touching the Restart Manager.
/// </summary>
internal static class FileLockLogic
{
    /// <summary>Upper bound on files registered with the Restart Manager for a folder scan.</summary>
    internal const int MaxRegisteredResources = 512;

    /// <summary>Two process start times within this window are treated as the same process.</summary>
    internal const double StartTimeToleranceSeconds = 2;

    private static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "secure system", "registry", "memory compression", "ntoskrnl",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "sihost", "userinit", "logonui",
    };

    internal static ProcessRisk Classify(int pid, string? processName, bool restartManagerCritical, int currentPid)
    {
        if (pid == currentPid) return ProcessRisk.Self;
        if (pid <= 4 || restartManagerCritical) return ProcessRisk.Critical;
        return CriticalProcessNames.Contains(NormalizeProcessName(processName)) ? ProcessRisk.Critical : ProcessRisk.None;
    }

    /// <summary>Trims and strips a trailing ".exe" so "svchost.exe", "SVCHOST" and "svchost" compare equal.</summary>
    internal static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];
        return trimmed;
    }

    /// <summary>
    /// Compares process start times with a small tolerance. A process is only "the same" as the
    /// one we scanned when both PID and start time agree; this guards against PID reuse.
    /// Two unknown start times are considered equal (best effort); one unknown side is not.
    /// </summary>
    internal static bool SameStartTime(DateTime? left, DateTime? right, double toleranceSeconds = StartTimeToleranceSeconds)
    {
        if (left is null || right is null) return left is null && right is null;
        var l = left.Value.Kind == DateTimeKind.Utc ? left.Value : left.Value.ToUniversalTime();
        var r = right.Value.Kind == DateTimeKind.Utc ? right.Value : right.Value.ToUniversalTime();
        return Math.Abs((l - r).TotalSeconds) <= toleranceSeconds;
    }

    /// <summary>Converts the two halves of a Win32 FILETIME to a UTC DateTime; null when zero/invalid.</summary>
    internal static DateTime? FileTimeToUtc(int highDateTime, int lowDateTime)
    {
        var ticks = ((long)(uint)highDateTime << 32) | (uint)lowDateTime;
        if (ticks <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ticks); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static string BuildScopeLabel(int resourceCount, bool limited, bool isFolder)
    {
        if (resourceCount == 0)
            return isFolder ? "The folder contains no files to check." : "No resources were checked.";
        if (limited)
            return $"Checked the first {resourceCount} files only (folder scan cap). Files beyond the cap were not inspected.";
        return isFolder
            ? $"Checked {resourceCount} file(s) in the folder."
            : "Checked 1 file.";
    }

    /// <summary>Stable display ordering: friendly name, then PID so duplicates (e.g. several svchost) are deterministic.</summary>
    internal static List<LockingProcessInfo> Sort(IEnumerable<LockingProcessInfo> processes)
        => processes
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .ToList();

    /// <summary>
    /// Restart Manager can report the same process once per registered resource in edge cases;
    /// collapse duplicates by PID + start time.
    /// </summary>
    internal static List<LockingProcessInfo> Dedupe(IEnumerable<LockingProcessInfo> processes)
    {
        var result = new List<LockingProcessInfo>();
        foreach (var p in processes)
        {
            if (result.Any(existing => existing.ProcessId == p.ProcessId && SameStartTime(existing.StartTimeUtc, p.StartTimeUtc)))
                continue;
            result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Picks the first existing path from a drag-drop payload and counts how many other items were ignored,
    /// so the UI can say so instead of silently dropping them.
    /// </summary>
    internal static (string? Target, int Ignored) PickDropTarget(IEnumerable<string>? paths, Func<string, bool> exists)
    {
        if (paths is null) return (null, 0);
        string? target = null;
        var ignored = 0;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();
            if (target is null && exists(path)) target = path;
            else ignored++;
        }
        return (target, ignored);
    }

    internal static string BuildStopConfirmation(LockingProcessInfo process, ProcessRisk risk)
    {
        var who = $"{process.DisplayName} (PID {process.ProcessId})";
        return risk switch
        {
            ProcessRisk.Critical =>
                $"WARNING: {who} is a Windows system process.\n\n" +
                "Terminating it can crash Windows, log you out, or lose unsaved work in every application. " +
                "Only continue if you understand the consequences.\n\nStop it anyway?",
            _ => $"Stop {who}?\n\nUnsaved work in that application will be lost.",
        };
    }
}
