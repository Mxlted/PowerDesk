using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerDesk.Modules.PathEditor.Models;

public enum PathScope
{
    User,
    Machine,
}

public sealed partial class PathEntry : ObservableObject
{
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _isMissing;
    [ObservableProperty] private bool _isDuplicate;
    [ObservableProperty] private bool _isValidated;

    public string MissingLabel => IsValidated ? (IsMissing ? "Missing" : "OK") : "Unchecked";
    public string DuplicateLabel => IsDuplicate ? "Duplicate" : "";

    partial void OnIsMissingChanged(bool value) => OnPropertyChanged(nameof(MissingLabel));
    partial void OnIsValidatedChanged(bool value) => OnPropertyChanged(nameof(MissingLabel));
    partial void OnIsDuplicateChanged(bool value) => OnPropertyChanged(nameof(DuplicateLabel));
}

public sealed class PathBackup
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public PathScope Scope { get; set; }
    public string Value { get; set; } = string.Empty;
    public string TimestampLabel => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class PathEditorSettings
{
    public List<PathBackup> Backups { get; set; } = new();
}
