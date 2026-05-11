using System;
using System.Drawing;
using System.Windows;
using PowerDesk.Core.Logging;
using Application = System.Windows.Application;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;
using MouseButtons = System.Windows.Forms.MouseButtons;

namespace PowerDesk.Core.Services;

/// <summary>
/// Wraps a Windows Forms NotifyIcon. Provides a context menu with shortcuts to each module plus utilities.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ILogger _log;
    private NotifyIcon? _icon;
    private bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<string>? OpenModuleRequested;
    public event EventHandler? RescanStartupRequested;
    public event EventHandler? SnapForegroundLeftRequested;
    public event EventHandler? SnapForegroundRightRequested;

    public TrayIconService(ILogger log) => _log = log;

    public void Initialize()
    {
        try
        {
            _icon = new NotifyIcon
            {
                Visible = true,
                Icon = TryLoadEmbeddedIcon() ?? SystemIcons.Application,
                Text = "PowerDesk",
            };
            _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
            _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowRequested?.Invoke(this, EventArgs.Empty); };
            _icon.ContextMenuStrip = BuildMenu();
        }
        catch (Exception ex)
        {
            _log.Error("Tray icon init", ex);
        }
    }

    public void ShowBalloon(string title, string text)
    {
        try { _icon?.ShowBalloonTip(2500, title, text, System.Windows.Forms.ToolTipIcon.Info); }
        catch { }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Show PowerDesk", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open WindowSizer", null, (_, _) => OpenModuleRequested?.Invoke(this, "WindowSizer")));
        menu.Items.Add(new ToolStripMenuItem("Open StartupPilot", null, (_, _) => OpenModuleRequested?.Invoke(this, "StartupPilot")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Snap foreground left",  null, (_, _) => SnapForegroundLeftRequested?.Invoke(this,  EventArgs.Empty)));
        menu.Items.Add(new ToolStripMenuItem("Snap foreground right", null, (_, _) => SnapForegroundRightRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripMenuItem("Rescan startup items",  null, (_, _) => RescanStartupRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
        return menu;
    }

    private static Icon? TryLoadEmbeddedIcon()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
                return Icon.ExtractAssociatedIcon(exe);
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); } } catch { }
        _icon = null;
    }
}
