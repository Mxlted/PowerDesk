using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using PowerDesk.Core.Services;
using PowerDesk.Modules.ColorPicker.ViewModels;
using Cursors = System.Windows.Input.Cursors;
using Grid = System.Windows.Controls.Grid;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MediaColor = System.Windows.Media.Color;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Window = System.Windows.Window;

namespace PowerDesk.Modules.ColorPicker.Views;

public partial class ColorPickerView : UserControl
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int WmDpiChanged = 0x02E0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly ColorPickerViewModel _vm;
    private Window? _pickerWindow;

    public ColorPickerView(ColorPickerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_vm.IsHexValid && _vm.CommitHexCommand.CanExecute(null))
            _vm.CommitHexCommand.Execute(null);
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        // Push the pending text through the binding before committing.
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _vm.CommitHexCommand.Execute(null);
        e.Handled = true;
    }

    private void PickScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_pickerWindow is not null)
        {
            _pickerWindow.Close();
            return;
        }

        try
        {
            StartPick();
        }
        catch (Exception ex)
        {
            App.Instance.Logger.Error("ColorPicker pick mode", ex);
            App.Instance.Status.Set("Could not start pick mode.", StatusKind.Warning);
        }
    }

    private void StartPick()
    {
        var owner = Window.GetWindow(this);
        var minimizeOwner = ConfirmPickMode(owner);
        if (minimizeOwner is null) return;

        var restoreOwner = minimizeOwner.Value && owner?.IsVisible == true;
        var previousState = owner?.WindowState;

        var transparent = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0));
        transparent.Freeze();

        var overlay = new Window
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            Cursor = Cursors.Cross,
            Left = System.Windows.SystemParameters.VirtualScreenLeft,
            Top = System.Windows.SystemParameters.VirtualScreenTop,
            Width = Math.Max(1, System.Windows.SystemParameters.VirtualScreenWidth),
            Height = Math.Max(1, System.Windows.SystemParameters.VirtualScreenHeight),
            Focusable = true,
            Content = new Grid { Background = transparent },
        };

        // Size the native window to the whole virtual screen in physical pixels. WPF's DIP-based
        // Left/Width would be scaled by the DPI of whichever monitor the window lands on, leaving
        // parts of a mixed-DPI desktop uncovered.
        overlay.SourceInitialized += (_, _) =>
        {
            try
            {
                var handle = new WindowInteropHelper(overlay).Handle;
                if (handle == IntPtr.Zero) return;
                HwndSource.FromHwnd(handle)?.AddHook(SuppressDpiChange);
                CoverVirtualScreen(handle);
            }
            catch (Exception ex)
            {
                App.Instance.Logger.Error("ColorPicker overlay sizing", ex);
            }
        };

        var restoredOwner = false;
        void RestoreOwner()
        {
            if (!restoreOwner || restoredOwner || owner is null) return;
            restoredOwner = true;
            try
            {
                if (owner == App.Instance.Shell)
                    App.Instance.ShowShell();
                else
                    owner.Show();

                owner.WindowState = previousState is null or System.Windows.WindowState.Minimized
                    ? System.Windows.WindowState.Normal
                    : previousState.Value;
                owner.Activate();
            }
            catch (Exception ex)
            {
                App.Instance.Logger.Error("ColorPicker restore owner", ex);
            }
        }

        var finished = false;
        void Finish()
        {
            if (finished) return;
            finished = true;
            try
            {
                if (overlay.IsVisible) overlay.Hide();
                overlay.Close();
            }
            catch (Exception ex)
            {
                App.Instance.Logger.Error("ColorPicker overlay close", ex);
            }
            RestoreOwner();
        }

        async void CompletePick()
        {
            if (finished) return;
            try
            {
                overlay.Hide();
                // Give the compositor a frame to remove the overlay before reading the pixel underneath.
                await System.Threading.Tasks.Task.Delay(75);
                // GetCursorPos returns physical pixels for the whole virtual screen, so no DPI conversion is needed.
                _vm.SampleAtCursor(announce: true);
            }
            catch (Exception ex)
            {
                App.Instance.Logger.Error("ColorPicker sample", ex);
                App.Instance.Status.Set("Screen pixel sample failed.", StatusKind.Warning);
            }
            finally
            {
                Finish();
            }
        }

        overlay.PreviewMouseLeftButtonDown += (_, args) => { args.Handled = true; CompletePick(); };
        overlay.PreviewMouseRightButtonDown += (_, args) => { args.Handled = true; Finish(); };
        overlay.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Finish();
        };
        overlay.Closed += (_, _) =>
        {
            _pickerWindow = null;
            finished = true;
            RestoreOwner();
        };

        _pickerWindow = overlay;
        App.Instance.Status.Set("Pick mode: left-click samples a pixel, right-click or Esc cancels.", StatusKind.Info);
        if (restoreOwner && owner is not null)
            owner.WindowState = System.Windows.WindowState.Minimized;
        overlay.Show();
        overlay.Activate();
        overlay.Focus();
    }

    private static void CoverVirtualScreen(IntPtr handle)
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var w = GetSystemMetrics(SmCxVirtualScreen);
        var h = GetSystemMetrics(SmCyVirtualScreen);
        if (w <= 0 || h <= 0) return;
        SetWindowPos(handle, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// A window spanning monitors with different DPI receives WM_DPICHANGED with a suggested rect
    /// that would shrink or grow it to one monitor. The overlay has no scalable content, so swallow it.
    /// </summary>
    private static IntPtr SuppressDpiChange(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDpiChanged)
        {
            handled = true;
            CoverVirtualScreen(hwnd);
        }
        return IntPtr.Zero;
    }

    private static bool? ConfirmPickMode(Window? owner)
    {
        var dialog = new ColorPickModeDialog
        {
            Owner = owner is { IsVisible: true } ? owner : null,
        };
        if (dialog.Owner is null)
            dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

        return dialog.ShowDialog() == true ? dialog.MinimizePowerDesk : null;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
