using System;
using System.Collections.Generic;
using System.Linq;
using PowerDesk.Modules.WindowSizer.Models;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;

namespace PowerDesk.Modules.WindowSizer.Services;

/// <summary>
/// Pure geometry / bookkeeping helpers for WindowSizer. No Win32 calls here so everything is unit-testable.
/// All rectangles are in physical pixels.
/// </summary>
internal static class WindowSizerLogic
{
    /// <summary>Distance (per side) from a window's Win32 rect to its visible DWM frame. Positive = invisible border.</summary>
    public readonly record struct FrameInsets(int Left, int Top, int Right, int Bottom)
    {
        public static readonly FrameInsets Zero = new(0, 0, 0, 0);
        public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
    }

    /// <summary>Largest plausible invisible border in pixels. Anything beyond this means the two rects are not in the same coordinate space.</summary>
    public const int MaxPlausibleInset = 64;

    /// <summary>Minimum size we will ever ask Windows for, so a typo cannot collapse a window to nothing.</summary>
    public const int MinWindowSize = 50;

    /// <summary>
    /// Computes the invisible-border insets between <paramref name="windowRect"/> (GetWindowRect) and
    /// <paramref name="frameRect"/> (DWMWA_EXTENDED_FRAME_BOUNDS). Returns <see cref="FrameInsets.Zero"/> when the
    /// two rectangles are not plausibly describing the same window (e.g. DPI-virtualised vs physical).
    /// </summary>
    public static FrameInsets ComputeInsets(RECT windowRect, RECT frameRect)
    {
        if (frameRect.Width <= 0 || frameRect.Height <= 0) return FrameInsets.Zero;
        var insets = new FrameInsets(
            frameRect.Left - windowRect.Left,
            frameRect.Top - windowRect.Top,
            windowRect.Right - frameRect.Right,
            windowRect.Bottom - frameRect.Bottom);
        if (!IsPlausible(insets.Left) || !IsPlausible(insets.Top) || !IsPlausible(insets.Right) || !IsPlausible(insets.Bottom))
            return FrameInsets.Zero;
        return insets;

        static bool IsPlausible(int v) => Math.Abs(v) <= MaxPlausibleInset;
    }

    /// <summary>
    /// Given the desired <em>visible</em> bounds, returns the Win32 rect to pass to SetWindowPos so that the visible
    /// frame lands exactly on the requested bounds. Enforces <see cref="MinWindowSize"/> on the visible size.
    /// </summary>
    public static RECT ToWindowRect(int x, int y, int width, int height, FrameInsets insets)
    {
        width = Math.Max(MinWindowSize, width);
        height = Math.Max(MinWindowSize, height);
        return new RECT(
            x - insets.Left,
            y - insets.Top,
            x + width + insets.Right,
            y + height + insets.Bottom);
    }

    /// <summary>Visible rect for a half-screen snap inside the monitor work area. Odd widths give the extra pixel to the right/bottom half.</summary>
    public static RECT SnapRect(RECT work, WindowService.SnapEdge edge)
    {
        int halfW = work.Width / 2;
        int halfH = work.Height / 2;
        return edge switch
        {
            WindowService.SnapEdge.Left   => new RECT(work.Left, work.Top, work.Left + halfW, work.Bottom),
            WindowService.SnapEdge.Right  => new RECT(work.Left + halfW, work.Top, work.Right, work.Bottom),
            WindowService.SnapEdge.Top    => new RECT(work.Left, work.Top, work.Right, work.Top + halfH),
            WindowService.SnapEdge.Bottom => new RECT(work.Left, work.Top + halfH, work.Right, work.Bottom),
            _ => work,
        };
    }

    /// <summary>Top-left origin that centres a window of the given visible size in the work area. Never places the origin above/left of the work area.</summary>
    public static (int X, int Y) CenterOrigin(RECT work, int width, int height)
    {
        int x = work.Left + Math.Max(0, (work.Width - width) / 2);
        int y = work.Top + Math.Max(0, (work.Height - height) / 2);
        return (x, y);
    }

    /// <summary>
    /// Translates a window's visible bounds from one monitor's work area to another, keeping the same relative
    /// offset where possible and clamping so the window stays inside the destination. Size is clamped to the
    /// destination work area.
    /// </summary>
    public static RECT TranslateToWorkArea(RECT window, RECT fromWork, RECT toWork)
    {
        int w = Math.Min(window.Width, toWork.Width);
        int h = Math.Min(window.Height, toWork.Height);
        int relX = window.Left - fromWork.Left;
        int relY = window.Top - fromWork.Top;
        int x = toWork.Left + Math.Clamp(relX, 0, Math.Max(0, toWork.Width - w));
        int y = toWork.Top + Math.Clamp(relY, 0, Math.Max(0, toWork.Height - h));
        return new RECT(x, y, x + w, y + h);
    }

    /// <summary>Turns a GDI device name such as <c>\\.\DISPLAY2</c> into <c>Display 2</c>.</summary>
    public static string FriendlyMonitorName(string? device)
    {
        if (string.IsNullOrWhiteSpace(device)) return string.Empty;
        var s = device.Trim();
        const string prefix = @"\\.\";
        if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..];
        if (s.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) && s.Length > 7 && int.TryParse(s[7..], out var n))
            return $"Display {n}";
        return s;
    }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",                    // desktop "Program Manager"
        "WorkerW",                    // desktop wallpaper host
        "Shell_TrayWnd",              // taskbar
        "Shell_SecondaryTrayWnd",     // taskbar on other monitors
        "Windows.UI.Core.CoreWindow", // UWP core windows (hosted inside ApplicationFrameWindow)
        "XamlExplorerHostIslandWindow",
        "Xaml_WindowedPopupClass",
    };

    /// <summary>True for OS shell surfaces that show up as visible titled top-level windows but must never be resized.</summary>
    public static bool IsShellClass(string? className) =>
        !string.IsNullOrEmpty(className) && ShellClasses.Contains(className);

    /// <summary>Alt-Tab style eligibility test on the extended style + owner relationship.</summary>
    public static bool IsAltTabEligible(long exStyle, bool hasOwner)
    {
        if ((exStyle & WS_EX_NOACTIVATE) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;
        if (hasOwner && (exStyle & WS_EX_APPWINDOW) == 0) return false;
        return true;
    }

    /// <summary>Process name derived from a full image path (mirrors Process.ProcessName).</summary>
    public static string ProcessNameFromPath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return string.Empty;
        try { return System.IO.Path.GetFileNameWithoutExtension(exePath); }
        catch { return string.Empty; }
    }

    // ---------- hotkeys ----------

    public sealed record HotkeyPlan(IReadOnlyList<HotkeyBinding> ToRegister, IReadOnlyList<HotkeyBinding> Duplicates);

    /// <summary>
    /// Decides which bindings should actually be registered with the OS: disabled or incomplete bindings are skipped,
    /// and a second binding with the same modifiers+key is reported as a duplicate instead of failing at the OS level.
    /// </summary>
    public static HotkeyPlan PlanRegistrations(IEnumerable<HotkeyBinding> bindings)
    {
        var register = new List<HotkeyBinding>();
        var duplicates = new List<HotkeyBinding>();
        var seen = new HashSet<(uint, uint)>();
        foreach (var b in bindings)
        {
            if (b is null || !b.Enabled || b.VirtualKey == 0 || b.Modifiers == 0) continue;
            if (!seen.Add((NormalizeModifiers(b.Modifiers), b.VirtualKey))) { duplicates.Add(b); continue; }
            register.Add(b);
        }
        return new HotkeyPlan(register, duplicates);
    }

    /// <summary>Strips MOD_NOREPEAT and anything outside the four real modifier bits so comparisons are stable.</summary>
    public static uint NormalizeModifiers(uint modifiers) => modifiers & (MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN);

    public static bool SameChord(HotkeyBinding a, HotkeyBinding b) =>
        NormalizeModifiers(a.Modifiers) == NormalizeModifiers(b.Modifiers) && a.VirtualKey == b.VirtualKey;

    public static string DescribeRegisterFailure(int win32Error) => win32Error switch
    {
        ERROR_HOTKEY_ALREADY_REGISTERED => "already registered by another application",
        ERROR_INVALID_PARAMETER => "invalid key combination",
        0 => "unknown error",
        _ => $"Win32 error {win32Error}",
    };

    // ---------- presets / settings ----------

    public static bool IsDuplicateSizePreset(IEnumerable<SizePreset> existing, int width, int height) =>
        existing.Any(p => p.Width == width && p.Height == height);

    public static string ResolvePresetName(string? requested, int width, int height) =>
        string.IsNullOrWhiteSpace(requested) ? $"{width}×{height}" : requested.Trim();

    /// <summary>Picks the window a layout should be applied to: the explicit selection wins, else the first window from the layout's process.</summary>
    public static WindowInfo? ResolveLayoutTarget(LayoutPreset preset, WindowInfo? selected, IEnumerable<WindowInfo> windows)
    {
        if (selected is not null) return selected;
        if (string.IsNullOrEmpty(preset.TargetProcessName)) return null;
        return windows.FirstOrDefault(w => string.Equals(w.ProcessName, preset.TargetProcessName, StringComparison.OrdinalIgnoreCase));
    }

    public static LayoutPreset? FindLayoutByName(IEnumerable<LayoutPreset> presets, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Makes a deserialised settings object safe to use: null lists become defaults, presets with impossible sizes are
    /// dropped, hotkeys without a key are dropped, and auto-refresh is clamped to a sane range.
    /// </summary>
    public static WindowSizerSettings Sanitize(WindowSizerSettings? settings)
    {
        settings ??= new WindowSizerSettings();

        settings.SizePresets = (settings.SizePresets ?? WindowSizerSettings.DefaultSizePresets())
            .Where(p => p is not null && p.Width >= MinWindowSize && p.Height >= MinWindowSize)
            .ToList();

        settings.LayoutPresets = (settings.LayoutPresets ?? new List<LayoutPreset>())
            .Where(p => p is not null && p.Width >= MinWindowSize && p.Height >= MinWindowSize)
            .ToList();

        settings.Hotkeys = (settings.Hotkeys ?? WindowSizerSettings.DefaultHotkeys())
            .Where(h => h is not null && h.VirtualKey != 0)
            .ToList();
        foreach (var h in settings.Hotkeys)
            if (string.IsNullOrWhiteSpace(h.Id)) h.Id = Guid.NewGuid().ToString("N");

        settings.AutoRefreshSeconds = Math.Clamp(settings.AutoRefreshSeconds, 0, 60);
        return settings;
    }
}
