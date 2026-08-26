using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Services;
using PowerDesk.Modules.HashDesk.ViewModels;
using PowerDesk.Modules.HashDesk.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.HashDesk;

public sealed class HashDeskModule : IPowerDeskModule
{
    public string Id => "HashDesk";
    public string DisplayName => "HashDesk";
    public string Description => "Compute SHA256, SHA1, and MD5 hashes for files or text.";
    public string IconKey => "HashDesk";
    // Filled 24x24 geometry (nonzero rule; holes wind the opposite way). Both the sidebar and the dashboard
    // render this with Fill only, so open stroke paths would be invisible or collapse into solid blobs.
    public string IconGeometry => "F1 M 2,7.5 H 22 V 10.5 H 2 Z M 2,13.5 H 22 V 16.5 H 2 Z M 10,2 H 13 L 9,22 H 6 Z M 17,2 H 20 L 16,22 H 13 Z";
    public bool RequiresAdminForFullControl => false;

    public UserControl MainView { get; }

    public HashDeskModule(ILogger log, StatusService status, RecentActionsService recent)
    {
        MainView = new HashDeskView(new HashDeskViewModel(log, status, recent));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;
}
