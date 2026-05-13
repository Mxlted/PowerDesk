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
    public string IconGeometry => "M 19,3 L 21,5 L 13,13 L 10,14 L 11,11 Z M 4,20 L 9,15 L 12,18 L 7,23 H 4 Z M 5,5 H 13 V 9 H 5 Z";
    public bool RequiresAdminForFullControl => false;

    public UserControl MainView { get; }

    public ColorPickerModule(ILogger log, StatusService status)
    {
        MainView = new ColorPickerView(new ColorPickerViewModel(log, status));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;
}
