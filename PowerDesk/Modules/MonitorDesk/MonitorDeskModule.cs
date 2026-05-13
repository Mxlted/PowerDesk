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
    public string IconGeometry => "M 3,4 H 21 V 16 H 3 Z M 9,20 H 15 M 12,16 V 20";
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
