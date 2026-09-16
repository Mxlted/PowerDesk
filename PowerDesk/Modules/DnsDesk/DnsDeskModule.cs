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
    // Filled 24x24 geometry (nonzero rule; holes wind the opposite way). Both the sidebar and the dashboard
    // render this with Fill only, so open stroke paths would be invisible or collapse into solid blobs.
    public string IconGeometry => "F1 M 12,2 A 10,10 0 1 1 12,22 A 10,10 0 1 1 12,2 Z M 12,4 A 8,8 0 1 0 12,20 A 8,8 0 1 0 12,4 Z M 4,11 H 20 V 13 H 4 Z M 12,4 A 4.5,8 0 1 1 12,20 A 4.5,8 0 1 1 12,4 Z M 12,6 A 2.5,6 0 1 0 12,18 A 2.5,6 0 1 0 12,6 Z";
    public bool RequiresAdminForFullControl => true;

    public DnsDeskViewModel ViewModel { get; }
    public UserControl MainView { get; }

    public DnsDeskModule(ILogger log, StatusService status, RecentActionsService recent, PermissionService permissions)
    {
        ViewModel = new DnsDeskViewModel(log, status, recent, permissions);
        MainView = new DnsDeskView(ViewModel);
    }

    // The scan runs off the UI thread; awaiting it keeps startup ordering deterministic without blocking.
    public Task InitializeAsync() => ViewModel.InitializeAsync();

    public Task ShutdownAsync() => Task.CompletedTask;
}
