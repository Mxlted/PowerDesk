using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Services;
using PowerDesk.Modules.HashDesk.Models;
using Clipboard = System.Windows.Clipboard;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace PowerDesk.Modules.HashDesk.ViewModels;

public sealed partial class HashDeskViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;

    public ObservableCollection<HashResult> Results { get; } = new();

    [ObservableProperty] private HashResult? _selectedResult;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _textSha256 = string.Empty;
    [ObservableProperty] private string _textSha1 = string.Empty;
    [ObservableProperty] private string _textMd5 = string.Empty;

    public int ResultCount => Results.Count;

    public HashDeskViewModel(ILogger log, StatusService status, RecentActionsService recent)
    {
        _log = log;
        _status = status;
        _recent = recent;
    }

    [RelayCommand]
    private async Task SelectFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "All files|*.*",
            Title = "Select files to hash",
        };
        if (dlg.ShowDialog() == true)
            await AddFilesAsync(dlg.FileNames);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            _status.Set("No files were selected.", StatusKind.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            foreach (var file in files)
            {
                var result = await Task.Run(() => ComputeFile(file));
                UiDispatcher.Invoke(() =>
                {
                    Results.Insert(0, result);
                    SelectedResult = result;
                    OnPropertyChanged(nameof(ResultCount));
                });
            }
            _recent.Add("HashDesk", $"Hashed {files.Count} file(s).");
            _status.Set($"HashDesk completed {files.Count} file(s).", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Hash files", ex);
            _status.Set("Hashing failed. See logs.", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static HashResult ComputeFile(string path)
    {
        using var sha256 = SHA256.Create();
        using var sha1 = SHA1.Create();
        using var md5 = MD5.Create();
        var buffer = new byte[1024 * 128];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.SequentialScan);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha256.TransformBlock(buffer, 0, read, null, 0);
            sha1.TransformBlock(buffer, 0, read, null, 0);
            md5.TransformBlock(buffer, 0, read, null, 0);
        }
        sha256.TransformFinalBlock([], 0, 0);
        sha1.TransformFinalBlock([], 0, 0);
        md5.TransformFinalBlock([], 0, 0);

        return new HashResult
        {
            FilePath = path,
            SizeBytes = new FileInfo(path).Length,
            Sha256 = ToHex(sha256.Hash),
            Sha1 = ToHex(sha1.Hash),
            Md5 = ToHex(md5.Hash),
        };
    }

    [RelayCommand]
    private void ComputeTextHash()
    {
        var bytes = Encoding.UTF8.GetBytes(InputText ?? string.Empty);
        TextSha256 = ToHex(SHA256.HashData(bytes));
        TextSha1 = ToHex(SHA1.HashData(bytes));
        TextMd5 = ToHex(MD5.HashData(bytes));
        _status.Set("Text hashes updated.", StatusKind.Success);
    }

    [RelayCommand]
    private void CopySha256(HashResult? result)
    {
        result ??= SelectedResult;
        if (result is null)
        {
            _status.Set("Select a hash result first.", StatusKind.Warning);
            return;
        }
        CopyText(result.Sha256, "SHA256 copied.");
    }

    [RelayCommand]
    private void CopyTextSha256() => CopyText(TextSha256, "Text SHA256 copied.");

    [RelayCommand]
    private void ClearResults()
    {
        Results.Clear();
        SelectedResult = null;
        OnPropertyChanged(nameof(ResultCount));
        _status.Set("Hash results cleared.", StatusKind.Info);
    }

    private void CopyText(string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Set("Nothing to copy.", StatusKind.Warning);
            return;
        }
        try
        {
            Clipboard.SetText(text);
            _status.Set(message, StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Copy hash", ex);
            _status.Set("Could not copy to clipboard.", StatusKind.Warning);
        }
    }

    private static string ToHex(byte[]? bytes) => bytes is null ? string.Empty : Convert.ToHexString(bytes).ToLowerInvariant();
}
