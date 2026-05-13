using System.Linq;
using System.Windows;
using PowerDesk.Modules.HashDesk.ViewModels;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
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

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await _vm.AddFilesAsync(paths.Where(System.IO.File.Exists));
    }
}
