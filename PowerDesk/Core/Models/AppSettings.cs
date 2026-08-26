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

    /// <summary>Last shell window placement (device-independent pixels). Null until the window has been shown once.</summary>
    public WindowPlacement? Window { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>
    /// True when the placement is sane and at least partially inside the given virtual-screen rectangle,
    /// so a window saved on a monitor that is now unplugged is not restored off-screen.
    /// </summary>
    public bool IsUsable(double screenLeft, double screenTop, double screenWidth, double screenHeight, double minWidth, double minHeight)
    {
        if (double.IsNaN(Left) || double.IsNaN(Top) || double.IsNaN(Width) || double.IsNaN(Height)) return false;
        if (Width < minWidth || Height < minHeight) return false;
        var right = Left + Width;
        var bottom = Top + Height;
        var screenRight = screenLeft + screenWidth;
        var screenBottom = screenTop + screenHeight;
        // Require a reasonable chunk of the title bar area to be visible.
        const double minVisible = 80;
        return right - minVisible > screenLeft && Left + minVisible < screenRight
            && bottom - minVisible > screenTop && Top + minVisible < screenBottom;
    }
}
