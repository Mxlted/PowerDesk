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
    public string IconGeometry => "M 5,11 H 19 V 21 H 5 Z M 8,11 V 8 A 4,4 0 0 1 16,8 V 11 M 12,15 V 18";
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
