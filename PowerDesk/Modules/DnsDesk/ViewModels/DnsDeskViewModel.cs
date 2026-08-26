using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Modules.DnsDesk.Models;
using PowerDesk.Modules.DnsDesk.Services;

namespace PowerDesk.Modules.DnsDesk.ViewModels;

public sealed partial class DnsDeskViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly PermissionService _permissions;
    private readonly DnsService _dns;
    private int _refreshing;

    public ObservableCollection<DnsAdapter> Adapters { get; } = new();
    public ObservableCollection<DnsProfile> Profiles { get; } = new()
    {
        new DnsProfile { Name = "Automatic (DHCP)", UseDhcp = true },
        new DnsProfile
        {
            Name = "Cloudflare",
            Ipv4Primary = "1.1.1.1",
            Ipv4Secondary = "1.0.0.1",
            Ipv6Primary = "2606:4700:4700::1111",
            Ipv6Secondary = "2606:4700:4700::1001",
        },
        new DnsProfile
        {
            Name = "Google",
            Ipv4Primary = "8.8.8.8",
            Ipv4Secondary = "8.8.4.4",
            Ipv6Primary = "2001:4860:4860::8888",
            Ipv6Secondary = "2001:4860:4860::8844",
        },
        new DnsProfile
        {
            Name = "Quad9",
            Ipv4Primary = "9.9.9.9",
            Ipv4Secondary = "149.112.112.112",
            Ipv6Primary = "2620:fe::fe",
            Ipv6Secondary = "2620:fe::9",
        },
        new DnsProfile
        {
            Name = "OpenDNS",
            Ipv4Primary = "208.67.222.222",
            Ipv4Secondary = "208.67.220.220",
            Ipv6Primary = "2620:119:35::35",
            Ipv6Secondary = "2620:119:53::53",
        },
        new DnsProfile { Name = "Custom", IsCustom = true },
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyProfileCommand))]
    private DnsAdapter? _selectedAdapter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyProfileCommand))]
    [NotifyPropertyChangedFor(nameof(IsCustomProfile))]
    private DnsProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlushDnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty] private DateTime? _lastRefresh;

    [ObservableProperty] private string _customIpv4Primary = string.Empty;
    [ObservableProperty] private string _customIpv4Secondary = string.Empty;
    [ObservableProperty] private string _customIpv6Primary = string.Empty;
    [ObservableProperty] private string _customIpv6Secondary = string.Empty;

    public bool IsAdmin => _permissions.IsAdministrator;
    public bool IsCustomProfile => SelectedProfile?.IsCustom == true;
    public int AdapterCount => Adapters.Count;
    public int OnlineCount => Adapters.Count(a => a.IsUp);
    public bool HasAdapters => Adapters.Count > 0;

    public DnsDeskViewModel(
        ILogger log,
        StatusService status,
        RecentActionsService recent,
        PermissionService permissions)
    {
        _log = log;
        _status = status;
        _recent = recent;
        _permissions = permissions;
        _dns = new DnsService(log);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    /// <summary>Kicks off the first adapter scan. Safe to call more than once.</summary>
    public void Initialize() => _ = RefreshAsync();

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        // Adapter enumeration can take a noticeable time; keep the UI responsive and skip overlapping scans.
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        IsBusy = true;
        try
        {
            var adapters = await Task.Run(() => _dns.GetAdapters());
            UiDispatcher.Invoke(() => ApplyAdapters(adapters));
            _status.Set($"DnsDesk found {adapters.Count} adapter(s), {adapters.Count(a => a.IsUp)} online.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("DnsDesk refresh", ex);
            _status.Set($"DnsDesk refresh failed: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void ApplyAdapters(IReadOnlyList<DnsAdapter> adapters)
    {
        var previous = SelectedAdapter?.Id;
        Adapters.Clear();
        foreach (var adapter in adapters) Adapters.Add(adapter);
        SelectedAdapter = DnsLogic.SelectAfterRefresh(adapters, previous);
        LastRefresh = DateTime.Now;
        OnPropertyChanged(nameof(AdapterCount));
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(HasAdapters));
    }

    private bool CanApplyProfile() => !IsBusy && IsAdmin && SelectedAdapter is not null && SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanApplyProfile))]
    private async Task ApplyProfileAsync()
    {
        var adapter = SelectedAdapter;
        if (adapter is null)
        {
            _status.Set("Select a network adapter first.", StatusKind.Warning);
            return;
        }
        if (SelectedProfile is null)
        {
            _status.Set("Select a DNS profile first.", StatusKind.Warning);
            return;
        }
        if (!IsAdmin)
        {
            _status.Set("Administrator privileges are required to change DNS settings.", StatusKind.Warning);
            return;
        }

        var profile = BuildEffectiveProfile();
        var errors = DnsLogic.ValidateProfile(profile);
        if (errors.Count > 0)
        {
            _status.Set($"Cannot apply '{profile.Name}': {string.Join(" ", errors)}", StatusKind.Warning);
            return;
        }

        string message;
        StatusKind kind;
        IsBusy = true;
        try
        {
            var result = await _dns.ApplyProfileAsync(adapter, profile);
            message = result.Message;
            kind = result.Success ? StatusKind.Success : StatusKind.Error;
            if (result.Success) _recent.Add("DnsDesk", $"Applied {profile.Name} DNS to {adapter.Name}.");
        }
        catch (Exception ex)
        {
            _log.Error("Apply DNS profile", ex);
            message = $"DNS profile change failed: {ex.Message}";
            kind = StatusKind.Error;
        }
        finally
        {
            IsBusy = false;
        }

        // Re-read adapter state so the grid reflects whatever netsh actually applied (even on a
        // partial failure), then surface the apply outcome rather than the refresh message.
        await RefreshAsync();
        _status.Set(message, kind);
    }

    private bool CanFlushDns() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFlushDns))]
    private async Task FlushDnsAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _dns.FlushDnsAsync();
            _status.Set(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            if (result.Success) _recent.Add("DnsDesk", "Flushed DNS resolver cache.");
        }
        catch (Exception ex)
        {
            _log.Error("Flush DNS", ex);
            _status.Set($"DNS flush failed: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
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

    /// <summary>
    /// The profile that will actually be applied: the selected preset, or a normalized copy of the
    /// custom addresses typed into the view.
    /// </summary>
    internal DnsProfile BuildEffectiveProfile()
    {
        var selected = SelectedProfile ?? Profiles[0];
        if (!selected.IsCustom) return DnsLogic.Normalize(selected);
        return DnsLogic.Normalize(new DnsProfile
        {
            Name = "Custom",
            IsCustom = true,
            Ipv4Primary = CustomIpv4Primary,
            Ipv4Secondary = CustomIpv4Secondary,
            Ipv6Primary = CustomIpv6Primary,
            Ipv6Secondary = CustomIpv6Secondary,
        });
    }
}
