using System;
using System.Collections.ObjectModel;

namespace PowerDesk.Core.Services;

public sealed class RecentAction
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Module { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string TimestampDisplay => Timestamp.ToString("HH:mm:ss");
}

/// <summary>
/// Lightweight cross-module activity feed used by the dashboard.
/// Mutations are marshalled to the UI thread so background callers can append safely.
/// </summary>
public sealed class RecentActionsService
{
    private const int Max = 50;
    public ObservableCollection<RecentAction> Items { get; } = new();

    public void Add(string module, string description)
    {
        var action = new RecentAction { Module = module, Description = description };
        UiDispatcher.Invoke(() =>
        {
            Items.Insert(0, action);
            while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
        });
    }
}
