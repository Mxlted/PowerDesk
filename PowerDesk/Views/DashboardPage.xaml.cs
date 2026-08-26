using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PowerDesk.Modules.StartupPilot;
using PowerDesk.Modules.WindowSizer;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;

namespace PowerDesk.Views;

public partial class DashboardPage : UserControl
{
    private readonly DispatcherTimer _refreshTimer;

    public DashboardPage()
    {
        InitializeComponent();

        var app = App.Instance;
        var cards = app.Modules.Modules.Select(m => new ToolCardData
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Description = m.Description,
            IconGeometry = m.IconGeometry,
            NeedsAdminBadge = m.RequiresAdminForFullControl && !app.Permissions.IsAdministrator,
        }).ToList();
        ToolCards.ItemsSource = cards;
        RecentList.ItemsSource = app.RecentActions.Items;
        AdminPill.Visibility = app.Permissions.IsAdministrator ? Visibility.Visible : Visibility.Collapsed;

        // Empty-state text for the activity feed.
        app.RecentActions.Items.CollectionChanged += OnRecentChanged;
        UpdateRecentEmptyState();

        // Build the timer in the stopped state; start/stop with the page's visual lifetime so we
        // don't poll counts when the dashboard isn't on screen.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _refreshTimer.Tick += (_, _) => RefreshStats();

        Loaded += (_, _) => { RefreshStats(); _refreshTimer.Start(); };
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    private void OnRecentChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateRecentEmptyState();

    private void UpdateRecentEmptyState()
    {
        var any = App.Instance.RecentActions.Items.Count > 0;
        RecentEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClearRecentButton.IsEnabled = any;
    }

    private void RefreshStats()
    {
        var app = App.Instance;
        try
        {
            if (app.WindowSizerModule is { } ws)
            {
                StatWindows.Text = ws.ViewModel?.Windows?.Count.ToString() ?? "—";
                StatHotkeys.Text = ws.ViewModel?.ActiveHotkeyCount.ToString() ?? "—";
            }
            if (app.StartupPilotModule is { } sp)
            {
                StatStartup.Text = sp.ViewModel?.StartupEntryCount.ToString() ?? "—";
                StatOrphans.Text = sp.ViewModel?.OrphanCount.ToString() ?? "—";
            }
        }
        catch { /* dashboard is best-effort */ }
    }

    private void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id)
            App.Instance.Shell?.NavigateTo(id);
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // The Open button handles its own click; only treat clicks on the card body as navigation.
        if (e.OriginalSource is DependencyObject src && FindAncestor<Button>(src) is not null) return;
        if (sender is Border card && card.Tag is string id)
            App.Instance.Shell?.NavigateTo(id);
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
        => App.Instance.RecentActions.Clear();

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var cur = start;
        while (cur is not null)
        {
            if (cur is T t) return t;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }
}

public sealed class ToolCardData
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconGeometry { get; set; } = string.Empty;
    public bool NeedsAdminBadge { get; set; }
}
