using RoutedEventArgs = System.Windows.RoutedEventArgs;
using Window = System.Windows.Window;

namespace PowerDesk.Modules.ColorPicker.Views;

public partial class ColorPickModeDialog : Window
{
    public bool MinimizePowerDesk { get; private set; }

    public ColorPickModeDialog()
    {
        InitializeComponent();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        MinimizePowerDesk = true;
        DialogResult = true;
    }

    private void KeepVisible_Click(object sender, RoutedEventArgs e)
    {
        MinimizePowerDesk = false;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
