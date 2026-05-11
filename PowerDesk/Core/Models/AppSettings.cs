namespace PowerDesk.Core.Models;

public enum AppTheme { Dark, Light, OledDark }

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool StartMinimized { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool RunAtWindowsStartup { get; set; } = false;
    public bool GlobalHotkeysEnabled { get; set; } = true;
    public string LastPage { get; set; } = "Dashboard";
}
