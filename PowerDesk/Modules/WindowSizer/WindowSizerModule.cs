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
    public string IconGeometry => "M 3,3 H 21 V 21 H 3 Z M 3,8 H 21 M 8,3 V 21";
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
