using PowerDesk.Modules.HashDesk.ViewModels;
using PowerDesk.Shared.DragDrop;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.HashDesk.Views;

public partial class HashDeskView : UserControl
{
    private readonly HashDeskViewModel _vm;

    public HashDeskView(HashDeskViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Root_PreviewDragOver(object sender, DragEventArgs e) => FileDrop.OnDragOver(e);

    private void Root_PreviewDrop(object sender, DragEventArgs e)
        => FileDrop.OnDrop(e, paths => _ = _vm.AddFilesAsync(paths));
}
