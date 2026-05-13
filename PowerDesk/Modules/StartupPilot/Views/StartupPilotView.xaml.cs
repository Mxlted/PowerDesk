using System;
using System.Windows;
using PowerDesk.Modules.StartupPilot.Models;
using PowerDesk.Modules.StartupPilot.ViewModels;
using UserControl = System.Windows.Controls.UserControl;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using DependencyObject = System.Windows.DependencyObject;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
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
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StartupPilotViewModel.LastScan))
                LastScanLabel.Text = vm.LastScan is null ? "" : $"Last scan: {vm.LastScan:HH:mm:ss}";
        };
    }

    private async void EnableToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb && cb.DataContext is StartupItem item)
        {
            // Revert the optimistic UI flip; the VM owns the truth.
            cb.IsChecked = item.Enabled;
            await _vm.ToggleItemAsync(item);
        }
    }

    private async void ToggleMenu_Click(object sender, RoutedEventArgs e) => await _vm.ToggleItemAsync(_vm.SelectedItem);
    private void OpenLocationMenu_Click(object sender, RoutedEventArgs e) => _vm.OpenFileLocation(_vm.SelectedItem);
    private void CopyMenu_Click(object sender, RoutedEventArgs e) => _vm.CopyCommandLine(_vm.SelectedItem);
    private async void PinMenu_Click(object sender, RoutedEventArgs e) => await _vm.TogglePinnedAsync(_vm.SelectedItem);

    private async void NoteMenu_Click(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedItem;
        if (item is null) return;
        var dlg = new NoteDialog(item.Name, item.Note)
        {
            Owner = System.Windows.Window.GetWindow(this),
        };
        if (dlg.ShowDialog() == true) await _vm.SetNoteAsync(item, dlg.NoteText);
    }

    private async void BulkEnable_Click(object sender, RoutedEventArgs e)  => await _vm.BulkEnableAsync(ItemsGrid.SelectedItems);
    private async void BulkDisable_Click(object sender, RoutedEventArgs e) => await _vm.BulkDisableAsync(ItemsGrid.SelectedItems);

    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;

        row.Focus();
        row.IsSelected = true;
        grid.SelectedItem = row.Item;
    }

    private async void ServiceAutomaticMenu_Click(object sender, RoutedEventArgs e) =>
        await _vm.SetServiceStartupTypeAsync(_vm.SelectedService, ServiceStartupType.Automatic);

    private async void ServiceManualMenu_Click(object sender, RoutedEventArgs e) =>
        await _vm.SetServiceStartupTypeAsync(_vm.SelectedService, ServiceStartupType.Manual);

    private async void ServiceDisabledMenu_Click(object sender, RoutedEventArgs e) =>
        await _vm.SetServiceStartupTypeAsync(_vm.SelectedService, ServiceStartupType.Disabled);

    private void ServiceOpenLocationMenu_Click(object sender, RoutedEventArgs e) => _vm.OpenFileLocation(_vm.SelectedService);
    private void ServiceCopyMenu_Click(object sender, RoutedEventArgs e) => _vm.CopyCommandLine(_vm.SelectedService);
    private async void ServicePinMenu_Click(object sender, RoutedEventArgs e) => await _vm.TogglePinnedAsync(_vm.SelectedService);

    private async void ServiceNoteMenu_Click(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedService;
        if (item is null) return;
        var dlg = new NoteDialog(item.Name, item.Note)
        {
            Owner = System.Windows.Window.GetWindow(this),
        };
        if (dlg.ShowDialog() == true) await _vm.SetNoteAsync(item, dlg.NoteText);
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
