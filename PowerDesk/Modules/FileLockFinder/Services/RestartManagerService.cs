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
    private const int MaxRegisteredResources = 512;

    public FileLockScanResult FindLockingProcesses(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new FileLockScanResult();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException("Path does not exist.", fullPath);

        var resources = CollectResources(fullPath, out var limited);
        if (resources.Count == 0)
            return new FileLockScanResult { ResourceLimitReached = limited };

        var key = new StringBuilder(32);
        var result = RmStartSession(out var handle, 0, key);
        if (result != 0) throw new Win32Exception(result);

        try
        {
            result = RmRegisterResources(handle, (uint)resources.Count, resources.ToArray(), 0, null, 0, null);
            if (result != 0) throw new Win32Exception(result);

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;
            result = RmGetList(handle, out needed, ref count, null, ref reasons);
            if (result == 0)
                return new FileLockScanResult { ResourceCount = resources.Count, ResourceLimitReached = limited };
            if (result != ErrorMoreData) throw new Win32Exception(result);

            count = needed;
            var processInfo = new RM_PROCESS_INFO[(int)count];
            result = RmGetList(handle, out needed, ref count, processInfo, ref reasons);
            if (result != 0) throw new Win32Exception(result);

            var list = new List<LockingProcessInfo>();
            for (var i = 0; i < (int)count; i++)
            {
                var rm = processInfo[i];
                var pid = rm.Process.dwProcessId;
                var info = BuildProcessInfo(pid, rm);
                list.Add(info);
            }
            return new FileLockScanResult
            {
                Processes = list,
                ResourceCount = resources.Count,
                ResourceLimitReached = limited,
            };
        }
        finally
        {
            RmEndSession(handle);
        }
    }

    private static List<string> CollectResources(string fullPath, out bool limited)
    {
        var resources = new List<string>(Math.Min(MaxRegisteredResources, 64));
        limited = false;
        if (File.Exists(fullPath))
        {
            resources.Add(fullPath);
            return resources;
        }

        foreach (var file in EnumerateFilesSafe(fullPath))
        {
            if (resources.Count >= MaxRegisteredResources)
            {
                limited = true;
                break;
            }
            resources.Add(file);
        }
        return resources;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = []; }

            foreach (var file in files) yield return file;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { subdirs = []; }
            foreach (var subdir in subdirs) pending.Push(subdir);
        }
    }

    private static LockingProcessInfo BuildProcessInfo(int pid, RM_PROCESS_INFO rm)
    {
        string processName = string.Empty;
        string mainWindowTitle = string.Empty;
        string path = string.Empty;
        DateTime? startTime = null;
        var restartManagerStartUtc = FileTimeToUtc(rm.Process.ProcessStartTime);

        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
            mainWindowTitle = process.MainWindowTitle ?? string.Empty;
            try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
            try { startTime = process.StartTime; } catch { }
        }
        catch
        {
            processName = rm.strAppName;
        }

        return new LockingProcessInfo
        {
            ProcessId = pid,
            ProcessName = processName,
            AppName = rm.strAppName,
            MainWindowTitle = mainWindowTitle,
            ProcessPath = path,
            Restartable = rm.bRestartable,
            SessionId = rm.TSSessionId,
            StartTime = startTime ?? restartManagerStartUtc?.ToLocalTime(),
            StartTimeUtc = restartManagerStartUtc,
        };
    }

    private static DateTime? FileTimeToUtc(FILETIME fileTime)
    {
        try
        {
            var high = ((long)(uint)fileTime.dwHighDateTime) << 32;
            var low = (uint)fileTime.dwLowDateTime;
            var ticks = high + low;
            return ticks <= 0 ? null : DateTime.FromFileTimeUtc(ticks);
        }
        catch
        {
            return null;
        }
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

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);
}
