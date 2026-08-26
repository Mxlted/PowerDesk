using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Models;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.WindowSizer.ViewModels;
using PowerDesk.Modules.WindowSizer.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.WindowSizer;

public sealed class WindowSizerModule : IPowerDeskModule
{
    public string Id => "WindowSizer";
    public string DisplayName => "WindowSizer";
    public string Description => "Resize, snap, and pin windows with presets and global hotkeys.";
    public string IconKey => "WindowSizer";
    // Filled 24x24 geometry (nonzero rule; holes wind the opposite way). Both the sidebar and the dashboard
    // render this with Fill only, so open stroke paths would be invisible or collapse into solid blobs.
    public string IconGeometry => "F1 M 2,3 H 22 V 21 H 2 Z M 4,5 V 19 H 20 V 5 Z M 4,5 H 20 V 8 H 4 Z M 7,10 H 18 V 17 H 7 Z M 9,12 V 15 H 16 V 12 Z";
    public bool RequiresAdminForFullControl => false;

    public WindowSizerViewModel ViewModel { get; }
    public UserControl MainView { get; }

    public WindowSizerModule(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        IconService icons,
        AppSettings appSettings)
    {
        ViewModel = new WindowSizerViewModel(log, storage, status, recent, icons, appSettings);
        MainView = new WindowSizerView(ViewModel);
    }

    public Task InitializeAsync() => ViewModel.InitializeAsync();
    public Task ShutdownAsync()   => ViewModel.ShutdownAsync();
}
