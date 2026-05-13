using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerDesk.Modules.StartupPilot.Models;

public enum StartupSource { Registry, StartupFolder, TaskScheduler, Service }
public enum StartupImpact { Low, Medium, High, Unknown }
public enum StartupStatusFilter { All, Enabled, Disabled }
public enum StartupImpactFilter { All, High, Medium, Low }
public enum ServiceStartupType { Unknown, Automatic, Manual, Disabled }

public sealed partial class StartupItem : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public StartupSource Source { get; init; }
    public string Scope { get; init; } = string.Empty; // e.g. HKCU, HKLM, AllUsers, PerUser
    public string Name { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    // Publisher/Description are not init-only: the scanner backfills them from FileVersionInfo
    // after the item is constructed if the source didn't already provide them.
    public string Publisher { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool RequiresAdmin { get; init; }

    /// <summary>Source-specific opaque locator (registry key path, .lnk path, task path, service name).</summary>
    public string Locator { get; set; } = string.Empty;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private StartupImpact _impact;
    [ObservableProperty] private bool _isOrphaned;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private BitmapSource? _icon;
    [ObservableProperty] private ServiceStartupType _serviceStartupType = ServiceStartupType.Unknown;

    public string StatusLabel => Source == StartupSource.Service ? StartupTypeLabel : (Enabled ? "Enabled" : "Disabled");

    public string StartupTypeLabel => ServiceStartupType switch
    {
        ServiceStartupType.Automatic => "Automatic",
        ServiceStartupType.Manual    => "Manual",
        ServiceStartupType.Disabled  => "Disabled",
        _                            => "Unknown",
    };

    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));
    partial void OnServiceStartupTypeChanged(ServiceStartupType value)
    {
        OnPropertyChanged(nameof(StartupTypeLabel));
        OnPropertyChanged(nameof(StatusLabel));
    }

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
    public ServiceStartupType? OldServiceStartupType { get; set; }
    public ServiceStartupType? NewServiceStartupType { get; set; }
    public string ItemLocator { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;

    public string Action => OldServiceStartupType.HasValue && NewServiceStartupType.HasValue
        ? $"{OldServiceStartupType.Value} → {NewServiceStartupType.Value}"
        : OldEnabled == NewEnabled
        ? "—"
        : (NewEnabled ? "Enabled" : "Disabled");
    public string OldValueLabel => OldServiceStartupType?.ToString() ?? (OldEnabled ? "Enabled" : "Disabled");
    public string NewValueLabel => NewServiceStartupType?.ToString() ?? (NewEnabled ? "Enabled" : "Disabled");
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
