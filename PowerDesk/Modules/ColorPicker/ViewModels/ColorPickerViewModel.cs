using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Services;
using PowerDesk.Modules.ColorPicker.Services;
using Clipboard = System.Windows.Clipboard;
using ColorDialog = System.Windows.Forms.ColorDialog;
using DialogResult = System.Windows.Forms.DialogResult;
using DrawingColor = System.Drawing.Color;
using IWin32Window = System.Windows.Forms.IWin32Window;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PowerDesk.Modules.ColorPicker.ViewModels;

/// <summary>A previously picked color shown in the history strip.</summary>
public sealed class ColorHistoryEntry
{
    public string Hex { get; init; } = string.Empty;
    public SolidColorBrush Brush { get; init; } = new(MediaColor.FromRgb(0, 0, 0));
}

public sealed partial class ColorPickerViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly StatusService _status;
    private bool _updating;

    [ObservableProperty] private string _hex = "#3b82f6";
    [ObservableProperty] private int _red = 59;
    [ObservableProperty] private int _green = 130;
    [ObservableProperty] private int _blue = 246;
    [ObservableProperty] private string _rgb = "rgb(59, 130, 246)";
    [ObservableProperty] private string _hsl = "hsl(217, 91%, 60%)";
    [ObservableProperty] private SolidColorBrush _swatchBrush = new(MediaColor.FromRgb(59, 130, 246));

    /// <summary>False while the HEX box contains text that is not a complete, valid color.</summary>
    [ObservableProperty] private bool _isHexValid = true;

    /// <summary>Most recent first; populated by screen samples, dialog picks, and committed hex entries.</summary>
    public ObservableCollection<ColorHistoryEntry> History { get; } = new();

    public bool HasHistory => History.Count > 0;

    /// <summary>Canonical lowercase #rrggbb for the current channels (what Copy HEX places on the clipboard).</summary>
    public string CanonicalHex => ColorLogic.FormatHex(Red, Green, Blue);

    public ColorPickerViewModel(ILogger log, StatusService status)
    {
        _log = log;
        _status = status;
        SetColor(Red, Green, Blue, rewriteHex: true);
    }

    partial void OnHexChanged(string value)
    {
        if (_updating) return;
        if (ColorLogic.TryParseHex(value, out var r, out var g, out var b))
        {
            // Keep the user's text as typed (rewriting it mid-keystroke made 6-digit entry impossible).
            SetColor(r, g, b, rewriteHex: false);
            IsHexValid = true;
        }
        else
        {
            IsHexValid = false;
        }
    }

    partial void OnRedChanged(int value)
    {
        if (!_updating) SetColor(value, Green, Blue, rewriteHex: true);
    }

    partial void OnGreenChanged(int value)
    {
        if (!_updating) SetColor(Red, value, Blue, rewriteHex: true);
    }

    partial void OnBlueChanged(int value)
    {
        if (!_updating) SetColor(Red, Green, value, rewriteHex: true);
    }

    /// <summary>Normalizes the HEX box to #rrggbb (Enter / focus loss) and records the color in history.</summary>
    [RelayCommand]
    private void CommitHex()
    {
        if (!ColorLogic.TryParseHex(Hex, out var r, out var g, out var b))
        {
            _status.Set("Enter a color as #RGB, #RRGGBB, or #RRGGBBAA.", StatusKind.Warning);
            return;
        }

        SetColor(r, g, b, rewriteHex: true);
        IsHexValid = true;
        AddToHistory();
    }

    [RelayCommand]
    private void OpenColorDialog()
    {
        try
        {
            using var dialog = new ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = DrawingColor.FromArgb(Red, Green, Blue),
            };

            var owner = TryGetShellOwner();
            var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (result == DialogResult.OK)
            {
                SetColor(dialog.Color.R, dialog.Color.G, dialog.Color.B, rewriteHex: true);
                AddToHistory();
                _status.Set($"Color set to {CanonicalHex}.", StatusKind.Success);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Color dialog", ex);
            _status.Set("Color picker dialog failed.", StatusKind.Warning);
        }
    }

    [RelayCommand]
    private void SampleCursorPixel() => SampleAtCursor(announce: true);

    /// <summary>Samples the pixel under the mouse cursor using physical screen coordinates.</summary>
    public bool SampleAtCursor(bool announce = true)
    {
        try
        {
            if (!GetCursorPos(out var point))
            {
                if (announce) _status.Set("Could not read cursor position.", StatusKind.Warning);
                return false;
            }

            return SampleScreenPixelAt(point.X, point.Y, announce);
        }
        catch (Exception ex)
        {
            _log.Error("Sample cursor pixel", ex);
            if (announce) _status.Set("Screen pixel sample failed.", StatusKind.Warning);
            return false;
        }
    }

    /// <summary>
    /// Samples a pixel from the whole virtual screen (negative coordinates for monitors left of or
    /// above the primary are valid). Coordinates are physical pixels, as returned by GetCursorPos.
    /// </summary>
    public bool SampleScreenPixelAt(int x, int y, bool announce = true)
    {
        var dc = IntPtr.Zero;
        try
        {
            dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                if (announce) _status.Set("Could not sample the screen.", StatusKind.Warning);
                return false;
            }

            var colorRef = GetPixel(dc, x, y);
            if (!ColorLogic.TryDecodeColorRef(colorRef, out var r, out var g, out var b))
            {
                if (announce) _status.Set($"No pixel at {x},{y} (outside every display).", StatusKind.Warning);
                return false;
            }

            SetColor(r, g, b, rewriteHex: true);
            AddToHistory();
            if (announce) _status.Set($"Sampled {CanonicalHex} at {x},{y}.", StatusKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Sample screen pixel", ex);
            if (announce) _status.Set("Screen pixel sample failed.", StatusKind.Warning);
            return false;
        }
        finally
        {
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [RelayCommand] private void CopyHex() => Copy(CanonicalHex, "HEX copied.");
    [RelayCommand] private void CopyRgb() => Copy(Rgb, "RGB copied.");
    [RelayCommand] private void CopyHsl() => Copy(Hsl, "HSL copied.");

    [RelayCommand]
    private void SelectHistory(ColorHistoryEntry? entry)
    {
        if (entry is null || !ColorLogic.TryParseHex(entry.Hex, out var r, out var g, out var b)) return;
        SetColor(r, g, b, rewriteHex: true);
        AddToHistory();
        _status.Set($"Color set to {CanonicalHex}.", StatusKind.Info);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        OnPropertyChanged(nameof(HasHistory));
    }

    private void AddToHistory()
    {
        var hex = CanonicalHex;
        var hexes = History.Select(h => h.Hex).ToList();
        if (!ColorLogic.PushHistory(hexes, hex)) return;

        History.Clear();
        foreach (var h in hexes)
        {
            ColorLogic.TryParseHex(h, out var r, out var g, out var b);
            var brush = new SolidColorBrush(MediaColor.FromRgb((byte)r, (byte)g, (byte)b));
            brush.Freeze();
            History.Add(new ColorHistoryEntry { Hex = h, Brush = brush });
        }
        OnPropertyChanged(nameof(HasHistory));
    }

    private void SetColor(int red, int green, int blue, bool rewriteHex)
    {
        _updating = true;
        try
        {
            Red = ColorLogic.Clamp(red);
            Green = ColorLogic.Clamp(green);
            Blue = ColorLogic.Clamp(blue);
            if (rewriteHex)
            {
                Hex = ColorLogic.FormatHex(Red, Green, Blue);
                IsHexValid = true;
            }
            Rgb = ColorLogic.FormatRgb(Red, Green, Blue);
            Hsl = ColorLogic.FormatHsl(Red, Green, Blue);
            var brush = new SolidColorBrush(MediaColor.FromRgb((byte)Red, (byte)Green, (byte)Blue));
            brush.Freeze();
            SwatchBrush = brush;
            OnPropertyChanged(nameof(CanonicalHex));
        }
        finally
        {
            _updating = false;
        }
    }

    private void Copy(string text, string message)
    {
        try
        {
            SetClipboardText(text);
            _status.Set(message, StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Copy color", ex);
            _status.Set("Could not copy color (clipboard is busy). Try again.", StatusKind.Warning);
        }
    }

    /// <summary>Clipboard access fails transiently (CLIPBRD_E_CANT_OPEN) while another app holds it; retry briefly.</summary>
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

    private static IWin32Window? TryGetShellOwner()
    {
        try
        {
            var window = System.Windows.Application.Current?.MainWindow;
            if (window is null || !window.IsVisible) return null;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            return handle == IntPtr.Zero ? null : new HandleOwner(handle);
        }
        catch
        {
            return null;
        }
    }

    private sealed class HandleOwner(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern int GetPixel(IntPtr hdc, int x, int y);
}
