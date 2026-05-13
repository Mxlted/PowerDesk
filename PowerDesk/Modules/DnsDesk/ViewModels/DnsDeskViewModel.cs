using System;
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
    };

    [ObservableProperty] private DnsAdapter? _selectedAdapter;
    [ObservableProperty] private DnsProfile? _selectedProfile;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private DateTime? _lastRefresh;

    public bool IsAdmin => _permissions.IsAdministrator;
    public int AdapterCount => Adapters.Count;
    public int OnlineCount => Adapters.Count(a => a.IsUp);

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

    public void Initialize() => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        try
        {
            var previous = SelectedAdapter?.Id;
            Adapters.Clear();
            foreach (var adapter in _dns.GetAdapters()) Adapters.Add(adapter);
            SelectedAdapter = Adapters.FirstOrDefault(a => string.Equals(a.Id, previous, StringComparison.OrdinalIgnoreCase))
                ?? Adapters.FirstOrDefault();
            LastRefresh = DateTime.Now;
            OnPropertyChanged(nameof(AdapterCount));
            OnPropertyChanged(nameof(OnlineCount));
            _status.Set($"DnsDesk found {Adapters.Count} adapter(s).", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("DnsDesk refresh", ex);
            _status.Set("DnsDesk refresh failed. See logs.", StatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task ApplyProfileAsync()
    {
        if (SelectedAdapter is null)
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

        IsBusy = true;
        try
        {
            var result = await _dns.ApplyProfileAsync(SelectedAdapter, SelectedProfile);
            if (!result.Success)
            {
                _status.Set(result.Message, StatusKind.Error);
                return;
            }
            _recent.Add("DnsDesk", $"Applied {SelectedProfile.Name} to {SelectedAdapter.Name}.");
            _status.Set(result.Message, StatusKind.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _log.Error("Apply DNS profile", ex);
            _status.Set("DNS profile change failed. See logs.", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
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
            _status.Set("DNS flush failed. See logs.", StatusKind.Error);
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
}
