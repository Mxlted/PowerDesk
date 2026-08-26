using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PowerDesk.Modules.FileLockFinder.Models;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace PowerDesk.Modules.FileLockFinder.Services;

public sealed class RestartManagerService
{
    private const int ErrorMoreData = 234;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int CchRmSessionKey = 32; // sizeof(GUID) * 2 hex chars, plus the terminator below
    private const int MaxGetListAttempts = 5;

    public FileLockScanResult FindLockingProcesses(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new FileLockScanResult();
        var fullPath = Path.GetFullPath(path.Trim());
        var isFolder = Directory.Exists(fullPath);
        if (!isFolder && !File.Exists(fullPath))
            throw new FileNotFoundException("Path does not exist.", fullPath);

        var resources = CollectResources(fullPath, isFolder, out var limited);
        if (resources.Count == 0)
            return new FileLockScanResult { ResourceLimitReached = limited, IsFolder = isFolder };

        // RmStartSession writes CCH_RM_SESSION_KEY characters plus a null terminator.
        var key = new StringBuilder(CchRmSessionKey + 1);
        var result = RmStartSession(out var handle, 0, key);
        if (result != 0) throw new Win32Exception(result, "RmStartSession failed.");

        try
        {
            result = RmRegisterResources(handle, (uint)resources.Count, resources.ToArray(), 0, null, 0, null);
            if (result != 0) throw new Win32Exception(result, "RmRegisterResources failed.");

            var processInfo = GetProcessList(handle);
            var list = new List<LockingProcessInfo>(processInfo.Length);
            var currentPid = Environment.ProcessId;
            foreach (var rm in processInfo)
                list.Add(BuildProcessInfo(rm, currentPid));

            return new FileLockScanResult
            {
                Processes = FileLockLogic.Dedupe(list),
                ResourceCount = resources.Count,
                ResourceLimitReached = limited,
                IsFolder = isFolder,
            };
        }
        finally
        {
            RmEndSession(handle);
        }
    }

    /// <summary>
    /// Standard two-call pattern: ask for the size, then fetch. Because processes can start
    /// between the two calls, ERROR_MORE_DATA on the second call means "grow and retry".
    /// </summary>
    private static RM_PROCESS_INFO[] GetProcessList(uint handle)
    {
        uint reasons = 0;
        uint count = 0;
        var result = RmGetList(handle, out var needed, ref count, null, ref reasons);
        if (result == 0) return [];
        if (result != ErrorMoreData) throw new Win32Exception(result, "RmGetList failed.");

        for (var attempt = 0; attempt < MaxGetListAttempts; attempt++)
        {
            if (needed == 0) return [];
            count = needed;
            var buffer = new RM_PROCESS_INFO[(int)count];
            result = RmGetList(handle, out needed, ref count, buffer, ref reasons);
            if (result == 0)
            {
                if (count == buffer.Length) return buffer;
                var trimmed = new RM_PROCESS_INFO[(int)count];
                Array.Copy(buffer, trimmed, (int)count);
                return trimmed;
            }
            if (result != ErrorMoreData) throw new Win32Exception(result, "RmGetList failed.");
        }
        throw new Win32Exception(ErrorMoreData, "The locking process list kept changing; try scanning again.");
    }

    private static List<string> CollectResources(string fullPath, bool isFolder, out bool limited)
    {
        var resources = new List<string>(isFolder ? 64 : 1);
        limited = false;
        if (!isFolder)
        {
            resources.Add(fullPath);
            return resources;
        }

        foreach (var file in EnumerateFilesSafe(fullPath))
        {
            if (resources.Count >= FileLockLogic.MaxRegisteredResources)
            {
                limited = true;
                break;
            }
            resources.Add(file);
        }
        return resources;
    }

    /// <summary>
    /// Breadth-first file enumeration that skips inaccessible entries and never follows reparse
    /// points (junctions/symlinks), which would otherwise loop forever on cyclic links.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        // Files: include everything (hidden/system/OneDrive placeholders are all legitimate lock targets).
        var fileOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.None,
            ReturnSpecialDirectories = false,
        };
        // Directories: never descend into junctions/symlinks, which can form cycles.
        var dirOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var dir = pending.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", fileOptions).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }

            foreach (var file in files) yield return file;

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir, "*", dirOptions).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { subdirs = []; }
            foreach (var subdir in subdirs) pending.Enqueue(subdir);
        }
    }

    private static LockingProcessInfo BuildProcessInfo(RM_PROCESS_INFO rm, int currentPid)
    {
        var pid = rm.Process.dwProcessId;
        var appName = rm.strAppName ?? string.Empty;
        var serviceName = rm.strServiceShortName ?? string.Empty;
        string processName = string.Empty;
        string mainWindowTitle = string.Empty;
        string path = string.Empty;
        DateTime? startTime = null;
        var restartManagerStartUtc = FileLockLogic.FileTimeToUtc(rm.Process.ProcessStartTime.dwHighDateTime, rm.Process.ProcessStartTime.dwLowDateTime);

        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
            try { mainWindowTitle = process.MainWindowTitle ?? string.Empty; } catch { }
            try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
            try { startTime = process.StartTime; } catch { }
        }
        catch
        {
            // Process already exited or access denied: fall back to what Restart Manager told us.
            processName = appName;
        }

        var risk = FileLockLogic.Classify(pid, string.IsNullOrWhiteSpace(processName) ? appName : processName,
            rm.ApplicationType == RM_APP_TYPE.RmCritical, currentPid);

        return new LockingProcessInfo
        {
            ProcessId = pid,
            ProcessName = processName,
            AppName = appName,
            ServiceName = rm.ApplicationType == RM_APP_TYPE.RmService ? serviceName : string.Empty,
            MainWindowTitle = mainWindowTitle,
            ProcessPath = path,
            Restartable = rm.bRestartable,
            SessionId = rm.TSSessionId,
            StartTime = startTime ?? restartManagerStartUtc?.ToLocalTime(),
            StartTimeUtc = restartManagerStartUtc ?? startTime?.ToUniversalTime(),
            IsCriticalSystemProcess = risk == ProcessRisk.Critical,
            IsCurrentProcess = risk == ProcessRisk.Self,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public FILETIME ProcessStartTime;
    }

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);
}
