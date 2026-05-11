using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerDesk.Core.Services;

public enum StatusKind { Info, Success, Warning, Error }

/// <summary>
/// Single status banner surface shared by the whole shell. Modules write here; the shell's status bar binds.
/// </summary>
public sealed partial class StatusService : ObservableObject
{
    [ObservableProperty] private string _message = "Ready";
    [ObservableProperty] private StatusKind _kind = StatusKind.Info;

    public void Set(string message, StatusKind kind = StatusKind.Info)
    {
        Message = message;
        Kind = kind;
    }
}
