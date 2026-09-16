using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.PathEditor.ViewModels;
using PowerDesk.Modules.PathEditor.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.PathEditor;

public sealed class PathEditorModule : IPowerDeskModule
{
    public string Id => "PathEditor";
    public string DisplayName => "PathEditor";
    public string Description => "Edit User and Machine PATH entries with validation and backup history.";
    // Filled 24x24 geometry (nonzero rule; holes wind the opposite way). Both the sidebar and the dashboard
    // render this with Fill only, so open stroke paths would be invisible or collapse into solid blobs.
    public string IconGeometry => "F1 M 2,4 H 9 L 11,6 H 22 V 20 H 2 Z M 4,8 V 18 H 20 V 8 Z M 10,9 L 14,13 L 10,17 L 8.5,15.5 L 11,13 L 8.5,10.5 Z";
    public bool RequiresAdminForFullControl => true;

    public PathEditorViewModel ViewModel { get; }
    public UserControl MainView { get; }

    public PathEditorModule(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        PermissionService permissions,
        IConfirmationService confirm)
    {
        ViewModel = new PathEditorViewModel(log, storage, status, recent, permissions, confirm);
        MainView = new PathEditorView(ViewModel);
    }

    public Task InitializeAsync() => ViewModel.InitializeAsync();
    public Task ShutdownAsync() => ViewModel.ShutdownAsync();
}
