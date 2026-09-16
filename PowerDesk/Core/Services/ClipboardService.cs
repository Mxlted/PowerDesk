using Clipboard = System.Windows.Clipboard;

namespace PowerDesk.Core.Services;

/// <summary>
/// Clipboard writes that survive the clipboard being briefly owned by another process.
/// <para>
/// <c>OpenClipboard</c> fails with CLIPBRD_E_CANT_OPEN whenever another application (a clipboard
/// manager, a remote-desktop session, Office) holds it, which happens often enough that a single
/// unguarded <c>Clipboard.SetText</c> throws for users several times a day. Every copy in the app
/// should route through here so they all get the same short retry and never crash the shell.
/// </para>
/// </summary>
public static class ClipboardService
{
    private const int Attempts = 5;

    /// <summary>Copies text, retrying briefly when the clipboard is busy. Returns false when every attempt failed.</summary>
    public static bool TrySetText(string? text)
    {
        var value = text ?? string.Empty;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // SetDataObject(copy: true) flushes to the OS so the text survives PowerDesk exiting.
                Clipboard.SetDataObject(value, true);
                return true;
            }
            catch (Exception) when (attempt < Attempts)
            {
                Thread.Sleep(30 * attempt);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
