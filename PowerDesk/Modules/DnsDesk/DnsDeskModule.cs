using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Modules.DnsDesk.ViewModels;
using PowerDesk.Modules.DnsDesk.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.DnsDesk;

public sealed class DnsDeskModule : IPowerDeskModule
{
    public string Id => "DnsDesk";
    public string DisplayName => "DnsDesk";
    public string Description => "Inspect network DNS state, apply common resolver profiles, and flush DNS.";
    public string IconKey => "DnsDesk";
    public string IconGeometry => "M 12,3 A 9,9 0 1 0 12,21 A 9,9 0 0 0 12,3 M 3,12 H 21 M 12,3 C 15,6 15,18 12,21 M 12,3 C 9,6 9,18 12,21";
    public bool RequiresAdminForFullControl => true;

    public DnsDeskViewModel ViewModel { get; }
    public UserControl MainView { get; }

    public DnsDeskModule(ILogger log, StatusService status, RecentActionsService recent, PermissionService permissions)
    {
        ViewModel = new DnsDeskViewModel(log, status, recent, permissions);
        MainView = new DnsDeskView(ViewModel);
    }

    public Task InitializeAsync()
    {
        ViewModel.Initialize();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}
