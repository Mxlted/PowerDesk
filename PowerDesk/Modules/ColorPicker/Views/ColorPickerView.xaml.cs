using PowerDesk.Modules.ColorPicker.ViewModels;
using PowerDesk.Core.Services;
using Grid = System.Windows.Controls.Grid;
using Cursors = System.Windows.Input.Cursors;
using Key = System.Windows.Input.Key;
using MediaColor = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using SystemParameters = System.Windows.SystemParameters;
using UserControl = System.Windows.Controls.UserControl;
using Window = System.Windows.Window;

namespace PowerDesk.Modules.ColorPicker.Views;

public partial class ColorPickerView : UserControl
{
    private readonly ColorPickerViewModel _vm;
    private Window? _pickerWindow;

    public ColorPickerView(ColorPickerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void PickScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_pickerWindow is not null)
        {
            _pickerWindow.Close();
            return;
        }

        var owner = Window.GetWindow(this);
        var minimizeOwner = ConfirmPickMode(owner);
        if (minimizeOwner is null) return;

        var restoreOwner = minimizeOwner.Value && owner?.IsVisible == true;
        var previousState = owner?.WindowState;

        var overlay = new Window
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)),
            ShowInTaskbar = false,
            Topmost = true,
            Cursor = Cursors.Cross,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
            Focusable = true,
            Content = new Grid
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)),
            },
        };

        var restoredOwner = false;
        void RestoreOwner()
        {
            if (!restoreOwner || restoredOwner || owner is null) return;

            if (owner == App.Instance.Shell)
            {
                App.Instance.ShowShell();
            }
            else
            {
                owner.Show();
            }

            owner.WindowState = previousState == System.Windows.WindowState.Minimized
                ? System.Windows.WindowState.Normal
                : previousState ?? System.Windows.WindowState.Normal;
            owner.Activate();
            restoredOwner = true;
        }

        void Finish(bool restore)
        {
            if (overlay.IsVisible) overlay.Hide();
            overlay.Close();
            if (restore) RestoreOwner();
        }

        async void CompletePick(MouseEventArgs args)
        {
            var point = overlay.PointToScreen(args.GetPosition(overlay));
            overlay.Hide();
            await System.Threading.Tasks.Task.Delay(75);
            _vm.SampleScreenPixelAt((int)point.X, (int)point.Y, announce: true);
            Finish(restore: true);
        }

        overlay.PreviewMouseLeftButtonDown += (_, args) => CompletePick(args);
        overlay.MouseRightButtonDown += (_, _) => Finish(restore: true);
        overlay.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) Finish(restore: true);
        };
        overlay.Closed += (_, _) =>
        {
            _pickerWindow = null;
            RestoreOwner();
        };

        _pickerWindow = overlay;
        App.Instance.Status.Set("Pick mode is active. Left-click samples; right-click or Esc cancels.", StatusKind.Info);
        if (restoreOwner && owner is not null)
            owner.WindowState = System.Windows.WindowState.Minimized;
        overlay.Show();
        overlay.Activate();
        overlay.Focus();
    }

    private static bool? ConfirmPickMode(Window? owner)
    {
        var dialog = new ColorPickModeDialog
        {
            Owner = owner,
        };

        return dialog.ShowDialog() == true ? dialog.MinimizePowerDesk : null;
    }
}
