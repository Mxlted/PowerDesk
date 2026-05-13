using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using PowerDesk.Modules.MonitorDesk.Models;

namespace PowerDesk.Modules.MonitorDesk.Services;

public sealed class DisplayLayoutService
{
    private const int EnumCurrentSettings = -1;
    private const int DmPosition = 0x00000020;
    private const int CdsUpdateRegistry = 0x00000001;
    private const int CdsNoReset = 0x10000000;
    private const int DispChangeSuccessful = 0;

    public void ApplyMonitorPosition(string deviceName, int x, int y)
        => ApplyPositions([new MonitorLayoutDisplay { DeviceName = deviceName, X = x, Y = y }]);

    public void ApplyPositions(IEnumerable<MonitorLayoutDisplay> displays)
    {
        var requested = displays
            .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
            .GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        if (requested.Count == 0)
            throw new InvalidOperationException("No display positions were supplied.");

        foreach (var display in requested)
        {
            var mode = CreateDevMode();
            if (!EnumDisplaySettings(display.DeviceName, EnumCurrentSettings, ref mode))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not read display settings for {display.DeviceName}.");

            mode.dmFields = DmPosition;
            mode.dmPosition.X = display.X;
            mode.dmPosition.Y = display.Y;

            var result = ChangeDisplaySettingsEx(
                display.DeviceName,
                ref mode,
                IntPtr.Zero,
                CdsUpdateRegistry | CdsNoReset,
                IntPtr.Zero);

            if (result != DispChangeSuccessful)
                throw new InvalidOperationException($"Windows rejected the position for {display.DeviceName}: {DisplayChangeMessage(result)}.");
        }

        var finalResult = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (finalResult != DispChangeSuccessful)
            throw new InvalidOperationException($"Windows could not apply the display layout: {DisplayChangeMessage(finalResult)}.");
    }

    private static DEVMODE CreateDevMode()
    {
        var mode = new DEVMODE
        {
            dmDeviceName = new string('\0', 32),
            dmFormName = new string('\0', 32),
        };
        mode.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        return mode;
    }

    private static string DisplayChangeMessage(int code) => code switch
    {
        -1 => "the computer must be restarted",
        -2 => "the display driver failed",
        -3 => "the mode is not supported",
        -4 => "bad flags were supplied",
        -5 => "bad parameters were supplied",
        -6 => "the settings could not be written to the registry",
        _ => $"error {code}",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
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

        public short dmLogPixels;
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
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName,
        ref DEVMODE devMode,
        IntPtr hwnd,
        int flags,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName,
        IntPtr devMode,
        IntPtr hwnd,
        int flags,
        IntPtr lParam);
}
