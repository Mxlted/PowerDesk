using PowerDesk.Modules.FileLockFinder.ViewModels;
using PowerDesk.Shared.DragDrop;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.FileLockFinder.Views;

public partial class FileLockFinderView : UserControl
{
    private readonly FileLockFinderViewModel _vm;

    public FileLockFinderView(FileLockFinderViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Root_PreviewDragOver(object sender, DragEventArgs e) => FileDrop.OnDragOver(e);

    private void Root_PreviewDrop(object sender, DragEventArgs e)
        => FileDrop.OnDrop(e, _vm.SetTargetPathsFromDrop);
}
