using System;

namespace PowerDesk.Modules.FileLockFinder.Models;

public sealed class LockingProcessInfo
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string MainWindowTitle { get; init; } = string.Empty;
    public string ProcessPath { get; init; } = string.Empty;
    public bool Restartable { get; init; }
    public uint SessionId { get; init; }
    public DateTime? StartTime { get; init; }
    public DateTime? StartTimeUtc { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(AppName) ? ProcessName : AppName;
    public string StartTimeLabel => StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public string RestartableLabel => Restartable ? "Yes" : "No";
}

public sealed class FileLockScanResult
{
    public IReadOnlyList<LockingProcessInfo> Processes { get; init; } = [];
    public int ResourceCount { get; init; }
    public bool ResourceLimitReached { get; init; }
}
