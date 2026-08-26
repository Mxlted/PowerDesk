using System;
using System.Collections.Generic;
using System.Globalization;

namespace PowerDesk.Modules.ColorPicker.Services;

/// <summary>
/// Pure color helpers for ColorPicker: hex parsing/formatting, RGB to HSL conversion with CSS-style
/// rounding, COLORREF decoding, and history bookkeeping. No WPF or Win32 dependencies so it can be
/// unit tested headlessly.
/// </summary>
internal static class ColorLogic
{
    public const int DefaultHistoryLimit = 12;

    /// <summary>
    /// Parses CSS-style hex colors: #RGB, #RGBA, #RRGGBB, #RRGGBBAA, with or without the leading '#'
    /// (a "0x" prefix is tolerated too). Alpha digits are accepted and ignored. Surrounding whitespace
    /// is trimmed. Returns false for anything else, including partial input while typing.
    /// </summary>
    public static bool TryParseHex(string? value, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var s = (value ?? string.Empty).Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        s = s.TrimStart('#');

        if (s.Length is 3 or 4)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        else if (s.Length == 8)
            s = s[..6];

        if (s.Length != 6) return false;

        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }

        r = int.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        g = int.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        b = int.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    public static int Clamp(int value) => Math.Clamp(value, 0, 255);

    public static string FormatHex(int r, int g, int b)
        => $"#{Clamp(r):x2}{Clamp(g):x2}{Clamp(b):x2}";

    public static string FormatRgb(int r, int g, int b)
        => $"rgb({Clamp(r)}, {Clamp(g)}, {Clamp(b)})";

    public static string FormatHsl(int r, int g, int b)
    {
        var (h, s, l) = ToHsl(r, g, b);
        return $"hsl({h}, {s}%, {l}%)";
    }

    /// <summary>
    /// Converts RGB to integer HSL (hue 0-359, saturation and lightness 0-100). Rounds half away
    /// from zero (not banker's rounding) and wraps a hue that rounds to 360 back to 0.
    /// </summary>
    public static (int Hue, int Saturation, int Lightness) ToHsl(int r, int g, int b)
    {
        var rd = Clamp(r) / 255d;
        var gd = Clamp(g) / 255d;
        var bd = Clamp(b) / 255d;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var light = (max + min) / 2d;
        double hue = 0;
        double sat = 0;

        var delta = max - min;
        if (delta > 0)
        {
            sat = light > 0.5 ? delta / (2d - max - min) : delta / (max + min);
            if (max == rd) hue = (gd - bd) / delta + (gd < bd ? 6 : 0);
            else if (max == gd) hue = (bd - rd) / delta + 2;
            else hue = (rd - gd) / delta + 4;
            hue /= 6;
        }

        var h = (int)Math.Round(hue * 360, MidpointRounding.AwayFromZero) % 360;
        var s = (int)Math.Round(sat * 100, MidpointRounding.AwayFromZero);
        var l = (int)Math.Round(light * 100, MidpointRounding.AwayFromZero);
        return (h, Math.Clamp(s, 0, 100), Math.Clamp(l, 0, 100));
    }

    /// <summary>Decodes a GDI COLORREF (0x00BBGGRR). Returns false for CLR_INVALID (0xFFFFFFFF).</summary>
    public static bool TryDecodeColorRef(int colorRef, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (colorRef == -1) return false;
        r = colorRef & 0xFF;
        g = (colorRef >> 8) & 0xFF;
        b = (colorRef >> 16) & 0xFF;
        return true;
    }

    /// <summary>
    /// Pushes a color onto the front of a most-recent-first history, removing any earlier duplicate
    /// (case-insensitive) and trimming to <paramref name="limit"/>. Returns true when the list changed.
    /// </summary>
    public static bool PushHistory(IList<string> history, string hex, int limit = DefaultHistoryLimit)
    {
        if (string.IsNullOrWhiteSpace(hex) || limit <= 0) return false;

        if (history.Count > 0 && string.Equals(history[0], hex, StringComparison.OrdinalIgnoreCase))
            return false;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (string.Equals(history[i], hex, StringComparison.OrdinalIgnoreCase))
                history.RemoveAt(i);
        }

        history.Insert(0, hex);
        while (history.Count > limit) history.RemoveAt(history.Count - 1);
        return true;
    }
}
