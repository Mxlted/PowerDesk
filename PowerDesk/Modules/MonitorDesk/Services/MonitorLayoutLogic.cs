using System;
using System.Collections.Generic;
using System.Linq;
using PowerDesk.Modules.MonitorDesk.Models;

namespace PowerDesk.Modules.MonitorDesk.Services;

/// <summary>
/// Pure, Windows-free logic for MonitorDesk: stable display numbering, preset-to-monitor matching,
/// layout validation (overlaps, gaps, primary rules), and naming helpers. Kept separate from the
/// view model and the Win32 service so it can be unit tested headlessly.
/// </summary>
internal static class MonitorLayoutLogic
{
    public const int MaxLayoutNameLength = 64;

    /// <summary>
    /// Extracts the trailing number from a GDI display device name such as <c>\\.\DISPLAY3</c>.
    /// This is the number Windows itself shows in Display settings, and unlike a position-based
    /// index it does not change when monitors are rearranged. Returns 0 when it cannot be parsed.
    /// </summary>
    public static int ParseDisplayNumber(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return 0;
        var end = deviceName.Length;
        var start = end;
        while (start > 0 && char.IsDigit(deviceName[start - 1])) start--;
        if (start == end) return 0;
        return int.TryParse(deviceName.AsSpan(start, end - start), out var n) && n > 0 ? n : 0;
    }

    /// <summary>Device-name number when available, otherwise a 1-based fallback index.</summary>
    public static int DisplayNumberOrIndex(string? deviceName, int index)
    {
        var parsed = ParseDisplayNumber(deviceName);
        return parsed > 0 ? parsed : index + 1;
    }

    /// <summary>
    /// Matches each saved display to a connected monitor. Device names win; a saved display whose
    /// device name is not connected falls back to its monitor number, but only to a monitor that no
    /// other saved display already claims by name. Each monitor is used at most once.
    /// Returns an empty list when <paramref name="requireAll"/> is set and any display is unmatched.
    /// </summary>
    public static List<(MonitorLayoutDisplay Saved, MonitorInfo Monitor)> MatchPresetToMonitors(
        MonitorLayoutPreset preset, IReadOnlyList<MonitorInfo> monitors, bool requireAll)
    {
        var result = new List<(MonitorLayoutDisplay, MonitorInfo)>();
        if (preset.Displays.Count == 0 || monitors.Count == 0)
            return result;

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<MonitorLayoutDisplay, MonitorInfo>();

        // Pass 1: exact device-name matches.
        foreach (var saved in preset.Displays)
        {
            if (string.IsNullOrWhiteSpace(saved.DeviceName)) continue;
            var monitor = monitors.FirstOrDefault(m =>
                string.Equals(m.DeviceName, saved.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (monitor is null || !claimed.Add(monitor.DeviceName)) continue;
            byName[saved] = monitor;
        }

        // Pass 2: number fallback for anything still unmatched.
        foreach (var saved in preset.Displays)
        {
            if (byName.TryGetValue(saved, out var matched))
            {
                result.Add((saved, matched));
                continue;
            }

            var candidate = saved.DisplayNumber > 0
                ? monitors.FirstOrDefault(m => m.DisplayNumber == saved.DisplayNumber && !claimed.Contains(m.DeviceName))
                : null;

            if (candidate is null)
            {
                if (requireAll) return new List<(MonitorLayoutDisplay, MonitorInfo)>();
                continue;
            }

            claimed.Add(candidate.DeviceName);
            result.Add((saved, candidate));
        }

        return result;
    }

    /// <summary>True when the preset describes exactly the current arrangement (same monitors, positions, sizes, primary).</summary>
    public static bool LayoutMatches(MonitorLayoutPreset preset, IReadOnlyList<MonitorInfo> monitors)
    {
        if (preset.Displays.Count == 0 || preset.Displays.Count != monitors.Count) return false;
        var pairs = MatchPresetToMonitors(preset, monitors, requireAll: true);
        if (pairs.Count != monitors.Count) return false;

        foreach (var (saved, monitor) in pairs)
        {
            if (saved.IsPrimary != monitor.IsPrimary) return false;
            if (saved.X != monitor.X || saved.Y != monitor.Y) return false;
            if (saved.Width != monitor.Width || saved.Height != monitor.Height) return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the complete target arrangement for all connected monitors: every monitor keeps its
    /// current size; monitors present in <paramref name="overrides"/> (matched by device name) take
    /// the requested position and primary flag. If any override claims primary, the current primary
    /// flag is cleared on the others so the result has a single primary.
    /// </summary>
    public static List<MonitorLayoutDisplay> BuildTargetLayout(
        IReadOnlyList<MonitorInfo> monitors, IEnumerable<MonitorLayoutDisplay> overrides)
    {
        var overrideByDevice = overrides
            .Where(o => !string.IsNullOrWhiteSpace(o.DeviceName))
            .GroupBy(o => o.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var overridesSetPrimary = overrideByDevice.Values.Any(o => o.IsPrimary);
        var target = new List<MonitorLayoutDisplay>(monitors.Count);

        foreach (var monitor in monitors)
        {
            overrideByDevice.TryGetValue(monitor.DeviceName, out var o);
            target.Add(new MonitorLayoutDisplay
            {
                DisplayNumber = monitor.DisplayNumber,
                DeviceName = monitor.DeviceName,
                IsPrimary = o is not null ? o.IsPrimary : (!overridesSetPrimary && monitor.IsPrimary),
                X = o?.X ?? monitor.X,
                Y = o?.Y ?? monitor.Y,
                Width = monitor.Width,
                Height = monitor.Height,
            });
        }

        return target;
    }

    /// <summary>
    /// Validates a full arrangement the way Windows will judge it: positive sizes, exactly one primary
    /// located at the origin, no overlapping rectangles, and every display sharing an edge with the
    /// group (no gaps or floating displays). Returns human-readable problems; empty means valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<MonitorLayoutDisplay> layout)
    {
        var problems = new List<string>();
        if (layout.Count == 0)
        {
            problems.Add("The layout contains no displays.");
            return problems;
        }

        foreach (var d in layout)
        {
            if (d.Width <= 0 || d.Height <= 0)
                problems.Add($"{Label(d)} has an invalid size {d.Width} x {d.Height}.");
        }
        if (problems.Count > 0) return problems;

        var primaries = layout.Where(d => d.IsPrimary).ToList();
        if (primaries.Count == 0)
            problems.Add("No display is marked as primary.");
        else if (primaries.Count > 1)
            problems.Add("More than one display is marked as primary.");
        else if (primaries[0].X != 0 || primaries[0].Y != 0)
            problems.Add($"{Label(primaries[0])} is the primary display and must stay at 0,0. Move the other displays instead.");

        for (var i = 0; i < layout.Count; i++)
        {
            for (var j = i + 1; j < layout.Count; j++)
            {
                if (Overlaps(layout[i], layout[j]))
                    problems.Add($"{Label(layout[i])} overlaps {Label(layout[j])}.");
            }
        }

        if (layout.Count > 1)
        {
            var visited = new bool[layout.Count];
            var queue = new Queue<int>();
            queue.Enqueue(0);
            visited[0] = true;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                for (var k = 0; k < layout.Count; k++)
                {
                    if (visited[k] || !SharesEdge(layout[current], layout[k])) continue;
                    visited[k] = true;
                    queue.Enqueue(k);
                }
            }

            for (var k = 0; k < layout.Count; k++)
            {
                if (!visited[k])
                    problems.Add($"{Label(layout[k])} does not touch the other displays (gaps are not allowed).");
            }
        }

        return problems;
    }

    public static bool Overlaps(MonitorLayoutDisplay a, MonitorLayoutDisplay b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width
        && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>True when the two rectangles share an edge segment of positive length (corner contact is not enough).</summary>
    public static bool SharesEdge(MonitorLayoutDisplay a, MonitorLayoutDisplay b)
    {
        var horizontalTouch = a.X + a.Width == b.X || b.X + b.Width == a.X;
        var verticalOverlap = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y) > 0;
        if (horizontalTouch && verticalOverlap) return true;

        var verticalTouch = a.Y + a.Height == b.Y || b.Y + b.Height == a.Y;
        var horizontalOverlap = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X) > 0;
        return verticalTouch && horizontalOverlap;
    }

    public static (int X, int Y, int Width, int Height) VirtualBounds(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return (0, 0, 0, 0);
        var x = monitors.Min(m => m.X);
        var y = monitors.Min(m => m.Y);
        var right = monitors.Max(m => m.X + m.Width);
        var bottom = monitors.Max(m => m.Y + m.Height);
        return (x, y, right - x, bottom - y);
    }

    /// <summary>Trims the requested name (or generates a timestamped one) and de-duplicates it against existing names.</summary>
    public static string UniqueLayoutName(string? requested, IEnumerable<string> existingNames, DateTime now)
    {
        var baseName = string.IsNullOrWhiteSpace(requested)
            ? $"Layout {now:yyyy-MM-dd HH-mm}"
            : requested.Trim();
        if (baseName.Length > MaxLayoutNameLength)
            baseName = baseName[..MaxLayoutNameLength].TrimEnd();

        var taken = new HashSet<string>(existingNames.Where(n => n is not null), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Fills in missing monitor numbers (legacy presets) from the device name, else the list index.</summary>
    public static void NormalizeDisplayNumbers(MonitorLayoutPreset preset)
    {
        for (var i = 0; i < preset.Displays.Count; i++)
        {
            var d = preset.Displays[i];
            if (d.DisplayNumber <= 0)
                d.DisplayNumber = DisplayNumberOrIndex(d.DeviceName, i);
        }
    }

    private static string Label(MonitorLayoutDisplay d)
        => d.DisplayNumber > 0 ? $"Monitor {d.DisplayNumber}" : d.DeviceName;

    // ---------------------------------------------------------------- import / export

    /// <summary>Marker written into exported files so unrelated JSON is rejected with a clear message.</summary>
    public const string ExportKind = "PowerDesk.MonitorLayout";
    public const int ExportVersion = 1;
    public const long MaxImportBytes = 256 * 1024;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Envelope for exported layouts. Public so System.Text.Json can materialise it.</summary>
    public sealed class LayoutExportFile
    {
        public string Kind { get; set; } = ExportKind;
        public int Version { get; set; } = ExportVersion;
        public List<MonitorLayoutPreset>? Layouts { get; set; }
    }

    public static string SerializeLayouts(IEnumerable<MonitorLayoutPreset> presets)
    {
        var file = new LayoutExportFile
        {
            Layouts = presets.Select(p => new MonitorLayoutPreset
            {
                Id = p.Id,
                Name = p.Name,
                CreatedAt = p.CreatedAt,
                Displays = p.Displays.Select(d => new MonitorLayoutDisplay
                {
                    DisplayNumber = d.DisplayNumber,
                    DeviceName = d.DeviceName,
                    IsPrimary = d.IsPrimary,
                    X = d.X,
                    Y = d.Y,
                    Width = d.Width,
                    Height = d.Height,
                }).ToList(),
            }).ToList(),
        };
        return System.Text.Json.JsonSerializer.Serialize(file, JsonOptions);
    }

    /// <summary>
    /// Parses an exported layout file (the envelope, a bare preset object, or a bare array of presets).
    /// Every returned preset has a fresh Id, normalised display numbers, a non-empty name and at least
    /// one display with a positive size. Returns false with a human-readable error otherwise.
    /// </summary>
    public static bool TryParseLayoutFile(string? json, out List<MonitorLayoutPreset> layouts, out string error)
    {
        layouts = new List<MonitorLayoutPreset>();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The file is empty.";
            return false;
        }

        List<MonitorLayoutPreset?>? raw = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                raw = System.Text.Json.JsonSerializer.Deserialize<List<MonitorLayoutPreset?>>(json, JsonOptions);
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(root, "Layouts", out _))
                {
                    var envelope = System.Text.Json.JsonSerializer.Deserialize<LayoutExportFile>(json, JsonOptions);
                    if (envelope is not null && !string.IsNullOrEmpty(envelope.Kind)
                        && !string.Equals(envelope.Kind, ExportKind, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "This JSON file is not a PowerDesk monitor layout.";
                        return false;
                    }
                    raw = envelope?.Layouts?.Cast<MonitorLayoutPreset?>().ToList();
                }
                else if (TryGetPropertyIgnoreCase(root, "Displays", out _))
                {
                    raw = [System.Text.Json.JsonSerializer.Deserialize<MonitorLayoutPreset>(json, JsonOptions)];
                }
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            error = $"The file is not valid JSON: {ex.Message}";
            return false;
        }

        if (raw is null)
        {
            error = "This JSON file is not a PowerDesk monitor layout.";
            return false;
        }

        foreach (var preset in raw)
        {
            if (preset is null) continue;
            preset.Displays ??= new List<MonitorLayoutDisplay>();
            preset.Displays.RemoveAll(d => d is null);
            if (preset.Displays.Count == 0) continue;
            if (preset.Displays.Any(d => d.Width <= 0 || d.Height <= 0)) continue;
            foreach (var d in preset.Displays) d.DeviceName ??= string.Empty;
            preset.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = "Imported layout";
            preset.Name = preset.Name.Trim();
            if (preset.Name.Length > MaxLayoutNameLength) preset.Name = preset.Name[..MaxLayoutNameLength].TrimEnd();
            if (preset.CreatedAt == default) preset.CreatedAt = DateTime.Now;
            NormalizeDisplayNumbers(preset);
            layouts.Add(preset);
        }

        if (layouts.Count == 0)
        {
            error = "The file contains no usable layouts (each needs at least one display with a positive size).";
            return false;
        }
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(System.Text.Json.JsonElement element, string name, out System.Text.Json.JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>File name proposed when exporting a layout.</summary>
    public static string ExportFileName(string? layoutName)
    {
        var name = (layoutName ?? string.Empty).Trim();
        if (name.Length == 0) name = "monitor-layout";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim('-', ' ', '.');
        if (safe.Length == 0) safe = "monitor-layout";
        return safe + ".monitorlayout.json";
    }
}
