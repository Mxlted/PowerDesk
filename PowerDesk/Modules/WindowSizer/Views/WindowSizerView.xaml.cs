using System;
using System.ComponentModel;
using System.Windows;
using PowerDesk.Core.Services;
using PowerDesk.Modules.WindowSizer.Models;
using PowerDesk.Modules.WindowSizer.ViewModels;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;
using UserControl = System.Windows.Controls.UserControl;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using KeyInterop = System.Windows.Input.KeyInterop;
using ModifierKeys = System.Windows.Input.ModifierKeys;

namespace PowerDesk.Modules.WindowSizer.Views;

public partial class WindowSizerView : UserControl, INotifyPropertyChanged
{
    private const string RecorderPrompt = "Press a combination…";

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly WindowSizerViewModel _vm;
    private uint _recordedModifiers;
    private uint _recordedVk;
    private string _recorderText = RecorderPrompt;
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
        ActionPicker.SelectedIndex = 0;
    }

    private void HotkeyRecorder_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Skip pure-modifier presses (the user is still building the chord).
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        if (key == Key.Escape)
        {
            _recordedVk = 0; _recordedModifiers = 0;
            RecorderText = RecorderPrompt;
            return;
        }

        var mods = Keyboard.Modifiers;
        uint modFlags = 0;
        if ((mods & ModifierKeys.Control) != 0) modFlags |= MOD_CONTROL;
        if ((mods & ModifierKeys.Alt)     != 0) modFlags |= MOD_ALT;
        if ((mods & ModifierKeys.Shift)   != 0) modFlags |= MOD_SHIFT;
        if ((mods & ModifierKeys.Windows) != 0) modFlags |= MOD_WIN;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (modFlags == 0 || vk == 0)
        {
            _recordedVk = 0; _recordedModifiers = 0;
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
        try
        {
            if (_recordedVk == 0 || _recordedModifiers == 0)
            {
                App.Instance.Status.Set("Press a key combination in the recorder first.", StatusKind.Warning);
                HotkeyRecorder.Focus();
                return;
            }
            if (ActionPicker.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
            if (!Enum.TryParse<HotkeyAction>(tag, out var action)) return;

            var binding = new HotkeyBinding
            {
                Action = action,
                Modifiers = _recordedModifiers,
                VirtualKey = _recordedVk,
                Enabled = true,
            };
            if (_vm.HasConflict(binding))
            {
                App.Instance.Status.Set("That combination is already used.", StatusKind.Warning);
                return;
            }
            await _vm.AddHotkeyAsync(binding);
            RecorderText = RecorderPrompt;
            _recordedVk = 0; _recordedModifiers = 0;
        }
        catch (Exception ex)
        {
            App.Instance.Logger.Error("Add hotkey", ex);
            App.Instance.Status.Set("Could not add that hotkey.", StatusKind.Error);
        }
    }

    private void ReapplyHotkeys_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.RefreshHotkeyRegistrations();
            if (!_vm.HasHotkeyWarning)
                App.Instance.Status.Set($"Hotkeys re-registered: {_vm.HotkeyStatusText}.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            App.Instance.Logger.Error("Re-apply hotkeys", ex);
        }
    }

    private async void HotkeyEnable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is CheckBox cb && cb.DataContext is HotkeyBinding b)
            {
                bool target = cb.IsChecked == true;
                // Keep the visual in sync with the model until we've committed. The VM is the source of truth.
                cb.IsChecked = b.Enabled;
                await _vm.SetHotkeyEnabledAsync(b, target);
            }
        }
        catch (Exception ex)
        {
            App.Instance.Logger.Error("Toggle hotkey", ex);
            App.Instance.Status.Set("Could not change that hotkey.", StatusKind.Error);
        }
    }
}
