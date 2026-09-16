using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.HostProfiles.Models;
using PowerDesk.Modules.HostProfiles.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace PowerDesk.Modules.HostProfiles.ViewModels;

public sealed partial class HostProfilesViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly JsonStorageService _storage;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly PermissionService _permissions;
    private readonly IConfirmationService _confirm;
    private readonly HostsFileService _hostsFile = new();
    private readonly string _settingsPath;
    private HostProfilesSettings _settings = new();
    private bool _settingsLoaded;

    public ObservableCollection<HostProfile> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedProfileCommand))]
    private HostProfile? _selectedProfile;

    [ObservableProperty] private string _currentHosts = string.Empty;
    [ObservableProperty] private string _currentHostsSummary = string.Empty;
    [ObservableProperty] private string _selectedProfileSummary = string.Empty;
    [ObservableProperty] private string _newProfileName = string.Empty;
    [ObservableProperty] private DateTime? _lastLoaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadCurrentHostsCommand))]
    private bool _isApplying;

    public bool IsAdmin => _permissions.IsAdministrator;
    public int ProfileCount => Profiles.Count;
    public bool HasProfiles => Profiles.Count > 0;
    public string HostsPath => _hostsFile.HostsPath;
    public string BackupsFolder => PathService.ModuleDir("HostProfiles");

    public HostProfilesViewModel(
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
        _settingsPath = PathService.ModuleSettingsFile("HostProfiles");
    }

    public async Task InitializeAsync()
    {
        try
        {
            _settings = await _storage.LoadAsync(_settingsPath, () => new HostProfilesSettings());
            _settingsLoaded = true;
        }
        catch (Exception ex)
        {
            _log.Error("HostProfiles settings load", ex);
            _settings = new HostProfilesSettings();
        }
        UiDispatcher.Invoke(() =>
        {
            Profiles.Clear();
            foreach (var profile in _settings.Profiles ?? []) Profiles.Add(profile);
            SelectedProfile = Profiles.FirstOrDefault();
            LoadHosts(announce: false);
            NotifyProfilesChanged();
        });
    }

    /// <summary>Only saves when the settings were actually loaded, so a failed load cannot wipe saved profiles.</summary>
    public async Task ShutdownAsync()
    {
        if (_settingsLoaded) await SaveAsync();
    }

    private async Task SaveAsync()
    {
        _settings.Profiles = Profiles.ToList();
        if (!await _storage.SaveAsync(_settingsPath, _settings))
            _status.Set("HostProfiles settings could not be saved.", StatusKind.Warning);
    }

    private bool CanLoad => !IsApplying;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private void LoadCurrentHosts() => LoadHosts(announce: true);

    private void LoadHosts(bool announce)
    {
        try
        {
            CurrentHosts = _hostsFile.Read();
            CurrentHostsSummary = HostProfilesLogic.Summarize(CurrentHosts).Label;
            LastLoaded = DateTime.Now;
            if (announce) _status.Set("Hosts file loaded.", StatusKind.Success);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error("Load hosts file (access denied)", ex);
            _status.Set("Access to the hosts file was denied. Relaunch as administrator to read it.", StatusKind.Error);
        }
        catch (Exception ex)
        {
            _log.Error("Load hosts file", ex);
            _status.Set("Could not read the hosts file.", StatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task SaveCurrentAsProfileAsync()
    {
        var name = ResolveNewProfileName($"Hosts {DateTime.Now:yyyy-MM-dd HHmm}");
        if (name is null) return;

        var profile = new HostProfile { Name = name, Content = CurrentHosts, UpdatedAt = DateTime.Now };
        AddProfile(profile);
        await SaveAsync();
        _status.Set($"Saved current hosts as '{name}'.", StatusKind.Success);
    }

    [RelayCommand]
    private async Task AddBlankProfileAsync()
    {
        var name = ResolveNewProfileName("New hosts profile");
        if (name is null) return;

        var profile = new HostProfile { Name = name, Content = DefaultHostsContent(), UpdatedAt = DateTime.Now };
        AddProfile(profile);
        await SaveAsync();
        _status.Set($"Created '{name}'. Edit it and press Save.", StatusKind.Info);
    }

    /// <summary>
    /// A typed name must be unique; an auto-generated fallback is made unique automatically.
    /// Returns null (after reporting) when the typed name is rejected.
    /// </summary>
    private string? ResolveNewProfileName(string fallback)
    {
        var existing = Profiles.Select(p => p.Name);
        if (string.IsNullOrWhiteSpace(NewProfileName))
            return HostProfilesLogic.MakeUniqueName(fallback, existing);

        var error = HostProfilesLogic.ValidateProfileName(NewProfileName, existing);
        if (error is not null)
        {
            _status.Set(error, StatusKind.Warning);
            return null;
        }
        return NewProfileName.Trim();
    }

    private void AddProfile(HostProfile profile)
    {
        Profiles.Insert(0, profile);
        SelectedProfile = profile;
        NewProfileName = string.Empty;
        NotifyProfilesChanged();
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        string[] files;
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import hosts file(s) as profiles",
                Filter = "Hosts files|hosts;*.hosts;*.txt|All files|*.*",
                Multiselect = true,
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            files = dlg.FileNames;
        }
        catch (Exception ex)
        {
            _log.Error("Import hosts profile (picker)", ex);
            _status.Set("Could not open the file picker.", StatusKind.Warning);
            return;
        }
        await ImportProfilesFromPathsAsync(files);
    }

    /// <summary>
    /// Creates one profile per dropped/picked hosts-style text file. The profile is named after the
    /// file (made unique) and the typed "new profile name" is used for a single import when set.
    /// Nothing touches the Windows hosts file. Never throws.
    /// </summary>
    public async Task ImportProfilesFromPathsAsync(IEnumerable<string>? paths)
    {
        var candidates = (paths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return;

        var imported = new List<string>();
        var skipped = new List<string>();
        var typedName = candidates.Count == 1 ? NewProfileName : string.Empty;

        foreach (var path in candidates)
        {
            try
            {
                if (Directory.Exists(path)) { skipped.Add($"{Path.GetFileName(path)} is a folder"); continue; }
                if (!File.Exists(path)) { skipped.Add($"{Path.GetFileName(path)} does not exist"); continue; }
                var info = new FileInfo(path);
                if (info.Length > HostProfilesLogic.MaxImportBytes)
                {
                    skipped.Add($"{info.Name} is too large to be a hosts file");
                    continue;
                }

                var content = HostProfilesLogic.Decode(File.ReadAllBytes(path));
                string name;
                if (!string.IsNullOrWhiteSpace(typedName))
                {
                    var error = HostProfilesLogic.ValidateProfileName(typedName, Profiles.Select(p => p.Name));
                    if (error is not null) { _status.Set(error, StatusKind.Warning); return; }
                    name = typedName.Trim();
                }
                else
                {
                    name = HostProfilesLogic.MakeUniqueName(HostProfilesLogic.ProfileNameFromPath(path), Profiles.Select(p => p.Name));
                }

                AddProfile(new HostProfile { Name = name, Content = content, UpdatedAt = DateTime.Now });
                imported.Add(name);
            }
            catch (Exception ex)
            {
                _log.Error($"Import hosts profile: {path}", ex);
                skipped.Add($"{Path.GetFileName(path)} could not be read");
            }
        }

        if (imported.Count > 0)
        {
            await SaveAsync();
            _recent.Add("HostProfiles", imported.Count == 1
                ? $"Imported hosts profile: {imported[0]}"
                : $"Imported {imported.Count} hosts profiles.");
        }

        var summary = imported.Count switch
        {
            0 => "Nothing was imported.",
            1 => $"Imported '{imported[0]}'. Review it and press Apply when ready.",
            _ => $"Imported {imported.Count} profiles.",
        };
        if (skipped.Count > 0) summary += $" Skipped: {string.Join("; ", skipped)}.";
        _status.Set(summary, imported.Count == 0 ? StatusKind.Warning : skipped.Count > 0 ? StatusKind.Warning : StatusKind.Success);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ExportSelectedProfile()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            _status.Set("Select a hosts profile first.", StatusKind.Warning);
            return;
        }
        try
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export hosts profile",
                FileName = HostProfilesLogic.ExportFileName(profile.Name),
                Filter = "Text files (*.txt)|*.txt|All files|*.*",
                AddExtension = true,
            };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, HostProfilesLogic.NormalizeForWrite(profile.Content), HostProfilesLogic.WriteEncoding);
            _recent.Add("HostProfiles", $"Exported hosts profile: {profile.Name}");
            _status.Set($"Exported '{profile.Name}' to {Path.GetFileName(dlg.FileName)}.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Export hosts profile", ex);
            _status.Set($"Could not export the profile: {ex.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        try
        {
            var folder = BackupsFolder;
            Directory.CreateDirectory(folder);
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Error("Open hosts backups folder", ex);
            _status.Set("Could not open the backups folder.", StatusKind.Warning);
        }
    }

    private bool HasSelection => SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedProfileAsync()
    {
        var profile = SelectedProfile;
        if (profile is null) return;
        if (!_confirm.Confirm($"Delete hosts profile '{profile.Name}'?\n\nThis does not change the Windows hosts file.", "Delete hosts profile", destructive: true))
            return;

        var index = Profiles.IndexOf(profile);
        Profiles.Remove(profile);
        SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
        NotifyProfilesChanged();
        await SaveAsync();
        _status.Set($"Deleted '{profile.Name}'.", StatusKind.Info);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveSelectedProfileAsync()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            _status.Set("Select a hosts profile first.", StatusKind.Warning);
            return;
        }

        var error = HostProfilesLogic.ValidateProfileName(profile.Name, Profiles.Where(p => !ReferenceEquals(p, profile)).Select(p => p.Name));
        if (error is not null)
        {
            _status.Set(error, StatusKind.Warning);
            return;
        }

        profile.Name = profile.Name.Trim();
        profile.UpdatedAt = DateTime.Now;
        await SaveAsync();
        _status.Set($"Profile '{profile.Name}' saved.", StatusKind.Success);
    }

    private bool CanApply => IsAdmin && !IsApplying && SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplySelectedProfileAsync()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            _status.Set("Select a hosts profile first.", StatusKind.Warning);
            return;
        }
        if (!IsAdmin)
        {
            _status.Set("Administrator privileges are required to apply a hosts profile.", StatusKind.Warning);
            return;
        }

        var summary = HostProfilesLogic.Summarize(profile.Content);
        var warning = summary.Invalid > 0
            ? $"\n\nWarning: {summary.Invalid} line(s) do not look like valid hosts entries and will be ignored by Windows."
            : string.Empty;
        if (!_confirm.Confirm(
                $"Apply '{profile.Name}' to the Windows hosts file?\n\n{summary.Label}.{warning}\n\nA timestamped backup of the current file is made first.",
                "Apply hosts profile", destructive: true))
            return;

        IsApplying = true;
        try
        {
            var content = profile.Content ?? string.Empty;
            var backupDir = PathService.ModuleDir("HostProfiles");
            var backup = await Task.Run(() => _hostsFile.WriteWithBackup(content, backupDir, DateTime.Now));
            var flushed = await Task.Run(HostsFileService.FlushDns);

            LoadHosts(announce: false);
            profile.UpdatedAt = DateTime.Now;
            _recent.Add("HostProfiles", $"Applied hosts profile: {profile.Name}");

            var backupNote = backup is null ? "No previous hosts file existed." : $"Backup: {Path.GetFileName(backup)}.";
            var dnsNote = flushed ? " DNS cache flushed." : " DNS cache could not be flushed; run 'ipconfig /flushdns' if changes do not take effect.";
            _status.Set($"Hosts profile applied. {backupNote}{dnsNote}", flushed ? StatusKind.Success : StatusKind.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error("Apply hosts profile (access denied)", ex);
            _status.Set("Access denied writing the hosts file. Antivirus or Controlled Folder Access may be protecting it.", StatusKind.Error);
        }
        catch (IOException ex)
        {
            _log.Error("Apply hosts profile (I/O)", ex);
            _status.Set($"Could not write the hosts file: {ex.Message}", StatusKind.Error);
        }
        catch (Exception ex)
        {
            _log.Error("Apply hosts profile", ex);
            _status.Set("Could not write the hosts file. See logs.", StatusKind.Error);
        }
        finally
        {
            IsApplying = false;
        }
        await SaveAsync();
    }

    [RelayCommand]
    private void CopyHostsPath()
    {
        if (ClipboardService.TrySetText(HostsPath))
            _status.Set("Hosts path copied.", StatusKind.Success);
        else
            _status.Set("Could not copy hosts path (clipboard is busy). Try again.", StatusKind.Warning);
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

    partial void OnSelectedProfileChanged(HostProfile? oldValue, HostProfile? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedProfileContentChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedProfileContentChanged;
        UpdateSelectedSummary();
    }

    private void OnSelectedProfileContentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(HostProfile.Content)) UpdateSelectedSummary();
    }

    private void UpdateSelectedSummary()
    {
        SelectedProfileSummary = SelectedProfile is null
            ? string.Empty
            : HostProfilesLogic.Summarize(SelectedProfile.Content).Label;
    }

    private void NotifyProfilesChanged()
    {
        OnPropertyChanged(nameof(ProfileCount));
        OnPropertyChanged(nameof(HasProfiles));
    }

    private static string DefaultHostsContent() =>
        "# Hosts profile managed by PowerDesk.\r\n# Format: <IP address> <hostname> [alias...]   # comment\r\n\r\n127.0.0.1 localhost\r\n::1 localhost\r\n";
}
