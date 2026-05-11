using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Services;
using PowerDesk.Views;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;

namespace PowerDesk;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private List<ModuleNavItem> _allModuleItems = new();

    private bool _forceClose;
    public void ForceClose() { _forceClose = true; Close(); }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

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

        // Populate sidebar with module nav items
        _allModuleItems = app.Modules.Modules.Select(m => new ModuleNavItem
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            IconGeometry = ResolveIconGeometry(m.IconKey),
        }).ToList();
        ModuleNavList.ItemsSource = _allModuleItems;

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
        StatusDot.Fill = app.Status.Kind switch
        {
            StatusKind.Success => (Brush)FindResource("SuccessBrush"),
            StatusKind.Warning => (Brush)FindResource("WarningBrush"),
            StatusKind.Error   => (Brush)FindResource("DangerBrush"),
            _                  => (Brush)FindResource("InfoBrush"),
        };
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
        RadioButton? target = id switch
        {
            "Dashboard" => NavDashboard,
            "Settings"  => NavSettings,
            "About"     => NavAbout,
            _ => FindModuleNavButton(id),
        };
        if (target is null) target = NavDashboard;
        target.IsChecked = true;
        // Fire the handler manually if the radio was already checked
        if (_pages.TryGetValue(id, out var page))
            ContentHost.Content = page;
    }

    private RadioButton? FindModuleNavButton(string id)
    {
        var presenters = FindVisualChildren<RadioButton>(ModuleNavList);
        return presenters.FirstOrDefault(rb => string.Equals(rb.Tag as string, id, StringComparison.Ordinal));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = (sender as TextBox)?.Text?.Trim() ?? string.Empty;
        if (q.Length == 0)
        {
            ModuleNavList.ItemsSource = _allModuleItems;
            return;
        }
        ModuleNavList.ItemsSource = _allModuleItems
            .Where(m => m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void NavigateTo(string id) => SelectNav(id);

    private static string ResolveIconGeometry(string key) => key switch
    {
        "WindowSizer"  => "M 3,3 H 21 V 21 H 3 Z M 3,8 H 21 M 8,3 V 21",
        "StartupPilot" => "M 12,2 L 5,12 H 10 V 22 L 19,10 H 13 Z",
        _              => "M 4,4 H 20 V 20 H 4 Z",
    };

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
}

public sealed class ModuleNavItem
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconGeometry { get; set; } = string.Empty;
}
