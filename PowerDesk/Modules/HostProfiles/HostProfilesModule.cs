using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.HostProfiles.ViewModels;
using PowerDesk.Modules.HostProfiles.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.HostProfiles;

public sealed class HostProfilesModule : IPowerDeskModule
{
    public string Id => "HostProfiles";
    public string DisplayName => "HostProfiles";
    public string Description => "Manage hosts file profiles with backups and admin-aware apply.";
    public string IconKey => "HostProfiles";
    // Filled 24x24 geometry (nonzero rule; holes wind the opposite way). Both the sidebar and the dashboard
    // render this with Fill only, so open stroke paths would be invisible or collapse into solid blobs.
    public string IconGeometry => "F1 M 5,2 H 14 L 19,7 V 22 H 5 Z M 7,4 V 20 H 17 V 9 H 12 V 4 Z M 9,12 H 15 V 14 H 9 Z M 9,16 H 15 V 18 H 9 Z";
    public bool RequiresAdminForFullControl => true;

    public HostProfilesViewModel ViewModel { get; }
    public UserControl MainView { get; }

    public HostProfilesModule(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        PermissionService permissions,
        IConfirmationService confirm)
    {
        ViewModel = new HostProfilesViewModel(log, storage, status, recent, permissions, confirm);
        MainView = new HostProfilesView(ViewModel);
    }

    public Task InitializeAsync() => ViewModel.InitializeAsync();
    public Task ShutdownAsync() => ViewModel.ShutdownAsync();
}
