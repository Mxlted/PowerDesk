using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.StartupPilot.Models;
using PowerDesk.Modules.StartupPilot.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Clipboard = System.Windows.Clipboard;

namespace PowerDesk.Modules.StartupPilot.ViewModels;

public sealed partial class StartupPilotViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly JsonStorageService _storage;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly IconService _icons;
    private readonly PermissionService _permissions;
    private readonly StartupScanner _scanner;
    private readonly StartupController _controller;
    private readonly string _settingsPath;

    public ObservableCollection<StartupItem> Items { get; } = new();
    public ObservableCollection<StartupHistoryEntry> History { get; } = new();
    public ICollectionView ItemsView { get; }
    public ICollectionView HistoryView { get; }

    public StartupPilotSettings Settings { get; private set; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private StartupStatusFilter _statusFilter = StartupStatusFilter.All;
    [ObservableProperty] private StartupImpactFilter _impactFilter = StartupImpactFilter.All;
    [ObservableProperty] private bool _filterRegistry = true;
    [ObservableProperty] private bool _filterStartupFolder = true;
    [ObservableProperty] private bool _filterTaskScheduler = true;
    [ObservableProperty] private bool _filterService = true;
    [ObservableProperty] private bool _showMicrosoft;
    [ObservableProperty] private bool _confirmBeforeDisable = true;
    [ObservableProperty] private DateTime? _lastScan;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _historySearchText = string.Empty;
    [ObservableProperty] private StartupItem? _selectedItem;
    [ObservableProperty] private HistoryRetention _retention = HistoryRetention.Last100;

    public int TotalCount     => Items.Count;
    public int EnabledCount   => Items.Count(i => i.Enabled);
    public int DisabledCount  => Items.Count(i => !i.Enabled);
    public int OrphanCount    => Items.Count(i => i.IsOrphaned);
    public int HighImpactCount   => Items.Count(i => i.Enabled && i.Impact == StartupImpact.High);
    public int MediumImpactCount => Items.Count(i => i.Enabled && i.Impact == StartupImpact.Medium);
    public int LowImpactCount    => Items.Count(i => i.Enabled && i.Impact == StartupImpact.Low);

    public bool IsAdmin => _permissions.IsAdministrator;

    public StartupPilotViewModel(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        IconService icons,
        PermissionService permissions)
    {
        _log = log;
        _storage = storage;
        _status = status;
        _recent = recent;
        _icons = icons;
        _permissions = permissions;
        _scanner = new StartupScanner(log, icons);
        _controller = new StartupController(log);
        _settingsPath = PathService.ModuleSettingsFile("StartupPilot");

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = ItemFilter;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(StartupItem.IsPinned), ListSortDirection.Descending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(StartupItem.Name), ListSortDirection.Ascending));

        HistoryView = CollectionViewSource.GetDefaultView(History);
        HistoryView.Filter = HistoryFilter;
        HistoryView.SortDescriptions.Add(new SortDescription(nameof(StartupHistoryEntry.Timestamp), ListSortDirection.Descending));
    }

    public async Task InitializeAsync()
    {
        Settings = await _storage.LoadAsync(_settingsPath, () => new StartupPilotSettings());
        ShowMicrosoft = Settings.ShowMicrosoftItems;
        ConfirmBeforeDisable = Settings.ConfirmBeforeDisable;
        LastScan = Settings.LastScan;
        Retention = Settings.Retention;
        History.Clear();
        foreach (var h in Settings.History) History.Add(h);

        if (Settings.AutoScanOnLaunch) await RescanAsync();
    }

    public async Task ShutdownAsync() => await SaveAsync();

    private async Task SaveAsync()
    {
        Settings.ShowMicrosoftItems = ShowMicrosoft;
        Settings.ConfirmBeforeDisable = ConfirmBeforeDisable;
        Settings.LastScan = LastScan;
        Settings.Retention = Retention;
        ApplyRetention();
        Settings.History = History.ToList();
        await _storage.SaveAsync(_settingsPath, Settings);
    }

    private void ApplyRetention()
    {
        switch (Retention)
        {
            case HistoryRetention.Last100:
                while (History.Count > 100) History.RemoveAt(History.Count - 1);
                break;
            case HistoryRetention.Last30Days:
                var cutoff = DateTime.Now.AddDays(-30);
                for (int i = History.Count - 1; i >= 0; i--)
                    if (History[i].Timestamp < cutoff) History.RemoveAt(i);
                break;
        }
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();
    partial void OnStatusFilterChanged(StartupStatusFilter value) => ItemsView.Refresh();
    partial void OnImpactFilterChanged(StartupImpactFilter value) => ItemsView.Refresh();
    partial void OnFilterRegistryChanged(bool value) => ItemsView.Refresh();
    partial void OnFilterStartupFolderChanged(bool value) => ItemsView.Refresh();
    partial void OnFilterTaskSchedulerChanged(bool value) => ItemsView.Refresh();
    partial void OnFilterServiceChanged(bool value) => ItemsView.Refresh();
    partial void OnShowMicrosoftChanged(bool value)
    {
        // re-scan to actually include/exclude Microsoft items, since the scanner filters at the source.
        _ = RescanAsync();
    }
    partial void OnHistorySearchTextChanged(string value) => HistoryView.Refresh();

    private bool ItemFilter(object obj)
    {
        if (obj is not StartupItem i) return false;
        switch (i.Source)
        {
            case StartupSource.Registry      when !FilterRegistry: return false;
            case StartupSource.StartupFolder when !FilterStartupFolder: return false;
            case StartupSource.TaskScheduler when !FilterTaskScheduler: return false;
            case StartupSource.Service       when !FilterService: return false;
        }
        if (StatusFilter == StartupStatusFilter.Enabled  && !i.Enabled) return false;
        if (StatusFilter == StartupStatusFilter.Disabled &&  i.Enabled) return false;
        if (ImpactFilter == StartupImpactFilter.High   && i.Impact != StartupImpact.High)   return false;
        if (ImpactFilter == StartupImpactFilter.Medium && i.Impact != StartupImpact.Medium) return false;
        if (ImpactFilter == StartupImpactFilter.Low    && i.Impact != StartupImpact.Low)    return false;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            if (i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                i.Publisher.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                i.CommandLine.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }
        return true;
    }

    private bool HistoryFilter(object obj)
    {
        if (obj is not StartupHistoryEntry h) return true;
        if (string.IsNullOrWhiteSpace(HistorySearchText)) return true;
        var q = HistorySearchText.Trim();
        return h.ItemName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
            || h.Source.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [RelayCommand]
    public async Task RescanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        _status.Set("Scanning startup items…", StatusKind.Info);
        try
        {
            var list = await _scanner.ScanAsync(ShowMicrosoft);
            // Merge notes/pinned from settings.
            foreach (var i in list)
            {
                var key = NoteKey(i);
                if (Settings.Notes.TryGetValue(key, out var note)) i.Note = note;
                i.IsPinned = Settings.Pinned.Contains(key);
            }
            Items.Clear();
            foreach (var i in list) Items.Add(i);
            LastScan = DateTime.Now;
            RaiseCounts();
            _status.Set($"Scan complete: {Items.Count} items.", StatusKind.Success);
            _recent.Add("StartupPilot", $"Scanned {Items.Count} startup items.");
            await SaveAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Rescan", ex);
            _status.Set("Scan failed. See logs.", StatusKind.Error);
        }
        finally { IsScanning = false; }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(DisabledCount));
        OnPropertyChanged(nameof(OrphanCount));
        OnPropertyChanged(nameof(HighImpactCount));
        OnPropertyChanged(nameof(MediumImpactCount));
        OnPropertyChanged(nameof(LowImpactCount));
    }

    private static string NoteKey(StartupItem i) => $"{i.Source}|{i.Locator}";

    [RelayCommand]
    public async Task ToggleItemAsync(StartupItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        bool target = !item.Enabled;

        if (!target && ConfirmBeforeDisable)
        {
            var ok = MessageBox.Show(
                $"Disable '{item.Name}'?\n\n{item.CommandLine}",
                "Confirm disable",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
        }

        var result = _controller.Toggle(item, target);
        if (result.NeedsElevation)
        {
            _status.Set("Administrator privileges required for that item.", StatusKind.Warning);
            return;
        }
        if (!result.Success)
        {
            _status.Set(result.Message, StatusKind.Error);
            return;
        }

        var entry = new StartupHistoryEntry
        {
            ItemName = item.Name, Source = item.Source,
            OldEnabled = item.Enabled, NewEnabled = target,
            ItemLocator = item.Locator, Scope = item.Scope,
        };
        History.Insert(0, entry);
        item.Enabled = target;
        RaiseCounts();
        _recent.Add("StartupPilot", $"{(target ? "Enabled" : "Disabled")}: {item.Name}");
        _status.Set(result.Message, StatusKind.Success);
        await SaveAsync();
    }

    [RelayCommand]
    public async Task BulkEnableAsync(System.Collections.IList? selection)
    {
        await BulkSetAsync(selection, enable: true);
    }
    [RelayCommand]
    public async Task BulkDisableAsync(System.Collections.IList? selection)
    {
        await BulkSetAsync(selection, enable: false);
    }
    private async Task BulkSetAsync(System.Collections.IList? selection, bool enable)
    {
        if (selection is null || selection.Count == 0)
        {
            _status.Set("Select rows in the table first.", StatusKind.Warning);
            return;
        }
        if (!enable && ConfirmBeforeDisable)
        {
            var ok = MessageBox.Show($"Disable {selection.Count} items?", "Confirm bulk disable",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
        }
        int changed = 0, needAdmin = 0, failed = 0;
        foreach (var s in selection)
        {
            if (s is not StartupItem item) continue;
            if (item.Enabled == enable) continue;
            var r = _controller.Toggle(item, enable);
            if (r.Success)
            {
                History.Insert(0, new StartupHistoryEntry
                {
                    ItemName = item.Name, Source = item.Source, OldEnabled = item.Enabled,
                    NewEnabled = enable, ItemLocator = item.Locator, Scope = item.Scope,
                });
                item.Enabled = enable;
                changed++;
            }
            else if (r.NeedsElevation) needAdmin++;
            else failed++;
        }
        RaiseCounts();
        _recent.Add("StartupPilot", $"Bulk {(enable ? "enable" : "disable")}: {changed} changed.");
        var msg = $"{changed} changed.";
        if (needAdmin > 0) msg += $" {needAdmin} need administrator.";
        if (failed > 0)    msg += $" {failed} failed.";
        _status.Set(msg, failed > 0 ? StatusKind.Warning : StatusKind.Success);
        await SaveAsync();
    }

    [RelayCommand]
    public async Task UndoLastAsync()
    {
        if (History.Count == 0) { _status.Set("Nothing to undo.", StatusKind.Info); return; }
        var last = History[0];
        if (last.OldEnabled == last.NewEnabled) { _status.Set("Last entry has no change.", StatusKind.Info); return; }
        var item = Items.FirstOrDefault(i => i.Locator == last.ItemLocator && i.Source == last.Source);
        if (item is null) { _status.Set("Item no longer present.", StatusKind.Warning); return; }
        var r = _controller.Toggle(item, last.OldEnabled);
        if (!r.Success) { _status.Set("Undo failed: " + r.Message, StatusKind.Error); return; }
        item.Enabled = last.OldEnabled;
        History.RemoveAt(0);
        History.Insert(0, new StartupHistoryEntry
        {
            ItemName = item.Name, Source = item.Source,
            OldEnabled = last.NewEnabled, NewEnabled = last.OldEnabled,
            ItemLocator = item.Locator, Scope = item.Scope,
        });
        RaiseCounts();
        _recent.Add("StartupPilot", $"Undo: {item.Name}");
        _status.Set("Undid last change.", StatusKind.Success);
        await SaveAsync();
    }

    [RelayCommand]
    public void OpenFileLocation(StartupItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        var path = item.TargetPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _status.Set("Target file not found.", StatusKind.Warning);
            return;
        }
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { _log.Error("Open file location", ex); }
    }

    [RelayCommand]
    public void CopyCommandLine(StartupItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        try { Clipboard.SetText(item.CommandLine); _status.Set("Command line copied.", StatusKind.Success); }
        catch (Exception ex) { _log.Error("Copy", ex); }
    }

    [RelayCommand]
    public async Task TogglePinnedAsync(StartupItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        item.IsPinned = !item.IsPinned;
        var key = NoteKey(item);
        if (item.IsPinned) Settings.Pinned.Add(key);
        else Settings.Pinned.Remove(key);
        ItemsView.Refresh();
        await SaveAsync();
    }

    public async Task SetNoteAsync(StartupItem item, string note)
    {
        item.Note = note ?? string.Empty;
        var key = NoteKey(item);
        if (string.IsNullOrWhiteSpace(item.Note)) Settings.Notes.Remove(key);
        else Settings.Notes[key] = item.Note;
        await SaveAsync();
    }

    [RelayCommand]
    public void ExportHistoryCsv()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"startuppilot-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var w = new StreamWriter(dlg.FileName);
            w.WriteLine("Timestamp,Item,Source,Scope,Old,New");
            foreach (var h in History)
                w.WriteLine($"\"{h.TimestampDisplay}\",\"{Escape(h.ItemName)}\",\"{h.Source}\",\"{Escape(h.Scope)}\",\"{(h.OldEnabled ? "Enabled" : "Disabled")}\",\"{(h.NewEnabled ? "Enabled" : "Disabled")}\"");
            _status.Set("History exported.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Export history", ex);
            _status.Set("Export failed.", StatusKind.Error);
        }
    }

    private static string Escape(string s) => (s ?? string.Empty).Replace("\"", "\"\"");

    [RelayCommand]
    public void RelaunchAsAdmin()
    {
        if (IsAdmin) { _status.Set("Already running as administrator.", StatusKind.Info); return; }
        if (!_permissions.TryRelaunchAsAdmin()) _status.Set("Elevation cancelled.", StatusKind.Warning);
        else App.Instance.Shell?.Close();
    }
}
