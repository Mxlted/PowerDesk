using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Models;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.WindowSizer.Models;
using PowerDesk.Modules.WindowSizer.Services;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;

namespace PowerDesk.Modules.WindowSizer.ViewModels;

public sealed partial class WindowSizerViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly JsonStorageService _storage;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly WindowService _windows;
    private readonly HotkeyService _hotkeys;
    private readonly AppSettings _appSettings;
    private readonly DispatcherTimer _refreshTimer;
    private readonly string _settingsPath;

    private bool _initialized;
    private bool _shutdown;
    private bool _shellVisible = true;
    private bool _refreshRunning;
    private bool _refreshQueued;

    public ObservableCollection<WindowInfo> Windows { get; } = new();
    public ObservableCollection<SizePreset> SizePresets { get; } = new();
    public ObservableCollection<LayoutPreset> LayoutPresets { get; } = new();
    public ObservableCollection<HotkeyBinding> Hotkeys { get; } = new();

    public WindowSizerSettings Settings { get; private set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGeometryCommand), nameof(ClearSelectionCommand),
        nameof(BringToFrontCommand), nameof(PinTopCommand), nameof(UnpinTopCommand), nameof(CenterCommand),
        nameof(SnapLeftCommand), nameof(SnapRightCommand), nameof(SnapTopCommand), nameof(SnapBottomCommand),
        nameof(MaximizeCommand), nameof(RestoreWindowCommand), nameof(NextMonitorCommand),
        nameof(ApplySizePresetCommand), nameof(CaptureLayoutCommand))]
    private WindowInfo? _selectedWindow;

    [ObservableProperty] private int _editX;
    [ObservableProperty] private int _editY;
    [ObservableProperty] private int _editWidth = 1280;
    [ObservableProperty] private int _editHeight = 720;
    [ObservableProperty] private string _newPresetName = string.Empty;
    [ObservableProperty] private int _newPresetWidth = 1280;
    [ObservableProperty] private int _newPresetHeight = 720;
    [ObservableProperty] private int _autoRefreshSeconds = 2;
    [ObservableProperty] private SizePreset? _selectedSizePreset;
    [ObservableProperty] private LayoutPreset? _selectedLayoutPreset;
    [ObservableProperty] private string _newLayoutName = string.Empty;
    [ObservableProperty] private HotkeyBinding? _selectedHotkey;

    /// <summary>True while a window enumeration is running on the background thread.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>True when the last refresh found nothing to manage (drives the empty-state message).</summary>
    [ObservableProperty] private bool _isWindowListEmpty = true;

    /// <summary>Human-readable summary of the OS-level hotkey state, e.g. "6 of 6 active".</summary>
    [ObservableProperty] private string _hotkeyStatusText = "Not registered";

    /// <summary>Non-empty when some hotkeys could not be registered or hotkeys are globally off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyWarning))]
    private string _hotkeyWarning = string.Empty;

    public bool HasSelection => SelectedWindow is not null;
    public bool HasHotkeyWarning => !string.IsNullOrEmpty(HotkeyWarning);
    public int ActiveHotkeyCount => _hotkeys.ActiveCount;

    public bool IsSizePresetListEmpty => SizePresets.Count == 0;
    public bool IsLayoutListEmpty => LayoutPresets.Count == 0;
    public bool IsHotkeyListEmpty => Hotkeys.Count == 0;

    public WindowSizerViewModel(
        ILogger log,
        JsonStorageService storage,
        StatusService status,
        RecentActionsService recent,
        IconService icons,
        AppSettings appSettings)
    {
        _log = log;
        _storage = storage;
        _status = status;
        _recent = recent;
        _appSettings = appSettings;
        _windows = new WindowService(icons);
        _hotkeys = new HotkeyService(log);
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        _settingsPath = PathService.ModuleSettingsFile("WindowSizer");

        // Application.Current is null under unit tests; fall back to the thread's dispatcher like UiDispatcher does.
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _refreshTimer.Tick += (_, _) => QueueRefresh();

        SizePresets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsSizePresetListEmpty));
        LayoutPresets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsLayoutListEmpty));
        Hotkeys.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsHotkeyListEmpty));
    }

    public async Task InitializeAsync()
    {
        WindowSizerSettings loaded;
        try { loaded = await _storage.LoadAsync(_settingsPath, () => new WindowSizerSettings()); }
        catch (Exception ex)
        {
            _log.Error("WindowSizer settings load", ex);
            loaded = new WindowSizerSettings();
        }
        Settings = WindowSizerLogic.Sanitize(loaded);

        SizePresets.Clear();
        foreach (var p in Settings.SizePresets) SizePresets.Add(p);
        LayoutPresets.Clear();
        foreach (var p in Settings.LayoutPresets) LayoutPresets.Add(p);
        Hotkeys.Clear();
        foreach (var h in Settings.Hotkeys) Hotkeys.Add(h);
        AutoRefreshSeconds = Settings.AutoRefreshSeconds;
        _initialized = true;

        try
        {
            _hotkeys.Initialize();
            RefreshHotkeyRegistrations();
        }
        catch (Exception ex) { _log.Error("WindowSizer hotkey init", ex); }

        ApplyAutoRefresh();
        await RefreshAsync();
    }

    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;
        _refreshTimer.Stop();
        try
        {
            _hotkeys.HotkeyPressed -= OnHotkeyPressed;
            _hotkeys.UnregisterAll();
            _hotkeys.Dispose();
        }
        catch (Exception ex) { _log.Warn($"WindowSizer hotkey shutdown: {ex.Message}"); }
        if (_initialized) await SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            Settings.SizePresets = SizePresets.ToList();
            Settings.LayoutPresets = LayoutPresets.ToList();
            Settings.Hotkeys = Hotkeys.ToList();
            Settings.AutoRefreshSeconds = AutoRefreshSeconds;
            if (!await _storage.SaveAsync(_settingsPath, Settings))
                _status.Set("WindowSizer settings could not be saved.", StatusKind.Warning);
        }
        catch (Exception ex)
        {
            _log.Error("WindowSizer settings save", ex);
            _status.Set("WindowSizer settings could not be saved.", StatusKind.Warning);
        }
    }

    /// <summary>Fire-and-forget save that can never surface as an unobserved exception.</summary>
    private void SaveInBackground()
    {
        _ = SaveAsync().ContinueWith(t => _log.Error("WindowSizer settings save", t.Exception),
                                     TaskContinuationOptions.OnlyOnFaulted);
    }

    partial void OnSelectedWindowChanged(WindowInfo? value)
    {
        if (value is null) return;
        EditX = value.X;
        EditY = value.Y;
        EditWidth = value.Width;
        EditHeight = value.Height;
    }

    partial void OnAutoRefreshSecondsChanged(int value)
    {
        ApplyAutoRefresh();
        if (_initialized && !_shutdown) SaveInBackground();
    }

    private void ApplyAutoRefresh()
    {
        _refreshTimer.Stop();
        if (_shutdown || AutoRefreshSeconds <= 0 || !_shellVisible) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(AutoRefreshSeconds);
        _refreshTimer.Start();
    }

    /// <summary>Pause/resume polling based on shell visibility (called by MainWindow).</summary>
    public void OnShellVisibilityChanged(bool visible)
    {
        _shellVisible = visible;
        if (visible) { ApplyAutoRefresh(); QueueRefresh(); }
        else _refreshTimer.Stop();
    }

    // ---------- refresh ----------

    /// <summary>Kick off a refresh without awaiting it; coalesces if one is already running.</summary>
    public void Refresh() => QueueRefresh();

    private void QueueRefresh()
    {
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_shutdown) return;
        if (_refreshRunning) { _refreshQueued = true; return; }
        _refreshRunning = true;
        IsBusy = true;
        try
        {
            do
            {
                _refreshQueued = false;
                var self = GetShellHwnd();
                var current = await Task.Run(() => _windows.EnumerateWindows(self));
                UiDispatcher.Invoke(() => MergeWindows(current));
            }
            while (_refreshQueued && !_shutdown);
        }
        catch (Exception ex) { _log.Error("WindowSizer refresh", ex); }
        finally
        {
            _refreshRunning = false;
            IsBusy = false;
        }
    }

    /// <summary>Diff the enumeration into <see cref="Windows"/> in place so the DataGrid selection doesn't blink.</summary>
    private void MergeWindows(List<WindowInfo> current)
    {
        var keep = new HashSet<IntPtr>(current.Select(c => c.Handle));
        for (int i = Windows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Windows[i].Handle)) Windows.RemoveAt(i);

        var byHandle = new Dictionary<IntPtr, WindowInfo>(Windows.Count);
        foreach (var w in Windows) byHandle[w.Handle] = w;

        foreach (var w in current)
        {
            if (!byHandle.TryGetValue(w.Handle, out var existing)) { Windows.Add(w); continue; }
            existing.X = w.X; existing.Y = w.Y;
            existing.Width = w.Width; existing.Height = w.Height;
            existing.IsTopmost = w.IsTopmost; existing.Monitor = w.Monitor;
            existing.IsMinimized = w.IsMinimized; existing.IsMaximized = w.IsMaximized;
            if (existing.Icon is null && w.Icon is not null) existing.Icon = w.Icon;
        }

        // The DataGrid clears SelectedItem when the row disappears, but only if it is bound; be explicit.
        if (SelectedWindow is not null && !keep.Contains(SelectedWindow.Handle)) SelectedWindow = null;
        IsWindowListEmpty = Windows.Count == 0;
    }

    // ---------- selection actions ----------

    /// <summary>Validates the selection still points at a live window; reports and refreshes otherwise.</summary>
    private bool TryGetTarget(out IntPtr hWnd, out WindowInfo info)
    {
        hWnd = IntPtr.Zero;
        info = null!;
        if (SelectedWindow is null) { _status.Set("Select a window first.", StatusKind.Warning); return false; }
        if (!_windows.IsAlive(SelectedWindow.Handle))
        {
            _status.Set($"\"{SelectedWindow.Title}\" has closed.", StatusKind.Warning);
            QueueRefresh();
            return false;
        }
        info = SelectedWindow;
        hWnd = info.Handle;
        return true;
    }

    private void ReportAction(bool ok, string successMessage, string? recentText = null)
    {
        if (ok)
        {
            if (recentText is not null) _recent.Add("WindowSizer", recentText);
            _status.Set(successMessage, StatusKind.Success);
        }
        else
        {
            _status.Set("Windows refused that action (the window may be gone, hung, or elevated).", StatusKind.Warning);
        }
        QueueRefresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelection() => SelectedWindow = null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ApplyGeometry()
    {
        if (!TryGetTarget(out var h, out var w)) return;
        if (EditWidth < WindowSizerLogic.MinWindowSize || EditHeight < WindowSizerLogic.MinWindowSize)
        {
            _status.Set($"Width and height must be {WindowSizerLogic.MinWindowSize} or larger.", StatusKind.Warning);
            return;
        }
        var ok = _windows.MoveAndResize(h, EditX, EditY, EditWidth, EditHeight);
        ReportAction(ok, "Window resized.", $"Resize: {w.Title} → {EditWidth}×{EditHeight} @ {EditX},{EditY}");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BringToFront()
    {
        if (!TryGetTarget(out var h, out _)) return;
        ReportAction(_windows.BringToFront(h), "Window brought to front.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PinTop()
    {
        if (!TryGetTarget(out var h, out var w)) return;
        ReportAction(_windows.SetTopmost(h, true), "Window pinned on top.", $"Pinned topmost: {w.Title}");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void UnpinTop()
    {
        if (!TryGetTarget(out var h, out var w)) return;
        ReportAction(_windows.SetTopmost(h, false), "Window unpinned.", $"Unpinned: {w.Title}");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Center()
    {
        if (!TryGetTarget(out var h, out var w)) return;
        ReportAction(_windows.Center(h), "Window centered.", $"Centered: {w.Title}");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))] private void SnapLeft()   => SnapSelected(WindowService.SnapEdge.Left,   "Snapped left.");
    [RelayCommand(CanExecute = nameof(HasSelection))] private void SnapRight()  => SnapSelected(WindowService.SnapEdge.Right,  "Snapped right.");
    [RelayCommand(CanExecute = nameof(HasSelection))] private void SnapTop()    => SnapSelected(WindowService.SnapEdge.Top,    "Snapped top.");
    [RelayCommand(CanExecute = nameof(HasSelection))] private void SnapBottom() => SnapSelected(WindowService.SnapEdge.Bottom, "Snapped bottom.");

    private void SnapSelected(WindowService.SnapEdge edge, string message)
    {
        if (!TryGetTarget(out var h, out var w)) return;
        ReportAction(_windows.Snap(h, edge), message, $"Snap {edge.ToString().ToLowerInvariant()}: {w.Title}");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Maximize()
    {
        if (!TryGetTarget(out var h, out _)) return;
        ReportAction(_windows.Maximize(h), "Window maximized.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RestoreWindow()
    {
        if (!TryGetTarget(out var h, out _)) return;
        ReportAction(_windows.Restore(h), "Window restored.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void NextMonitor()
    {
        if (!TryGetTarget(out var h, out var w)) return;
        if (_windows.EnumerateMonitorWorkAreas().Count <= 1)
        {
            _status.Set("Only one monitor is connected.", StatusKind.Info);
            return;
        }
        ReportAction(_windows.MoveToNextMonitor(h), "Moved to next monitor.", $"Next monitor: {w.Title}");
    }

    // ---------- size presets ----------

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ApplySizePreset(SizePreset? preset)
    {
        preset ??= SelectedSizePreset;
        if (preset is null) { _status.Set("Pick a size preset.", StatusKind.Warning); return; }
        if (!TryGetTarget(out var h, out var w)) return;
        var ok = _windows.MoveAndResize(h, w.X, w.Y, preset.Width, preset.Height);
        ReportAction(ok, $"Applied {preset}.", $"Preset: {preset} on {w.Title}");
    }

    [RelayCommand]
    private async Task AddSizePresetAsync()
    {
        if (NewPresetWidth < WindowSizerLogic.MinWindowSize || NewPresetHeight < WindowSizerLogic.MinWindowSize)
        {
            _status.Set($"Width and height must be {WindowSizerLogic.MinWindowSize} or larger.", StatusKind.Warning);
            return;
        }
        if (WindowSizerLogic.IsDuplicateSizePreset(SizePresets, NewPresetWidth, NewPresetHeight))
        {
            _status.Set($"A {NewPresetWidth} × {NewPresetHeight} preset already exists.", StatusKind.Warning);
            return;
        }
        var name = WindowSizerLogic.ResolvePresetName(NewPresetName, NewPresetWidth, NewPresetHeight);
        var preset = new SizePreset { Name = name, Width = NewPresetWidth, Height = NewPresetHeight };
        SizePresets.Add(preset);
        SelectedSizePreset = preset;
        NewPresetName = string.Empty;
        await SaveAsync();
        _status.Set("Preset added.", StatusKind.Success);
    }

    [RelayCommand]
    private async Task RemoveSizePresetAsync(SizePreset? preset)
    {
        preset ??= SelectedSizePreset;
        if (preset is null) { _status.Set("Pick a size preset to remove.", StatusKind.Warning); return; }
        if (!SizePresets.Remove(preset)) return;
        if (SelectedSizePreset == preset) SelectedSizePreset = null;
        await SaveAsync();
        _status.Set("Preset removed.", StatusKind.Info);
    }

    [RelayCommand]
    private async Task ResetSizePresetsAsync()
    {
        SizePresets.Clear();
        SelectedSizePreset = null;
        foreach (var p in WindowSizerSettings.DefaultSizePresets()) SizePresets.Add(p);
        await SaveAsync();
        _status.Set("Size presets reset.", StatusKind.Info);
    }

    // ---------- layouts ----------

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CaptureLayoutAsync()
    {
        if (!TryGetTarget(out var h, out var w)) return;
        // Read the live geometry rather than the last refresh so the capture is exact.
        var bounds = _windows.GetVisibleBounds(h);
        int x = bounds.Width > 0 ? bounds.Left : w.X;
        int y = bounds.Width > 0 ? bounds.Top : w.Y;
        int width = bounds.Width > 0 ? bounds.Width : w.Width;
        int height = bounds.Height > 0 ? bounds.Height : w.Height;
        if (width < WindowSizerLogic.MinWindowSize || height < WindowSizerLogic.MinWindowSize)
        {
            _status.Set("That window is minimized or too small to capture.", StatusKind.Warning);
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewLayoutName) ? w.Title : NewLayoutName.Trim();
        var preset = new LayoutPreset
        {
            Name = name,
            X = x, Y = y, Width = width, Height = height,
            TargetProcessName = w.ProcessName,
        };
        LayoutPresets.Add(preset);
        SelectedLayoutPreset = preset;
        NewLayoutName = string.Empty;
        await SaveAsync();
        _status.Set("Layout captured.", StatusKind.Success);
    }

    [RelayCommand]
    private void ApplyLayout(LayoutPreset? preset)
    {
        preset ??= SelectedLayoutPreset;
        if (preset is null) { _status.Set("Pick a layout.", StatusKind.Warning); return; }

        var target = WindowSizerLogic.ResolveLayoutTarget(preset, SelectedWindow, Windows);
        if (target is null)
        {
            _status.Set(string.IsNullOrEmpty(preset.TargetProcessName)
                ? "Select a window to apply this layout to."
                : $"No open window from \"{preset.TargetProcessName}\" — select a window instead.", StatusKind.Warning);
            return;
        }
        if (!_windows.IsAlive(target.Handle))
        {
            _status.Set($"\"{target.Title}\" has closed.", StatusKind.Warning);
            QueueRefresh();
            return;
        }
        var ok = _windows.MoveAndResize(target.Handle, preset.X, preset.Y, preset.Width, preset.Height);
        ReportAction(ok, $"Applied layout {preset.Name}.", $"Layout: {preset.Name} → {target.Title}");
    }

    [RelayCommand]
    private async Task RemoveLayoutAsync(LayoutPreset? preset)
    {
        preset ??= SelectedLayoutPreset;
        if (preset is null) { _status.Set("Pick a layout to remove.", StatusKind.Warning); return; }
        if (!LayoutPresets.Remove(preset)) return;
        if (SelectedLayoutPreset == preset) SelectedLayoutPreset = null;
        await SaveAsync();
        _status.Set("Layout removed.", StatusKind.Info);
    }

    // ---------- hotkeys ----------

    public async Task AddHotkeyAsync(HotkeyBinding binding)
    {
        if (binding is null || binding.VirtualKey == 0 || WindowSizerLogic.NormalizeModifiers(binding.Modifiers) == 0)
        {
            _status.Set("A hotkey needs at least one modifier and a key.", StatusKind.Warning);
            return;
        }
        if (HasConflict(binding))
        {
            _status.Set("That hotkey is already bound.", StatusKind.Warning);
            return;
        }
        Hotkeys.Add(binding);
        SelectedHotkey = binding;
        await SaveAsync();
        RefreshHotkeyRegistrations();
        if (!HasHotkeyWarning) _status.Set($"Hotkey added: {binding.DisplayText} → {binding.ActionLabel}", StatusKind.Success);
    }

    /// <summary>Flip Enabled, persist, and rewire registrations so the OS-level hotkey actually goes away.</summary>
    public async Task SetHotkeyEnabledAsync(HotkeyBinding binding, bool enabled)
    {
        if (binding is null || binding.Enabled == enabled) return;
        binding.Enabled = enabled;
        await SaveAsync();
        RefreshHotkeyRegistrations();
        if (!HasHotkeyWarning)
            _status.Set(enabled ? $"Hotkey enabled: {binding.ActionLabel}" : $"Hotkey disabled: {binding.ActionLabel}",
                        StatusKind.Info);
    }

    public bool HasConflict(HotkeyBinding candidate) =>
        Hotkeys.Any(h => h.Id != candidate.Id && WindowSizerLogic.SameChord(h, candidate));

    [RelayCommand]
    private async Task RemoveHotkeyAsync(HotkeyBinding? binding)
    {
        binding ??= SelectedHotkey;
        if (binding is null) { _status.Set("Pick a hotkey to remove.", StatusKind.Warning); return; }
        if (!Hotkeys.Remove(binding)) return;
        if (SelectedHotkey == binding) SelectedHotkey = null;
        await SaveAsync();
        RefreshHotkeyRegistrations();
        _status.Set($"Hotkey removed: {binding.DisplayText}", StatusKind.Info);
    }

    public void RefreshHotkeyRegistrations()
    {
        try
        {
            if (_shutdown) return;
            if (!_appSettings.GlobalHotkeysEnabled)
            {
                _hotkeys.UnregisterAll();
                HotkeyStatusText = "Off";
                HotkeyWarning = "Global hotkeys are turned off in Settings.";
                OnPropertyChanged(nameof(ActiveHotkeyCount));
                return;
            }

            var result = _hotkeys.RegisterAll(Hotkeys);
            int wanted = Hotkeys.Count(h => h.Enabled);
            HotkeyStatusText = $"{result.Registered} of {wanted} active";
            OnPropertyChanged(nameof(ActiveHotkeyCount));

            if (result.AllOk)
            {
                HotkeyWarning = string.Empty;
                return;
            }

            var first = result.Failures[0];
            var detail = result.Failures.Count == 1
                ? $"{first.Binding.DisplayText}: {first.Reason}."
                : $"{first.Binding.DisplayText}: {first.Reason} (+{result.Failures.Count - 1} more).";
            HotkeyWarning = $"{result.Failures.Count} hotkey(s) not registered — {detail}";
            _status.Set($"Hotkey not registered — {detail}", StatusKind.Warning);
        }
        catch (Exception ex)
        {
            _log.Error("Hotkey registration", ex);
            HotkeyWarning = "Hotkeys could not be registered. See logs.";
            _status.Set("Hotkeys could not be registered. See logs.", StatusKind.Error);
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyBinding b)
    {
        try
        {
            // Hotkeys act on the current foreground window, not the WindowSizer selection.
            var fg = GetForegroundWindowSafe();
            if (fg == IntPtr.Zero) return;
            if (!_windows.CanManageWindow(fg, GetShellHwnd())) return;
            bool ok = b.Action switch
            {
                HotkeyAction.SnapLeft   => _windows.Snap(fg, WindowService.SnapEdge.Left),
                HotkeyAction.SnapRight  => _windows.Snap(fg, WindowService.SnapEdge.Right),
                HotkeyAction.SnapTop    => _windows.Snap(fg, WindowService.SnapEdge.Top),
                HotkeyAction.SnapBottom => _windows.Snap(fg, WindowService.SnapEdge.Bottom),
                HotkeyAction.Center     => _windows.Center(fg),
                HotkeyAction.Maximize   => _windows.Maximize(fg),
                HotkeyAction.ApplyLayoutPreset => ApplyLayoutByName(fg, b.LayoutPresetName),
                _ => false,
            };
            if (ok) _recent.Add("WindowSizer", $"Hotkey: {b.ActionLabel} ({b.DisplayText})");
            if (_shellVisible) QueueRefresh();
        }
        catch (Exception ex) { _log.Error("Hotkey action", ex); }
    }

    private bool ApplyLayoutByName(IntPtr hWnd, string? name)
    {
        var p = WindowSizerLogic.FindLayoutByName(LayoutPresets, name);
        return p is not null && _windows.MoveAndResize(hWnd, p.X, p.Y, p.Width, p.Height);
    }

    private static IntPtr GetForegroundWindowSafe()
    {
        try { return GetForegroundWindow(); } catch { return IntPtr.Zero; }
    }

    private static IntPtr GetShellHwnd()
    {
        try
        {
            return Application.Current is App { Shell: { } shell }
                ? new System.Windows.Interop.WindowInteropHelper(shell).Handle
                : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Snap the current foreground window left or right. Used by the tray menu.</summary>
    public void InvokeForegroundSnap(bool left)
    {
        try
        {
            var fg = GetForegroundWindowSafe();
            if (fg == IntPtr.Zero) { _status.Set("No foreground window to snap.", StatusKind.Warning); return; }
            if (!_windows.CanManageWindow(fg, GetShellHwnd()))
            {
                _status.Set("Foreground window cannot be managed safely.", StatusKind.Warning);
                return;
            }
            var ok = _windows.Snap(fg, left ? WindowService.SnapEdge.Left : WindowService.SnapEdge.Right);
            ReportAction(ok, "Foreground window snapped.", $"Tray snap {(left ? "left" : "right")}: foreground window");
        }
        catch (Exception ex) { _log.Error("Tray snap", ex); }
    }
}
