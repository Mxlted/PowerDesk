using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.PathEditor.Models;
using PowerDesk.Modules.PathEditor.Services;
using Clipboard = System.Windows.Clipboard;
using DialogResult = System.Windows.Forms.DialogResult;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;

namespace PowerDesk.Modules.PathEditor.ViewModels;

public sealed partial class PathEditorViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly JsonStorageService _storage;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly PermissionService _permissions;
    private readonly IConfirmationService _confirm;
    private readonly IPathStore _store;
    private readonly string _settingsPath;
    private PathEditorSettings _settings = new();
    private bool _loading;
    private bool _suppressScopeHandler;
    private bool _settingsDirty;
    private int _validationVersion;

    public ObservableCollection<PathEntry> Entries { get; } = new();
    public ObservableCollection<PathBackup> Backups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdminHint))]
    [NotifyCanExecuteChangedFor(nameof(SavePathCommand))]
    private PathScope _selectedScope = PathScope.User;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedDownCommand))]
    private PathEntry? _selectedEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddEntryCommand))]
    private string _newEntry = string.Empty;

    [ObservableProperty] private DateTime? _lastLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ValidateEntriesCommand))]
    private bool _isValidating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(SavePathCommand))]
    private bool _isSaving;

    [ObservableProperty] private bool _isDirty;

    public bool IsAdmin => _permissions.IsAdministrator;
    public bool ShowAdminHint => SelectedScope == PathScope.Machine && !IsAdmin;
    public bool IsBusy => IsValidating || IsSaving;
    public bool HasEntries => Entries.Count > 0;
    public bool HasBackups => Backups.Count > 0;
    public int EntryCount => Entries.Count;
    public int MissingCount => Entries.Count(e => e.IsMissing);
    public int DuplicateCount => Entries.Count(e => e.IsDuplicate);
    public string RawPath => PathLogic.Join(Entries.Select(e => e.Value));

    public PathEditorViewModel(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        PermissionService permissions,
        IConfirmationService confirm)
        : this(log, storage, status, recent, permissions, confirm, new RegistryPathStore())
    {
    }

    internal PathEditorViewModel(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        PermissionService permissions,
        IConfirmationService confirm,
        IPathStore store,
        string? settingsPath = null)
    {
        _log = log;
        _storage = storage;
        _status = status;
        _recent = recent;
        _permissions = permissions;
        _confirm = confirm;
        _store = store;
        _settingsPath = settingsPath ?? PathService.ModuleSettingsFile("PathEditor");
    }

    public async Task InitializeAsync()
    {
        try
        {
            _settings = await _storage.LoadAsync(_settingsPath, () => new PathEditorSettings());
        }
        catch (Exception ex)
        {
            _log.Error("PathEditor settings load", ex);
            _settings = new PathEditorSettings();
        }
        UiDispatcher.Invoke(() =>
        {
            Backups.Clear();
            foreach (var backup in _settings.Backups.OrderByDescending(b => b.Timestamp)) Backups.Add(backup);
            OnPropertyChanged(nameof(HasBackups));
            LoadPathCore();
        });
    }

    public async Task ShutdownAsync()
    {
        if (_settingsDirty) await SaveSettingsAsync();
    }

    partial void OnSelectedScopeChanged(PathScope oldValue, PathScope newValue)
    {
        if (_loading || _suppressScopeHandler) return;
        if (IsDirty && !_confirm.Confirm($"Discard unsaved changes to the {oldValue} PATH and switch to {newValue}?", "Switch scope", destructive: true))
        {
            // Revert after the binding finishes its own update, otherwise the ComboBox keeps the rejected value.
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                _suppressScopeHandler = true;
                try { SelectedScope = oldValue; }
                finally { _suppressScopeHandler = false; }
            }), DispatcherPriority.Background);
            return;
        }
        LoadPathCore();
    }

    [RelayCommand]
    private void LoadPath()
    {
        if (IsDirty && !_confirm.Confirm($"Discard unsaved changes and reload the {SelectedScope} PATH?", "Reload PATH", destructive: true))
            return;
        LoadPathCore();
    }

    private void LoadPathCore()
    {
        try
        {
            _loading = true;
            var value = _store.Read(SelectedScope);
            ReplaceEntries(PathLogic.Split(value));
            LastLoaded = DateTime.Now;
            IsDirty = false;
            _status.Set($"{SelectedScope} PATH loaded ({Entries.Count} entries).", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Load PATH", ex);
            _status.Set($"Could not load {SelectedScope} PATH: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            _loading = false;
        }
        _ = ValidateEntriesAsync();
    }

    private bool CanAddEntry() => !string.IsNullOrWhiteSpace(NewEntry);

    [RelayCommand(CanExecute = nameof(CanAddEntry))]
    private void AddEntry()
    {
        var value = NewEntry.Trim();
        var problem = PathLogic.ValidateNewEntry(value);
        if (problem is not null)
        {
            _status.Set(problem, StatusKind.Warning);
            return;
        }
        var entry = new PathEntry { Value = value };
        AddTrackedEntry(entry);
        SelectedEntry = entry;
        NewEntry = string.Empty;
        MarkDirty();
        ValidateDuplicates();
        _status.Set(entry.IsDuplicate
            ? $"Added '{value}' (already present in this PATH)."
            : $"Added '{value}'. Press Save to apply.", entry.IsDuplicate ? StatusKind.Warning : StatusKind.Info);
        _ = ValidateSingleAsync(entry);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        try
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select a PATH folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (dlg.ShowDialog() == DialogResult.OK) NewEntry = dlg.SelectedPath;
        }
        catch (Exception ex)
        {
            _log.Error("Browse folder", ex);
            _status.Set("The folder picker could not be opened.", StatusKind.Warning);
        }
    }

    private bool HasSelection() => SelectedEntry is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelectedEntry()
    {
        if (SelectedEntry is null) return;
        var index = Entries.IndexOf(SelectedEntry);
        if (index < 0) return;
        SelectedEntry.PropertyChanged -= OnEntryPropertyChanged;
        Entries.RemoveAt(index);
        SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Clamp(index, 0, Entries.Count - 1)];
        MarkDirty();
        ValidateDuplicates();
        RefreshEntryCommands();
    }

    private bool CanMoveUp() => SelectedEntry is not null && Entries.IndexOf(SelectedEntry) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveSelectedUp()
    {
        var entry = SelectedEntry;
        if (entry is null) return;
        var index = Entries.IndexOf(entry);
        if (index <= 0) return;
        Entries.Move(index, index - 1);
        SelectedEntry = entry;
        MarkDirty();
        ValidateDuplicates();
        RefreshEntryCommands();
    }

    private bool CanMoveDown() => SelectedEntry is not null && Entries.IndexOf(SelectedEntry) is var i && i >= 0 && i < Entries.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveSelectedDown()
    {
        var entry = SelectedEntry;
        if (entry is null) return;
        var index = Entries.IndexOf(entry);
        if (index < 0 || index >= Entries.Count - 1) return;
        Entries.Move(index, index + 1);
        SelectedEntry = entry;
        MarkDirty();
        ValidateDuplicates();
        RefreshEntryCommands();
    }

    private bool CanValidate() => !IsValidating;

    /// <summary>
    /// Checks every entry's folder off the UI thread. Results are written back to the exact
    /// PathEntry objects that were snapshotted, so edits made while the check runs cannot
    /// mis-align rows; a newer validation supersedes an older one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanValidate))]
    private async Task ValidateEntriesAsync()
    {
        var version = ++_validationVersion;
        ValidateDuplicates();
        var snapshot = Entries.Select(e => (Entry: e, Value: e.Value)).ToList();
        IsValidating = true;
        try
        {
            var exists = await Task.Run(() => snapshot.Select(s => PathLogic.EntryExists(s.Value)).ToList());
            if (version != _validationVersion) return;
            UiDispatcher.Invoke(() =>
            {
                for (var i = 0; i < snapshot.Count; i++)
                {
                    var (entry, value) = snapshot[i];
                    if (!string.Equals(entry.Value, value, StringComparison.Ordinal))
                        continue; // edited meanwhile; leave it "Unchecked"
                    entry.IsMissing = !exists[i];
                    entry.IsValidated = true;
                }
                RaiseCounts();
            });
            _status.Set($"Validated {snapshot.Count} entries: {MissingCount} missing, {DuplicateCount} duplicate.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Validate PATH entries", ex);
            _status.Set($"Validation failed: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            if (version == _validationVersion) IsValidating = false;
        }
    }

    private async Task ValidateSingleAsync(PathEntry entry)
    {
        var value = entry.Value;
        try
        {
            var exists = await Task.Run(() => PathLogic.EntryExists(value));
            UiDispatcher.Invoke(() =>
            {
                if (!string.Equals(entry.Value, value, StringComparison.Ordinal)) return;
                entry.IsMissing = !exists;
                entry.IsValidated = true;
                RaiseCounts();
            });
        }
        catch (Exception ex)
        {
            _log.Error("Validate PATH entry", ex);
        }
    }

    private bool CanSave() => !IsSaving && (SelectedScope == PathScope.User || IsAdmin);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SavePathAsync()
    {
        if (SelectedScope == PathScope.Machine && !IsAdmin)
        {
            _status.Set("Administrator privileges are required to save the Machine PATH.", StatusKind.Warning);
            return;
        }
        var scope = SelectedScope;
        var raw = RawPath;
        var summary = $"Save {Entries.Count} entries to the {scope} PATH? A backup of the current value is kept.";
        if (!_confirm.Confirm(summary, "Save PATH", destructive: scope == PathScope.Machine))
            return;

        IsSaving = true;
        try
        {
            var current = await Task.Run(() => _store.Read(scope));
            AddBackup(scope, current);
            await Task.Run(() => _store.Write(scope, raw));
            IsDirty = false;
            LastLoaded = DateTime.Now;
            _recent.Add("PathEditor", $"Saved {scope} PATH with {Entries.Count} entries.");
            _status.Set($"{scope} PATH saved. New console windows will pick it up; running programs keep their old PATH.", StatusKind.Success);
            await SaveSettingsAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            _log.Error("Save PATH", ex);
            _status.Set($"Access denied writing the {scope} PATH: {ex.Message}", StatusKind.Error);
        }
        catch (Exception ex)
        {
            _log.Error("Save PATH", ex);
            _status.Set($"Could not save the {scope} PATH: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Loads a backup into the editor without writing anything; the user reviews and saves.</summary>
    [RelayCommand]
    private void RestoreBackup(PathBackup? backup)
    {
        if (backup is null) return;
        if (IsDirty && !_confirm.Confirm("Discard unsaved changes and load this backup into the editor?", "Restore backup", destructive: true))
            return;
        try
        {
            _loading = true;
            SelectedScope = backup.Scope;
            ReplaceEntries(PathLogic.Split(backup.Value));
        }
        finally
        {
            _loading = false;
        }
        IsDirty = true;
        _status.Set($"Loaded the {backup.Scope} PATH backup from {backup.TimestampLabel}. Review the entries and press Save to apply it.", StatusKind.Info);
        _ = ValidateEntriesAsync();
    }

    [RelayCommand]
    private async Task DeleteBackupAsync(PathBackup? backup)
    {
        if (backup is null) return;
        if (!_confirm.Confirm($"Delete the {backup.Scope} PATH backup from {backup.TimestampLabel}?", "Delete backup", destructive: true))
            return;
        Backups.Remove(backup);
        OnPropertyChanged(nameof(HasBackups));
        _settingsDirty = true;
        await SaveSettingsAsync();
        _status.Set("Backup deleted.", StatusKind.Info);
    }

    [RelayCommand]
    private void CopyRawPath()
    {
        try
        {
            Clipboard.SetText(RawPath);
            _status.Set("PATH copied to the clipboard.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Copy PATH", ex);
            _status.Set("Could not copy PATH.", StatusKind.Warning);
        }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (IsAdmin)
        {
            _status.Set("Already running as administrator.", StatusKind.Info);
            return;
        }
        if (!_permissions.TryRelaunchAsAdmin()) _status.Set("Elevation cancelled.", StatusKind.Warning);
        else App.Instance.Shell?.ForceClose();
    }

    /// <summary>Records the pre-save value unless it is identical to the newest backup for that scope.</summary>
    internal void AddBackup(PathScope scope, string value)
    {
        var latest = Backups.FirstOrDefault(b => b.Scope == scope);
        if (latest is not null && string.Equals(latest.Value, value, StringComparison.Ordinal)) return;
        Backups.Insert(0, new PathBackup { Scope = scope, Value = value, Timestamp = DateTime.Now });
        while (Backups.Count > PathLogic.MaxBackups) Backups.RemoveAt(Backups.Count - 1);
        _settingsDirty = true;
        OnPropertyChanged(nameof(HasBackups));
    }

    private async Task SaveSettingsAsync()
    {
        _settings.Backups = Backups.ToList();
        if (await _storage.SaveAsync(_settingsPath, _settings)) _settingsDirty = false;
        else _status.Set("PathEditor settings could not be saved.", StatusKind.Warning);
    }

    private void MarkDirty()
    {
        if (!_loading) IsDirty = true;
    }

    private void RefreshEntryCommands()
    {
        RemoveSelectedEntryCommand.NotifyCanExecuteChanged();
        MoveSelectedUpCommand.NotifyCanExecuteChanged();
        MoveSelectedDownCommand.NotifyCanExecuteChanged();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(EntryCount));
        OnPropertyChanged(nameof(MissingCount));
        OnPropertyChanged(nameof(DuplicateCount));
        OnPropertyChanged(nameof(RawPath));
        OnPropertyChanged(nameof(HasEntries));
    }

    private void ReplaceEntries(IEnumerable<string> values)
    {
        ClearEntries();
        foreach (var value in values) AddTrackedEntry(new PathEntry { Value = value });
        SelectedEntry = Entries.FirstOrDefault();
        ValidateDuplicates();
        RefreshEntryCommands();
    }

    private void AddTrackedEntry(PathEntry entry)
    {
        entry.PropertyChanged += OnEntryPropertyChanged;
        Entries.Add(entry);
    }

    private void ClearEntries()
    {
        foreach (var entry in Entries)
            entry.PropertyChanged -= OnEntryPropertyChanged;
        Entries.Clear();
        SelectedEntry = null;
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PathEntry.Value)) return;
        if (sender is PathEntry entry)
        {
            entry.IsValidated = false;
            entry.IsMissing = false;
        }
        MarkDirty();
        ValidateDuplicates();
    }

    private void ValidateDuplicates()
    {
        var flags = PathLogic.FlagDuplicates(Entries.Select(e => e.Value));
        for (var i = 0; i < flags.Length && i < Entries.Count; i++)
            Entries[i].IsDuplicate = flags[i];
        RaiseCounts();
    }
}
