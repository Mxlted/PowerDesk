using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Modules.FileLockFinder.ViewModels;
using PowerDesk.Modules.FileLockFinder.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.FileLockFinder;

public sealed class FileLockFinderModule : IPowerDeskModule
{
    public string Id => "FileLockFinder";
    public string DisplayName => "FileLockFinder";
    public string Description => "Find and inspect processes locking a file or folder.";
    public string IconKey => "FileLockFinder";
    // Filled 24x24 geometry (nonzero rule; holes wind the opposite way). Both the sidebar and the dashboard
    // render this with Fill only, so open stroke paths would be invisible or collapse into solid blobs.
    public string IconGeometry => "F1 M 6,10 V 8 A 6,6 0 0 1 18,8 V 10 H 16 V 8 A 4,4 0 0 0 8,8 V 10 Z M 3,10 H 21 V 22 H 3 Z M 13,19 V 15.9 A 1.75,1.75 0 1 0 11,15.9 V 19 Z";
    public bool RequiresAdminForFullControl => true;

    public UserControl MainView { get; }

    public FileLockFinderModule(
        ILogger log,
        StatusService status,
        RecentActionsService recent,
        PermissionService permissions,
        IConfirmationService confirm)
    {
        MainView = new FileLockFinderView(new FileLockFinderViewModel(log, status, recent, permissions, confirm));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;
}
