using System.ComponentModel;
using System.Windows;
using PowerDesk.Modules.WindowSizer.Models;
using PowerDesk.Modules.WindowSizer.ViewModels;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;
using UserControl = System.Windows.Controls.UserControl;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using KeyInterop = System.Windows.Input.KeyInterop;
using ModifierKeys = System.Windows.Input.ModifierKeys;

namespace PowerDesk.Modules.WindowSizer.Views;

public partial class WindowSizerView : UserControl, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private WindowSizerViewModel? _vm;
    private uint _recordedModifiers;
    private uint _recordedVk;
    private string _recorderText = "Press a combination…";
    public string RecorderText
    {
        get => _recorderText;
        set { _recorderText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecorderText))); }
    }

    public WindowSizerView(WindowSizerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void HotkeyRecorder_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // Skip pure-modifier presses (the user is still building the chord).
        if (e.Key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
                  or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        uint modFlags = 0;
        if ((mods & ModifierKeys.Control) != 0) modFlags |= MOD_CONTROL;
        if ((mods & ModifierKeys.Alt)     != 0) modFlags |= MOD_ALT;
        if ((mods & ModifierKeys.Shift)   != 0) modFlags |= MOD_SHIFT;
        if ((mods & ModifierKeys.Windows) != 0) modFlags |= MOD_WIN;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (modFlags == 0 || vk == 0)
        {
            RecorderText = "Add at least one modifier (Ctrl/Alt/Shift/Win) + a key.";
            return;
        }

        _recordedModifiers = modFlags;
        _recordedVk = vk;
        var temp = new HotkeyBinding { Modifiers = modFlags, VirtualKey = vk };
        RecorderText = temp.DisplayText;
    }

    private async void AddHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_recordedVk == 0 || _recordedModifiers == 0)
        {
            App.Instance.Status.Set("Press a key combination first.", Core.Services.StatusKind.Warning);
            return;
        }
        if (ActionPicker.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!System.Enum.TryParse<HotkeyAction>(tag, out var action)) return;

        var binding = new HotkeyBinding
        {
            Action = action,
            Modifiers = _recordedModifiers,
            VirtualKey = _recordedVk,
            Enabled = true,
        };
        if (_vm.HasConflict(binding))
        {
            App.Instance.Status.Set("That combination is already used.", Core.Services.StatusKind.Warning);
            return;
        }
        await _vm.AddHotkeyAsync(binding);
        RecorderText = "Press a combination…";
        _recordedVk = 0; _recordedModifiers = 0;
    }

    private void ReapplyHotkeys_Click(object sender, RoutedEventArgs e) => _vm?.RefreshHotkeyRegistrations();
}
