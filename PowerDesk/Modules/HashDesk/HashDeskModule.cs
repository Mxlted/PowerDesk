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
    public string IconGeometry => "M 7,3 H 17 L 21,7 V 21 H 7 Z M 17,3 V 7 H 21 M 3,8 H 12 M 3,12 H 15 M 3,16 H 13";
    public bool RequiresAdminForFullControl => false;

    public UserControl MainView { get; }

    public HashDeskModule(ILogger log, StatusService status, RecentActionsService recent)
    {
        MainView = new HashDeskView(new HashDeskViewModel(log, status, recent));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;
}
