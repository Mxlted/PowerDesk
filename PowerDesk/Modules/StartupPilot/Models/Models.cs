using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerDesk.Modules.StartupPilot.Models;

public enum StartupSource { Registry, StartupFolder, TaskScheduler, Service }
public enum StartupImpact { Low, Medium, High, Unknown }
public enum StartupStatusFilter { All, Enabled, Disabled }
public enum StartupImpactFilter { All, High, Medium, Low }

public sealed partial class StartupItem : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public StartupSource Source { get; init; }
    public string Scope { get; init; } = string.Empty; // e.g. HKCU, HKLM, AllUsers, PerUser
    public string Name { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool RequiresAdmin { get; init; }

    /// <summary>Source-specific opaque locator (registry key path, .lnk path, task path, service name).</summary>
    public string Locator { get; init; } = string.Empty;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private StartupImpact _impact;
    [ObservableProperty] private bool _isOrphaned;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private BitmapSource? _icon;

    public string SourceLabel => Source switch
    {
        StartupSource.Registry      => "Registry",
        StartupSource.StartupFolder => "Startup Folder",
        StartupSource.TaskScheduler => "Task Scheduler",
        StartupSource.Service       => "Service",
        _ => Source.ToString(),
    };

    public string ImpactLabel => Impact switch
    {
        StartupImpact.High   => "High",
        StartupImpact.Medium => "Medium",
        StartupImpact.Low    => "Low",
        _ => "Unknown",
    };
}

public sealed class StartupHistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ItemName { get; set; } = string.Empty;
    public StartupSource Source { get; set; }
    public bool OldEnabled { get; set; }
    public bool NewEnabled { get; set; }
    public string ItemLocator { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;

    public string Action => OldEnabled == NewEnabled
        ? "—"
        : (NewEnabled ? "Enabled" : "Disabled");
    public string TimestampDisplay => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
}

public enum HistoryRetention { KeepAll, Last100, Last30Days }

public sealed class StartupPilotSettings
{
    public Dictionary<string, string> Notes { get; set; } = new();     // key: source|locator
    public HashSet<string> Pinned { get; set; } = new();               // key: source|locator
    public bool ShowMicrosoftItems { get; set; } = false;
    public bool ConfirmBeforeDisable { get; set; } = true;
    public bool AutoScanOnLaunch { get; set; } = true;
    public HistoryRetention Retention { get; set; } = HistoryRetention.Last100;
    public List<StartupHistoryEntry> History { get; set; } = new();
    public DateTime? LastScan { get; set; }
}
