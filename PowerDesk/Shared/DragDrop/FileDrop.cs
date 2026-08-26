using System;
using System.Collections.Generic;
using System.Linq;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace PowerDesk.Shared.DragDrop;

/// <summary>
/// Shared Explorer drag-and-drop plumbing for module pages.
/// <para>
/// Pages wire <c>PreviewDragOver</c> / <c>PreviewDrop</c> on their root element (not the bubbling
/// <c>DragOver</c> / <c>Drop</c>): WPF text boxes and data grids handle the bubbling events
/// themselves and refuse file payloads, which made a drop over an input box silently do nothing.
/// The tunneling events run first, so the whole page is a drop target no matter what is under
/// the cursor. Non-file payloads (text, etc.) are left alone so text boxes keep their own text
/// drag support.
/// </para>
/// </summary>
public static class FileDrop
{
    /// <summary>True when the payload contains Explorer file/folder paths.</summary>
    public static bool HasFiles(DragEventArgs? e)
    {
        try { return e?.Data?.GetDataPresent(DataFormats.FileDrop) == true; }
        catch { return false; }
    }

    /// <summary>
    /// Extracts the dropped paths. Never throws: drag sources can hand over broken data objects,
    /// and a drop must never take the shell down.
    /// </summary>
    public static IReadOnlyList<string> GetPaths(DragEventArgs? e)
    {
        try
        {
            if (!HasFiles(e)) return Array.Empty<string>();
            if (e!.Data.GetData(DataFormats.FileDrop) is not string[] raw) return Array.Empty<string>();
            return raw.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Standard <c>PreviewDragOver</c> handler: shows the copy cursor for file payloads and claims
    /// the event so child controls cannot veto it. Other payloads are ignored.
    /// </summary>
    public static void OnDragOver(DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>
    /// Standard <c>PreviewDrop</c> handler: hands the paths to <paramref name="accept"/> and
    /// swallows anything the callback throws (the view models report their own failures).
    /// Returns true when the payload was a file drop.
    /// </summary>
    public static bool OnDrop(DragEventArgs e, Action<IReadOnlyList<string>> accept)
    {
        if (!HasFiles(e)) return false;
        e.Handled = true;
        try
        {
            var paths = GetPaths(e);
            if (paths.Count > 0) accept(paths);
        }
        catch (Exception ex)
        {
            App.Instance?.Logger?.Error("File drop", ex);
        }
        return true;
    }
}
