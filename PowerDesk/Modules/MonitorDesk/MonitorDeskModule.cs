using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.MonitorDesk.ViewModels;
using PowerDesk.Modules.MonitorDesk.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.MonitorDesk;

public sealed class MonitorDeskModule : IPowerDeskModule
{
    public string Id => "MonitorDesk";
    public string DisplayName => "MonitorDesk";
    public string Description => "Inspect display bounds, work areas, and the virtual desktop layout.";
    public string IconKey => "MonitorDesk";
    // Filled 24x24 geometry (nonzero rule; holes wind the opposite way). Both the sidebar and the dashboard
    // render this with Fill only, so open stroke paths would be invisible or collapse into solid blobs.
    public string IconGeometry => "F1 M 2,4 H 22 V 17 H 2 Z M 4,6 V 15 H 20 V 6 Z M 11,17 H 13 V 19 H 11 Z M 7,19 H 17 V 21 H 7 Z";
    public bool RequiresAdminForFullControl => false;

    public MonitorDeskViewModel ViewModel { get; }
    public UserControl MainView { get; }

    public MonitorDeskModule(ILogger log, JsonStorageService storage, StatusService status, RecentActionsService recent)
    {
        ViewModel = new MonitorDeskViewModel(log, storage, status, recent);
        MainView = new MonitorDeskView(ViewModel);
    }

    public Task InitializeAsync() => ViewModel.InitializeAsync();

    public Task ShutdownAsync() => ViewModel.ShutdownAsync();
}
