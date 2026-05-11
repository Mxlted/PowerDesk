using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.StartupPilot.ViewModels;
using PowerDesk.Modules.StartupPilot.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.StartupPilot;

public sealed class StartupPilotModule : IPowerDeskModule
{
    public string Id => "StartupPilot";
    public string DisplayName => "StartupPilot";
    public string Description => "See and control everything Windows runs at sign-in: registry, startup folder, tasks, and services.";
    public string IconKey => "StartupPilot";
    public bool RequiresAdminForFullControl => true;

    public StartupPilotViewModel ViewModel { get; }
    public UserControl MainView { get; }

    public StartupPilotModule(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        IconService icons,
        PermissionService permissions)
    {
        ViewModel = new StartupPilotViewModel(log, storage, status, recent, icons, permissions);
        MainView = new StartupPilotView(ViewModel);
    }

    public Task InitializeAsync() => ViewModel.InitializeAsync();
    public Task ShutdownAsync()   => ViewModel.ShutdownAsync();
}
