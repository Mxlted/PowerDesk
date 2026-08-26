using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Services;
using PowerDesk.Modules.StartupPilot.Models;
using Task = System.Threading.Tasks.Task;
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace PowerDesk.Modules.StartupPilot.Services;

/// <summary>
/// Enumerates every Windows-recognised autorun location and returns a unified list of <see cref="StartupItem"/>.
/// Each enumeration is defensive: a failure in one source never poisons the others.
/// </summary>
public sealed class StartupScanner
{
    private readonly ILogger _log;
    private readonly IconService _icons;

    public StartupScanner(ILogger log, IconService icons)
    {
        _log = log;
        _icons = icons;
    }

    public Task<List<StartupItem>> ScanAsync(bool includeMicrosoft) =>
        Task.Run(() => Scan(includeMicrosoft));

    private List<StartupItem> Scan(bool includeMicrosoft)
    {
        var items = new List<StartupItem>(128);

        SafeAdd(items, () => ScanRegistry(includeMicrosoft));
        SafeAdd(items, () => ScanStartupFolders(includeMicrosoft));
        SafeAdd(items, () => ScanTaskScheduler(includeMicrosoft));
        SafeAdd(items, () => ScanServices(includeMicrosoft));

        // Decorate with publisher/description/impact/orphan once everything is collected.
        foreach (var i in items) Decorate(i);
        return items;
    }

    private void SafeAdd(List<StartupItem> items, Func<IEnumerable<StartupItem>> source)
    {
        try { items.AddRange(source()); }
        catch (Exception ex) { _log.Error("Startup scan source failed", ex); }
    }

    private static RegistryKey OpenBase(RegistryHive hive) =>
        // HKLM: force Registry64 so we read the real 64-bit keys; the Wow6432Node paths are listed
        // explicitly so WoW redirection must not fold them in. HKCU\Software is never redirected.
        RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);

    // ---------- Registry ----------

    private static readonly (RegistryHive Hive, string Path, string Scope)[] RegistryKeys =
    {
        (RegistryHive.CurrentUser,  StartupPilotLogic.RunKey,       "HKCU\\Run"),
        (RegistryHive.CurrentUser,  StartupPilotLogic.RunOnceKey,   "HKCU\\RunOnce"),
        (RegistryHive.LocalMachine, StartupPilotLogic.RunKey,       "HKLM\\Run"),
        (RegistryHive.LocalMachine, StartupPilotLogic.RunOnceKey,   "HKLM\\RunOnce"),
        (RegistryHive.LocalMachine, StartupPilotLogic.Run32Key,     "HKLM\\Run (Wow64)"),
        (RegistryHive.LocalMachine, StartupPilotLogic.RunOnce32Key, "HKLM\\RunOnce (Wow64)"),
    };

    private IEnumerable<StartupItem> ScanRegistry(bool includeMicrosoft)
    {
        foreach (var (hive, path, scope) in RegistryKeys)
        {
            foreach (var item in ScanRegistryKey(hive, path, scope, includeMicrosoft, parkedAsDisabled: false))
                yield return item;
            // Values parked under <key>\AutorunsDisabled (Autoruns convention, also used by our controller
            // for keys without StartupApproved support) are disabled entries.
            foreach (var item in ScanRegistryKey(hive, StartupPilotLogic.AutorunsDisabledKeyFor(path), scope, includeMicrosoft, parkedAsDisabled: true))
                yield return item;
        }
    }

    private IEnumerable<StartupItem> ScanRegistryKey(RegistryHive hive, string path, string scope, bool includeMicrosoft, bool parkedAsDisabled)
    {
        using var baseKey = OpenBase(hive);
        using var key = baseKey.OpenSubKey(path, writable: false);
        if (key is null) yield break;

        var approvedSubkey = parkedAsDisabled ? null : StartupPilotLogic.StartupApprovedSubkeyForRegistryPath(path);
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue; // the (Default) value is never executed
            string cmd;
            try { cmd = key.GetValue(name)?.ToString() ?? string.Empty; }
            catch (Exception ex) { _log.Warn($"Registry value '{path}\\{name}': {ex.Message}"); continue; }

            // Windows runs every value in a Run key regardless of its name; the only state it honours is
            // StartupApproved (Task Manager). A leading '!' in RunOnce means "wait for completion", not disabled.
            bool enabled = !parkedAsDisabled;
            if (approvedSubkey is not null)
            {
                var approved = ReadStartupApprovedState(hive, approvedSubkey, name);
                if (approved.HasValue) enabled = approved.Value;
            }

            if (!includeMicrosoft && StartupPilotLogic.LooksLikeMicrosoft(cmd, name)) continue;

            yield return new StartupItem
            {
                Source = StartupSource.Registry,
                Scope = scope,
                Name = name,
                CommandLine = cmd,
                TargetPath = ExtractExecutablePath(cmd),
                Enabled = enabled,
                Locator = StartupPilotLogic.FormatRegistryLocator(hive, path, name),
                RequiresAdmin = hive == RegistryHive.LocalMachine,
            };
        }
    }

    // ---------- Startup folders ----------

    private IEnumerable<StartupItem> ScanStartupFolders(bool includeMicrosoft)
    {
        var perUser = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var allUsers = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

        foreach (var (folder, scope, admin) in new[] { (perUser, "Startup (User)", false), (allUsers, "Startup (All Users)", true) })
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            foreach (var item in ScanStartupFolder(folder, scope, admin, includeMicrosoft, isDisabled: false))
                yield return item;
            var disabledDir = Path.Combine(folder, "Disabled");
            if (Directory.Exists(disabledDir))
                foreach (var item in ScanStartupFolder(disabledDir, scope, admin, includeMicrosoft, isDisabled: true))
                    yield return item;
        }
    }

    private IEnumerable<StartupItem> ScanStartupFolder(string folder, string scope, bool admin, bool includeMicrosoft, bool isDisabled)
    {
        if (!Directory.Exists(folder)) yield break;
        List<string> files;
        try { files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception ex) { _log.Warn($"Startup folder enum: {folder}: {ex.Message}"); yield break; }

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            // Explorer launches every file in the folder, not just shortcuts (.exe, .bat, .url, ... all run).
            bool isShortcut = fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            string command = isShortcut ? ResolveShortcut(file) : $"\"{file}\"";
            string name = Path.GetFileNameWithoutExtension(file);
            if (!includeMicrosoft && StartupPilotLogic.LooksLikeMicrosoft(command, name)) continue;

            bool enabled = !isDisabled;
            if (!isDisabled)
            {
                // Task Manager keys StartupApproved\StartupFolder by the file name including its extension.
                var approvedHive = admin ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                var approved = ReadStartupApprovedState(approvedHive, "StartupFolder", fileName);
                if (approved.HasValue) enabled = approved.Value;
            }
            yield return new StartupItem
            {
                Source = StartupSource.StartupFolder,
                Scope = scope,
                Name = name,
                CommandLine = command,
                TargetPath = ExtractExecutablePath(command),
                Enabled = enabled,
                Locator = file,
                RequiresAdmin = admin,
            };
        }
    }

    private string ResolveShortcut(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return string.Empty;
            dynamic? shell = Activator.CreateInstance(t);
            if (shell is null) return string.Empty;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath ?? string.Empty;
            string args = shortcut.Arguments ?? string.Empty;
            return string.IsNullOrEmpty(args) ? target : $"\"{target}\" {args}";
        }
        catch (Exception ex)
        {
            _log.Warn($"Resolve shortcut '{lnkPath}': {ex.Message}");
            return string.Empty;
        }
    }

    // ---------- Task Scheduler ----------

    private IEnumerable<StartupItem> ScanTaskScheduler(bool includeMicrosoft)
    {
        TaskService? ts = null;
        try { ts = new TaskService(); }
        catch (Exception ex) { _log.Warn($"TaskService init: {ex.Message}"); yield break; }
        using (ts)
        {
            IEnumerator<ScheduledTask> e;
            try { e = ts.AllTasks.GetEnumerator(); }
            catch (Exception ex) { _log.Warn($"TaskService enum: {ex.Message}"); yield break; }

            using (e)
            {
                while (true)
                {
                    ScheduledTask t;
                    // AllTasks walks folders lazily; a folder the current user can't read throws from MoveNext,
                    // which must not discard the tasks already collected.
                    try
                    {
                        if (!e.MoveNext()) break;
                        t = e.Current;
                    }
                    catch (Exception ex) { _log.Warn($"TaskService enum: {ex.Message}"); break; }

                    StartupItem? item = null;
                    try { item = BuildTaskItem(t, includeMicrosoft); }
                    catch (Exception ex) { _log.Warn($"Task scan '{SafeTaskPath(t)}': {ex.Message}"); }
                    finally { try { t.Dispose(); } catch { } }
                    if (item is not null) yield return item;
                }
            }
        }
    }

    private static string SafeTaskPath(ScheduledTask t)
    {
        try { return t.Path; } catch { return "?"; }
    }

    private static StartupItem? BuildTaskItem(ScheduledTask t, bool includeMicrosoft)
    {
        // Only login or boot triggers count as "startup".
        bool relevant = false;
        foreach (var tr in t.Definition.Triggers)
        {
            if (tr.TriggerType == TaskTriggerType.Logon || tr.TriggerType == TaskTriggerType.Boot)
            { relevant = true; break; }
        }
        if (!relevant) return null;

        var path = t.Path;
        if (!includeMicrosoft && (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
                                  || path.Equals(@"\MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase)))
            return null;

        var action = t.Definition.Actions.Count > 0 ? t.Definition.Actions[0] : null;
        var actionStr = action?.ToString() ?? string.Empty;
        string exe = string.Empty;
        if (action is ExecAction exec)
        {
            exe = exec.Path ?? string.Empty;
            actionStr = string.IsNullOrEmpty(exec.Arguments) ? exe : $"\"{exe}\" {exec.Arguments}";
        }

        return new StartupItem
        {
            Source = StartupSource.TaskScheduler,
            Scope = "Scheduled Task",
            Name = t.Name,
            Description = t.Definition.RegistrationInfo.Description ?? string.Empty,
            Publisher = t.Definition.RegistrationInfo.Author ?? string.Empty,
            CommandLine = actionStr,
            TargetPath = ExtractExecutablePath(exe.Length > 0 ? $"\"{exe}\"" : actionStr),
            Enabled = t.Enabled,
            Locator = path,
            RequiresAdmin = true, // disabling/enabling a task that's not yours generally needs elevation
        };
    }

    // ---------- Services ----------

    private IEnumerable<StartupItem> ScanServices(bool includeMicrosoft)
    {
        ServiceController[] services;
        try { services = ServiceController.GetServices(); }
        catch (Exception ex) { _log.Warn($"Services enum: {ex.Message}"); yield break; }

        foreach (var sc in services)
        {
            StartupItem? item = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{sc.ServiceName}", writable: false);
                if (key is null) continue;
                int start = key.GetValue("Start") is int s ? s : 4;
                var startupType = StartupPilotLogic.ServiceStartupTypeFromStartValue(start);
                if (startupType == ServiceStartupType.Unknown) continue;

                string path = (key.GetValue("ImagePath") as string) ?? string.Empty;
                path = Environment.ExpandEnvironmentVariables(path);
                string desc = (key.GetValue("Description") as string) ?? string.Empty;
                if (desc.StartsWith('@')) desc = string.Empty; // MUI resource reference, not readable text
                string display = string.IsNullOrWhiteSpace(sc.DisplayName) ? sc.ServiceName : sc.DisplayName;

                if (!includeMicrosoft && StartupPilotLogic.LooksLikeMicrosoft(path, display)) continue;

                item = new StartupItem
                {
                    Source = StartupSource.Service,
                    Scope = "Service",
                    Name = display,
                    Description = desc,
                    CommandLine = path,
                    TargetPath = ExtractExecutablePath(path),
                    Enabled = startupType == ServiceStartupType.Automatic,
                    ServiceStartupType = startupType,
                    Locator = sc.ServiceName,
                    RequiresAdmin = true,
                };
            }
            catch (Exception ex) { _log.Warn($"Service '{sc.ServiceName}': {ex.Message}"); }
            finally { sc.Dispose(); }
            if (item is not null) yield return item;
        }
    }

    // ---------- Decoration ----------

    private void Decorate(StartupItem item)
    {
        try
        {
            var path = item.TargetPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var fi = new FileInfo(path);
                item.Impact = StartupPilotLogic.ImpactFromFileSize(fi.Length);
                item.Icon = _icons.GetIcon(path);
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (string.IsNullOrEmpty(item.Publisher) && !string.IsNullOrWhiteSpace(info.CompanyName))
                        item.Publisher = info.CompanyName!;
                    if (string.IsNullOrEmpty(item.Description) && !string.IsNullOrWhiteSpace(info.FileDescription))
                        item.Description = info.FileDescription!;
                }
                catch { }
                item.IsOrphaned = false;
            }
            else
            {
                item.Impact = StartupImpact.Unknown;
                item.IsOrphaned = !string.IsNullOrWhiteSpace(item.TargetPath) || !string.IsNullOrWhiteSpace(item.CommandLine);
            }
        }
        catch (Exception ex) { _log.Warn($"Decorate '{item.Name}': {ex.Message}"); }
    }

    // ---------- helpers ----------

    private static bool? ReadStartupApprovedState(RegistryHive hive, string? approvedSubkey, string valueName)
    {
        if (string.IsNullOrWhiteSpace(approvedSubkey) || string.IsNullOrWhiteSpace(valueName)) return null;
        try
        {
            using var baseKey = OpenBase(hive);
            using var key = baseKey.OpenSubKey($@"{StartupPilotLogic.StartupApprovedRoot}\{approvedSubkey}", writable: false);
            return StartupPilotLogic.ParseStartupApprovedBlob(key?.GetValue(valueName) as byte[]);
        }
        catch
        {
            return null;
        }
    }

    private static readonly Lazy<IReadOnlyList<string>> SearchDirs = new(() =>
    {
        var dirs = new List<string>();
        void Add(string? d) { if (!string.IsNullOrWhiteSpace(d) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d); }
        try
        {
            Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
                Add(p.Trim().Trim('"'));
        }
        catch { }
        return dirs;
    });

    public static string ExtractExecutablePath(string command) =>
        StartupPilotLogic.ExtractExecutablePath(command, File.Exists, SearchDirs.Value);
}
