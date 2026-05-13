using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.HostProfiles.Models;
using Clipboard = System.Windows.Clipboard;

namespace PowerDesk.Modules.HostProfiles.ViewModels;

public sealed partial class HostProfilesViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly JsonStorageService _storage;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly PermissionService _permissions;
    private readonly IConfirmationService _confirm;
    private readonly string _settingsPath;
    private HostProfilesSettings _settings = new();

    public ObservableCollection<HostProfile> Profiles { get; } = new();

    [ObservableProperty] private HostProfile? _selectedProfile;
    [ObservableProperty] private string _currentHosts = string.Empty;
    [ObservableProperty] private string _newProfileName = string.Empty;
    [ObservableProperty] private DateTime? _lastLoaded;

    public bool IsAdmin => _permissions.IsAdministrator;
    public int ProfileCount => Profiles.Count;
    public string HostsPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

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
        _settings = await _storage.LoadAsync(_settingsPath, () => new HostProfilesSettings());
        Profiles.Clear();
        foreach (var profile in _settings.Profiles) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault();
        LoadCurrentHosts();
        OnPropertyChanged(nameof(ProfileCount));
    }

    public async Task ShutdownAsync() => await SaveAsync();

    private async Task SaveAsync()
    {
        _settings.Profiles = Profiles.ToList();
        if (!await _storage.SaveAsync(_settingsPath, _settings))
            _status.Set("HostProfiles settings could not be saved.", StatusKind.Warning);
    }

    [RelayCommand]
    private void LoadCurrentHosts()
    {
        try
        {
            CurrentHosts = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : string.Empty;
            LastLoaded = DateTime.Now;
            _status.Set("Hosts file loaded.", StatusKind.Success);
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
        var name = string.IsNullOrWhiteSpace(NewProfileName) ? $"Hosts {DateTime.Now:yyyy-MM-dd HHmm}" : NewProfileName.Trim();
        var profile = new HostProfile { Name = name, Content = CurrentHosts, UpdatedAt = DateTime.Now };
        Profiles.Insert(0, profile);
        SelectedProfile = profile;
        NewProfileName = string.Empty;
        OnPropertyChanged(nameof(ProfileCount));
        await SaveAsync();
        _status.Set("Hosts profile saved.", StatusKind.Success);
    }

    [RelayCommand]
    private async Task AddBlankProfileAsync()
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName) ? "New hosts profile" : NewProfileName.Trim();
        var profile = new HostProfile { Name = name, Content = DefaultHostsContent(), UpdatedAt = DateTime.Now };
        Profiles.Insert(0, profile);
        SelectedProfile = profile;
        NewProfileName = string.Empty;
        OnPropertyChanged(nameof(ProfileCount));
        await SaveAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedProfileAsync()
    {
        if (SelectedProfile is null) return;
        if (!_confirm.Confirm($"Delete hosts profile '{SelectedProfile.Name}'?", "Delete hosts profile", destructive: true))
            return;

        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(ProfileCount));
        await SaveAsync();
        _status.Set("Hosts profile deleted.", StatusKind.Info);
    }

    [RelayCommand]
    private async Task SaveSelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            _status.Set("Select a hosts profile first.", StatusKind.Warning);
            return;
        }
        SelectedProfile.UpdatedAt = DateTime.Now;
        await SaveAsync();
        _status.Set("Hosts profile saved.", StatusKind.Success);
    }

    [RelayCommand]
    private async Task ApplySelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            _status.Set("Select a hosts profile first.", StatusKind.Warning);
            return;
        }
        if (!IsAdmin)
        {
            _status.Set("Administrator privileges are required to apply a hosts profile.", StatusKind.Warning);
            return;
        }
        if (!_confirm.Confirm($"Apply '{SelectedProfile.Name}' to the Windows hosts file?", "Apply hosts profile", destructive: true))
            return;

        try
        {
            var backup = Path.Combine(PathService.ModuleDir("HostProfiles"), $"hosts-backup-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            if (File.Exists(HostsPath)) File.Copy(HostsPath, backup, overwrite: true);
            File.WriteAllText(HostsPath, SelectedProfile.Content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            LoadCurrentHosts();
            _recent.Add("HostProfiles", $"Applied hosts profile: {SelectedProfile.Name}");
            _status.Set($"Hosts profile applied. Backup saved to {Path.GetFileName(backup)}.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Apply hosts profile", ex);
            _status.Set("Could not write the hosts file.", StatusKind.Error);
        }
        await SaveAsync();
    }

    [RelayCommand]
    private void CopyHostsPath()
    {
        try
        {
            Clipboard.SetText(HostsPath);
            _status.Set("Hosts path copied.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Copy hosts path", ex);
            _status.Set("Could not copy hosts path.", StatusKind.Warning);
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

    private static string DefaultHostsContent() =>
        "# Copyright (c) Microsoft Corp.\r\n#\r\n# Local hosts profile managed by PowerDesk.\r\n\r\n127.0.0.1 localhost\r\n::1 localhost\r\n";
}
