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

    /// <param name="modules">Registered tools, in sidebar order; each gets an "Open …" entry in the menu.</param>
    public void Initialize(IReadOnlyList<(string Id, string DisplayName)> modules)
    {
        try
        {
            _icon = new NotifyIcon
            {
                Visible = true,
                Icon = TryLoadEmbeddedIcon() ?? SystemIcons.Application,
                Text = "PowerDesk",
            };
            // A single left click already shows the shell; DoubleClick would fire it a second time.
            _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowRequested?.Invoke(this, EventArgs.Empty); };
            _icon.ContextMenuStrip = BuildMenu(modules);
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

    private ContextMenuStrip BuildMenu(IReadOnlyList<(string Id, string DisplayName)> modules)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Show PowerDesk", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        // One entry per registered tool, so new modules show up here without touching the shell.
        if (modules.Count > 0)
        {
            var open = new ToolStripMenuItem("Open tool");
            foreach (var (id, name) in modules)
            {
                var moduleId = id;
                open.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => OpenModuleRequested?.Invoke(this, moduleId)));
            }
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());
        }
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
            var exe = Environment.ProcessPath;
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
        try
        {
            if (_icon is not null)
            {
                _icon.Visible = false;
                var ico = _icon.Icon;
                _icon.ContextMenuStrip?.Dispose();
                _icon.Dispose();
                if (ico is not null && !ReferenceEquals(ico, SystemIcons.Application)) ico.Dispose();
            }
        }
        catch { }
        _icon = null;
    }
}
