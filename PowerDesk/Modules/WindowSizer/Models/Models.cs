using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerDesk.Modules.WindowSizer.Models;

/// <summary>A top-level window. Geometry is the visible frame in physical pixels.</summary>
public sealed partial class WindowInfo : ObservableObject
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ExePath { get; init; } = string.Empty;
    public int ProcessId { get; init; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Geometry))] private int _x;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Geometry))] private int _y;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Geometry))] private int _width;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Geometry))] private int _height;
    [ObservableProperty] private bool _isTopmost;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateLabel))] private bool _isMinimized;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateLabel))] private bool _isMaximized;
    [ObservableProperty] private string _monitor = string.Empty;
    [ObservableProperty] private BitmapSource? _icon;

    public string Geometry => $"{Width} × {Height} @ {X},{Y}";

    /// <summary>Short state word for the grid; empty for a normal window.</summary>
    public string StateLabel => IsMinimized ? "Min" : IsMaximized ? "Max" : string.Empty;
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

    /// <summary>One-line description for lists; not persisted.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var s = $"{Width} × {Height} @ {X},{Y}";
            return string.IsNullOrWhiteSpace(TargetProcessName) ? s : $"{s} • {TargetProcessName}";
        }
    }
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

    /// <summary>
    /// Human-readable name for a Windows virtual-key code. The recorder accepts any key, so this covers
    /// everything a keyboard is likely to send (navigation, editing, numpad, F1-F24, OEM punctuation,
    /// media keys) and only falls back to the raw code for truly exotic keys.
    /// </summary>
    internal static string KeyNameFromVk(uint vk)
    {
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0C => "Clear",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5D => "Menu",
            >= 0x30 and <= 0x39 => ((char)('0' + (vk - 0x30))).ToString(),
            >= 0x41 and <= 0x5A => ((char)('A' + (vk - 0x41))).ToString(),
            >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
            0x6A => "Num*",
            0x6B => "Num+",
            0x6C => "NumEnter",
            0x6D => "Num-",
            0x6E => "Num.",
            0x6F => "Num/",
            >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xA6 => "BrowserBack",
            0xA7 => "BrowserForward",
            0xA8 => "BrowserRefresh",
            0xAC => "BrowserHome",
            0xAD => "VolumeMute",
            0xAE => "VolumeDown",
            0xAF => "VolumeUp",
            0xB0 => "MediaNext",
            0xB1 => "MediaPrev",
            0xB2 => "MediaStop",
            0xB3 => "MediaPlayPause",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            0xE2 => "\\",
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
