using PowerDesk.Modules.PathEditor.ViewModels;
using PowerDesk.Shared.DragDrop;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.PathEditor.Views;

public partial class PathEditorView : UserControl
{
    private readonly PathEditorViewModel _vm;

    public PathEditorView(PathEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Root_PreviewDragOver(object sender, DragEventArgs e) => FileDrop.OnDragOver(e);

    private void Root_PreviewDrop(object sender, DragEventArgs e)
        => FileDrop.OnDrop(e, paths => _vm.AddEntriesFromDrop(paths));
}
