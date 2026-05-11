using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PowerDesk.Core.Logging;

namespace PowerDesk.Core.Services;

/// <summary>
/// Resolves a small executable icon to a frozen WPF BitmapSource, with an in-memory cache keyed by full path
/// (lowered). Falls back to the shell's generic icon and finally <c>null</c>.
/// </summary>
public sealed class IconService
{
    private readonly ConcurrentDictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _log;

    public IconService(ILogger log) => _log = log;

    public BitmapSource? GetIcon(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        // Normalize: strip args / quotes if a command line was passed in.
        var path = NormalizeExePath(exePath);
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (_cache.TryGetValue(path, out var cached)) return cached;

        BitmapSource? bmp = null;
        try
        {
            if (File.Exists(path))
            {
                using var ico = Icon.ExtractAssociatedIcon(path);
                if (ico is not null)
                {
                    bmp = Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(16, 16));
                    bmp?.Freeze();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"IconService: failed to extract icon for '{path}': {ex.Message}");
        }

        _cache[path] = bmp;
        return bmp;
    }

    public BitmapSource? GetIconForProcess(int pid)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            var path = proc.MainModule?.FileName;
            return GetIcon(path);
        }
        catch { return null; }
    }

    private static string NormalizeExePath(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return s;

        // Strip leading/trailing quotes around the executable.
        if (s.StartsWith("\""))
        {
            var end = s.IndexOf('"', 1);
            if (end > 1) return s.Substring(1, end - 1);
        }
        // Take everything up to the first space if no quotes and not an existing path.
        if (!File.Exists(s))
        {
            var sp = s.IndexOf(' ');
            if (sp > 0)
            {
                var head = s[..sp];
                if (File.Exists(head)) return head;
            }
            // Try expanding env vars (%SystemRoot%, etc.)
            var expanded = Environment.ExpandEnvironmentVariables(s);
            if (File.Exists(expanded)) return expanded;
            var spExp = expanded.IndexOf(' ');
            if (spExp > 0 && File.Exists(expanded[..spExp])) return expanded[..spExp];
        }
        return s;
    }
}
