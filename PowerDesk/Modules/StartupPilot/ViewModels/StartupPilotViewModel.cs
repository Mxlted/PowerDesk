using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
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

namespace PowerDesk.Modules.StartupPilot.ViewModels;

public sealed partial class StartupPilotViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly JsonStorageService _storage;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly IconService _icons;
    private readonly PermissionService _permissions;
    private readonly IConfirmationService _confirm;
    private readonly StartupScanner _scanner;
    private readonly StartupController _controller;
    private readonly string _settingsPath;
    private bool _initializing;
    private bool _settingsLoaded;
    private bool _rescanRequested;

    public ObservableCollection<StartupItem> Items { get; } = new();
    public ObservableCollection<StartupHistoryEntry> History { get; } = new();
    public ICollectionView ItemsView { get; }
    public ICollectionView ServicesView { get; }
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isScanning;

    /// <summary>True while a toggle / service change is being applied on a worker thread.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetSelectedServiceAutomaticCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetSelectedServiceManualCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetSelectedServiceDisabledCommand))]
    private bool _isApplying;

    [ObservableProperty] private string _historySearchText = string.Empty;
    [ObservableProperty] private StartupItem? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetSelectedServiceAutomaticCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetSelectedServiceManualCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetSelectedServiceDisabledCommand))]
    private StartupItem? _selectedService;

    [ObservableProperty] private HistoryRetention _retention = HistoryRetention.Last100;

    public bool IsBusy => IsScanning || IsApplying;

    public int TotalCount     => Items.Count;
    public int StartupEntryCount => Items.Count(IsStartupEntry);
    public int EnabledCount   => Items.Count(i => IsStartupEntry(i) && i.Enabled);
    public int DisabledCount  => Items.Count(i => IsStartupEntry(i) && !i.Enabled);
    public int OrphanCount    => Items.Count(i => IsStartupEntry(i) && i.IsOrphaned);
    public int HighImpactCount   => Items.Count(i => IsStartupEntry(i) && i.Enabled && i.Impact == StartupImpact.High);
    public int MediumImpactCount => Items.Count(i => IsStartupEntry(i) && i.Enabled && i.Impact == StartupImpact.Medium);
    public int LowImpactCount    => Items.Count(i => IsStartupEntry(i) && i.Enabled && i.Impact == StartupImpact.Low);
    public int ServiceCount => Items.Count(i => i.Source == StartupSource.Service);
    public int AutomaticServiceCount => Items.Count(i => i.Source == StartupSource.Service && i.ServiceStartupType == ServiceStartupType.Automatic);
    public int ManualServiceCount => Items.Count(i => i.Source == StartupSource.Service && i.ServiceStartupType == ServiceStartupType.Manual);
    public int DisabledServiceCount => Items.Count(i => i.Source == StartupSource.Service && i.ServiceStartupType == ServiceStartupType.Disabled);

    public bool IsAdmin => _permissions.IsAdministrator;

    public StartupPilotViewModel(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        IconService icons,
        PermissionService permissions,
        IConfirmationService confirm)
    {
        _log = log;
        _storage = storage;
        _status = status;
        _recent = recent;
        _icons = icons;
        _permissions = permissions;
        _confirm = confirm;
        _scanner = new StartupScanner(log, icons);
        _controller = new StartupController(log);
        _settingsPath = PathService.ModuleSettingsFile("StartupPilot");

        ItemsView = new CollectionViewSource { Source = Items }.View;
        ItemsView.Filter = ItemFilter;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(StartupItem.IsPinned), ListSortDirection.Descending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(StartupItem.Name), ListSortDirection.Ascending));

        ServicesView = new CollectionViewSource { Source = Items }.View;
        ServicesView.Filter = ServiceFilter;
        ServicesView.SortDescriptions.Add(new SortDescription(nameof(StartupItem.IsPinned), ListSortDirection.Descending));
        ServicesView.SortDescriptions.Add(new SortDescription(nameof(StartupItem.Name), ListSortDirection.Ascending));

        HistoryView = CollectionViewSource.GetDefaultView(History);
        HistoryView.Filter = HistoryFilter;
        HistoryView.SortDescriptions.Add(new SortDescription(nameof(StartupHistoryEntry.Timestamp), ListSortDirection.Descending));
        History.CollectionChanged += (_, _) => UndoLastCommand.NotifyCanExecuteChanged();
    }

    public async Task InitializeAsync()
    {
        _initializing = true;
        try
        {
            Settings = await _storage.LoadAsync(_settingsPath, () => new StartupPilotSettings());
            Settings.Notes ??= new();
            Settings.Pinned ??= new();
            Settings.History ??= new();
            ShowMicrosoft = Settings.ShowMicrosoftItems;
            ConfirmBeforeDisable = Settings.ConfirmBeforeDisable;
            LastScan = Settings.LastScan;
            Retention = Settings.Retention;
            History.Clear();
            foreach (var h in Settings.History) History.Add(h);
            _settingsLoaded = true;
        }
        catch (Exception ex)
        {
            _log.Error("StartupPilot settings load", ex);
        }
        finally
        {
            _initializing = false;
        }

        if (Settings.AutoScanOnLaunch) await RescanAsync();
    }

    public async Task ShutdownAsync() => await SaveAsync();

    private async Task SaveAsync()
    {
        // Never overwrite the user's notes/pins/history with defaults when the load itself failed.
        if (!_settingsLoaded) return;
        Settings.ShowMicrosoftItems = ShowMicrosoft;
        Settings.ConfirmBeforeDisable = ConfirmBeforeDisable;
        Settings.LastScan = LastScan;
        Settings.Retention = Retention;
        StartupPilotLogic.ApplyRetention(History, Retention, DateTime.Now);
        Settings.History = History.ToList();
        if (!await _storage.SaveAsync(_settingsPath, Settings))
            _status.Set("StartupPilot settings could not be saved.", StatusKind.Warning);
    }

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
        ServicesView.Refresh();
    }
    partial void OnStatusFilterChanged(StartupStatusFilter value) => ItemsView.Refresh();
    partial void OnImpactFilterChanged(StartupImpactFilter value) => ItemsView.Refresh();
    partial void OnFilterRegistryChanged(bool value) => ItemsView.Refresh();
    partial void OnFilterStartupFolderChanged(bool value) => ItemsView.Refresh();
    partial void OnFilterTaskSchedulerChanged(bool value) => ItemsView.Refresh();
    partial void OnFilterServiceChanged(bool value) => ItemsView.Refresh();
    partial void OnShowMicrosoftChanged(bool value)
    {
        if (_initializing) return;
        // re-scan to actually include/exclude Microsoft items, since the scanner filters at the source.
        _ = RescanAsync();
    }
    partial void OnConfirmBeforeDisableChanged(bool value)
    {
        if (!_initializing) _ = SaveAsync();
    }
    partial void OnRetentionChanged(HistoryRetention value)
    {
        if (!_initializing) _ = SaveAsync();
    }
    partial void OnHistorySearchTextChanged(string value) => HistoryView.Refresh();

    private bool ItemFilter(object obj)
    {
        if (obj is not StartupItem i) return false;
        if (!IsStartupEntry(i)) return false;
        switch (i.Source)
        {
            case StartupSource.Registry      when !FilterRegistry: return false;
            case StartupSource.StartupFolder when !FilterStartupFolder: return false;
            case StartupSource.TaskScheduler when !FilterTaskScheduler: return false;
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

    private bool ServiceFilter(object obj)
    {
        if (obj is not StartupItem i || i.Source != StartupSource.Service) return false;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            if (i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                i.Publisher.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                i.CommandLine.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                i.Locator.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
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
        // Callers outside the UI thread (tray menu handlers, shell hooks) are marshalled so every
        // collection / property mutation below happens on the dispatcher.
        if (!UiDispatcher.IsOnUiThread && System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            Func<Task> rescan = RescanAsync;
            await dispatcher.InvokeAsync(rescan).Task.Unwrap();
            return;
        }

        // A request that arrives while a scan is running (e.g. flipping "Microsoft" mid-scan) is queued
        // rather than dropped, so the list always reflects the latest options.
        if (IsScanning) { _rescanRequested = true; return; }
        IsScanning = true;
        try
        {
            do
            {
                _rescanRequested = false;
                await RunScanOnceAsync();
            } while (_rescanRequested);
        }
        finally { IsScanning = false; }
    }

    private async Task RunScanOnceAsync()
    {
        _status.Set("Scanning startup items…", StatusKind.Info);
        try
        {
            var includeMicrosoft = ShowMicrosoft;
            var list = await _scanner.ScanAsync(includeMicrosoft);
            var isAdmin = IsAdmin;
            // Merge notes/pinned from settings.
            foreach (var i in list)
            {
                var key = NoteKey(i);
                if (Settings.Notes.TryGetValue(key, out var note)) i.Note = note;
                i.IsPinned = Settings.Pinned.Contains(key);
                i.NeedsElevation = i.RequiresAdmin && !isAdmin;
            }
            UiDispatcher.Invoke(() =>
            {
                // Keep the selection across rescans by identity (source + locator) rather than object reference.
                var selectedKey = SelectedItem is { } s ? NoteKey(s) : null;
                var selectedServiceKey = SelectedService is { } ss ? NoteKey(ss) : null;

                Items.Clear();
                foreach (var i in list) Items.Add(i);
                SelectedItem = selectedKey is null ? null : Items.FirstOrDefault(i => NoteKey(i) == selectedKey);
                SelectedService = selectedServiceKey is null ? null : Items.FirstOrDefault(i => NoteKey(i) == selectedServiceKey);
                LastScan = DateTime.Now;
                RaiseCounts();
            });
            _status.Set($"Scan complete: {StartupEntryCount} startup entries, {ServiceCount} services.", StatusKind.Success);
            _recent.Add("StartupPilot", $"Scanned {Items.Count} startup items.");
            await SaveAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Rescan", ex);
            _status.Set("Scan failed. See logs.", StatusKind.Error);
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(StartupEntryCount));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(DisabledCount));
        OnPropertyChanged(nameof(OrphanCount));
        OnPropertyChanged(nameof(HighImpactCount));
        OnPropertyChanged(nameof(MediumImpactCount));
        OnPropertyChanged(nameof(LowImpactCount));
        OnPropertyChanged(nameof(ServiceCount));
        OnPropertyChanged(nameof(AutomaticServiceCount));
        OnPropertyChanged(nameof(ManualServiceCount));
        OnPropertyChanged(nameof(DisabledServiceCount));
    }

    private static bool IsStartupEntry(StartupItem item) => item.Source != StartupSource.Service;

    private static string NoteKey(StartupItem i) => StartupPilotLogic.NoteKey(i.Source, i.Locator);

    /// <summary>
    /// Runs a controller operation on a worker thread. Registry, Task Scheduler COM and sc.exe calls can
    /// each block for seconds; keeping them off the dispatcher keeps the window responsive.
    /// </summary>
    private async Task<StartupActionResult> RunControllerAsync(Func<StartupActionResult> operation)
    {
        try { return await Task.Run(operation); }
        catch (Exception ex)
        {
            _log.Error("Startup change", ex);
            return new StartupActionResult { Success = false, Message = ex.Message };
        }
    }

    private bool TryBeginApply()
    {
        if (IsApplying)
        {
            _status.Set("Another change is still being applied.", StatusKind.Warning);
            return false;
        }
        IsApplying = true;
        return true;
    }

    private void RefreshViews()
    {
        ItemsView.Refresh();
        ServicesView.Refresh();
        RaiseCounts();
    }

    [RelayCommand]
    public async Task ToggleItemAsync(StartupItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        if (item.Source == StartupSource.Service)
        {
            var targetType = item.ServiceStartupType == ServiceStartupType.Automatic
                ? ServiceStartupType.Disabled
                : ServiceStartupType.Automatic;
            await SetServiceStartupTypeAsync(item, targetType);
            return;
        }
        bool target = !item.Enabled;

        if (item.NeedsElevation)
        {
            _status.Set("Administrator privileges are required to change that item.", StatusKind.Warning);
            return;
        }

        if (!target && ConfirmBeforeDisable)
        {
            if (!_confirm.Confirm($"Disable '{item.Name}'?\n\n{item.CommandLine}", "Confirm disable", destructive: true))
                return;
        }

        if (!TryBeginApply()) return;
        try
        {
            var oldKey = NoteKey(item);
            var result = await RunControllerAsync(() => _controller.Toggle(item, target));
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
            var updatedLocator = result.UpdatedLocator ?? item.Locator;
            MigrateItemMetadata(item, oldKey, updatedLocator);
            item.Locator = updatedLocator;

            var entry = new StartupHistoryEntry
            {
                ItemName = item.Name, Source = item.Source,
                OldEnabled = item.Enabled, NewEnabled = target,
                ItemLocator = item.Locator, Scope = item.Scope,
            };
            History.Insert(0, entry);
            item.Enabled = target;
            RefreshViews();
            _recent.Add("StartupPilot", $"{(target ? "Enabled" : "Disabled")}: {item.Name}");
            _status.Set(result.Message, StatusKind.Success);
            await SaveAsync();
        }
        finally { IsApplying = false; }
    }

    [RelayCommand]
    public async Task AddStartupEntryAsync()
    {
        string[] files;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add to Startup",
                Filter = "Programs and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd;*.ps1;*.vbs;*.msc|All files|*.*",
                Multiselect = true,
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            files = dlg.FileNames;
        }
        catch (Exception ex)
        {
            _log.Error("Add startup entry (picker)", ex);
            _status.Set("Could not open the file picker.", StatusKind.Warning);
            return;
        }
        await AddStartupEntriesAsync(files);
    }

    /// <summary>
    /// Adds dropped/picked programs to the current user's Startup folder (as shortcuts) after one
    /// confirmation, then rescans so they show up in the list. Never throws.
    /// </summary>
    public async Task AddStartupEntriesAsync(IEnumerable<string>? paths)
    {
        var (files, folders, missing) = StartupPilotLogic.PickStartupDropTargets(paths, File.Exists, Directory.Exists);
        if (files.Count == 0)
        {
            _status.Set(folders > 0
                ? "Folders cannot be launched at sign-in. Drop the program or shortcut itself."
                : "Dropped items are not files on disk.", StatusKind.Warning);
            return;
        }

        var preview = string.Join("\n", files.Take(6).Select(f => "  • " + Path.GetFileName(f)));
        if (files.Count > 6) preview += $"\n  … and {files.Count - 6} more";
        var prompt = files.Count == 1
            ? $"Add '{Path.GetFileName(files[0])}' to your Startup folder?\n\nIt will launch every time you sign in. You can disable it from this list later or delete the shortcut from the Startup folder."
            : $"Add {files.Count} items to your Startup folder?\n\n{preview}\n\nThey will launch every time you sign in. You can disable them from this list later or delete the shortcuts from the Startup folder.";
        if (!_confirm.Confirm(prompt, "Add to startup", destructive: false)) return;

        if (!TryBeginApply()) return;
        var added = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var file in files)
            {
                var result = await RunControllerAsync(() => _controller.AddStartupFolderEntry(file, allUsers: false));
                if (result.Success && result.UpdatedLocator is { } locator) added.Add(locator);
                else failed.Add($"{Path.GetFileName(file)}: {result.Message}");
            }
        }
        finally { IsApplying = false; }

        if (added.Count > 0)
        {
            _recent.Add("StartupPilot", added.Count == 1
                ? $"Added to startup: {Path.GetFileNameWithoutExtension(added[0])}"
                : $"Added {added.Count} startup entries.");
            await RescanAsync();
            var created = Items.FirstOrDefault(i => i.Source == StartupSource.StartupFolder
                && string.Equals(i.Locator, added[0], StringComparison.OrdinalIgnoreCase));
            if (created is not null) SelectedItem = created;
        }

        var summary = added.Count switch
        {
            0 => "Nothing was added.",
            1 => $"Added '{Path.GetFileNameWithoutExtension(added[0])}' to your Startup folder.",
            _ => $"Added {added.Count} entries to your Startup folder.",
        };
        if (failed.Count > 0) summary += $" Failed: {string.Join("; ", failed)}";
        if (folders > 0) summary += $" {folders} folder(s) skipped.";
        if (missing > 0) summary += $" {missing} item(s) not found.";
        _status.Set(summary, added.Count == 0 ? StatusKind.Error : failed.Count > 0 || folders > 0 || missing > 0 ? StatusKind.Warning : StatusKind.Success);
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
        var items = selection?.OfType<StartupItem>().ToList() ?? new List<StartupItem>();
        if (items.Count == 0)
        {
            _status.Set("Select rows in the table first.", StatusKind.Warning);
            return;
        }
        var skippedServices = items.RemoveAll(i => i.Source == StartupSource.Service);
        if (items.Count == 0)
        {
            _status.Set("Service items must be changed one at a time.", StatusKind.Warning);
            return;
        }
        var pending = items.Where(i => i.Enabled != enable).ToList();
        if (pending.Count == 0)
        {
            _status.Set($"All selected items are already {(enable ? "enabled" : "disabled")}.", StatusKind.Info);
            return;
        }
        if (!enable && ConfirmBeforeDisable)
        {
            if (!_confirm.Confirm($"Disable {pending.Count} item(s)?", "Confirm bulk disable", destructive: true))
                return;
        }
        if (!TryBeginApply()) return;
        int changed = 0, needAdmin = 0, failed = 0;
        try
        {
            foreach (var item in pending)
            {
                if (item.NeedsElevation) { needAdmin++; continue; }
                var oldKey = NoteKey(item);
                var r = await RunControllerAsync(() => _controller.Toggle(item, enable));
                if (r.Success)
                {
                    var updatedLocator = r.UpdatedLocator ?? item.Locator;
                    MigrateItemMetadata(item, oldKey, updatedLocator);
                    item.Locator = updatedLocator;
                    History.Insert(0, new StartupHistoryEntry
                    {
                        ItemName = item.Name, Source = item.Source, OldEnabled = item.Enabled,
                        NewEnabled = enable, ItemLocator = item.Locator, Scope = item.Scope,
                    });
                    item.Enabled = enable;
                    changed++;
                }
                else if (r.NeedsElevation) needAdmin++;
                else { failed++; _log.Warn($"Bulk {(enable ? "enable" : "disable")} '{item.Name}': {r.Message}"); }
            }
            RefreshViews();
            _recent.Add("StartupPilot", $"Bulk {(enable ? "enable" : "disable")}: {changed} changed.");
            var msg = $"{changed} changed.";
            if (needAdmin > 0) msg += $" {needAdmin} need administrator.";
            if (failed > 0)    msg += $" {failed} failed (see log).";
            if (skippedServices > 0) msg += $" {skippedServices} service item(s) skipped.";
            _status.Set(msg, failed > 0 || needAdmin > 0 || skippedServices > 0 ? StatusKind.Warning : StatusKind.Success);
            await SaveAsync();
        }
        finally { IsApplying = false; }
    }

    private bool CanChangeSelectedService() => SelectedService is not null && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanChangeSelectedService))]
    public async Task SetSelectedServiceAutomaticAsync() =>
        await SetServiceStartupTypeAsync(SelectedService, ServiceStartupType.Automatic);

    [RelayCommand(CanExecute = nameof(CanChangeSelectedService))]
    public async Task SetSelectedServiceManualAsync() =>
        await SetServiceStartupTypeAsync(SelectedService, ServiceStartupType.Manual);

    [RelayCommand(CanExecute = nameof(CanChangeSelectedService))]
    public async Task SetSelectedServiceDisabledAsync() =>
        await SetServiceStartupTypeAsync(SelectedService, ServiceStartupType.Disabled);

    public async Task SetServiceStartupTypeAsync(StartupItem? item, ServiceStartupType startupType)
    {
        if (item is null) return;
        if (item.Source != StartupSource.Service)
        {
            _status.Set("Select a service first.", StatusKind.Warning);
            return;
        }
        if (item.ServiceStartupType == startupType)
        {
            _status.Set($"Service is already {item.StartupTypeLabel}.", StatusKind.Info);
            return;
        }
        if (!IsAdmin)
        {
            _status.Set("Changing a service start type requires administrator privileges.", StatusKind.Warning);
            return;
        }
        if (startupType == ServiceStartupType.Automatic)
        {
            if (!_confirm.Confirm(
                    $"Set '{item.Name}' to Automatic?\n\nThis service can start when Windows starts.",
                    "Confirm service startup type",
                    destructive: true))
                return;
        }
        if (startupType == ServiceStartupType.Disabled && ConfirmBeforeDisable)
        {
            if (!_confirm.Confirm(
                    $"Disable service '{item.Name}'?\n\nDisabled services cannot start until their startup type is changed again.",
                    "Confirm service disable",
                    destructive: true))
                return;
        }

        if (!TryBeginApply()) return;
        try
        {
            var oldType = item.ServiceStartupType;
            var oldEnabled = item.Enabled;
            var result = await RunControllerAsync(() => _controller.SetServiceStartupType(item, startupType));
            if (result.NeedsElevation)
            {
                _status.Set("Administrator privileges required for that service.", StatusKind.Warning);
                return;
            }
            if (!result.Success)
            {
                _status.Set(result.Message, StatusKind.Error);
                return;
            }

            item.ServiceStartupType = startupType;
            item.Enabled = startupType == ServiceStartupType.Automatic;
            History.Insert(0, new StartupHistoryEntry
            {
                ItemName = item.Name,
                Source = item.Source,
                OldEnabled = oldEnabled,
                NewEnabled = item.Enabled,
                OldServiceStartupType = oldType,
                NewServiceStartupType = startupType,
                ItemLocator = item.Locator,
                Scope = item.Scope,
            });
            RefreshViews();
            _recent.Add("StartupPilot", $"Service startup type: {item.Name} → {item.StartupTypeLabel}");
            _status.Set(result.Message, StatusKind.Success);
            await SaveAsync();
        }
        finally { IsApplying = false; }
    }

    private bool CanUndoLast() => History.Count > 0 && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanUndoLast))]
    public async Task UndoLastAsync()
    {
        if (History.Count == 0) { _status.Set("Nothing to undo.", StatusKind.Info); return; }
        var last = History[0];
        if (!StartupPilotLogic.IsUndoable(last))
        {
            _status.Set("Last entry has no change.", StatusKind.Info);
            return;
        }
        var item = Items.FirstOrDefault(i => i.Locator == last.ItemLocator && i.Source == last.Source);
        if (item is null) { _status.Set("Item no longer present.", StatusKind.Warning); return; }
        if (item.NeedsElevation)
        {
            _status.Set("Undo requires administrator privileges.", StatusKind.Warning);
            return;
        }
        if (!TryBeginApply()) return;
        try
        {
            if (last.Source == StartupSource.Service && last.OldServiceStartupType.HasValue)
            {
                var restoreType = last.OldServiceStartupType.Value;
                var oldType = item.ServiceStartupType;
                var oldEnabled = item.Enabled;
                var rService = await RunControllerAsync(() => _controller.SetServiceStartupType(item, restoreType));
                if (rService.NeedsElevation) { _status.Set("Undo requires administrator privileges.", StatusKind.Warning); return; }
                if (!rService.Success) { _status.Set("Undo failed: " + rService.Message, StatusKind.Error); return; }
                item.ServiceStartupType = restoreType;
                item.Enabled = item.ServiceStartupType == ServiceStartupType.Automatic;
                History.RemoveAt(0);
                History.Insert(0, new StartupHistoryEntry
                {
                    ItemName = item.Name, Source = item.Source,
                    OldEnabled = oldEnabled, NewEnabled = item.Enabled,
                    OldServiceStartupType = oldType,
                    NewServiceStartupType = item.ServiceStartupType,
                    ItemLocator = item.Locator, Scope = item.Scope,
                });
                RefreshViews();
                _recent.Add("StartupPilot", $"Undo service startup type: {item.Name}");
                _status.Set("Undid last change.", StatusKind.Success);
                await SaveAsync();
                return;
            }
            var oldKey = NoteKey(item);
            var restoreEnabled = last.OldEnabled;
            var r = await RunControllerAsync(() => _controller.Toggle(item, restoreEnabled));
            if (r.NeedsElevation) { _status.Set("Undo requires administrator privileges.", StatusKind.Warning); return; }
            if (!r.Success) { _status.Set("Undo failed: " + r.Message, StatusKind.Error); return; }
            var updatedLocator = r.UpdatedLocator ?? item.Locator;
            MigrateItemMetadata(item, oldKey, updatedLocator);
            item.Locator = updatedLocator;
            item.Enabled = restoreEnabled;
            History.RemoveAt(0);
            History.Insert(0, new StartupHistoryEntry
            {
                ItemName = item.Name, Source = item.Source,
                OldEnabled = last.NewEnabled, NewEnabled = restoreEnabled,
                ItemLocator = item.Locator, Scope = item.Scope,
            });
            RefreshViews();
            _recent.Add("StartupPilot", $"Undo: {item.Name}");
            _status.Set("Undid last change.", StatusKind.Success);
            await SaveAsync();
        }
        finally { IsApplying = false; }
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
        catch (Exception ex)
        {
            _log.Error("Open file location", ex);
            _status.Set("Could not open Explorer.", StatusKind.Error);
        }
    }

    [RelayCommand]
    public void CopyCommandLine(StartupItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        if (ClipboardService.TrySetText(item.CommandLine)) _status.Set("Command line copied.", StatusKind.Success);
        else _status.Set("Clipboard is busy; try again.", StatusKind.Warning);
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
        ServicesView.Refresh();
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

    private void MigrateItemMetadata(StartupItem item, string oldKey, string updatedLocator)
    {
        var newKey = StartupPilotLogic.NoteKey(item.Source, updatedLocator);
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;

        var hadNote = Settings.Notes.Remove(oldKey, out var note);
        var noteToKeep = string.IsNullOrWhiteSpace(item.Note) ? note : item.Note;
        if ((hadNote || !string.IsNullOrWhiteSpace(item.Note)) && !string.IsNullOrWhiteSpace(noteToKeep))
            Settings.Notes[newKey] = noteToKeep;

        if (Settings.Pinned.Remove(oldKey) || item.IsPinned)
            Settings.Pinned.Add(newKey);
    }

    [RelayCommand]
    public void ExportHistoryCsv()
    {
        if (History.Count == 0)
        {
            _status.Set("History is empty; nothing to export.", StatusKind.Info);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"startuppilot-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // UTF-8 with BOM so Excel reads non-ASCII item names correctly.
            using var w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            w.WriteLine("Timestamp,Item,Source,Scope,Old,New");
            foreach (var h in History)
                w.WriteLine(StartupPilotLogic.CsvLine(h.TimestampDisplay, h.ItemName, h.Source.ToString(), h.Scope, h.OldValueLabel, h.NewValueLabel));
            _status.Set("History exported.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Export history", ex);
            _status.Set("Export failed.", StatusKind.Error);
        }
    }

    [RelayCommand]
    public void RelaunchAsAdmin() => _permissions.RequestElevation(_status);
}
