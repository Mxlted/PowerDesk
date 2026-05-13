using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.PathEditor.Models;
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
    private readonly string _settingsPath;
    private PathEditorSettings _settings = new();
    private bool _loading;

    public ObservableCollection<PathEntry> Entries { get; } = new();
    public ObservableCollection<PathBackup> Backups { get; } = new();

    [ObservableProperty] private PathScope _selectedScope = PathScope.User;
    [ObservableProperty] private PathEntry? _selectedEntry;
    [ObservableProperty] private string _newEntry = string.Empty;
    [ObservableProperty] private DateTime? _lastLoaded;
    [ObservableProperty] private bool _isValidating;

    public bool IsAdmin => _permissions.IsAdministrator;
    public int EntryCount => Entries.Count;
    public int MissingCount => Entries.Count(e => e.IsMissing);
    public int DuplicateCount => Entries.Count(e => e.IsDuplicate);
    public string RawPath => string.Join(";", Entries.Select(e => e.Value.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)));

    public PathEditorViewModel(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        PermissionService permissions,
        IConfirmationService confirm)
    {
        _log = log;
        _storage = storage;
        _status = status;
        _recent = recent;
        _permissions = permissions;
        _confirm = confirm;
        _settingsPath = PathService.ModuleSettingsFile("PathEditor");
    }

    public async Task InitializeAsync()
    {
        _settings = await _storage.LoadAsync(_settingsPath, () => new PathEditorSettings());
        Backups.Clear();
        foreach (var backup in _settings.Backups.OrderByDescending(b => b.Timestamp)) Backups.Add(backup);
        LoadPath();
    }

    public async Task ShutdownAsync() => await SaveSettingsAsync();

    partial void OnSelectedScopeChanged(PathScope value)
    {
        if (!_loading) LoadPath();
    }

    [RelayCommand]
    private void LoadPath()
    {
        try
        {
            _loading = true;
            ClearEntries();
            var value = Environment.GetEnvironmentVariable("Path", ToTarget(SelectedScope)) ?? string.Empty;
            foreach (var entry in SplitPath(value))
                AddTrackedEntry(new PathEntry { Value = entry });
            SelectedEntry = Entries.FirstOrDefault();
            LastLoaded = DateTime.Now;
            ValidateDuplicates();
            _status.Set($"{SelectedScope} PATH loaded.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Load PATH", ex);
            _status.Set("Could not load PATH.", StatusKind.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void AddEntry()
    {
        var value = NewEntry.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _status.Set("Enter a folder path first.", StatusKind.Warning);
            return;
        }
        var entry = new PathEntry { Value = value };
        AddTrackedEntry(entry);
        SelectedEntry = entry;
        NewEntry = string.Empty;
        ValidateDuplicates();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select a PATH folder",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() == DialogResult.OK) NewEntry = dlg.SelectedPath;
    }

    [RelayCommand]
    private void RemoveSelectedEntry()
    {
        if (SelectedEntry is null) return;
        var index = Entries.IndexOf(SelectedEntry);
        SelectedEntry.PropertyChanged -= OnEntryPropertyChanged;
        Entries.Remove(SelectedEntry);
        SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Clamp(index, 0, Entries.Count - 1)];
        ValidateDuplicates();
    }

    [RelayCommand]
    private void MoveSelectedUp()
    {
        if (SelectedEntry is null) return;
        var index = Entries.IndexOf(SelectedEntry);
        if (index <= 0) return;
        Entries.Move(index, index - 1);
        ValidateDuplicates();
    }

    [RelayCommand]
    private void MoveSelectedDown()
    {
        if (SelectedEntry is null) return;
        var index = Entries.IndexOf(SelectedEntry);
        if (index < 0 || index >= Entries.Count - 1) return;
        Entries.Move(index, index + 1);
        ValidateDuplicates();
    }

    [RelayCommand]
    private async Task ValidateEntriesAsync()
    {
        if (IsValidating) return;
        IsValidating = true;
        try
        {
            ValidateDuplicates();
            var snapshot = Entries.Select(e => e.Value).ToList();
            var states = await Task.Run(() => snapshot.Select(EntryExists).ToList());
            UiDispatcher.Invoke(() =>
            {
                for (var i = 0; i < states.Count && i < Entries.Count; i++)
                {
                    Entries[i].IsMissing = !states[i];
                    Entries[i].IsValidated = true;
                }
                RaiseCounts();
            });
            _status.Set($"Validation complete: {MissingCount} missing, {DuplicateCount} duplicate.", StatusKind.Info);
        }
        finally
        {
            IsValidating = false;
        }
    }

    [RelayCommand]
    private async Task SavePathAsync()
    {
        if (SelectedScope == PathScope.Machine && !IsAdmin)
        {
            _status.Set("Administrator privileges are required to save Machine PATH.", StatusKind.Warning);
            return;
        }
        if (!_confirm.Confirm($"Save changes to {SelectedScope} PATH?", "Save PATH", destructive: SelectedScope == PathScope.Machine))
            return;

        try
        {
            var target = ToTarget(SelectedScope);
            var current = Environment.GetEnvironmentVariable("Path", target) ?? string.Empty;
            Backups.Insert(0, new PathBackup { Scope = SelectedScope, Value = current, Timestamp = DateTime.Now });
            while (Backups.Count > 20) Backups.RemoveAt(Backups.Count - 1);

            Environment.SetEnvironmentVariable("Path", RawPath, target);
            BroadcastEnvironmentChange();
            _recent.Add("PathEditor", $"Saved {SelectedScope} PATH with {Entries.Count} entries.");
            _status.Set($"{SelectedScope} PATH saved.", StatusKind.Success);
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Save PATH", ex);
            _status.Set("Could not save PATH.", StatusKind.Error);
        }
    }

    [RelayCommand]
    private void CopyRawPath()
    {
        try
        {
            Clipboard.SetText(RawPath);
            _status.Set("PATH copied.", StatusKind.Success);
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

    private async Task SaveSettingsAsync()
    {
        _settings.Backups = Backups.ToList();
        if (!await _storage.SaveAsync(_settingsPath, _settings))
            _status.Set("PathEditor settings could not be saved.", StatusKind.Warning);
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(EntryCount));
        OnPropertyChanged(nameof(MissingCount));
        OnPropertyChanged(nameof(DuplicateCount));
        OnPropertyChanged(nameof(RawPath));
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
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PathEntry.Value)) return;
        if (sender is PathEntry entry)
        {
            entry.IsValidated = false;
            entry.IsMissing = false;
        }
        ValidateDuplicates();
    }

    private void ValidateDuplicates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            var normalized = NormalizeForCompare(entry.Value);
            entry.IsDuplicate = !string.IsNullOrEmpty(normalized) && !seen.Add(normalized);
        }
        RaiseCounts();
    }

    private static string[] SplitPath(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static EnvironmentVariableTarget ToTarget(PathScope scope) =>
        scope == PathScope.Machine ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;

    private static bool EntryExists(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            return Directory.Exists(expanded);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeForCompare(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().Trim('"').TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed)).TrimEnd('\\'); }
        catch { return trimmed; }
    }

    private static void BroadcastEnvironmentChange()
    {
        SendMessageTimeout(
            new IntPtr(0xffff),
            0x001A,
            IntPtr.Zero,
            "Environment",
            0x0002,
            5000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}
