using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Modules.FileLockFinder.Models;
using PowerDesk.Modules.FileLockFinder.Services;
using DialogResult = System.Windows.Forms.DialogResult;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace PowerDesk.Modules.FileLockFinder.ViewModels;

public sealed partial class FileLockFinderViewModel : ObservableObject
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _log;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly PermissionService _permissions;
    private readonly IConfirmationService _confirm;
    private readonly RestartManagerService _restartManager = new();

    public ObservableCollection<LockingProcessInfo> Processes { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyProcessIdCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProcessLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopProcessCommand))]
    private LockingProcessInfo? _selectedProcess;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopProcessCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isScanning;

    [ObservableProperty] private DateTime? _lastScan;
    [ObservableProperty] private string _scanScopeLabel = string.Empty;
    [ObservableProperty] private string _emptyStateText = "Choose a file or folder, then press Scan.";

    public bool IsAdmin => _permissions.IsAdministrator;
    public int ProcessCount => Processes.Count;
    public bool HasProcesses => Processes.Count > 0;
    public bool ShowEmptyState => !IsScanning && Processes.Count == 0;

    public FileLockFinderViewModel(
        ILogger log,
        StatusService status,
        RecentActionsService recent,
        PermissionService permissions,
        IConfirmationService confirm)
    {
        _log = log;
        _status = status;
        _recent = recent;
        _permissions = permissions;
        _confirm = confirm;
    }

    public void SetTargetPathFromDrop(string path) => SetTargetPathsFromDrop([path]);

    /// <summary>
    /// Accepts a full drag-drop payload. Only one target can be scanned at a time, so the first
    /// existing path wins and the user is told how many other items were ignored.
    /// </summary>
    public void SetTargetPathsFromDrop(IEnumerable<string>? paths)
    {
        var (target, ignored) = FileLockLogic.PickDropTarget(paths, p => File.Exists(p) || Directory.Exists(p));
        if (target is null)
        {
            _status.Set("Dropped items are not files or folders on disk.", StatusKind.Warning);
            return;
        }

        TargetPath = target;
        var kind = Directory.Exists(target) ? "Folder" : "File";
        _status.Set(ignored > 0
            ? $"{kind} path loaded. {ignored} other dropped item(s) ignored: one target at a time."
            : $"{kind} path loaded.", StatusKind.Info);

        if (ScanCommand.CanExecute(null)) ScanCommand.Execute(null);
    }

    [RelayCommand]
    private void SelectFile()
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Filter = "All files|*.*",
                Title = "Select a locked file",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() == true) TargetPath = dlg.FileName;
        }
        catch (Exception ex)
        {
            _log.Error("Select file", ex);
            _status.Set("Could not open the file picker.", StatusKind.Warning);
        }
    }

    [RelayCommand]
    private void SelectFolder()
    {
        try
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select a folder to inspect",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() == DialogResult.OK) TargetPath = dlg.SelectedPath;
        }
        catch (Exception ex)
        {
            _log.Error("Select folder", ex);
            _status.Set("Could not open the folder picker.", StatusKind.Warning);
        }
    }

    private bool CanScan => !IsScanning && !string.IsNullOrWhiteSpace(TargetPath);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => RunScanAsync();

    private async Task RunScanAsync()
    {
        var path = TargetPath?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            _status.Set("Choose a file or folder first.", StatusKind.Warning);
            return;
        }

        IsScanning = true;
        try
        {
            var result = await Task.Run(() => _restartManager.FindLockingProcesses(path));
            var list = FileLockLogic.Sort(result.Processes);
            UiDispatcher.Invoke(() =>
            {
                var previousPid = SelectedProcess?.ProcessId;
                Processes.Clear();
                foreach (var item in list) Processes.Add(item);
                SelectedProcess = list.Find(p => p.ProcessId == previousPid) ?? list.FirstOrDefault();
                LastScan = DateTime.Now;
                ScanScopeLabel = FileLockLogic.BuildScopeLabel(result.ResourceCount, result.ResourceLimitReached, result.IsFolder);
                EmptyStateText = "No processes are locking this path.";
                NotifyProcessesChanged();
            });

            _recent.Add("FileLockFinder", $"Scanned {Path.GetFileName(path)}.");
            var suffix = result.ResourceLimitReached
                ? $" Only the first {result.ResourceCount} files were checked."
                : string.Empty;
            _status.Set(list.Count == 0
                ? $"No locking processes found.{suffix}"
                : $"Found {list.Count} locking process(es).{suffix}", StatusKind.Success);
        }
        catch (FileNotFoundException)
        {
            _status.Set("That path does not exist.", StatusKind.Warning);
        }
        catch (Win32Exception ex)
        {
            _log.Error("File lock scan (Restart Manager)", ex);
            _status.Set($"Restart Manager error: {ex.Message}", StatusKind.Error);
        }
        catch (Exception ex)
        {
            _log.Error("File lock scan", ex);
            _status.Set("Lock scan failed. See logs.", StatusKind.Error);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool HasSelection => SelectedProcess is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyProcessId()
    {
        if (SelectedProcess is null)
        {
            _status.Set("Select a process first.", StatusKind.Warning);
            return;
        }
        if (ClipboardService.TrySetText(SelectedProcess.ProcessId.ToString()))
            _status.Set("Process ID copied.", StatusKind.Success);
        else
            _status.Set("Could not copy process ID (clipboard is busy). Try again.", StatusKind.Warning);
    }

    private bool CanOpenLocation => !string.IsNullOrWhiteSpace(SelectedProcess?.ProcessPath);

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenProcessLocation()
    {
        var path = SelectedProcess?.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _status.Set("Process location is unavailable.", StatusKind.Warning);
            return;
        }
        try
        {
            using var explorer = Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            _log.Error("Open process location", ex);
            _status.Set("Could not open process location.", StatusKind.Warning);
        }
    }

    private bool CanStop => !IsScanning && SelectedProcess is { CanStop: true };

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopProcessAsync()
    {
        var selected = SelectedProcess;
        if (selected is null)
        {
            _status.Set("Select a process first.", StatusKind.Warning);
            return;
        }
        if (selected.IsCurrentProcess)
        {
            _status.Set("PowerDesk itself is holding this lock. Close the file inside PowerDesk instead.", StatusKind.Warning);
            return;
        }

        var risk = selected.IsCriticalSystemProcess ? ProcessRisk.Critical : ProcessRisk.None;
        var title = risk == ProcessRisk.Critical ? "Stop a Windows system process?" : "Stop locking process";
        if (!_confirm.Confirm(FileLockLogic.BuildStopConfirmation(selected, risk), title, destructive: true))
            return;

        IsScanning = true;
        try
        {
            var owner = await ResolveCurrentLockOwnerAsync(selected);
            if (owner is null)
            {
                _status.Set("That process is no longer locking the selected path.", StatusKind.Warning);
                await RunScanAsync();
                return;
            }

            using var process = Process.GetProcessById(owner.ProcessId);
            if (!MatchesStartTime(process, owner))
            {
                _status.Set("The process ID was reused by another process, so nothing was stopped.", StatusKind.Warning);
                await RunScanAsync();
                return;
            }

            process.Kill(entireProcessTree: false);
            using var cts = new CancellationTokenSource(StopTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _status.Set("Stop was requested but the process has not exited yet. Rescan in a moment.", StatusKind.Warning);
                return;
            }

            _recent.Add("FileLockFinder", $"Stopped {owner.DisplayName} (PID {owner.ProcessId}).");
            _status.Set($"Stopped {owner.DisplayName}.", StatusKind.Success);
            await RunScanAsync();
        }
        catch (ArgumentException)
        {
            // Process.GetProcessById: the process exited between the scan and the stop.
            _status.Set("The process already exited.", StatusKind.Info);
            await RunScanAsync();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            _log.Error("Stop locking process (access denied)", ex);
            _status.Set(IsAdmin
                ? "Access denied. That process is protected by Windows and cannot be stopped."
                : "Access denied. Relaunch PowerDesk as administrator to stop this process.", StatusKind.Error);
        }
        catch (Exception ex)
        {
            _log.Error("Stop locking process", ex);
            _status.Set(IsAdmin ? "Could not stop the process." : "Could not stop the process. Administrator may be required.", StatusKind.Error);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void RelaunchAsAdmin() => _permissions.RequestElevation(_status);

    private void NotifyProcessesChanged()
    {
        OnPropertyChanged(nameof(ProcessCount));
        OnPropertyChanged(nameof(HasProcesses));
        OnPropertyChanged(nameof(ShowEmptyState));
        StopProcessCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rescans so we only stop a process that is still holding the lock right now (guards PID reuse).</summary>
    private async Task<LockingProcessInfo?> ResolveCurrentLockOwnerAsync(LockingProcessInfo selected)
    {
        var path = TargetPath?.Trim();
        if (string.IsNullOrWhiteSpace(path)) return null;
        var result = await Task.Run(() => _restartManager.FindLockingProcesses(path));
        return result.Processes.FirstOrDefault(p =>
            p.ProcessId == selected.ProcessId &&
            FileLockLogic.SameStartTime(p.StartTimeUtc, selected.StartTimeUtc));
    }

    private static bool MatchesStartTime(Process process, LockingProcessInfo expected)
    {
        if (expected.StartTimeUtc is null) return true;
        try
        {
            return FileLockLogic.SameStartTime(process.StartTime.ToUniversalTime(), expected.StartTimeUtc);
        }
        catch
        {
            return false;
        }
    }
}
