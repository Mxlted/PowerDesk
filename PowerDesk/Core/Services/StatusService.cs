using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerDesk.Core.Services;

public enum StatusKind { Info, Success, Warning, Error }

/// <summary>
/// Single status banner surface shared by the whole shell. Modules write here; the shell's status bar binds.
/// Writes are marshalled to the UI thread so background callers (scanner continuations, etc.) can use Set freely.
/// </summary>
public sealed partial class StatusService : ObservableObject
{
    [ObservableProperty] private string _message = "Ready";
    [ObservableProperty] private StatusKind _kind = StatusKind.Info;

    public void Set(string message, StatusKind kind = StatusKind.Info)
    {
        UiDispatcher.Invoke(() =>
        {
            Message = message;
            Kind = kind;
        });
    }
}
