using System;
using System.Windows;
using PowerDesk.Modules.StartupPilot.Models;
using PowerDesk.Modules.StartupPilot.ViewModels;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;

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
        var dlg = new NoteDialog(item.Name, item.Note);
        if (dlg.ShowDialog() == true) await _vm.SetNoteAsync(item, dlg.NoteText);
    }

    private async void BulkEnable_Click(object sender, RoutedEventArgs e)  => await _vm.BulkEnableAsync(ItemsGrid.SelectedItems);
    private async void BulkDisable_Click(object sender, RoutedEventArgs e) => await _vm.BulkDisableAsync(ItemsGrid.SelectedItems);
}
