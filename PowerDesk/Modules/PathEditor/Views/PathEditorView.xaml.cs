using PowerDesk.Modules.PathEditor.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Modules.PathEditor.Views;

public partial class PathEditorView : UserControl
{
    public PathEditorView(PathEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
