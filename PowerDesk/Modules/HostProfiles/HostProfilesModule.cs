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
    public string IconGeometry => "M 5,3 H 19 V 21 H 5 Z M 8,7 H 16 M 8,11 H 16 M 8,15 H 13";
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
