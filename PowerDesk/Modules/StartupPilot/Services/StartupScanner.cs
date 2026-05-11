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

    // ---------- Registry ----------

    private static readonly (RegistryHive Hive, string Path, string Scope)[] RegistryKeys =
    {
        (RegistryHive.CurrentUser,  @"Software\Microsoft\Windows\CurrentVersion\Run",     "HKCU\\Run"),
        (RegistryHive.CurrentUser,  @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU\\RunOnce"),
        (RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run",     "HKLM\\Run"),
        (RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM\\RunOnce"),
        (RegistryHive.LocalMachine, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run",     "HKLM\\Run (Wow64)"),
        (RegistryHive.LocalMachine, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM\\RunOnce (Wow64)"),
    };

    private IEnumerable<StartupItem> ScanRegistry(bool includeMicrosoft)
    {
        foreach (var (hive, path, scope) in RegistryKeys)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive,
                hive == RegistryHive.LocalMachine ? RegistryView.Default : RegistryView.Default);
            using var key = baseKey.OpenSubKey(path, writable: false);
            if (key is null) continue;

            foreach (var rawName in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(rawName)) continue;
                var cmd = key.GetValue(rawName)?.ToString() ?? string.Empty;
                bool enabled = !rawName.StartsWith("!", StringComparison.Ordinal);
                var name = enabled ? rawName : rawName.TrimStart('!');

                if (!includeMicrosoft && LooksLikeMicrosoft(cmd, name)) continue;

                yield return new StartupItem
                {
                    Source = StartupSource.Registry,
                    Scope = scope,
                    Name = name,
                    CommandLine = cmd,
                    TargetPath = ExtractExecutablePath(cmd),
                    Enabled = enabled,
                    Locator = $"{hive}|{path}|{rawName}",
                    RequiresAdmin = hive == RegistryHive.LocalMachine,
                };
            }
        }
    }

    // ---------- Startup folders ----------

    private IEnumerable<StartupItem> ScanStartupFolders(bool includeMicrosoft)
    {
        var perUser = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var allUsers = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

        foreach (var (folder, scope, admin) in new[] { (perUser, "Startup (User)", false), (allUsers, "Startup (All Users)", true) })
        {
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
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) { _log.Warn($"Startup folder enum: {folder}: {ex.Message}"); yield break; }

        foreach (var file in files)
        {
            string target = ResolveShortcut(file);
            string name = Path.GetFileNameWithoutExtension(file);
            if (!includeMicrosoft && LooksLikeMicrosoft(target, name)) continue;
            yield return new StartupItem
            {
                Source = StartupSource.StartupFolder,
                Scope = scope,
                Name = name,
                CommandLine = target,
                TargetPath = ExtractExecutablePath(target),
                Enabled = !isDisabled,
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
            IEnumerable<Microsoft.Win32.TaskScheduler.Task> all;
            try { all = ts.AllTasks; }
            catch (Exception ex) { _log.Warn($"TaskService enum: {ex.Message}"); yield break; }
            foreach (var t in all)
            {
                StartupItem? item = null;
                try
                {
                    // Only login or boot triggers count as "startup".
                    var triggers = t.Definition.Triggers;
                    bool relevant = false;
                    foreach (var tr in triggers)
                    {
                        if (tr.TriggerType == TaskTriggerType.Logon || tr.TriggerType == TaskTriggerType.Boot)
                        { relevant = true; break; }
                    }
                    if (!relevant) { continue; }

                    var path = t.Path;
                    if (!includeMicrosoft && (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
                                              || path.Equals(@"\MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var action = t.Definition.Actions.Count > 0 ? t.Definition.Actions[0] : null;
                    var actionStr = action?.ToString() ?? string.Empty;
                    string exe = string.Empty;
                    if (action is ExecAction exec)
                    {
                        exe = exec.Path ?? string.Empty;
                        actionStr = string.IsNullOrEmpty(exec.Arguments) ? exe : $"\"{exe}\" {exec.Arguments}";
                    }

                    item = new StartupItem
                    {
                        Source = StartupSource.TaskScheduler,
                        Scope = "Scheduled Task",
                        Name = t.Name,
                        Description = t.Definition.RegistrationInfo.Description ?? string.Empty,
                        Publisher = t.Definition.RegistrationInfo.Author ?? string.Empty,
                        CommandLine = actionStr,
                        TargetPath = ExtractExecutablePath(exe.Length > 0 ? exe : actionStr),
                        Enabled = t.Enabled,
                        Locator = t.Path,
                        RequiresAdmin = true, // disabling/enabling a task that's not yours generally needs elevation
                    };
                }
                catch (Exception ex) { _log.Warn($"Task scan '{t.Path}': {ex.Message}"); }
                if (item is not null) yield return item;
            }
        }
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
                if (key is null) { sc.Dispose(); continue; }
                int start = (int)(key.GetValue("Start") ?? 4);
                // 2 = Automatic, 3 = Manual, 4 = Disabled. We track items that *would* run at startup.
                if (start != 2 && start != 4) { sc.Dispose(); continue; }

                string path = (key.GetValue("ImagePath") as string) ?? string.Empty;
                path = Environment.ExpandEnvironmentVariables(path).Trim('"');
                string desc = (key.GetValue("Description") as string) ?? string.Empty;
                if (desc.StartsWith("@")) desc = string.Empty; // mui ref, skip
                string display = sc.DisplayName ?? sc.ServiceName;

                if (!includeMicrosoft && LooksLikeMicrosoft(path, display)) { sc.Dispose(); continue; }

                item = new StartupItem
                {
                    Source = StartupSource.Service,
                    Scope = "Service",
                    Name = display,
                    Description = desc,
                    CommandLine = path,
                    TargetPath = ExtractExecutablePath(path),
                    Enabled = start == 2,
                    Locator = sc.ServiceName,
                    RequiresAdmin = true,
                };
            }
            catch (Exception ex) { _log.Warn($"Service '{sc.ServiceName}': {ex.Message}"); }
            sc.Dispose();
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
                long size = fi.Length;
                item.Impact = size switch
                {
                    > 50L * 1024 * 1024 => StartupImpact.High,
                    > 10L * 1024 * 1024 => StartupImpact.Medium,
                    _                    => StartupImpact.Low,
                };
                item.Icon = _icons.GetIcon(path);
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    var pub = !string.IsNullOrWhiteSpace(info.CompanyName) ? info.CompanyName : string.Empty;
                    var desc = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : string.Empty;
                    if (string.IsNullOrEmpty(item.Publisher)) typeof(StartupItem).GetProperty(nameof(StartupItem.Publisher))?.SetValue(item, pub);
                    if (string.IsNullOrEmpty(item.Description)) typeof(StartupItem).GetProperty(nameof(StartupItem.Description))?.SetValue(item, desc);
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

    private static bool LooksLikeMicrosoft(string commandOrPath, string name)
    {
        var s = (commandOrPath ?? string.Empty).ToLowerInvariant();
        if (s.Contains(@"\windows\system32") || s.Contains(@"\windows\syswow64") || s.Contains(@"\windowsapps\microsoft")
            || s.Contains(@"\microsoft\edge") || s.Contains(@"\microsoft\onedrive"))
            return true;
        var lname = (name ?? string.Empty).ToLowerInvariant();
        return lname.StartsWith("microsoft ") || lname.StartsWith("windows ");
    }

    public static string ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        var s = command.Trim();
        if (s.StartsWith("\""))
        {
            int end = s.IndexOf('"', 1);
            if (end > 1) return s.Substring(1, end - 1);
        }
        int sp = s.IndexOf(' ');
        var head = sp > 0 ? s[..sp] : s;
        head = Environment.ExpandEnvironmentVariables(head);
        return head;
    }
}
