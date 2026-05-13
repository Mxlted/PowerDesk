using System;
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Services;
using Clipboard = System.Windows.Clipboard;
using ColorDialog = System.Windows.Forms.ColorDialog;
using DialogResult = System.Windows.Forms.DialogResult;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PowerDesk.Modules.ColorPicker.ViewModels;

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

    public ColorPickerViewModel(ILogger log, StatusService status)
    {
        _log = log;
        _status = status;
        SetColor(Red, Green, Blue);
    }

    partial void OnHexChanged(string value)
    {
        if (_updating) return;
        if (TryParseHex(value, out var r, out var g, out var b))
            SetColor(r, g, b);
    }

    partial void OnRedChanged(int value)
    {
        if (!_updating) SetColor(value, Green, Blue);
    }

    partial void OnGreenChanged(int value)
    {
        if (!_updating) SetColor(Red, value, Blue);
    }

    partial void OnBlueChanged(int value)
    {
        if (!_updating) SetColor(Red, Green, value);
    }

    [RelayCommand]
    private void OpenColorDialog()
    {
        try
        {
            using var dialog = new ColorDialog
            {
                FullOpen = true,
                Color = DrawingColor.FromArgb(Red, Green, Blue),
            };
            if (dialog.ShowDialog() == DialogResult.OK)
                SetColor(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        }
        catch (Exception ex)
        {
            _log.Error("Color dialog", ex);
            _status.Set("Color picker dialog failed.", StatusKind.Warning);
        }
    }

    [RelayCommand]
    private void SampleCursorPixel()
    {
        try
        {
            if (!GetCursorPos(out var point))
            {
                _status.Set("Could not read cursor position.", StatusKind.Warning);
                return;
            }

            SampleScreenPixelAt(point.X, point.Y);
        }
        catch (Exception ex)
        {
            _log.Error("Sample cursor pixel", ex);
            _status.Set("Screen pixel sample failed.", StatusKind.Warning);
        }
    }

    public bool SampleScreenPixelAt(int x, int y, bool announce = true)
    {
        try
        {
            var dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                if (announce) _status.Set("Could not sample the screen.", StatusKind.Warning);
                return false;
            }

            try
            {
                var colorRef = GetPixel(dc, x, y);
                if (colorRef == -1)
                {
                    if (announce) _status.Set("Screen pixel sample failed.", StatusKind.Warning);
                    return false;
                }

                var r = colorRef & 0xFF;
                var g = (colorRef >> 8) & 0xFF;
                var b = (colorRef >> 16) & 0xFF;
                SetColor(r, g, b);
                if (announce) _status.Set($"Sampled pixel at {x},{y}.", StatusKind.Success);
                return true;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Sample screen pixel", ex);
            if (announce) _status.Set("Screen pixel sample failed.", StatusKind.Warning);
            return false;
        }
    }

    [RelayCommand] private void CopyHex() => Copy(Hex, "HEX copied.");
    [RelayCommand] private void CopyRgb() => Copy(Rgb, "RGB copied.");
    [RelayCommand] private void CopyHsl() => Copy(Hsl, "HSL copied.");

    private void SetColor(int red, int green, int blue)
    {
        _updating = true;
        try
        {
            Red = Clamp(red);
            Green = Clamp(green);
            Blue = Clamp(blue);
            Hex = $"#{Red:X2}{Green:X2}{Blue:X2}".ToLowerInvariant();
            Rgb = $"rgb({Red}, {Green}, {Blue})";
            Hsl = ToHsl(Red, Green, Blue);
            var brush = new SolidColorBrush(MediaColor.FromRgb((byte)Red, (byte)Green, (byte)Blue));
            brush.Freeze();
            SwatchBrush = brush;
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
            Clipboard.SetText(text);
            _status.Set(message, StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Copy color", ex);
            _status.Set("Could not copy color.", StatusKind.Warning);
        }
    }

    private static bool TryParseHex(string value, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var s = (value ?? string.Empty).Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return false;
        return int.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && int.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && int.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 255);

    private static string ToHsl(int r, int g, int b)
    {
        var rd = r / 255d;
        var gd = g / 255d;
        var bd = b / 255d;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var light = (max + min) / 2d;
        double hue = 0;
        double sat = 0;

        if (Math.Abs(max - min) > double.Epsilon)
        {
            var delta = max - min;
            sat = light > 0.5 ? delta / (2d - max - min) : delta / (max + min);
            if (Math.Abs(max - rd) < double.Epsilon) hue = (gd - bd) / delta + (gd < bd ? 6 : 0);
            else if (Math.Abs(max - gd) < double.Epsilon) hue = (bd - rd) / delta + 2;
            else hue = (rd - gd) / delta + 4;
            hue /= 6;
        }

        return $"hsl({Math.Round(hue * 360)}, {Math.Round(sat * 100)}%, {Math.Round(light * 100)}%)";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern int GetPixel(IntPtr hdc, int x, int y);
}
