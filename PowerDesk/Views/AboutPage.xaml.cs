using System.Reflection;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Views;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionText.Text = ver;
        ModuleList.ItemsSource = App.Instance.Modules.Modules;
    }
}
