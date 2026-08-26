using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Services;
using PowerDesk.Modules.HashDesk.Models;
using PowerDesk.Modules.HashDesk.Services;
using Clipboard = System.Windows.Clipboard;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace PowerDesk.Modules.HashDesk.ViewModels;

public sealed partial class HashDeskViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private CancellationTokenSource? _cts;
    private HashTriple _textHashes;

    public ObservableCollection<HashResult> Results { get; } = new();

    /// <summary>Comparison of <see cref="ExpectedHash"/> against the selected file result.</summary>
    public HashMatchIndicator FileMatch { get; } = new();

    /// <summary>Comparison of <see cref="ExpectedHash"/> against the text hashes.</summary>
    public HashMatchIndicator TextMatch { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySha256Command))]
    private HashResult? _selectedResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearResultsCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isBusy;

    /// <summary>0..100 for the file currently being hashed.</summary>
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _currentFileLabel = string.Empty;
    [ObservableProperty] private string _expectedHash = string.Empty;
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _textSha256 = string.Empty;
    [ObservableProperty] private string _textSha1 = string.Empty;
    [ObservableProperty] private string _textMd5 = string.Empty;

    public int ResultCount => Results.Count;
    public bool HasResults => Results.Count > 0;
    public bool ShowEmptyState => !IsBusy && Results.Count == 0;

    public HashDeskViewModel(ILogger log, StatusService status, RecentActionsService recent)
    {
        _log = log;
        _status = status;
        _recent = recent;
    }

    private bool IsIdle => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SelectFilesAsync()
    {
        string[] files;
        try
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "All files|*.*",
                Title = "Select files to hash",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            files = dlg.FileNames;
        }
        catch (Exception ex)
        {
            _log.Error("Select files", ex);
            _status.Set("Could not open the file picker.", StatusKind.Warning);
            return;
        }
        await AddFilesAsync(files);
    }

    /// <summary>
    /// Hashes each file once (SHA256/SHA1/MD5 in a single pass). Safe to call from the drop handler;
    /// never throws. Folders contribute their top-level files; checksum sidecars (.sha256, .md5, ...)
    /// are loaded into the expected-hash box instead of being hashed. A second call while busy is
    /// refused so the busy state cannot get out of sync.
    /// </summary>
    public async Task AddFilesAsync(IEnumerable<string>? paths)
    {
        HashDeskLogic.DropExpansion expansion;
        try
        {
            expansion = HashDeskLogic.ExpandDropPaths(
                paths, File.Exists, Directory.Exists,
                dir => Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            _log.Error("Hash files (enumerate)", ex);
            expansion = new HashDeskLogic.DropExpansion([], [], 0, false, 0);
        }

        var notes = new List<string>();
        if (expansion.ChecksumFiles.Count > 0)
            notes.Add(LoadChecksumFile(expansion.ChecksumFiles[0], expansion.Files));
        if (expansion.Truncated)
            notes.Add($"Only the first {HashDeskLogic.MaxFolderFiles} files of a folder are taken.");
        if (expansion.FoldersExpanded > 0)
            notes.Add("Subfolders are not included.");
        if (expansion.Ignored > 0)
            notes.Add($"{expansion.Ignored} dropped item(s) do not exist on disk.");

        var files = expansion.Files;
        if (files.Count == 0)
        {
            var reason = expansion.ChecksumFiles.Count > 0
                ? string.Join(" ", notes)
                : expansion.FoldersExpanded > 0
                    ? "The dropped folder(s) contain no files. " + string.Join(" ", notes)
                    : "No files were selected.";
            _status.Set(reason.Trim(), expansion.ChecksumFiles.Count > 0 ? StatusKind.Info : StatusKind.Warning);
            return;
        }
        if (IsBusy)
        {
            _status.Set("Hashing is already in progress. Wait for it to finish or cancel it first.", StatusKind.Warning);
            return;
        }
        if (notes.Count > 0)
            _status.Set(string.Join(" ", notes), StatusKind.Info);

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        var completed = 0;
        var failed = 0;
        var cancelled = false;
        try
        {
            // Progress<T> captures the UI SynchronizationContext here, so reports land on the UI thread.
            var progress = new Progress<double>(fraction => Progress = Math.Clamp(fraction, 0, 1) * 100);

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (cts.IsCancellationRequested) { cancelled = true; break; }

                CurrentFileLabel = $"{Path.GetFileName(file)} ({i + 1} of {files.Count})";
                Progress = 0;

                HashResult result;
                try
                {
                    result = await Task.Run(() => ComputeFile(file, cts.Token, progress), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    _log.Error($"Hash file: {file}", ex);
                    _status.Set($"Could not read {Path.GetFileName(file)}: {ex.Message}", StatusKind.Warning);
                    continue;
                }

                UiDispatcher.Invoke(() =>
                {
                    RemoveExistingResult(result.FilePath);
                    Results.Insert(0, result);
                    SelectedResult = result;
                    NotifyResultsChanged();
                });
                completed++;
            }

            if (cancelled)
            {
                _status.Set(completed == 0
                    ? "Hashing cancelled."
                    : $"Hashing cancelled after {completed} file(s).", StatusKind.Warning);
            }
            else if (completed > 0)
            {
                _recent.Add("HashDesk", $"Hashed {completed} file(s).");
                _status.Set(failed == 0
                    ? $"Hashed {completed} file(s)."
                    : $"Hashed {completed} file(s); {failed} could not be read.", failed == 0 ? StatusKind.Success : StatusKind.Warning);
            }
            else
            {
                _status.Set("No files could be hashed. See logs.", StatusKind.Error);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Hash files", ex);
            _status.Set("Hashing failed. See logs.", StatusKind.Error);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            Progress = 0;
            CurrentFileLabel = string.Empty;
        }
    }

    /// <summary>
    /// Loads the first digest from a checksum sidecar into <see cref="ExpectedHash"/>. When the sidecar
    /// names a file that sits next to it and that file is not already queued, it is queued too, so
    /// dropping "setup.exe.sha256" alone verifies setup.exe in one go. Returns a status note.
    /// </summary>
    private string LoadChecksumFile(string checksumPath, List<string> queue)
    {
        try
        {
            var info = new FileInfo(checksumPath);
            if (info.Length > 64 * 1024) return $"{info.Name} is too large to be a checksum file; ignored.";
            var entry = HashDeskLogic.ParseChecksumFile(File.ReadAllText(checksumPath));
            if (entry is null) return $"No SHA256/SHA1/MD5 digest found in {info.Name}.";

            ExpectedHash = entry.Digest;
            var note = $"Expected hash loaded from {info.Name}.";
            if (entry.FileName is null) return note;

            var sibling = Path.Combine(info.DirectoryName ?? string.Empty, Path.GetFileName(entry.FileName));
            if (File.Exists(sibling) && !queue.Contains(sibling, StringComparer.OrdinalIgnoreCase))
            {
                queue.Insert(0, sibling);
                note += $" Also hashing {Path.GetFileName(sibling)} named in it.";
            }
            return note;
        }
        catch (Exception ex)
        {
            _log.Error($"Read checksum file: {checksumPath}", ex);
            return $"Could not read {Path.GetFileName(checksumPath)}.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _status.Set("Cancelling hash...", StatusKind.Info);
    }

    private static HashResult ComputeFile(string path, CancellationToken cancellationToken, IProgress<double> progress)
    {
        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            HashDeskLogic.BufferSize, FileOptions.SequentialScan);
        var hashes = HashDeskLogic.ComputeStream(stream, info.Length, cancellationToken, progress);
        return new HashResult
        {
            FilePath = path,
            SizeBytes = info.Length,
            Sha256 = hashes.Sha256,
            Sha1 = hashes.Sha1,
            Md5 = hashes.Md5,
            CompletedAt = DateTime.Now,
        };
    }

    [RelayCommand]
    private void ComputeTextHash()
    {
        try
        {
            _textHashes = HashDeskLogic.ComputeText(InputText);
            TextSha256 = _textHashes.Sha256;
            TextSha1 = _textHashes.Sha1;
            TextMd5 = _textHashes.Md5;
            UpdateTextMatch();
            _status.Set("Text hashes updated (UTF-8, no BOM).", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Hash text", ex);
            _status.Set("Could not hash the text.", StatusKind.Error);
        }
    }

    private bool HasSelectedResult => SelectedResult is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedResult))]
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

    /// <summary>Copies any digest passed as the command parameter (SHA256/SHA1/MD5 buttons).</summary>
    [RelayCommand]
    private void CopyHash(string? text) => CopyText(text ?? string.Empty, "Hash copied.");

    [RelayCommand]
    private void CopyTextSha256() => CopyText(TextSha256, "Text SHA256 copied.");

    private bool CanClear => !IsBusy && Results.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearResults()
    {
        Results.Clear();
        SelectedResult = null;
        NotifyResultsChanged();
        _status.Set("Hash results cleared.", StatusKind.Info);
    }

    partial void OnSelectedResultChanged(HashResult? value) => UpdateFileMatch();

    partial void OnExpectedHashChanged(string value)
    {
        UpdateFileMatch();
        UpdateTextMatch();
    }

    private void UpdateFileMatch()
    {
        var selected = SelectedResult;
        if (selected is null)
        {
            FileMatch.Clear();
            return;
        }
        FileMatch.Apply(HashDeskLogic.Compare(ExpectedHash, new HashTriple(selected.Sha256, selected.Sha1, selected.Md5)));
    }

    private void UpdateTextMatch()
    {
        if (string.IsNullOrEmpty(TextSha256))
        {
            TextMatch.Clear();
            return;
        }
        TextMatch.Apply(HashDeskLogic.Compare(ExpectedHash, _textHashes));
    }

    private void RemoveExistingResult(string path)
    {
        for (var i = Results.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Results[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                Results.RemoveAt(i);
        }
    }

    private void NotifyResultsChanged()
    {
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
        ClearResultsCommand.NotifyCanExecuteChanged();
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
}
