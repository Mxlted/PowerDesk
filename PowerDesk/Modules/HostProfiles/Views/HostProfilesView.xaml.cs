using PowerDesk.Modules.HostProfiles.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.HostProfiles.Views;

public partial class HostProfilesView : UserControl
{
    public HostProfilesView(HostProfilesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
