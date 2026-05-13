using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerDesk.Modules.WindowSizer.Models;

public sealed partial class WindowInfo : ObservableObject
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ExePath { get; init; } = string.Empty;
    public int ProcessId { get; init; }

    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;
    [ObservableProperty] private int _width;
    [ObservableProperty] private int _height;
    [ObservableProperty] private bool _isTopmost;
    [ObservableProperty] private string _monitor = string.Empty;
    [ObservableProperty] private BitmapSource? _icon;

    public string Geometry => $"{Width} × {Height} @ {X},{Y}";
}

public sealed class SizePreset
{
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }

    public override string ToString() => string.IsNullOrEmpty(Name) ? $"{Width} × {Height}" : $"{Name} ({Width} × {Height})";
}

public sealed class LayoutPreset
{
    public string Name { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? TargetProcessName { get; set; }
}

public enum HotkeyAction
{
    SnapLeft,
    SnapRight,
    SnapTop,
    SnapBottom,
    Center,
    Maximize,
    ApplyLayoutPreset,
}

public sealed partial class HotkeyBinding : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public HotkeyAction Action { get; set; }
    public string? LayoutPresetName { get; set; }
    public uint Modifiers { get; set; } // MOD_ALT|MOD_CONTROL|etc
    public uint VirtualKey { get; set; }
    [ObservableProperty] private bool _enabled = true;

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if ((Modifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((Modifiers & 0x0001) != 0) parts.Add("Alt");
            if ((Modifiers & 0x0004) != 0) parts.Add("Shift");
            if ((Modifiers & 0x0008) != 0) parts.Add("Win");
            parts.Add(KeyNameFromVk(VirtualKey));
            return string.Join("+", parts);
        }
    }

    public string ActionLabel
    {
        get => Action switch
        {
            HotkeyAction.SnapLeft     => "Snap left",
            HotkeyAction.SnapRight    => "Snap right",
            HotkeyAction.SnapTop      => "Snap top",
            HotkeyAction.SnapBottom   => "Snap bottom",
            HotkeyAction.Center       => "Center",
            HotkeyAction.Maximize     => "Maximize",
            HotkeyAction.ApplyLayoutPreset => $"Apply layout: {LayoutPresetName}",
            _ => Action.ToString(),
        };
    }

    private static string KeyNameFromVk(uint vk)
    {
        // Virtual-key name table for the handful we expose in the recorder.
        return vk switch
        {
            0x25 => "Left",
            0x27 => "Right",
            0x26 => "Up",
            0x28 => "Down",
            0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4", 0x74 => "F5", 0x75 => "F6",
            0x76 => "F7", 0x77 => "F8", 0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
            >= 0x30 and <= 0x39 => ((char)('0' + (vk - 0x30))).ToString(),
            >= 0x41 and <= 0x5A => ((char)('A' + (vk - 0x41))).ToString(),
            _ => $"VK_0x{vk:X2}",
        };
    }
}

public sealed class WindowSizerSettings
{
    public List<SizePreset> SizePresets { get; set; } = DefaultSizePresets();
    public List<LayoutPreset> LayoutPresets { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = DefaultHotkeys();
    public int AutoRefreshSeconds { get; set; } = 2;

    public static List<SizePreset> DefaultSizePresets() => new()
    {
        new SizePreset { Name = "1080p",    Width = 1920, Height = 1080 },
        new SizePreset { Name = "1440p",    Width = 2560, Height = 1440 },
        new SizePreset { Name = "720p",     Width = 1280, Height = 720  },
        new SizePreset { Name = "Compact",  Width = 800,  Height = 600  },
        new SizePreset { Name = "Portrait", Width = 1080, Height = 1920 },
    };

    public static List<HotkeyBinding> DefaultHotkeys() => new()
    {
        new HotkeyBinding { Action = HotkeyAction.SnapLeft,   Modifiers = 0x0002 | 0x0001, VirtualKey = 0x25 },
        new HotkeyBinding { Action = HotkeyAction.SnapRight,  Modifiers = 0x0002 | 0x0001, VirtualKey = 0x27 },
        new HotkeyBinding { Action = HotkeyAction.SnapTop,    Modifiers = 0x0002 | 0x0001, VirtualKey = 0x26 },
        new HotkeyBinding { Action = HotkeyAction.SnapBottom, Modifiers = 0x0002 | 0x0001, VirtualKey = 0x28 },
        new HotkeyBinding { Action = HotkeyAction.Center,     Modifiers = 0x0002 | 0x0001, VirtualKey = 0x43 }, // C
        new HotkeyBinding { Action = HotkeyAction.Maximize,   Modifiers = 0x0002 | 0x0001, VirtualKey = 0x4D }, // M
    };
}
