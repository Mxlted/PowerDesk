using PowerDesk.Modules.DnsDesk.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.DnsDesk.Views;

public partial class DnsDeskView : UserControl
{
    public DnsDeskView(DnsDeskViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
