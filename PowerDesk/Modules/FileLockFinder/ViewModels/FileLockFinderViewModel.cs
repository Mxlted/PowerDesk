using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Modules.FileLockFinder.Models;
using PowerDesk.Modules.FileLockFinder.Services;
using Clipboard = System.Windows.Clipboard;
using DialogResult = System.Windows.Forms.DialogResult;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace PowerDesk.Modules.FileLockFinder.ViewModels;

public sealed partial class FileLockFinderViewModel : ObservableObject
{
    private readonly ILogger _log;
    private readonly StatusService _status;
    private readonly RecentActionsService _recent;
    private readonly PermissionService _permissions;
    private readonly IConfirmationService _confirm;
    private readonly RestartManagerService _restartManager = new();

    public ObservableCollection<LockingProcessInfo> Processes { get; } = new();

    [ObservableProperty] private string _targetPath = string.Empty;
    [ObservableProperty] private LockingProcessInfo? _selectedProcess;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private DateTime? _lastScan;
    [ObservableProperty] private string _scanScopeLabel = string.Empty;

    public bool IsAdmin => _permissions.IsAdministrator;
    public int ProcessCount => Processes.Count;

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

    public void SetTargetPathFromDrop(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        TargetPath = path;
        _status.Set(Directory.Exists(path) ? "Folder path loaded." : "File path loaded.", StatusKind.Info);
    }

    [RelayCommand]
    private void SelectFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "All files|*.*",
            Title = "Select a locked file",
        };
        if (dlg.ShowDialog() == true) TargetPath = dlg.FileName;
    }

    [RelayCommand]
    private void SelectFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select a folder to inspect",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() == DialogResult.OK) TargetPath = dlg.SelectedPath;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            _status.Set("Choose a file or folder first.", StatusKind.Warning);
            return;
        }

        IsScanning = true;
        try
        {
            var path = TargetPath.Trim();
            var result = await Task.Run(() => _restartManager.FindLockingProcesses(path));
            var list = result.Processes;
            UiDispatcher.Invoke(() =>
            {
                Processes.Clear();
                foreach (var item in list.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
                    Processes.Add(item);
                SelectedProcess = Processes.FirstOrDefault();
                LastScan = DateTime.Now;
                ScanScopeLabel = result.ResourceLimitReached
                    ? $"Scanned first {result.ResourceCount} resources."
                    : $"Scanned {result.ResourceCount} resource(s).";
                OnPropertyChanged(nameof(ProcessCount));
            });
            _recent.Add("FileLockFinder", $"Scanned {Path.GetFileName(path)}.");
            var suffix = result.ResourceLimitReached ? " Folder scan was capped for responsiveness." : string.Empty;
            _status.Set(list.Count == 0 ? $"No locking processes found.{suffix}" : $"Found {list.Count} locking process(es).{suffix}", StatusKind.Success);
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

    [RelayCommand]
    private void CopyProcessId()
    {
        if (SelectedProcess is null)
        {
            _status.Set("Select a process first.", StatusKind.Warning);
            return;
        }
        try
        {
            Clipboard.SetText(SelectedProcess.ProcessId.ToString());
            _status.Set("Process ID copied.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Copy process id", ex);
            _status.Set("Could not copy process ID.", StatusKind.Warning);
        }
    }

    [RelayCommand]
    private void OpenProcessLocation()
    {
        if (SelectedProcess is null || string.IsNullOrWhiteSpace(SelectedProcess.ProcessPath) || !File.Exists(SelectedProcess.ProcessPath))
        {
            _status.Set("Process location is unavailable.", StatusKind.Warning);
            return;
        }
        try
        {
            Process.Start("explorer.exe", $"/select,\"{SelectedProcess.ProcessPath}\"");
        }
        catch (Exception ex)
        {
            _log.Error("Open process location", ex);
            _status.Set("Could not open process location.", StatusKind.Warning);
        }
    }

    [RelayCommand]
    private async Task StopProcessAsync()
    {
        if (SelectedProcess is null)
        {
            _status.Set("Select a process first.", StatusKind.Warning);
            return;
        }
        if (!_confirm.Confirm($"Stop process {SelectedProcess.DisplayName} ({SelectedProcess.ProcessId})?", "Stop locking process", destructive: true))
            return;

        try
        {
            var lockedProcess = await ResolveCurrentLockOwnerAsync(SelectedProcess);
            if (lockedProcess is null)
            {
                _status.Set("That process is no longer locking the selected path.", StatusKind.Warning);
                await ScanAsync();
                return;
            }

            using var process = Process.GetProcessById(lockedProcess.ProcessId);
            if (!MatchesStartTime(process, lockedProcess))
            {
                _status.Set("The process ID was reused before it could be stopped.", StatusKind.Warning);
                await ScanAsync();
                return;
            }
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync();
            _recent.Add("FileLockFinder", $"Stopped process {lockedProcess.ProcessId}.");
            _status.Set("Process stopped.", StatusKind.Success);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Stop locking process", ex);
            _status.Set(IsAdmin ? "Could not stop the process." : "Could not stop the process. Administrator may be required.", StatusKind.Error);
        }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (IsAdmin)
        {
            _status.Set("Already running as administrator.", StatusKind.Info);
            return;
        }
        if (!_permissions.TryRelaunchAsAdmin()) _status.Set("Elevation cancelled.", StatusKind.Warning);
        else App.Instance.Shell?.ForceClose();
    }

    private async Task<LockingProcessInfo?> ResolveCurrentLockOwnerAsync(LockingProcessInfo selected)
    {
        if (string.IsNullOrWhiteSpace(TargetPath)) return null;
        var result = await Task.Run(() => _restartManager.FindLockingProcesses(TargetPath.Trim()));
        return result.Processes.FirstOrDefault(p =>
            p.ProcessId == selected.ProcessId &&
            SameStartTime(p.StartTimeUtc, selected.StartTimeUtc));
    }

    private static bool MatchesStartTime(Process process, LockingProcessInfo expected)
    {
        if (expected.StartTimeUtc is null) return true;
        try
        {
            return SameStartTime(process.StartTime.ToUniversalTime(), expected.StartTimeUtc);
        }
        catch
        {
            return false;
        }
    }

    private static bool SameStartTime(DateTime? left, DateTime? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return Math.Abs((left.Value.ToUniversalTime() - right.Value.ToUniversalTime()).TotalSeconds) < 2;
    }
}
