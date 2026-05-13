using System;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PowerDesk.Core.Services;

/// <summary>
/// Abstracts modal confirmation prompts so view-models don't reach into <c>MessageBox</c> directly.
/// The default implementation uses <see cref="MessageBox"/> rooted at the shell window so dialogs
/// always appear on the correct monitor and stay modal to the right owner.
/// </summary>
public interface IConfirmationService
{
    /// <summary>Returns true if the user confirms (OK/Yes), false on cancel or error.</summary>
    bool Confirm(string message, string title, bool destructive = false);
}

public sealed class ConfirmationService : IConfirmationService
{
    public bool Confirm(string message, string title, bool destructive = false)
    {
        Window? owner = null;
        try { owner = System.Windows.Application.Current?.MainWindow; } catch { }

        MessageBoxResult result = MessageBoxResult.None;
        UiDispatcher.Invoke(() =>
        {
            var image = destructive ? MessageBoxImage.Warning : MessageBoxImage.Question;
            if (owner is { IsVisible: true })
                result = MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, image);
            else
                result = MessageBox.Show(message, title, MessageBoxButton.OKCancel, image);
        });
        return result == MessageBoxResult.OK;
    }
}
