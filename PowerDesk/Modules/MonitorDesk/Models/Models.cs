namespace PowerDesk.Modules.MonitorDesk.Models;

public sealed class MonitorInfo
{
    public string DeviceName { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int WorkX { get; init; }
    public int WorkY { get; init; }
    public int WorkWidth { get; init; }
    public int WorkHeight { get; init; }
    public int BitsPerPixel { get; init; }

    public string PrimaryLabel => IsPrimary ? "Primary" : "Secondary";
    public string BoundsLabel => $"{Width} x {Height} @ {X},{Y}";
    public string WorkAreaLabel => $"{WorkWidth} x {WorkHeight} @ {WorkX},{WorkY}";
}

public sealed class MonitorLayoutPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<MonitorLayoutDisplay> Displays { get; set; } = new();

    public int DisplayCount => Displays.Count;
    public string DisplayCountLabel => $"{DisplayCount} display{(DisplayCount == 1 ? string.Empty : "s")}";
    public string Summary => Displays.Count == 0
        ? "No displays"
        : string.Join("; ", Displays.Select(d => $"{d.DeviceName} {d.BoundsLabel}"));

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "Unnamed layout" : Name;
}

public sealed class MonitorLayoutDisplay
{
    public string DeviceName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public string PrimaryLabel => IsPrimary ? "Primary" : "Secondary";
    public string BoundsLabel => $"{Width} x {Height} @ {X},{Y}";
}

public sealed class MonitorDeskSettings
{
    public List<MonitorLayoutPreset> LayoutPresets { get; set; } = new();
}
