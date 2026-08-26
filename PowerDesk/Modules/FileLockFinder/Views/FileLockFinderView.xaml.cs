using PowerDesk.Modules.FileLockFinder.ViewModels;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
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

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            e.Handled = true;
            _vm.SetTargetPathsFromDrop(paths);
        }
        catch
        {
            // Drag sources can hand over broken data objects; never let a drop crash the shell.
        }
    }
}
