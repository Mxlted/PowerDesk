using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Services;
using PowerDesk.Modules.ColorPicker.ViewModels;
using PowerDesk.Modules.ColorPicker.Views;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.ColorPicker;

public sealed class ColorPickerModule : IPowerDeskModule
{
    public string Id => "ColorPicker";
    public string DisplayName => "ColorPicker";
    public string Description => "Sample screen pixels and copy HEX, RGB, or HSL color values.";
    public string IconKey => "ColorPicker";
    // Filled 24x24 geometry (nonzero rule; holes wind the opposite way). Both the sidebar and the dashboard
    // render this with Fill only, so open stroke paths would be invisible or collapse into solid blobs.
    public string IconGeometry => "F1 M 19,2 A 3,3 0 1 1 19,8 A 3,3 0 1 1 19,2 Z M 11.7,8.7 L 15.2,5.2 L 18.8,8.8 L 15.3,12.3 Z M 3.9,17.9 L 13.4,8.4 L 15.6,10.6 L 6.1,20.1 Z M 3.9,17.9 L 6.1,20.1 L 2,22 Z";
    public bool RequiresAdminForFullControl => false;

    public UserControl MainView { get; }

    public ColorPickerModule(ILogger log, StatusService status)
    {
        MainView = new ColorPickerView(new ColorPickerViewModel(log, status));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;
}
