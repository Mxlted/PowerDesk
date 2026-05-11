using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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

    public ObservableCollection<WindowInfo> Windows { get; } = new();
    public ObservableCollection<SizePreset> SizePresets { get; } = new();
    public ObservableCollection<LayoutPreset> LayoutPresets { get; } = new();
    public ObservableCollection<HotkeyBinding> Hotkeys { get; } = new();

    public WindowSizerSettings Settings { get; private set; } = new();

    [ObservableProperty] private WindowInfo? _selectedWindow;
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

    public int ActiveHotkeyCount => _hotkeys.ActiveCount;

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

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _refreshTimer.Tick += (_, _) => Refresh();
    }

    public async Task InitializeAsync()
    {
        Settings = await _storage.LoadAsync(_settingsPath, () => new WindowSizerSettings());
        SizePresets.Clear();
        foreach (var p in Settings.SizePresets) SizePresets.Add(p);
        LayoutPresets.Clear();
        foreach (var p in Settings.LayoutPresets) LayoutPresets.Add(p);
        Hotkeys.Clear();
        foreach (var h in Settings.Hotkeys) Hotkeys.Add(h);
        AutoRefreshSeconds = Math.Max(0, Settings.AutoRefreshSeconds);

        _hotkeys.Initialize();
        RefreshHotkeyRegistrations();

        ApplyAutoRefresh();
        Refresh();
    }

    public async Task ShutdownAsync()
    {
        _refreshTimer.Stop();
        _hotkeys.UnregisterAll();
        _hotkeys.Dispose();
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        Settings.SizePresets = SizePresets.ToList();
        Settings.LayoutPresets = LayoutPresets.ToList();
        Settings.Hotkeys = Hotkeys.ToList();
        Settings.AutoRefreshSeconds = AutoRefreshSeconds;
        await _storage.SaveAsync(_settingsPath, Settings);
    }

    partial void OnSelectedWindowChanged(WindowInfo? value)
    {
        if (value is null) return;
        EditX = value.X;
        EditY = value.Y;
        EditWidth = value.Width;
        EditHeight = value.Height;
    }

    partial void OnAutoRefreshSecondsChanged(int value) => ApplyAutoRefresh();

    private void ApplyAutoRefresh()
    {
        _refreshTimer.Stop();
        if (AutoRefreshSeconds <= 0) return;
        if (App.Instance.Shell is { } shell && shell.WindowState == WindowState.Minimized) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(AutoRefreshSeconds);
        _refreshTimer.Start();
    }

    /// <summary>Pause/resume polling based on shell visibility.</summary>
    public void OnShellVisibilityChanged(bool visible)
    {
        if (visible) ApplyAutoRefresh();
        else _refreshTimer.Stop();
    }

    [RelayCommand]
    public void Refresh()
    {
        try
        {
            IntPtr self = IntPtr.Zero;
            if (App.Instance.Shell is { } shell)
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(shell);
                self = helper.Handle;
            }
            var current = _windows.EnumerateWindows(self);

            // Diff in-place so the DataGrid selection doesn't blink.
            var keep = new HashSet<IntPtr>(current.Select(c => c.Handle));
            for (int i = Windows.Count - 1; i >= 0; i--)
                if (!keep.Contains(Windows[i].Handle)) Windows.RemoveAt(i);

            foreach (var w in current)
            {
                var existing = Windows.FirstOrDefault(x => x.Handle == w.Handle);
                if (existing is null) Windows.Add(w);
                else
                {
                    existing.X = w.X; existing.Y = w.Y;
                    existing.Width = w.Width; existing.Height = w.Height;
                    existing.IsTopmost = w.IsTopmost; existing.Monitor = w.Monitor;
                    if (existing.Icon is null && w.Icon is not null) existing.Icon = w.Icon;
                }
            }
        }
        catch (Exception ex) { _log.Error("WindowSizer refresh", ex); }
    }

    [RelayCommand]
    private void ClearSelection() => SelectedWindow = null;

    [RelayCommand]
    private void ApplyGeometry()
    {
        if (SelectedWindow is null) { _status.Set("Select a window first.", StatusKind.Warning); return; }
        _windows.MoveAndResize(SelectedWindow.Handle, EditX, EditY, EditWidth, EditHeight);
        _recent.Add("WindowSizer", $"Resize: {SelectedWindow.Title} → {EditWidth}×{EditHeight} @ {EditX},{EditY}");
        _status.Set("Window resized.", StatusKind.Success);
        Refresh();
    }

    [RelayCommand] private void BringToFront() { if (SelectedWindow is not null) { _windows.BringToFront(SelectedWindow.Handle); _status.Set("Window brought to front.", StatusKind.Success); } }
    [RelayCommand] private void PinTop()        { if (SelectedWindow is not null) { _windows.SetTopmost(SelectedWindow.Handle, true);  _recent.Add("WindowSizer", $"Pinned topmost: {SelectedWindow.Title}"); Refresh(); _status.Set("Window pinned on top.", StatusKind.Success); } }
    [RelayCommand] private void UnpinTop()      { if (SelectedWindow is not null) { _windows.SetTopmost(SelectedWindow.Handle, false); _recent.Add("WindowSizer", $"Unpinned: {SelectedWindow.Title}"); Refresh(); _status.Set("Window unpinned.", StatusKind.Success); } }
    [RelayCommand] private void Center()        { if (SelectedWindow is not null) { _windows.Center(SelectedWindow.Handle); _recent.Add("WindowSizer", $"Centered: {SelectedWindow.Title}"); Refresh(); _status.Set("Window centered.", StatusKind.Success); } }
    [RelayCommand] private void SnapLeft()      { if (SelectedWindow is not null) { _windows.Snap(SelectedWindow.Handle, WindowService.SnapEdge.Left);   Refresh(); _status.Set("Snapped left.", StatusKind.Success); } }
    [RelayCommand] private void SnapRight()     { if (SelectedWindow is not null) { _windows.Snap(SelectedWindow.Handle, WindowService.SnapEdge.Right);  Refresh(); _status.Set("Snapped right.", StatusKind.Success); } }
    [RelayCommand] private void SnapTop()       { if (SelectedWindow is not null) { _windows.Snap(SelectedWindow.Handle, WindowService.SnapEdge.Top);    Refresh(); _status.Set("Snapped top.", StatusKind.Success); } }
    [RelayCommand] private void SnapBottom()    { if (SelectedWindow is not null) { _windows.Snap(SelectedWindow.Handle, WindowService.SnapEdge.Bottom); Refresh(); _status.Set("Snapped bottom.", StatusKind.Success); } }
    [RelayCommand] private void Maximize()      { if (SelectedWindow is not null) { _windows.Maximize(SelectedWindow.Handle); Refresh(); _status.Set("Window maximized.", StatusKind.Success); } }
    [RelayCommand] private void RestoreWindow() { if (SelectedWindow is not null) { _windows.Restore(SelectedWindow.Handle);  Refresh(); _status.Set("Window restored.", StatusKind.Success); } }
    [RelayCommand] private void NextMonitor()   { if (SelectedWindow is not null) { _windows.MoveToNextMonitor(SelectedWindow.Handle); Refresh(); _status.Set("Moved to next monitor.", StatusKind.Success); } }

    [RelayCommand]
    private async Task ApplySizePresetAsync(SizePreset? preset)
    {
        preset ??= SelectedSizePreset;
        if (preset is null) { _status.Set("Pick a size preset.", StatusKind.Warning); return; }
        if (SelectedWindow is null) { _status.Set("Select a window first.", StatusKind.Warning); return; }
        _windows.MoveAndResize(SelectedWindow.Handle, SelectedWindow.X, SelectedWindow.Y, preset.Width, preset.Height);
        _recent.Add("WindowSizer", $"Preset: {preset} on {SelectedWindow.Title}");
        _status.Set($"Applied {preset}.", StatusKind.Success);
        Refresh();
        await SaveAsync();
    }

    [RelayCommand]
    private async Task AddSizePresetAsync()
    {
        if (NewPresetWidth < 50 || NewPresetHeight < 50) { _status.Set("Width and height must be 50 or larger.", StatusKind.Warning); return; }
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? $"{NewPresetWidth}×{NewPresetHeight}" : NewPresetName.Trim();
        SizePresets.Add(new SizePreset { Name = name, Width = NewPresetWidth, Height = NewPresetHeight });
        NewPresetName = string.Empty;
        await SaveAsync();
        _status.Set("Preset added.", StatusKind.Success);
    }

    [RelayCommand]
    private async Task RemoveSizePresetAsync(SizePreset? preset)
    {
        preset ??= SelectedSizePreset;
        if (preset is null) return;
        SizePresets.Remove(preset);
        await SaveAsync();
        _status.Set("Preset removed.", StatusKind.Info);
    }

    [RelayCommand]
    private async Task ResetSizePresetsAsync()
    {
        SizePresets.Clear();
        foreach (var p in WindowSizerSettings.DefaultSizePresets()) SizePresets.Add(p);
        await SaveAsync();
        _status.Set("Size presets reset.", StatusKind.Info);
    }

    [RelayCommand]
    private async Task CaptureLayoutAsync()
    {
        if (SelectedWindow is null) { _status.Set("Select a window first.", StatusKind.Warning); return; }
        var name = string.IsNullOrWhiteSpace(NewLayoutName) ? SelectedWindow.Title : NewLayoutName.Trim();
        LayoutPresets.Add(new LayoutPreset
        {
            Name = name,
            X = SelectedWindow.X, Y = SelectedWindow.Y,
            Width = SelectedWindow.Width, Height = SelectedWindow.Height,
            TargetProcessName = SelectedWindow.ProcessName,
        });
        NewLayoutName = string.Empty;
        await SaveAsync();
        _status.Set("Layout captured.", StatusKind.Success);
    }

    [RelayCommand]
    private async Task ApplyLayoutAsync(LayoutPreset? preset)
    {
        preset ??= SelectedLayoutPreset;
        if (preset is null) { _status.Set("Pick a layout.", StatusKind.Warning); return; }

        var target = ResolveLayoutTarget(preset);
        if (target is null) { _status.Set("No matching window for that layout.", StatusKind.Warning); return; }
        _windows.MoveAndResize(target.Handle, preset.X, preset.Y, preset.Width, preset.Height);
        _recent.Add("WindowSizer", $"Layout: {preset.Name} → {target.Title}");
        _status.Set($"Applied layout {preset.Name}.", StatusKind.Success);
        Refresh();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RemoveLayoutAsync(LayoutPreset? preset)
    {
        preset ??= SelectedLayoutPreset;
        if (preset is null) return;
        LayoutPresets.Remove(preset);
        await SaveAsync();
        _status.Set("Layout removed.", StatusKind.Info);
    }

    private WindowInfo? ResolveLayoutTarget(LayoutPreset preset)
    {
        if (SelectedWindow is not null) return SelectedWindow;
        if (!string.IsNullOrEmpty(preset.TargetProcessName))
            return Windows.FirstOrDefault(w => string.Equals(w.ProcessName, preset.TargetProcessName, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    public async Task AddHotkeyAsync(HotkeyBinding binding)
    {
        if (HasConflict(binding))
        {
            _status.Set("That hotkey is already bound.", StatusKind.Warning);
            return;
        }
        Hotkeys.Add(binding);
        await SaveAsync();
        RefreshHotkeyRegistrations();
    }

    public bool HasConflict(HotkeyBinding candidate) =>
        Hotkeys.Any(h => h.Id != candidate.Id && h.Modifiers == candidate.Modifiers && h.VirtualKey == candidate.VirtualKey);

    [RelayCommand]
    private async Task RemoveHotkeyAsync(HotkeyBinding? binding)
    {
        binding ??= SelectedHotkey;
        if (binding is null) return;
        Hotkeys.Remove(binding);
        await SaveAsync();
        RefreshHotkeyRegistrations();
    }

    public void RefreshHotkeyRegistrations()
    {
        if (!_appSettings.GlobalHotkeysEnabled)
        {
            _hotkeys.UnregisterAll();
            OnPropertyChanged(nameof(ActiveHotkeyCount));
            return;
        }
        bool ok = _hotkeys.RegisterAll(Hotkeys);
        OnPropertyChanged(nameof(ActiveHotkeyCount));
        if (!ok) _status.Set("Some hotkeys could not be registered (another app may hold them).", StatusKind.Warning);
    }

    private void OnHotkeyPressed(object? sender, HotkeyBinding b)
    {
        try
        {
            // Hotkeys act on the current foreground window, not the WindowSizer selection.
            var fg = GetForegroundWindowSafe();
            if (fg == IntPtr.Zero) return;
            switch (b.Action)
            {
                case HotkeyAction.SnapLeft:   _windows.Snap(fg, WindowService.SnapEdge.Left);   break;
                case HotkeyAction.SnapRight:  _windows.Snap(fg, WindowService.SnapEdge.Right);  break;
                case HotkeyAction.SnapTop:    _windows.Snap(fg, WindowService.SnapEdge.Top);    break;
                case HotkeyAction.SnapBottom: _windows.Snap(fg, WindowService.SnapEdge.Bottom); break;
                case HotkeyAction.Center:     _windows.Center(fg); break;
                case HotkeyAction.Maximize:   _windows.Maximize(fg); break;
                case HotkeyAction.ApplyLayoutPreset:
                    var p = LayoutPresets.FirstOrDefault(x => x.Name == b.LayoutPresetName);
                    if (p is not null) _windows.MoveAndResize(fg, p.X, p.Y, p.Width, p.Height);
                    break;
            }
            _recent.Add("WindowSizer", $"Hotkey: {b.ActionLabel} ({b.DisplayText})");
        }
        catch (Exception ex) { _log.Error("Hotkey action", ex); }
    }

    private static IntPtr GetForegroundWindowSafe()
    {
        try { return Win32GetForegroundWindow(); } catch { return IntPtr.Zero; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr Win32GetForegroundWindow();

    /// <summary>Snap the current foreground window left or right. Used by the tray menu.</summary>
    public void InvokeForegroundSnap(bool left)
    {
        var fg = GetForegroundWindowSafe();
        if (fg == IntPtr.Zero) { _status.Set("No foreground window to snap.", StatusKind.Warning); return; }
        _windows.Snap(fg, left ? WindowService.SnapEdge.Left : WindowService.SnapEdge.Right);
        _recent.Add("WindowSizer", $"Tray snap {(left ? "left" : "right")}: foreground window");
        _status.Set("Foreground window snapped.", StatusKind.Success);
    }
}
