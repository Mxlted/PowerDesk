using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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

    /// <summary>Confirmation prompt used before destructive actions; replaceable for tests.</summary>
    internal IConfirmationService Confirmation { get; set; } = new ConfirmationService();

    public ObservableCollection<MonitorInfo> Monitors { get; } = new();
    public ObservableCollection<MonitorLayoutPreset> LayoutPresets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedMonitorPositionCommand))]
    private MonitorInfo? _selectedMonitor;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyLayoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveLayoutCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedLayoutSummary))]
    [NotifyPropertyChangedFor(nameof(IsSelectedLayoutActive))]
    private MonitorLayoutPreset? _selectedLayout;

    [ObservableProperty] private DateTime? _lastRefresh;
    [ObservableProperty] private string _newLayoutName = string.Empty;
    [ObservableProperty] private int _editX;
    [ObservableProperty] private int _editY;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedMonitorPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyLayoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveLayoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCurrentLayoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>Validation or apply problem shown inline; empty when there is nothing to report.</summary>
    [ObservableProperty] private string _layoutWarning = string.Empty;

    public int MonitorCount => Monitors.Count;
    public int LayoutCount => LayoutPresets.Count;
    public bool HasMonitors => Monitors.Count > 0;
    public bool HasLayouts => LayoutPresets.Count > 0;
    public int VirtualX => MonitorLayoutLogic.VirtualBounds(Monitors).X;
    public int VirtualY => MonitorLayoutLogic.VirtualBounds(Monitors).Y;
    public int VirtualWidth => MonitorLayoutLogic.VirtualBounds(Monitors).Width;
    public int VirtualHeight => MonitorLayoutLogic.VirtualBounds(Monitors).Height;
    public string VirtualBoundsLabel => Monitors.Count == 0 ? "-" : $"{VirtualWidth} x {VirtualHeight} @ {VirtualX},{VirtualY}";
    public string SelectedLayoutSummary => SelectedLayout is null ? string.Empty : SelectedLayout.Summary;
    public bool IsSelectedLayoutActive => SelectedLayout is not null && MonitorLayoutLogic.LayoutMatches(SelectedLayout, Monitors);

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
        try
        {
            _settings = await _storage.LoadAsync(_settingsPath, () => new MonitorDeskSettings());
        }
        catch (Exception ex)
        {
            _log.Error("MonitorDesk settings load", ex);
            _settings = new MonitorDeskSettings();
        }

        LayoutPresets.Clear();
        foreach (var preset in _settings.LayoutPresets.Where(p => p is not null))
        {
            preset.Displays ??= new List<MonitorLayoutDisplay>();
            MonitorLayoutLogic.NormalizeDisplayNumbers(preset);
            LayoutPresets.Add(preset);
        }
        SelectedLayout = LayoutPresets.FirstOrDefault();
        Refresh();
        RaiseLayoutCounts();
    }

    public async Task ShutdownAsync() => await SaveAsync();

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public void Refresh()
    {
        try
        {
            var selectedDevice = SelectedMonitor?.DeviceName;
            var fresh = ReadScreens();

            Monitors.Clear();
            foreach (var monitor in fresh) Monitors.Add(monitor);

            SelectedMonitor = Monitors.FirstOrDefault(m => string.Equals(m.DeviceName, selectedDevice, StringComparison.OrdinalIgnoreCase))
                ?? Monitors.FirstOrDefault(m => m.IsPrimary)
                ?? Monitors.FirstOrDefault();
            LastRefresh = DateTime.Now;
            RaiseCounts();
            SelectMatchingLayout();
            _status.Set($"MonitorDesk refreshed {Monitors.Count} display(s).", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("MonitorDesk refresh", ex);
            RaiseCounts();
            _status.Set("MonitorDesk refresh failed. See logs.", StatusKind.Error);
        }
    }

    /// <summary>
    /// Reads the connected screens in physical pixels. Monitor numbers come from the GDI device name
    /// (\\.\DISPLAYn) so they stay stable when displays are rearranged, matching Windows' own numbering.
    /// </summary>
    private static List<MonitorInfo> ReadScreens()
    {
        var screens = Screen.AllScreens.ToList();
        var list = new List<MonitorInfo>(screens.Count);
        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            list.Add(new MonitorInfo
            {
                DisplayNumber = MonitorLayoutLogic.DisplayNumberOrIndex(screen.DeviceName, i),
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

        return list
            .OrderBy(m => m.DisplayNumber)
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        OnPropertyChanged(nameof(HasMonitors));
        OnPropertyChanged(nameof(VirtualX));
        OnPropertyChanged(nameof(VirtualY));
        OnPropertyChanged(nameof(VirtualWidth));
        OnPropertyChanged(nameof(VirtualHeight));
        OnPropertyChanged(nameof(VirtualBoundsLabel));
        OnPropertyChanged(nameof(IsSelectedLayoutActive));
        SaveCurrentLayoutCommand.NotifyCanExecuteChanged();
    }

    private void RaiseLayoutCounts()
    {
        OnPropertyChanged(nameof(LayoutCount));
        OnPropertyChanged(nameof(HasLayouts));
        OnPropertyChanged(nameof(IsSelectedLayoutActive));
    }

    [RelayCommand]
    private void CopySummary()
    {
        if (Monitors.Count == 0)
        {
            _status.Set("No displays to copy.", StatusKind.Warning);
            return;
        }

        try
        {
            var lines = Monitors.Select(m => $"{m.DisplayLabel} | {m.DeviceName} | {m.PrimaryLabel} | {m.BoundsLabel} | work {m.WorkAreaLabel} | {m.BitsPerPixel} bpp");
            SetClipboardText(string.Join(Environment.NewLine, lines));
            _recent.Add("MonitorDesk", $"Copied {Monitors.Count} monitor record(s).");
            _status.Set("Monitor summary copied.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("MonitorDesk copy", ex);
            _status.Set("Could not copy monitor summary (clipboard busy).", StatusKind.Warning);
        }
    }

    /// <summary>Clipboard access can fail transiently with CLIPBRD_E_CANT_OPEN while another app holds it; retry briefly.</summary>
    private static void SetClipboardText(string text)
    {
        const int attempts = 4;
        for (var i = 1; ; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return;
            }
            catch (Exception) when (i < attempts)
            {
                Thread.Sleep(40 * i);
            }
        }
    }

    private bool CanSaveCurrentLayout() => !IsBusy && Monitors.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSaveCurrentLayout))]
    private async Task SaveCurrentLayoutAsync()
    {
        if (Monitors.Count == 0)
        {
            _status.Set("Refresh monitors before saving a layout.", StatusKind.Warning);
            return;
        }

        var name = MonitorLayoutLogic.UniqueLayoutName(NewLayoutName, LayoutPresets.Select(p => p.Name), DateTime.Now);

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
        LayoutWarning = string.Empty;
        RaiseLayoutCounts();
        await SaveAsync();
        _recent.Add("MonitorDesk", $"Saved layout {preset.Name}.");
        _status.Set($"Monitor layout \"{preset.Name}\" saved.", StatusKind.Success);
    }

    private bool CanApplySelectedMonitorPosition() => !IsBusy && SelectedMonitor is not null;

    [RelayCommand(CanExecute = nameof(CanApplySelectedMonitorPosition))]
    private async Task ApplySelectedMonitorPositionAsync()
    {
        var monitor = SelectedMonitor;
        if (monitor is null)
        {
            _status.Set("Select a display first.", StatusKind.Warning);
            return;
        }

        var x = EditX;
        var y = EditY;
        if (x == monitor.X && y == monitor.Y)
        {
            _status.Set($"{monitor.DisplayLabel} is already at {x},{y}.", StatusKind.Info);
            return;
        }

        var target = MonitorLayoutLogic.BuildTargetLayout(Monitors,
            [new MonitorLayoutDisplay { DeviceName = monitor.DeviceName, DisplayNumber = monitor.DisplayNumber, IsPrimary = monitor.IsPrimary, X = x, Y = y }]);

        if (!ValidateOrWarn(target)) return;

        await RunApplyAsync(
            () => _displayLayout.ApplyPositions(target),
            $"Moved {monitor.DisplayLabel} to {x},{y}.",
            "Display position applied.",
            "Windows rejected that display position.");
    }

    private bool CanApplyLayout(MonitorLayoutPreset? preset) => !IsBusy && (preset ?? SelectedLayout) is not null;

    [RelayCommand(CanExecute = nameof(CanApplyLayout))]
    private async Task ApplyLayoutAsync(MonitorLayoutPreset? preset)
    {
        preset ??= SelectedLayout;
        if (preset is null)
        {
            _status.Set("Choose a saved layout first.", StatusKind.Warning);
            return;
        }

        if (Monitors.Count == 0)
        {
            _status.Set("Refresh monitors before applying a layout.", StatusKind.Warning);
            return;
        }

        var pairs = MonitorLayoutLogic.MatchPresetToMonitors(preset, Monitors, requireAll: false);
        if (pairs.Count == 0)
        {
            LayoutWarning = "None of the saved displays are connected right now.";
            _status.Set("None of the saved displays match the connected monitors.", StatusKind.Warning);
            return;
        }

        var overrides = pairs.Select(p => new MonitorLayoutDisplay
        {
            DisplayNumber = p.Monitor.DisplayNumber,
            DeviceName = p.Monitor.DeviceName,
            IsPrimary = p.Saved.IsPrimary,
            X = p.Saved.X,
            Y = p.Saved.Y,
            Width = p.Monitor.Width,
            Height = p.Monitor.Height,
        }).ToList();

        var target = MonitorLayoutLogic.BuildTargetLayout(Monitors, overrides);
        if (!ValidateOrWarn(target)) return;

        if (MonitorLayoutLogic.LayoutMatches(preset, Monitors))
        {
            LayoutWarning = string.Empty;
            _status.Set($"Layout \"{preset.Name}\" is already active.", StatusKind.Info);
            return;
        }

        var skipped = preset.Displays.Count - pairs.Count;
        var suffix = skipped == 0 ? string.Empty : $" Skipped {skipped} saved display(s) that are not connected.";

        var applied = await RunApplyAsync(
            () => _displayLayout.ApplyPositions(target),
            $"Applied layout {preset.Name}.",
            $"Monitor layout \"{preset.Name}\" applied.{suffix}",
            "Windows rejected that monitor layout.");

        if (applied) await SaveAsync();
    }

    private bool CanRemoveLayout(MonitorLayoutPreset? preset) => !IsBusy && (preset ?? SelectedLayout) is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveLayout))]
    private async Task RemoveLayoutAsync(MonitorLayoutPreset? preset)
    {
        preset ??= SelectedLayout;
        if (preset is null) return;

        if (!Confirmation.Confirm($"Remove saved layout \"{preset.Name}\"?", "Remove layout", destructive: true))
            return;

        var index = LayoutPresets.IndexOf(preset);
        LayoutPresets.Remove(preset);
        if (ReferenceEquals(SelectedLayout, preset) || SelectedLayout is null)
        {
            SelectedLayout = LayoutPresets.FirstOrDefault(MatchesCurrentDisplays)
                ?? (LayoutPresets.Count == 0 ? null : LayoutPresets[Math.Clamp(index, 0, LayoutPresets.Count - 1)]);
        }
        LayoutWarning = string.Empty;
        RaiseLayoutCounts();
        await SaveAsync();
        _recent.Add("MonitorDesk", $"Removed layout {preset.Name}.");
        _status.Set($"Monitor layout \"{preset.Name}\" removed.", StatusKind.Info);
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

    /// <summary>Runs a display change off the UI thread, then refreshes. Returns true when Windows accepted it.</summary>
    private async Task<bool> RunApplyAsync(Func<DisplayLayoutService.ApplyResult> apply, string recentText, string successText, string failureText)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            var result = await Task.Run(apply);
            LayoutWarning = string.Empty;
            _recent.Add("MonitorDesk", recentText);
            var restart = result.RestartRequired ? " Windows reports a restart is required." : string.Empty;
            _status.Set(successText + restart, result.RestartRequired ? StatusKind.Warning : StatusKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("MonitorDesk apply", ex);
            LayoutWarning = ex.Message;
            _status.Set($"{failureText} See logs.", StatusKind.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private bool ValidateOrWarn(IReadOnlyList<MonitorLayoutDisplay> target)
    {
        var problems = MonitorLayoutLogic.Validate(target);
        if (problems.Count == 0)
        {
            LayoutWarning = string.Empty;
            return true;
        }

        LayoutWarning = string.Join(" ", problems);
        _status.Set(problems[0], StatusKind.Warning);
        return false;
    }

    private async Task SaveAsync()
    {
        _settings.LayoutPresets = LayoutPresets.ToList();
        try
        {
            if (!await _storage.SaveAsync(_settingsPath, _settings))
                _status.Set("MonitorDesk settings could not be saved.", StatusKind.Warning);
        }
        catch (Exception ex)
        {
            _log.Error("MonitorDesk settings save", ex);
            _status.Set("MonitorDesk settings could not be saved.", StatusKind.Warning);
        }
    }

    /// <summary>
    /// Selects the preset that matches the current arrangement. When nothing matches, the user's
    /// existing selection is kept (falling back to the first preset) instead of being cleared.
    /// </summary>
    private void SelectMatchingLayout()
    {
        var match = LayoutPresets.FirstOrDefault(MatchesCurrentDisplays);
        if (match is not null)
            SelectedLayout = match;
        else if (SelectedLayout is null || !LayoutPresets.Contains(SelectedLayout))
            SelectedLayout = LayoutPresets.FirstOrDefault();
        OnPropertyChanged(nameof(IsSelectedLayoutActive));
    }

    private bool MatchesCurrentDisplays(MonitorLayoutPreset preset)
        => MonitorLayoutLogic.LayoutMatches(preset, Monitors);
}
