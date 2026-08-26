using System;
using System.Threading.Tasks;
using System.Windows;
using PowerDesk.Modules.StartupPilot.Models;
using PowerDesk.Modules.StartupPilot.ViewModels;
using UserControl = System.Windows.Controls.UserControl;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using DependencyObject = System.Windows.DependencyObject;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace PowerDesk.Modules.StartupPilot.Views;

public partial class StartupPilotView : UserControl
{
    private readonly StartupPilotViewModel _vm;

    public StartupPilotView(StartupPilotViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>
    /// Awaits view-model work from an <c>async void</c> handler. The view model already reports failures through the
    /// status bar; this keeps anything unexpected from escaping as an unobserved exception that would crash the app.
    /// </summary>
    private static async void Run(Task work)
    {
        try { await work; }
        catch (Exception ex) { App.Instance?.Logger?.Error("StartupPilot view", ex); }
    }

    private void EnableToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb && cb.DataContext is StartupItem item)
        {
            // Revert the optimistic UI flip; the VM owns the truth.
            cb.IsChecked = item.Enabled;
            Run(_vm.ToggleItemAsync(item));
        }
    }

    private void ToggleMenu_Click(object sender, RoutedEventArgs e) => Run(_vm.ToggleItemAsync(_vm.SelectedItem));
    private void OpenLocationMenu_Click(object sender, RoutedEventArgs e) => _vm.OpenFileLocation(_vm.SelectedItem);
    private void CopyMenu_Click(object sender, RoutedEventArgs e) => _vm.CopyCommandLine(_vm.SelectedItem);
    private void PinMenu_Click(object sender, RoutedEventArgs e) => Run(_vm.TogglePinnedAsync(_vm.SelectedItem));
    private void NoteMenu_Click(object sender, RoutedEventArgs e) => EditNote(_vm.SelectedItem);

    private void BulkEnable_Click(object sender, RoutedEventArgs e)  => Run(_vm.BulkEnableAsync(ItemsGrid.SelectedItems));
    private void BulkDisable_Click(object sender, RoutedEventArgs e) => Run(_vm.BulkDisableAsync(ItemsGrid.SelectedItems));

    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;

        row.Focus();
        // Keep an existing multi-selection when right-clicking inside it; otherwise select just this row.
        if (!row.IsSelected)
            grid.SelectedItem = row.Item;
    }

    private void ServiceAutomaticMenu_Click(object sender, RoutedEventArgs e) =>
        Run(_vm.SetServiceStartupTypeAsync(_vm.SelectedService, ServiceStartupType.Automatic));

    private void ServiceManualMenu_Click(object sender, RoutedEventArgs e) =>
        Run(_vm.SetServiceStartupTypeAsync(_vm.SelectedService, ServiceStartupType.Manual));

    private void ServiceDisabledMenu_Click(object sender, RoutedEventArgs e) =>
        Run(_vm.SetServiceStartupTypeAsync(_vm.SelectedService, ServiceStartupType.Disabled));

    private void ServiceOpenLocationMenu_Click(object sender, RoutedEventArgs e) => _vm.OpenFileLocation(_vm.SelectedService);
    private void ServiceCopyMenu_Click(object sender, RoutedEventArgs e) => _vm.CopyCommandLine(_vm.SelectedService);
    private void ServicePinMenu_Click(object sender, RoutedEventArgs e) => Run(_vm.TogglePinnedAsync(_vm.SelectedService));
    private void ServiceNoteMenu_Click(object sender, RoutedEventArgs e) => EditNote(_vm.SelectedService);

    private void EditNote(StartupItem? item)
    {
        if (item is null) return;
        try
        {
            var dlg = new NoteDialog(item.Name, item.Note)
            {
                Owner = System.Windows.Window.GetWindow(this),
            };
            if (dlg.ShowDialog() == true) Run(_vm.SetNoteAsync(item, dlg.NoteText));
        }
        catch (Exception ex)
        {
            App.Instance?.Logger?.Error("StartupPilot note dialog", ex);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
