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
    public string IconKey => "PathEditor";
    public string IconGeometry => "M 4,5 H 20 V 9 H 4 Z M 4,15 H 20 V 19 H 4 Z M 8,9 V 15 M 16,9 V 15";
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
