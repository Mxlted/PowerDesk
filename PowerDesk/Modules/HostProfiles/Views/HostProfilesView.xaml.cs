using PowerDesk.Modules.HostProfiles.ViewModels;
using PowerDesk.Shared.DragDrop;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.HostProfiles.Views;

public partial class HostProfilesView : UserControl
{
    private readonly HostProfilesViewModel _vm;

    public HostProfilesView(HostProfilesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Root_PreviewDragOver(object sender, DragEventArgs e) => FileDrop.OnDragOver(e);

    private void Root_PreviewDrop(object sender, DragEventArgs e)
        => FileDrop.OnDrop(e, paths => _ = _vm.ImportProfilesFromPathsAsync(paths));
}
