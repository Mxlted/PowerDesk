using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.MonitorDesk.Models;
using PowerDesk.Modules.MonitorDesk.Services;
using Clipboard = System.Windows.Clipboard;
using Screen = System.Windows.Forms.Screen;

namespace PowerDesk.Modules.MonitorDesk.ViewModels;

public sealed partial class MonitorDeskViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly JsonStorageService _storage;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly DisplayLayoutService _displayLayout = new();
    private readonly string _settingsPath;
    private MonitorDeskSettings _settings = new();

    public ObservableCollection<MonitorInfo> Monitors { get; } = new();
    public ObservableCollection<MonitorLayoutPreset> LayoutPresets { get; } = new();

    [ObservableProperty] private MonitorInfo? _selectedMonitor;
    [ObservableProperty] private MonitorLayoutPreset? _selectedLayout;
    [ObservableProperty] private DateTime? _lastRefresh;
    [ObservableProperty] private string _newLayoutName = string.Empty;
    [ObservableProperty] private int _editX;
    [ObservableProperty] private int _editY;

    public int MonitorCount => Monitors.Count;
    public int LayoutCount => LayoutPresets.Count;
    public int VirtualX => Monitors.Count == 0 ? 0 : Monitors.Min(m => m.X);
    public int VirtualY => Monitors.Count == 0 ? 0 : Monitors.Min(m => m.Y);
    public int VirtualWidth => Monitors.Count == 0 ? 0 : Monitors.Max(m => m.X + m.Width) - VirtualX;
    public int VirtualHeight => Monitors.Count == 0 ? 0 : Monitors.Max(m => m.Y + m.Height) - VirtualY;
    public string VirtualBoundsLabel => Monitors.Count == 0 ? "-" : $"{VirtualWidth} x {VirtualHeight} @ {VirtualX},{VirtualY}";

    public MonitorDeskViewModel(ILogger log, JsonStorageService storage, StatusService status, RecentActionsService recent)
    {
        _log = log;
        _storage = storage;
        _status = status;
        _recent = recent;
        _settingsPath = PathService.ModuleSettingsFile("MonitorDesk");
    }

    public async Task InitializeAsync()
    {
        _settings = await _storage.LoadAsync(_settingsPath, () => new MonitorDeskSettings());
        LayoutPresets.Clear();
        foreach (var preset in _settings.LayoutPresets)
        {
            NormalizeDisplayNumbers(preset);
            LayoutPresets.Add(preset);
        }
        SelectedLayout = LayoutPresets.FirstOrDefault();
        Refresh();
        RaiseLayoutCounts();
    }

    public async Task ShutdownAsync() => await SaveAsync();

    [RelayCommand]
    public void Refresh()
    {
        try
        {
            var selectedDevice = SelectedMonitor?.DeviceName;
            var selectedNumber = SelectedMonitor?.DisplayNumber;
            Monitors.Clear();
            var screens = Screen.AllScreens
                .OrderByDescending(s => s.Primary)
                .ThenBy(s => s.Bounds.X)
                .ThenBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < screens.Count; i++)
            {
                var screen = screens[i];
                Monitors.Add(new MonitorInfo
                {
                    DisplayNumber = i + 1,
                    DeviceName = screen.DeviceName,
                    IsPrimary = screen.Primary,
                    X = screen.Bounds.X,
                    Y = screen.Bounds.Y,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height,
                    WorkX = screen.WorkingArea.X,
                    WorkY = screen.WorkingArea.Y,
                    WorkWidth = screen.WorkingArea.Width,
                    WorkHeight = screen.WorkingArea.Height,
                    BitsPerPixel = screen.BitsPerPixel,
                });
            }
            SelectedMonitor = Monitors.FirstOrDefault(m => m.DisplayNumber == selectedNumber)
                ?? Monitors.FirstOrDefault(m => string.Equals(m.DeviceName, selectedDevice, StringComparison.OrdinalIgnoreCase))
                ?? Monitors.FirstOrDefault();
            LastRefresh = DateTime.Now;
            RaiseCounts();
            SelectMatchingLayout();
            _status.Set($"MonitorDesk refreshed {Monitors.Count} display(s).", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("MonitorDesk refresh", ex);
            _status.Set("MonitorDesk refresh failed. See logs.", StatusKind.Error);
        }
    }

    partial void OnSelectedMonitorChanged(MonitorInfo? value)
    {
        if (value is null) return;
        EditX = value.X;
        EditY = value.Y;
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(MonitorCount));
        OnPropertyChanged(nameof(VirtualX));
        OnPropertyChanged(nameof(VirtualY));
        OnPropertyChanged(nameof(VirtualWidth));
        OnPropertyChanged(nameof(VirtualHeight));
        OnPropertyChanged(nameof(VirtualBoundsLabel));
    }

    private void RaiseLayoutCounts() => OnPropertyChanged(nameof(LayoutCount));

    [RelayCommand]
    private void CopySummary()
    {
        try
        {
            var lines = Monitors.Select(m => $"{m.DisplayLabel} | {m.DeviceName} | {m.PrimaryLabel} | {m.BoundsLabel} | work {m.WorkAreaLabel} | {m.BitsPerPixel} bpp");
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            _recent.Add("MonitorDesk", $"Copied {Monitors.Count} monitor record(s).");
            _status.Set("Monitor summary copied.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("MonitorDesk copy", ex);
            _status.Set("Could not copy monitor summary.", StatusKind.Warning);
        }
    }

    [RelayCommand]
    private async Task SaveCurrentLayoutAsync()
    {
        if (Monitors.Count == 0)
        {
            _status.Set("Refresh monitors before saving a layout.", StatusKind.Warning);
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewLayoutName)
            ? $"Layout {DateTime.Now:yyyy-MM-dd HH-mm}"
            : NewLayoutName.Trim();

        var preset = new MonitorLayoutPreset
        {
            Name = name,
            CreatedAt = DateTime.Now,
            Displays = Monitors.Select(m => new MonitorLayoutDisplay
            {
                DisplayNumber = m.DisplayNumber,
                DeviceName = m.DeviceName,
                IsPrimary = m.IsPrimary,
                X = m.X,
                Y = m.Y,
                Width = m.Width,
                Height = m.Height,
            }).ToList(),
        };

        LayoutPresets.Insert(0, preset);
        SelectedLayout = preset;
        NewLayoutName = string.Empty;
        RaiseLayoutCounts();
        SelectMatchingLayout();
        await SaveAsync();
        _recent.Add("MonitorDesk", $"Saved layout {preset.Name}.");
        _status.Set("Monitor layout saved.", StatusKind.Success);
    }

    [RelayCommand]
    private async Task ApplySelectedMonitorPositionAsync()
    {
        if (SelectedMonitor is null)
        {
            _status.Set("Select a display first.", StatusKind.Warning);
            return;
        }

        try
        {
            await Task.Run(() => _displayLayout.ApplyMonitorPosition(SelectedMonitor.DeviceName, EditX, EditY));
            _recent.Add("MonitorDesk", $"Moved {SelectedMonitor.DisplayLabel} to {EditX},{EditY}.");
            _status.Set("Display position applied.", StatusKind.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _log.Error("Apply monitor position", ex);
            _status.Set("Windows rejected that display position. See logs.", StatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task ApplyLayoutAsync(MonitorLayoutPreset? preset)
    {
        preset ??= SelectedLayout;
        if (preset is null)
        {
            _status.Set("Choose a saved layout first.", StatusKind.Warning);
            return;
        }

        var displays = MapPresetToConnectedDisplays(preset, requireAll: false);

        if (displays.Count == 0)
        {
            _status.Set("No saved monitor numbers match the current display list.", StatusKind.Warning);
            return;
        }

        try
        {
            await Task.Run(() => _displayLayout.ApplyPositions(displays));
            _recent.Add("MonitorDesk", $"Applied layout {preset.Name}.");
            var suffix = displays.Count == preset.Displays.Count
                ? string.Empty
                : $" Skipped {preset.Displays.Count - displays.Count} monitor number(s) with no connected match.";
            _status.Set($"Monitor layout applied by monitor number.{suffix}", StatusKind.Success);
            Refresh();
            await SaveAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Apply monitor layout", ex);
            _status.Set("Windows rejected that monitor layout. See logs.", StatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task RemoveLayoutAsync(MonitorLayoutPreset? preset)
    {
        preset ??= SelectedLayout;
        if (preset is null) return;

        LayoutPresets.Remove(preset);
        SelectMatchingLayout();
        RaiseLayoutCounts();
        await SaveAsync();
        _status.Set("Monitor layout removed.", StatusKind.Info);
    }

    [RelayCommand]
    private void OpenDisplaySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Open display settings", ex);
            _status.Set("Could not open Windows display settings.", StatusKind.Warning);
        }
    }

    private async Task SaveAsync()
    {
        _settings.LayoutPresets = LayoutPresets.ToList();
        if (!await _storage.SaveAsync(_settingsPath, _settings))
            _status.Set("MonitorDesk settings could not be saved.", StatusKind.Warning);
    }

    private void SelectMatchingLayout()
    {
        SelectedLayout = LayoutPresets.FirstOrDefault(LayoutMatchesCurrentDisplays);
    }

    private bool LayoutMatchesCurrentDisplays(MonitorLayoutPreset preset)
    {
        if (preset.Displays.Count != Monitors.Count) return false;

        var displays = MapPresetToConnectedDisplays(preset, requireAll: true);
        if (displays.Count != Monitors.Count) return false;

        foreach (var display in displays)
        {
            var monitor = Monitors.FirstOrDefault(m =>
                string.Equals(m.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase));

            if (monitor is null) return false;
            if (display.IsPrimary != monitor.IsPrimary) return false;
            if (display.X != monitor.X || display.Y != monitor.Y) return false;
            if (display.Width != monitor.Width || display.Height != monitor.Height) return false;
        }

        return true;
    }

    private List<MonitorLayoutDisplay> MapPresetToConnectedDisplays(MonitorLayoutPreset preset, bool requireAll)
    {
        NormalizeDisplayNumbers(preset);

        var monitorByNumber = Monitors
            .GroupBy(m => m.DisplayNumber)
            .ToDictionary(g => g.Key, g => g.First());
        var displays = new List<MonitorLayoutDisplay>();

        foreach (var saved in preset.Displays)
        {
            if (!monitorByNumber.TryGetValue(saved.DisplayNumber, out var monitor))
            {
                if (requireAll)
                    return new List<MonitorLayoutDisplay>();
                continue;
            }

            displays.Add(new MonitorLayoutDisplay
            {
                DisplayNumber = saved.DisplayNumber,
                DeviceName = monitor.DeviceName,
                IsPrimary = saved.IsPrimary,
                X = saved.X,
                Y = saved.Y,
                Width = saved.Width,
                Height = saved.Height,
            });
        }

        return displays;
    }

    private static void NormalizeDisplayNumbers(MonitorLayoutPreset preset)
    {
        for (var i = 0; i < preset.Displays.Count; i++)
        {
            if (preset.Displays[i].DisplayNumber <= 0)
                preset.Displays[i].DisplayNumber = i + 1;
        }
    }
}
