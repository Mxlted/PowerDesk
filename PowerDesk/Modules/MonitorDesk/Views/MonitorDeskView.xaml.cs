using PowerDesk.Modules.MonitorDesk.ViewModels;
using PowerDesk.Shared.DragDrop;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.MonitorDesk.Views;

public partial class MonitorDeskView : UserControl
{
    private readonly MonitorDeskViewModel _vm;

    public MonitorDeskView(MonitorDeskViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Root_PreviewDragOver(object sender, DragEventArgs e) => FileDrop.OnDragOver(e);

    private void Root_PreviewDrop(object sender, DragEventArgs e)
        => FileDrop.OnDrop(e, paths => _ = _vm.ImportLayoutsFromPathsAsync(paths));
}
