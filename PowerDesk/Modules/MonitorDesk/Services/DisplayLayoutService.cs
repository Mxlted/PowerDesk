using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using PowerDesk.Modules.MonitorDesk.Models;

namespace PowerDesk.Modules.MonitorDesk.Services;

/// <summary>
/// Applies monitor positions through ChangeDisplaySettingsEx. Each display is staged with
/// CDS_UPDATEREGISTRY | CDS_NORESET and the whole arrangement is committed with a final
/// ChangeDisplaySettingsEx(NULL, NULL, ...) call. If any step fails, the registry entries that were
/// already staged are rolled back to their previous positions so a later commit (or reboot) does not
/// apply a half-written layout.
/// </summary>
public sealed class DisplayLayoutService
{
    private const int EnumCurrentSettings = -1;
    private const int DmPosition = 0x00000020;
    private const int CdsUpdateRegistry = 0x00000001;
    private const int CdsSetPrimary = 0x00000010;
    private const int CdsNoReset = 0x10000000;

    private const int DispChangeSuccessful = 0;
    private const int DispChangeRestart = 1;

    /// <summary>Result of applying a layout; <see cref="RestartRequired"/> is set when Windows returned DISP_CHANGE_RESTART.</summary>
    public readonly record struct ApplyResult(bool RestartRequired);

    public ApplyResult ApplyMonitorPosition(string deviceName, int x, int y)
        => ApplyPositions([new MonitorLayoutDisplay { DeviceName = deviceName, X = x, Y = y }]);

    public ApplyResult ApplyPositions(IEnumerable<MonitorLayoutDisplay> displays)
    {
        var requested = displays
            .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
            .GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            // Windows expects the new primary to be staged first so the other offsets are relative to it.
            .OrderByDescending(d => d.IsPrimary)
            .ToList();

        if (requested.Count == 0)
            throw new InvalidOperationException("No display positions were supplied.");

        // Snapshot the current positions of every display we are about to touch, for rollback.
        var originals = new List<(string Device, int X, int Y, bool WasPrimary)>(requested.Count);
        foreach (var display in requested)
        {
            var current = ReadCurrentMode(display.DeviceName);
            originals.Add((display.DeviceName, current.dmPosition.X, current.dmPosition.Y,
                current.dmPosition.X == 0 && current.dmPosition.Y == 0));
        }

        var staged = new List<(string Device, int X, int Y, bool WasPrimary)>(requested.Count);
        var restartRequired = false;

        try
        {
            for (var i = 0; i < requested.Count; i++)
            {
                var display = requested[i];
                var mode = ReadCurrentMode(display.DeviceName);
                mode.dmFields = DmPosition;
                mode.dmPosition.X = display.X;
                mode.dmPosition.Y = display.Y;

                var flags = CdsUpdateRegistry | CdsNoReset | (display.IsPrimary ? CdsSetPrimary : 0);
                var result = ChangeDisplaySettingsEx(display.DeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
                if (result != DispChangeSuccessful && result != DispChangeRestart)
                    throw new InvalidOperationException($"Windows rejected the position for {display.DeviceName}: {DisplayChangeMessage(result)}.");

                staged.Add(originals[i]);
                restartRequired |= result == DispChangeRestart;
            }

            var finalResult = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (finalResult != DispChangeSuccessful && finalResult != DispChangeRestart)
                throw new InvalidOperationException($"Windows could not apply the display layout: {DisplayChangeMessage(finalResult)}.");

            restartRequired |= finalResult == DispChangeRestart;
            return new ApplyResult(restartRequired);
        }
        catch
        {
            Rollback(staged);
            throw;
        }
    }

    private static void Rollback(List<(string Device, int X, int Y, bool WasPrimary)> staged)
    {
        if (staged.Count == 0) return;
        try
        {
            foreach (var original in staged.OrderByDescending(s => s.WasPrimary))
            {
                var mode = ReadCurrentMode(original.Device);
                mode.dmFields = DmPosition;
                mode.dmPosition.X = original.X;
                mode.dmPosition.Y = original.Y;
                var flags = CdsUpdateRegistry | CdsNoReset | (original.WasPrimary ? CdsSetPrimary : 0);
                ChangeDisplaySettingsEx(original.Device, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
            }
            ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        }
        catch
        {
            // Best effort: the original failure is the one worth surfacing.
        }
    }

    private static DEVMODE ReadCurrentMode(string deviceName)
    {
        var mode = CreateDevMode();
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not read display settings for {deviceName}.");
        return mode;
    }

    private static DEVMODE CreateDevMode()
    {
        var mode = new DEVMODE
        {
            dmDeviceName = new string('\0', 32),
            dmFormName = new string('\0', 32),
        };
        mode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        return mode;
    }

    /// <summary>Maps DISP_CHANGE_* return codes to readable text (see winuser.h).</summary>
    internal static string DisplayChangeMessage(int code) => code switch
    {
        DispChangeSuccessful => "the change succeeded",
        DispChangeRestart => "the computer must be restarted for the change to take effect",
        -1 => "the display driver failed the requested mode",
        -2 => "the requested mode is not supported",
        -3 => "the settings could not be written to the registry",
        -4 => "bad flags were supplied",
        -5 => "bad parameters were supplied (positions may overlap or leave gaps)",
        -6 => "the settings conflict with DualView",
        _ => $"error {code}",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int X;
        public int Y;
    }

    /// <summary>DEVMODEW with the display branch of the union expanded (220 bytes, Pack 8 is safe here).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public int dmFields;
        public POINTL dmPosition;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName,
        ref DEVMODE devMode,
        IntPtr hwnd,
        int flags,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName,
        IntPtr devMode,
        IntPtr hwnd,
        int flags,
        IntPtr lParam);
}
