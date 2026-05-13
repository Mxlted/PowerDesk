using PowerDesk.Modules.MonitorDesk.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.MonitorDesk.Views;

public partial class MonitorDeskView : UserControl
{
    public MonitorDeskView(MonitorDeskViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
