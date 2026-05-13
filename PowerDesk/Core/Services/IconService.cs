using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PowerDesk.Core.Logging;

namespace PowerDesk.Core.Services;

/// <summary>
/// Resolves a small executable icon to a frozen WPF BitmapSource, with a bounded LRU cache keyed
/// by full path (case-insensitive). Falls back to <c>null</c> when extraction fails. Bounded so
/// long-running sessions on machines with lots of executable churn don't grow memory unboundedly.
/// </summary>
public sealed class IconService
{
    private const int MaxEntries = 512;

    // LinkedList tracks LRU order; map points entries by path so we can move them to the front.
    private readonly LinkedList<(string Key, BitmapSource? Bmp)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource? Bmp)>> _map =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly ILogger _log;

    public IconService(ILogger log) => _log = log;

    public BitmapSource? GetIcon(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        // Normalize: strip args / quotes if a command line was passed in.
        var path = NormalizeExePath(exePath);
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (_sync)
        {
            if (_map.TryGetValue(path, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Bmp;
            }
        }

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

        lock (_sync)
        {
            // Another thread may have raced us — if so, prefer the existing entry.
            if (_map.TryGetValue(path, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Bmp;
            }
            var node = new LinkedListNode<(string, BitmapSource?)>((path, bmp));
            _lru.AddFirst(node);
            _map[path] = node;
            while (_lru.Count > MaxEntries)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
        return bmp;
    }

    public BitmapSource? GetIconForProcess(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
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
