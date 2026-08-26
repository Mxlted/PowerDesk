using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using PowerDesk.Core.Services;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace PowerDesk.Views;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = GetVersion();
        ModuleList.ItemsSource = App.Instance.Modules.Modules;
    }

    private static string GetVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip any "+commit" suffix that SourceLink-style builds append.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex)
        {
            App.Instance.Logger.Error("Open link", ex);
            App.Instance.Status.Set("Could not open the browser.", StatusKind.Error);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var app = App.Instance;
        var text =
            $"PowerDesk {GetVersion()}\n" +
            $"OS: {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})\n" +
            $".NET: {Environment.Version}\n" +
            $"Elevated: {app.Permissions.IsAdministrator}\n" +
            $"Theme: {app.Settings.Theme}\n" +
            $"Modules: {string.Join(", ", app.Modules.Modules.Select(m => m.Id))}\n" +
            $"Data: {PathService.Root}";
        try
        {
            Clipboard.SetDataObject(text, copy: true);
            app.Status.Set("Diagnostics copied to clipboard.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            app.Logger.Warn($"Clipboard unavailable: {ex.Message}");
            app.Status.Set("Clipboard is busy; try again.", StatusKind.Warning);
        }
    }
}
