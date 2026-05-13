using System;
using System.IO;

namespace PowerDesk.Modules.HashDesk.Models;

public sealed class HashResult
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public long SizeBytes { get; init; }
    public string SizeLabel => FormatBytes(SizeBytes);
    public string Sha256 { get; init; } = string.Empty;
    public string Sha1 { get; init; } = string.Empty;
    public string Md5 { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; } = DateTime.Now;
    public string CompletedLabel => CompletedAt.ToString("HH:mm:ss");

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{bytes} B" : $"{value:0.##} {units[index]}";
    }
}
