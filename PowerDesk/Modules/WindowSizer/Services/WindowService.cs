using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PowerDesk.Core.Services;
using PowerDesk.Modules.WindowSizer.Models;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;

namespace PowerDesk.Modules.WindowSizer.Services;

/// <summary>
/// Win32-backed enumeration and manipulation of top-level visible windows.
/// All geometry is expressed as the <em>visible</em> frame (DWM extended frame bounds) in physical pixels, so
/// snapping and layouts line up edge-to-edge without the invisible resize borders Windows 10/11 add.
/// All methods are tolerant of HWNDs that have gone away mid-call and may be called from any thread.
/// </summary>
public sealed class WindowService
{
    private readonly IconService _icons;

    public WindowService(IconService icons) => _icons = icons;

    public enum SnapEdge { Left, Right, Top, Bottom }

    public List<WindowInfo> EnumerateWindows(IntPtr selfHwnd)
    {
        var list = new List<WindowInfo>(64);
        var processCache = new Dictionary<int, (string Name, string Exe)>();
        int selfPid = Environment.ProcessId;

        using var dpi = PhysicalPixelScope.Enter();

        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsRelevantWindow(hWnd, selfHwnd)) return true;

                var title = GetTitle(hWnd);
                if (string.IsNullOrWhiteSpace(title)) return true;

                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0 || pid == selfPid) return true;

                if (!processCache.TryGetValue(pid, out var proc))
                {
                    proc = DescribeProcess(pid);
                    processCache[pid] = proc;
                }

                var rect = GetVisibleBounds(hWnd);
                var ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                var topmost = (ex & WS_EX_TOPMOST) != 0;

                list.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessName = proc.Name,
                    ExePath = proc.Exe,
                    ProcessId = pid,
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height,
                    IsTopmost = topmost,
                    IsMinimized = IsIconic(hWnd),
                    IsMaximized = IsZoomed(hWnd),
                    Monitor = GetMonitorName(hWnd),
                    Icon = _icons.GetIcon(proc.Exe),
                });
            }
            catch { /* skip individual window */ }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassNameSafe(IntPtr hWnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Resolves process name and image path via QueryFullProcessImageName, which works for elevated processes
    /// where <c>Process.MainModule</c> throws. Falls back to <see cref="Process"/> for the name only.
    /// </summary>
    private static (string Name, string Exe) DescribeProcess(int pid)
    {
        string exe = string.Empty;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) exe = sb.ToString(0, size);
            }
        }
        catch { }
        finally
        {
            if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } }
        }

        var name = WindowSizerLogic.ProcessNameFromPath(exe);
        if (name.Length == 0)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
            }
            catch { }
        }
        return (name, exe);
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch { return false; }
    }

    private static bool IsRelevantWindow(IntPtr hWnd, IntPtr selfHwnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (hWnd == selfHwnd) return false;
        if (!IsWindow(hWnd)) return false;
        if (!IsWindowVisible(hWnd)) return false;
        if (IsCloaked(hWnd)) return false; // suspended UWP apps, windows on other virtual desktops

        var ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        var owner = GetWindow(hWnd, GW_OWNER);
        if (!WindowSizerLogic.IsAltTabEligible(ex, owner != IntPtr.Zero)) return false;

        if (WindowSizerLogic.IsShellClass(GetClassNameSafe(hWnd))) return false;

        return true;
    }

    /// <summary>True if the handle still refers to a live window.</summary>
    public bool IsAlive(IntPtr hWnd)
    {
        try { return hWnd != IntPtr.Zero && IsWindow(hWnd); }
        catch { return false; }
    }

    public bool CanManageWindow(IntPtr hWnd, IntPtr selfHwnd)
    {
        try
        {
            if (!IsRelevantWindow(hWnd, selfHwnd)) return false;
            if (GetWindowTextLength(hWnd) <= 0) return false;
            GetWindowThreadProcessId(hWnd, out var pid);
            return pid != 0 && pid != Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    // ---------- geometry ----------

    /// <summary>Visible frame bounds (physical pixels). Falls back to GetWindowRect when DWM has no answer.</summary>
    public RECT GetVisibleBounds(IntPtr hWnd)
    {
        using var dpi = PhysicalPixelScope.Enter();
        return GetVisibleBoundsCore(hWnd);
    }

    private static RECT GetVisibleBoundsCore(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var wr)) return default;
        var insets = GetFrameInsetsCore(hWnd, wr);
        return new RECT(wr.Left + insets.Left, wr.Top + insets.Top, wr.Right - insets.Right, wr.Bottom - insets.Bottom);
    }

    private static WindowSizerLogic.FrameInsets GetFrameInsetsCore(IntPtr hWnd, RECT windowRect)
    {
        try
        {
            int hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>());
            if (hr != 0) return WindowSizerLogic.FrameInsets.Zero;
            return WindowSizerLogic.ComputeInsets(windowRect, frame);
        }
        catch { return WindowSizerLogic.FrameInsets.Zero; }
    }

    /// <summary>
    /// Moves/resizes so that the window's <em>visible</em> frame occupies (x, y, w, h). Restores minimized or
    /// maximized windows first, since SetWindowPos on a maximized window leaves it in a broken half-state.
    /// Returns false when the window no longer exists or Windows refused the call.
    /// </summary>
    public bool MoveAndResize(IntPtr hWnd, int x, int y, int w, int h)
    {
        try
        {
            if (!IsAlive(hWnd)) return false;
            using var dpi = PhysicalPixelScope.Enter();

            if (IsIconic(hWnd) || IsZoomed(hWnd)) ShowWindow(hWnd, SW_RESTORE);

            if (!GetWindowRect(hWnd, out var wr)) return false;
            var insets = GetFrameInsetsCore(hWnd, wr);
            var target = WindowSizerLogic.ToWindowRect(x, y, w, h, insets);
            return SetWindowPos(hWnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch { return false; }
    }

    public bool BringToFront(IntPtr hWnd)
    {
        try
        {
            if (!IsAlive(hWnd)) return false;
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            return SetForegroundWindow(hWnd);
        }
        catch { return false; }
    }

    public bool SetTopmost(IntPtr hWnd, bool topmost)
    {
        try
        {
            if (!IsAlive(hWnd)) return false;
            return SetWindowPos(hWnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { return false; }
    }

    public bool Maximize(IntPtr hWnd)
    {
        try { if (!IsAlive(hWnd)) return false; ShowWindow(hWnd, SW_MAXIMIZE); return true; } catch { return false; }
    }

    public bool Restore(IntPtr hWnd)
    {
        try { if (!IsAlive(hWnd)) return false; ShowWindow(hWnd, SW_RESTORE); return true; } catch { return false; }
    }

    public bool Center(IntPtr hWnd)
    {
        try
        {
            if (!IsAlive(hWnd)) return false;
            using var dpi = PhysicalPixelScope.Enter();
            if (!TryGetWorkAreaCore(hWnd, out var work)) return false;

            if (IsIconic(hWnd) || IsZoomed(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            var bounds = GetVisibleBoundsCore(hWnd);
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            var (x, y) = WindowSizerLogic.CenterOrigin(work, bounds.Width, bounds.Height);
            return MoveAndResize(hWnd, x, y, bounds.Width, bounds.Height);
        }
        catch { return false; }
    }

    public bool Snap(IntPtr hWnd, SnapEdge edge)
    {
        try
        {
            if (!IsAlive(hWnd)) return false;
            using var dpi = PhysicalPixelScope.Enter();
            if (!TryGetWorkAreaCore(hWnd, out var work)) return false;
            var r = WindowSizerLogic.SnapRect(work, edge);
            return MoveAndResize(hWnd, r.Left, r.Top, r.Width, r.Height);
        }
        catch { return false; }
    }

    public bool MoveToNextMonitor(IntPtr hWnd)
    {
        try
        {
            if (!IsAlive(hWnd)) return false;
            using var dpi = PhysicalPixelScope.Enter();

            var monitors = EnumerateMonitorWorkAreas();
            if (monitors.Count <= 1) return false;

            var current = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            int currentIdx = monitors.FindIndex(m => m.handle == current);
            if (currentIdx < 0) currentIdx = 0;
            var fromInfo = monitors[currentIdx];
            var toInfo = monitors[(currentIdx + 1) % monitors.Count];

            bool wasMaximized = IsZoomed(hWnd);
            if (IsIconic(hWnd) || wasMaximized) ShowWindow(hWnd, SW_RESTORE);

            var bounds = GetVisibleBoundsCore(hWnd);
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            var target = WindowSizerLogic.TranslateToWorkArea(bounds, fromInfo.work, toInfo.work);
            bool ok = MoveAndResize(hWnd, target.Left, target.Top, target.Width, target.Height);
            if (ok && wasMaximized) ShowWindow(hWnd, SW_MAXIMIZE);
            return ok;
        }
        catch { return false; }
    }

    // ---------- monitors ----------

    public static string GetMonitorName(IntPtr hWnd)
    {
        try
        {
            var mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return string.Empty;
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            return GetMonitorInfo(mon, ref info) ? WindowSizerLogic.FriendlyMonitorName(info.szDevice) : string.Empty;
        }
        catch { return string.Empty; }
    }

    public static bool TryGetWorkAreaFor(IntPtr hWnd, out RECT work)
    {
        using var dpi = PhysicalPixelScope.Enter();
        return TryGetWorkAreaCore(hWnd, out work);
    }

    private static bool TryGetWorkAreaCore(IntPtr hWnd, out RECT work)
    {
        work = default;
        var mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(mon, ref info)) return false;
        work = info.rcWork;
        return true;
    }

    public List<(IntPtr handle, RECT work)> EnumerateMonitorWorkAreas()
    {
        var list = new List<(IntPtr, RECT)>();
        try
        {
            using var dpi = PhysicalPixelScope.Enter();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref info)) list.Add((h, info.rcWork));
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }
}
