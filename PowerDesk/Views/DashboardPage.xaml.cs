using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using PowerDesk.Modules.StartupPilot;
using PowerDesk.Modules.WindowSizer;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;

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
            IconGeometry = ResolveIconGeometry(m.IconKey),
            NeedsAdminBadge = m.RequiresAdminForFullControl && !app.Permissions.IsAdministrator,
        }).ToList();
        ToolCards.ItemsSource = cards;
        RecentList.ItemsSource = app.RecentActions.Items;

        Loaded += (_, _) => RefreshStats();

        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => RefreshStats(), Dispatcher);
        _refreshTimer.Start();
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
                StatStartup.Text = sp.ViewModel?.Items?.Count.ToString() ?? "—";
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

    private static string ResolveIconGeometry(string key) => key switch
    {
        "WindowSizer"  => "M 3,3 H 21 V 21 H 3 Z M 3,8 H 21 M 8,3 V 21",
        "StartupPilot" => "M 12,2 L 5,12 H 10 V 22 L 19,10 H 13 Z",
        _              => "M 4,4 H 20 V 20 H 4 Z",
    };
}

public sealed class ToolCardData
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconGeometry { get; set; } = string.Empty;
    public bool NeedsAdminBadge { get; set; }
}
