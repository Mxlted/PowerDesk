using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Interop;
using PowerDesk.Core.Models;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Services;
using PowerDesk.Views;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace PowerDesk;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly ObservableCollection<ModuleNavItem> _allModuleItems = new();
    private ICollectionView? _moduleView;
    private string _navSearchQuery = string.Empty;

    private bool _forceClose;
    public void ForceClose() { _forceClose = true; Close(); }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        SourceInitialized += (_, _) => ApplyNativeTitleBarTheme();
        App.Instance.ThemeService.ThemeChanged += (_, _) => ApplyNativeTitleBarTheme();

        StateChanged += (_, _) =>
        {
            var app = App.Instance;
            if (WindowState == WindowState.Minimized && app.Settings.MinimizeToTrayOnClose)
            {
                Hide();
                ShowInTaskbar = false;
            }
            // Inform WindowSizer it can pause/resume auto-refresh.
            app.WindowSizerModule?.ViewModel?.OnShellVisibilityChanged(WindowState != WindowState.Minimized && IsVisible);
        };
        IsVisibleChanged += (_, _) =>
        {
            App.Instance.WindowSizerModule?.ViewModel?.OnShellVisibilityChanged(WindowState != WindowState.Minimized && IsVisible);
        };
        Closing += (_, e) =>
        {
            var app = App.Instance;
            if (_forceClose) return;
            if (app.Settings.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                app.Tray?.ShowBalloon("PowerDesk is still running", "Right-click the tray icon to exit.");
            }
        };

        var app = App.Instance;

        // Status bar binding
        app.Status.PropertyChanged += OnStatusChanged;
        RefreshStatus();

        // Admin badge
        AdminBadge.Text = app.Permissions.IsAdministrator ? "Administrator" : string.Empty;

        // Built-in pages
        _pages["Dashboard"] = new DashboardPage();
        _pages["Settings"]  = new SettingsPage();
        _pages["About"]     = new AboutPage();

        // Module pages
        foreach (var m in app.Modules.Modules)
            _pages[m.Id] = m.MainView;

        // Populate sidebar with module nav items. Items live in an ObservableCollection so
        // filtering through a CollectionView does NOT recreate RadioButton containers — that
        // would clobber the currently-checked module on every keystroke.
        foreach (var m in app.Modules.Modules)
        {
            _allModuleItems.Add(new ModuleNavItem
            {
                Id = m.Id,
                DisplayName = m.DisplayName,
                IconGeometry = m.IconGeometry,
            });
        }
        _moduleView = CollectionViewSource.GetDefaultView(_allModuleItems);
        _moduleView.Filter = ModuleNavFilter;
        ModuleNavList.ItemsSource = _moduleView;

        // Restore last page after the visual tree is built
        Loaded += (_, _) =>
        {
            var target = string.IsNullOrEmpty(app.Settings.LastPage) ? "Dashboard" : app.Settings.LastPage;
            SelectNav(target);
        };
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        var app = App.Instance;
        StatusText.Text = app.Status.Message;
        var key = app.Status.Kind switch
        {
            StatusKind.Success => "SuccessBrush",
            StatusKind.Warning => "WarningBrush",
            StatusKind.Error   => "DangerBrush",
            _                  => "InfoBrush",
        };
        // TryFindResource so a future theme that forgets a key won't crash the shell.
        StatusDot.Fill = TryFindResource(key) as Brush
            ?? System.Windows.Media.Brushes.Gray;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string id)
        {
            if (_pages.TryGetValue(id, out var page))
            {
                ContentHost.Content = page;
                App.Instance.Settings.LastPage = id;
            }
        }
    }

    private void SelectNav(string id)
    {
        if (!_pages.ContainsKey(id)) id = "Dashboard";
        RadioButton? target = id switch
        {
            "Dashboard" => NavDashboard,
            "Settings"  => NavSettings,
            "About"     => NavAbout,
            _ => FindModuleNavButton(id),
        };
        if (target is null)
        {
            if (id is not ("Dashboard" or "Settings" or "About") && !string.IsNullOrEmpty(_navSearchQuery))
            {
                _navSearchQuery = string.Empty;
                SearchBox.Text = string.Empty;
                _moduleView?.Refresh();
            }
            ModuleNavList.ApplyTemplate();
            ModuleNavList.UpdateLayout();
            target = FindModuleNavButton(id);
        }
        if (target is null)
        {
            id = "Dashboard";
            target = NavDashboard;
        }
        target.IsChecked = true;
        // Fire the handler manually if the radio was already checked
        if (_pages.TryGetValue(id, out var page))
        {
            ContentHost.Content = page;
            App.Instance.Settings.LastPage = id;
        }
    }

    private RadioButton? FindModuleNavButton(string id)
    {
        var presenters = FindVisualChildren<RadioButton>(ModuleNavList);
        return presenters.FirstOrDefault(rb => string.Equals(rb.Tag as string, id, StringComparison.Ordinal));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _navSearchQuery = (sender as TextBox)?.Text?.Trim() ?? string.Empty;
        _moduleView?.Refresh();
    }

    private bool ModuleNavFilter(object obj)
    {
        if (string.IsNullOrEmpty(_navSearchQuery)) return true;
        if (obj is not ModuleNavItem m) return false;
        return m.DisplayName.IndexOf(_navSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public void NavigateTo(string id) => SelectNav(id);

    private void ApplyNativeTitleBarTheme()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var dark = App.Instance.ThemeService.Current != AppTheme.Light;
            var useDark = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));

            if (TryFindResource("SurfaceColor") is MediaColor captionColor)
            {
                var caption = ToColorRef(captionColor);
                _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            }

            if (TryFindResource("TextPrimaryColor") is MediaColor textColor)
            {
                var text = ToColorRef(textColor);
                _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
            }

            if (TryFindResource("DividerColor") is MediaColor borderColor)
            {
                var border = ToColorRef(borderColor);
                _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
            }
        }
        catch
        {
            // Native caption theming is best-effort and should never block the shell.
        }
    }

    private static int ToColorRef(MediaColor color)
        => color.R | (color.G << 8) | (color.B << 16);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj is null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }

    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}

public sealed class ModuleNavItem
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconGeometry { get; set; } = string.Empty;
}
