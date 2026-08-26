using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PowerDesk.Modules.HashDesk.Services;

namespace PowerDesk.Modules.HashDesk.Models;

public sealed class HashResult
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public long SizeBytes { get; init; }
    public string SizeLabel => HashDeskLogic.FormatBytes(SizeBytes);
    public string Sha256 { get; init; } = string.Empty;
    public string Sha1 { get; init; } = string.Empty;
    public string Md5 { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; } = DateTime.Now;
    public string CompletedLabel => CompletedAt.ToString("HH:mm:ss");
}

/// <summary>Bindable outcome of comparing an expected digest with computed hashes.</summary>
public sealed partial class HashMatchIndicator : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private bool _isMatch;
    [ObservableProperty] private bool _isMismatch;
    [ObservableProperty] private bool _isInvalid;

    internal void Apply(HashMatch match)
    {
        Label = HashDeskLogic.DescribeMatch(match);
        IsMatch = match.State == HashMatchState.Match;
        IsMismatch = match.State == HashMatchState.Mismatch;
        IsInvalid = match.State == HashMatchState.InvalidExpected;
    }

    internal void Clear()
    {
        Label = string.Empty;
        IsMatch = false;
        IsMismatch = false;
        IsInvalid = false;
    }
}
