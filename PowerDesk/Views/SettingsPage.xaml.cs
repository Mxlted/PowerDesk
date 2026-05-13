using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PowerDesk.Core.Models;
using PowerDesk.Core.Services;
using UserControl = System.Windows.Controls.UserControl;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PowerDesk.Views;

public partial class SettingsPage : UserControl
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PowerDesk";
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var app = App.Instance;

        foreach (ComboBoxItem item in ThemeBox.Items)
            if ((string)item.Tag == app.Settings.Theme.ToString())
                ThemeBox.SelectedItem = item;
        if (ThemeBox.SelectedItem is null) ThemeBox.SelectedIndex = 0;

        StartMinimizedBox.IsChecked = app.Settings.StartMinimized;
        TrayOnCloseBox.IsChecked = app.Settings.MinimizeToTrayOnClose;
        RunAtStartupBox.IsChecked = app.Settings.RunAtWindowsStartup;
        GlobalHotkeysBox.IsChecked = app.Settings.GlobalHotkeysEnabled;
        LogPathRun.Text = app.Logger.LogFilePath;
        _loading = false;
    }

    private async void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<AppTheme>(tag, out var theme))
        {
            var previous = App.Instance.Settings.Theme;
            App.Instance.ThemeService.Apply(theme);
            App.Instance.Settings.Theme = theme;
            if (!await App.Instance.SaveSettingsAsync())
            {
                App.Instance.Settings.Theme = previous;
                App.Instance.ThemeService.Apply(previous);
                _loading = true;
                foreach (ComboBoxItem themeItem in ThemeBox.Items)
                    if ((string)themeItem.Tag == previous.ToString())
                        ThemeBox.SelectedItem = themeItem;
                _loading = false;
                App.Instance.Status.Set("Could not save theme setting.", StatusKind.Error);
            }
        }
    }

    private async void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var app = App.Instance;
        var previousStartMinimized = app.Settings.StartMinimized;
        var previousTrayOnClose = app.Settings.MinimizeToTrayOnClose;
        app.Settings.StartMinimized = StartMinimizedBox.IsChecked == true;
        app.Settings.MinimizeToTrayOnClose = TrayOnCloseBox.IsChecked == true;
        if (!await app.SaveSettingsAsync())
        {
            app.Settings.StartMinimized = previousStartMinimized;
            app.Settings.MinimizeToTrayOnClose = previousTrayOnClose;
            _loading = true;
            StartMinimizedBox.IsChecked = previousStartMinimized;
            TrayOnCloseBox.IsChecked = previousTrayOnClose;
            _loading = false;
            app.Status.Set("Could not save behavior setting.", StatusKind.Error);
        }
    }

    private async void RunAtStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var app = App.Instance;
        bool enable = RunAtStartupBox.IsChecked == true;
        bool previous = app.Settings.RunAtWindowsStartup;

        // Update the registry FIRST; only flip the persisted setting after the registry write
        // succeeded. If the subsequent save fails we restore the registry to keep the two in sync.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)!;
            if (enable)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exe))
                {
                    app.Status.Set("Could not determine PowerDesk's exe path.", StatusKind.Error);
                    _loading = true; RunAtStartupBox.IsChecked = previous; _loading = false;
                    return;
                }
                key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                try { key.DeleteValue(RunValue, throwOnMissingValue: false); } catch { }
            }
        }
        catch (Exception ex)
        {
            app.Logger.Error("Toggle startup (registry)", ex);
            app.Status.Set("Could not update Windows startup entry.", StatusKind.Error);
            _loading = true; RunAtStartupBox.IsChecked = previous; _loading = false;
            return;
        }

        app.Settings.RunAtWindowsStartup = enable;
        var saved = false;
        try { saved = await app.SaveSettingsAsync(); }
        catch (Exception ex) { app.Logger.Error("Toggle startup (save)", ex); }

        if (saved)
        {
            app.Status.Set(enable ? "PowerDesk will start with Windows." : "Removed from Windows startup.", StatusKind.Success);
            return;
        }

        // Save failed: roll the registry back so what's on disk matches what's persisted.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is not null)
            {
                if (previous)
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValue, $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue(RunValue, throwOnMissingValue: false);
                }
            }
        }
        catch (Exception revertEx) { app.Logger.Error("Toggle startup revert", revertEx); }
        app.Settings.RunAtWindowsStartup = previous;
        _loading = true; RunAtStartupBox.IsChecked = previous; _loading = false;
        app.Status.Set("Could not persist startup change.", StatusKind.Error);
    }

    private async void GlobalHotkeys_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var app = App.Instance;
        var previous = app.Settings.GlobalHotkeysEnabled;
        app.Settings.GlobalHotkeysEnabled = GlobalHotkeysBox.IsChecked == true;
        if (!await app.SaveSettingsAsync())
        {
            app.Settings.GlobalHotkeysEnabled = previous;
            _loading = true; GlobalHotkeysBox.IsChecked = previous; _loading = false;
            app.WindowSizerModule?.ViewModel?.RefreshHotkeyRegistrations();
            app.Status.Set("Could not save hotkey setting.", StatusKind.Error);
            return;
        }
        app.WindowSizerModule?.ViewModel?.RefreshHotkeyRegistrations();
        app.Status.Set(app.Settings.GlobalHotkeysEnabled ? "Global hotkeys enabled." : "Global hotkeys disabled.", StatusKind.Info);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = PathService.Root, UseShellExecute = true }); }
        catch (Exception ex) { App.Instance.Logger.Error("Open folder", ex); }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = App.Instance.Logger.LogFilePath, UseShellExecute = true }); }
        catch (Exception ex) { App.Instance.Logger.Error("Open log", ex); }
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"powerdesk-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                Filter = "Log files (*.log)|*.log|All files|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                File.Copy(App.Instance.Logger.LogFilePath, dlg.FileName, overwrite: true);
                App.Instance.Status.Set("Log exported.", StatusKind.Success);
            }
        }
        catch (Exception ex)
        {
            App.Instance.Logger.Error("Export logs", ex);
            App.Instance.Status.Set("Could not export logs.", StatusKind.Error);
        }
    }

    private void ResetData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            Window.GetWindow(this) ?? App.Instance.Shell!,
            "This permanently deletes all PowerDesk settings, presets, and history. " +
            "PowerDesk will restart to load defaults.\n\nContinue?",
            "Reset PowerDesk data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        var app = App.Instance;
        try
        {
            // Replace in-memory settings with defaults BEFORE deleting files so any subsequent
            // save (on shutdown, etc.) writes defaults rather than resurrecting old state.
            app.Settings.Theme = AppTheme.Dark;
            app.Settings.StartMinimized = false;
            app.Settings.MinimizeToTrayOnClose = true;
            app.Settings.GlobalHotkeysEnabled = true;
            app.Settings.LastPage = "Dashboard";
            // Run-at-startup is kept in sync with the registry; flip it off explicitly below.

            // Best-effort: also wipe the registry "Run" entry so a reset really is a reset.
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                key?.DeleteValue(RunValue, throwOnMissingValue: false);
            }
            catch { }
            app.Settings.RunAtWindowsStartup = false;

            // Delete file contents but keep the folder so the running app remains writable.
            foreach (var f in Directory.EnumerateFiles(PathService.Root, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { }
            }

            // Relaunch so every module re-initializes from a clean slate.
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            }
            catch (Exception ex) { app.Logger.Error("Reset relaunch", ex); }

            app.Status.Set("Local data reset. Restarting…", StatusKind.Warning);
            app.SkipShutdownPersistenceOnce();
            app.Shell?.ForceClose();
        }
        catch (Exception ex)
        {
            app.Logger.Error("Reset data", ex);
            app.Status.Set("Reset failed. See logs.", StatusKind.Error);
        }
    }

    private void RelaunchAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (App.Instance.Permissions.IsAdministrator)
        {
            App.Instance.Status.Set("Already running as administrator.", StatusKind.Info);
            return;
        }
        if (App.Instance.Permissions.TryRelaunchAsAdmin())
            App.Instance.Shell?.ForceClose();
        else
            App.Instance.Status.Set("Elevation cancelled.", StatusKind.Warning);
    }
}
