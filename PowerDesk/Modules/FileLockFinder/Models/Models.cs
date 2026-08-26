using System;

namespace PowerDesk.Modules.FileLockFinder.Models;

public sealed class LockingProcessInfo
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string MainWindowTitle { get; init; } = string.Empty;
    public string ProcessPath { get; init; } = string.Empty;
    public bool Restartable { get; init; }
    public uint SessionId { get; init; }
    public DateTime? StartTime { get; init; }
    public DateTime? StartTimeUtc { get; init; }

    /// <summary>True when Restart Manager flags the owner as a critical system process, or it is a well-known one.</summary>
    public bool IsCriticalSystemProcess { get; init; }
    /// <summary>True when the lock owner is PowerDesk itself.</summary>
    public bool IsCurrentProcess { get; init; }
    public bool IsService => !string.IsNullOrWhiteSpace(ServiceName);

    public string DisplayName => string.IsNullOrWhiteSpace(AppName) ? ProcessName : AppName;
    public string StartTimeLabel => StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public string RestartableLabel => Restartable ? "Yes" : "No";
    public string KindLabel => IsCurrentProcess ? "PowerDesk" : IsCriticalSystemProcess ? "System" : IsService ? "Service" : string.Empty;
    public bool HasKindLabel => KindLabel.Length > 0;
    public bool CanStop => !IsCurrentProcess;
}

public sealed class FileLockScanResult
{
    public IReadOnlyList<LockingProcessInfo> Processes { get; init; } = [];
    public int ResourceCount { get; init; }
    public bool ResourceLimitReached { get; init; }
    public bool IsFolder { get; init; }
}
