using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using PowerDesk.Core.Services;
using PowerDesk.Modules.WindowSizer.Models;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;

namespace PowerDesk.Modules.WindowSizer.Services;

/// <summary>
/// Win32-backed enumeration and manipulation of top-level visible windows.
/// All methods are tolerant of HWNDs that have gone away mid-call.
/// </summary>
public sealed class WindowService
{
    private readonly IconService _icons;

    public WindowService(IconService icons) => _icons = icons;

    public List<WindowInfo> EnumerateWindows(IntPtr selfHwnd)
    {
        var list = new List<WindowInfo>(64);

        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsRelevantWindow(hWnd, selfHwnd)) return true;

                var titleLen = GetWindowTextLength(hWnd);
                if (titleLen <= 0) return true;

                var sb = new StringBuilder(titleLen + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                GetWindowThreadProcessId(hWnd, out var pid);
                string proc = "";
                string exe  = "";
                try
                {
                    var p = Process.GetProcessById(pid);
                    proc = p.ProcessName;
                    try { exe = p.MainModule?.FileName ?? string.Empty; } catch { }
                }
                catch { }

                if (string.Equals(proc, "PowerDesk", StringComparison.OrdinalIgnoreCase)) return true;

                GetWindowRect(hWnd, out var rect);

                var ex = GetWindowLong(hWnd, GWL_EXSTYLE);
                var topmost = (ex & WS_EX_TOPMOST) != 0;
                var monitorName = GetMonitorName(hWnd);

                list.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessName = proc,
                    ExePath = exe,
                    ProcessId = pid,
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height,
                    IsTopmost = topmost,
                    Monitor = monitorName,
                    Icon = _icons.GetIcon(exe),
                });
            }
            catch { /* skip individual window */ }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static bool IsRelevantWindow(IntPtr hWnd, IntPtr selfHwnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (hWnd == selfHwnd) return false;
        if (!IsWindowVisible(hWnd)) return false;

        var ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;

        var owner = GetWindow(hWnd, GW_OWNER);
        if (owner != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;

        return true;
    }

    public void MoveAndResize(IntPtr hWnd, int x, int y, int w, int h)
    {
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            if (IsZoomed(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            MoveWindow(hWnd, x, y, Math.Max(50, w), Math.Max(50, h), true);
        }
        catch { }
    }

    public void BringToFront(IntPtr hWnd)
    {
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
        catch { }
    }

    public void SetTopmost(IntPtr hWnd, bool topmost)
    {
        try
        {
            SetWindowPos(hWnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    public void Maximize(IntPtr hWnd)
    {
        try { ShowWindow(hWnd, SW_MAXIMIZE); } catch { }
    }

    public void Restore(IntPtr hWnd)
    {
        try { ShowWindow(hWnd, SW_RESTORE); } catch { }
    }

    public void Center(IntPtr hWnd)
    {
        if (!TryGetWorkAreaFor(hWnd, out var work)) return;
        if (!GetWindowRect(hWnd, out var rect)) return;

        int w = rect.Width, h = rect.Height;
        int x = work.Left + (work.Width - w) / 2;
        int y = work.Top + (work.Height - h) / 2;
        MoveAndResize(hWnd, x, y, w, h);
    }

    public enum SnapEdge { Left, Right, Top, Bottom }

    public void Snap(IntPtr hWnd, SnapEdge edge)
    {
        if (!TryGetWorkAreaFor(hWnd, out var work)) return;
        int x = work.Left, y = work.Top, w = work.Width, h = work.Height;
        switch (edge)
        {
            case SnapEdge.Left:   w = work.Width / 2; break;
            case SnapEdge.Right:  x = work.Left + work.Width / 2; w = work.Width - work.Width / 2; break;
            case SnapEdge.Top:    h = work.Height / 2; break;
            case SnapEdge.Bottom: y = work.Top + work.Height / 2; h = work.Height - work.Height / 2; break;
        }
        MoveAndResize(hWnd, x, y, w, h);
    }

    public void MoveToNextMonitor(IntPtr hWnd)
    {
        var monitors = EnumerateMonitorWorkAreas();
        if (monitors.Count <= 1) return;

        var current = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        int currentIdx = monitors.FindIndex(m => m.handle == current);
        if (currentIdx < 0) currentIdx = 0;
        int nextIdx = (currentIdx + 1) % monitors.Count;
        var fromInfo = monitors[currentIdx];
        var toInfo = monitors[nextIdx];

        if (!GetWindowRect(hWnd, out var rect)) return;

        // Translate origin from old work-area to new work-area, keep size.
        int relX = rect.Left - fromInfo.work.Left;
        int relY = rect.Top - fromInfo.work.Top;
        int newX = toInfo.work.Left + Math.Min(relX, Math.Max(0, toInfo.work.Width - rect.Width));
        int newY = toInfo.work.Top  + Math.Min(relY, Math.Max(0, toInfo.work.Height - rect.Height));
        MoveAndResize(hWnd, newX, newY, rect.Width, rect.Height);
    }

    public static string GetMonitorName(IntPtr hWnd)
    {
        var mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return string.Empty;
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(mon, ref info) ? info.szDevice : string.Empty;
    }

    public static bool TryGetWorkAreaFor(IntPtr hWnd, out RECT work)
    {
        work = default;
        var mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(mon, ref info)) return false;
        work = info.rcWork;
        return true;
    }

    public List<(IntPtr handle, RECT work)> EnumerateMonitorWorkAreas()
    {
        var list = new List<(IntPtr, RECT)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref info)) list.Add((h, info.rcWork));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
