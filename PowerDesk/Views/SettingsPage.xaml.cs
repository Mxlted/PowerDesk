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
            App.Instance.ThemeService.Apply(theme);
            App.Instance.Settings.Theme = theme;
            await App.Instance.SaveSettingsAsync();
        }
    }

    private async void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var app = App.Instance;
        app.Settings.StartMinimized = StartMinimizedBox.IsChecked == true;
        app.Settings.MinimizeToTrayOnClose = TrayOnCloseBox.IsChecked == true;
        await app.SaveSettingsAsync();
    }

    private async void RunAtStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var app = App.Instance;
        bool enable = RunAtStartupBox.IsChecked == true;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)!;
            if (enable)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                try { key.DeleteValue(RunValue, throwOnMissingValue: false); } catch { }
            }
            app.Settings.RunAtWindowsStartup = enable;
            await app.SaveSettingsAsync();
            app.Status.Set(enable ? "PowerDesk will start with Windows." : "Removed from Windows startup.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            app.Logger.Error("Toggle startup failed", ex);
            app.Status.Set("Could not update Windows startup entry.", StatusKind.Error);
        }
    }

    private async void GlobalHotkeys_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var app = App.Instance;
        app.Settings.GlobalHotkeysEnabled = GlobalHotkeysBox.IsChecked == true;
        await app.SaveSettingsAsync();
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
            "This permanently deletes all PowerDesk settings, presets, and history.\n\nContinue?",
            "Reset PowerDesk data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            // Delete file contents but keep the folder so the running app remains writable.
            foreach (var f in Directory.EnumerateFiles(PathService.Root, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { }
            }
            App.Instance.Status.Set("Local data reset. Restart PowerDesk to load defaults.", StatusKind.Warning);
        }
        catch (Exception ex)
        {
            App.Instance.Logger.Error("Reset data", ex);
            App.Instance.Status.Set("Reset failed. See logs.", StatusKind.Error);
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
            App.Instance.Shell?.Close();
        else
            App.Instance.Status.Set("Elevation cancelled.", StatusKind.Warning);
    }
}
