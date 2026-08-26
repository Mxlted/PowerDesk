using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using PowerDesk.Core.Logging;
using PowerDesk.Modules.StartupPilot.Models;

namespace PowerDesk.Modules.StartupPilot.Services;

public sealed class StartupActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool NeedsElevation { get; init; }
    public string? UpdatedLocator { get; init; }
}

/// <summary>
/// Applies enable/disable to startup items using the same conventions Task Manager uses where possible:
/// Run keys and startup-folder entries via Explorer's StartupApproved state, RunOnce values parked in an
/// AutorunsDisabled sub-key (Autoruns convention), tasks toggled through the Task Scheduler API and
/// services flipped between Automatic, Manual and Disabled via sc.exe.
/// </summary>
public sealed class StartupController
{
    private readonly ILogger _log;

    public StartupController(ILogger log) => _log = log;

    private static StartupActionResult Fail(string message) => new() { Success = false, Message = message };
    private static StartupActionResult Elevate(string message) => new() { Success = false, NeedsElevation = true, Message = message };
    private static StartupActionResult Ok(string message, string? locator = null) => new() { Success = true, Message = message, UpdatedLocator = locator };

    public StartupActionResult Toggle(StartupItem item, bool enable)
    {
        if (item.Enabled == enable)
            return Ok("No change.");
        try
        {
            return item.Source switch
            {
                StartupSource.Registry      => ToggleRegistry(item, enable),
                StartupSource.StartupFolder => ToggleStartupFolder(item, enable),
                StartupSource.TaskScheduler => ToggleTask(item, enable),
                StartupSource.Service       => SetServiceStartupType(item, enable ? ServiceStartupType.Automatic : ServiceStartupType.Disabled),
                _ => Fail("Unknown source."),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Elevate("Administrator privileges required.");
        }
        catch (System.Security.SecurityException)
        {
            return Elevate("Administrator privileges required.");
        }
        catch (Exception ex)
        {
            _log.Error($"Toggle '{item.Name}'", ex);
            return Fail(ex.Message);
        }
    }

    private static RegistryKey OpenBase(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);

    private StartupActionResult ToggleRegistry(StartupItem item, bool enable)
    {
        if (!StartupPilotLogic.TryParseRegistryLocator(item.Locator, out var hive, out var keyPath, out var valueName))
            return Fail("Malformed registry locator.");

        if (hive == RegistryHive.LocalMachine && !IsAdmin())
            return Elevate("Editing HKLM requires administrator.");

        using var baseKey = OpenBase(hive);

        // 1. Value parked under <key>\AutorunsDisabled: enabling moves it back to the live key.
        if (StartupPilotLogic.IsAutorunsDisabledKey(keyPath))
        {
            if (!enable) return Ok("Already disabled.", item.Locator);
            var livePath = StartupPilotLogic.ParentOfAutorunsDisabledKey(keyPath);
            var moveError = MoveRegistryValue(baseKey, keyPath, livePath, valueName);
            if (moveError is not null) return moveError;
            var liveApproved = StartupPilotLogic.StartupApprovedSubkeyForRegistryPath(livePath);
            if (liveApproved is not null) WriteStartupApprovedState(hive, liveApproved, valueName, enable: true);
            return Ok("Enabled.", StartupPilotLogic.FormatRegistryLocator(hive, livePath, valueName));
        }

        // 2. Run keys: Explorer honours StartupApproved, exactly like Task Manager. The value itself stays put.
        var approvedSubkey = StartupPilotLogic.StartupApprovedSubkeyForRegistryPath(keyPath);
        if (approvedSubkey is not null)
        {
            using var key = baseKey.OpenSubKey(keyPath, writable: false);
            if (key is null) return Fail("Registry key not found.");
            if (!HasValue(key, valueName)) return Fail("Registry value not found.");
            WriteStartupApprovedState(hive, approvedSubkey, valueName, enable);
            return Ok(enable ? "Enabled." : "Disabled.", item.Locator);
        }

        // 3. RunOnce keys have no StartupApproved support and Windows runs every value they contain
        //    regardless of name, so the only way to disable one is to move it out of the key.
        if (enable) return Ok("Already enabled.", item.Locator);
        var parkedPath = StartupPilotLogic.AutorunsDisabledKeyFor(keyPath);
        var error = MoveRegistryValue(baseKey, keyPath, parkedPath, valueName);
        return error ?? Ok("Disabled.", StartupPilotLogic.FormatRegistryLocator(hive, parkedPath, valueName));
    }

    private static bool HasValue(RegistryKey key, string valueName) =>
        Array.Exists(key.GetValueNames(), n => string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Moves a value between two keys of the same hive without expanding it or changing its kind. Returns an error result or null.</summary>
    private static StartupActionResult? MoveRegistryValue(RegistryKey baseKey, string fromPath, string toPath, string valueName)
    {
        using var from = baseKey.OpenSubKey(fromPath, writable: true);
        if (from is null) return Fail("Registry key not found.");
        if (!HasValue(from, valueName)) return Fail("Registry value not found.");

        var data = from.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (data is null) return Fail("Registry value not found.");
        var kind = from.GetValueKind(valueName);

        using var to = baseKey.CreateSubKey(toPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open {toPath}.");
        if (HasValue(to, valueName))
            return Fail($"A value named '{valueName}' already exists under {toPath}; refusing to overwrite it.");

        to.SetValue(valueName, data, kind);
        from.DeleteValue(valueName, throwOnMissingValue: false);
        return null;
    }

    private StartupActionResult ToggleStartupFolder(StartupItem item, bool enable)
    {
        var file = item.Locator;
        if (string.IsNullOrEmpty(file)) return Fail("Empty shortcut path.");
        var dir = Path.GetDirectoryName(file) ?? string.Empty;
        if (string.IsNullOrEmpty(dir)) return Fail("No directory for shortcut.");
        if (!File.Exists(file)) return Fail("Startup file not found.");

        bool isDisabled = string.Equals(Path.GetFileName(dir), "Disabled", StringComparison.OrdinalIgnoreCase);
        var fileName = Path.GetFileName(file);

        if (enable && isDisabled)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrEmpty(parent) || !TryGetStartupFolderApprovedHive(parent, out var hive))
                return Fail("Disabled entry isn't inside a known startup folder; refusing to move.");
            if (hive == RegistryHive.LocalMachine && !IsAdmin())
                return Elevate("Editing the all-users startup folder requires administrator.");
            var dest = Path.Combine(parent, fileName);
            if (File.Exists(dest))
                return Fail("A file with that name already exists in the startup folder; refusing to overwrite it.");
            File.Move(file, dest);
            WriteStartupApprovedState(hive, "StartupFolder", fileName, enable: true);
            return Ok("Enabled.", dest);
        }
        if (!isDisabled)
        {
            if (!TryGetStartupFolderApprovedHive(dir, out var hive))
                return Fail("Entry isn't inside a known startup folder; refusing to change it.");
            if (hive == RegistryHive.LocalMachine && !IsAdmin())
                return Elevate("Editing all-users startup approval requires administrator.");
            WriteStartupApprovedState(hive, "StartupFolder", fileName, enable);
            return Ok(enable ? "Enabled." : "Disabled.", file);
        }
        return Ok("Already in desired state.", file);
    }

    /// <summary>
    /// Adds a program to the Startup folder so Explorer launches it at sign-in. Shortcuts (.lnk/.url)
    /// are copied in as-is; anything else gets a new .lnk created for it. The entry is also marked
    /// enabled in StartupApproved so a stale "disabled" record for the same file name cannot mute it.
    /// Never overwrites an existing entry. Returns the created file path as the locator.
    /// </summary>
    public StartupActionResult AddStartupFolderEntry(string targetPath, bool allUsers)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
                return Fail("The file does not exist.");
            if (allUsers && !IsAdmin())
                return Elevate("Adding to the all-users Startup folder requires administrator.");

            var folder = Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(folder))
                return Fail("Windows did not report a Startup folder location.");
            Directory.CreateDirectory(folder);

            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? string.Empty;
            if (string.Equals(Path.TrimEndingDirectorySeparator(sourceDir), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase))
                return Fail("That file is already in the Startup folder.");

            var fileName = StartupPilotLogic.StartupEntryFileName(targetPath);
            var dest = Path.Combine(folder, fileName);
            if (File.Exists(dest) || File.Exists(Path.Combine(folder, "Disabled", fileName)))
                return Fail($"'{fileName}' already exists in the Startup folder; refusing to overwrite it.");

            if (StartupPilotLogic.IsShortcutLike(targetPath))
                File.Copy(targetPath, dest, overwrite: false);
            else
                CreateShortcut(dest, targetPath);

            var hive = allUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            try { WriteStartupApprovedState(hive, "StartupFolder", fileName, enable: true); }
            catch (Exception ex) { _log.Warn($"StartupApproved for '{fileName}': {ex.Message}"); }

            var scope = allUsers ? "all-users" : "your";
            return Ok($"Added '{Path.GetFileNameWithoutExtension(fileName)}' to {scope} Startup folder.", dest);
        }
        catch (UnauthorizedAccessException)
        {
            return Elevate("Administrator privileges required.");
        }
        catch (Exception ex)
        {
            _log.Error($"Add startup entry '{targetPath}'", ex);
            return Fail(ex.Message);
        }
    }

    /// <summary>Creates a .lnk via the Windows Script Host shell object (the same COM API the scanner uses to read them).</summary>
    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is not available to create shortcuts.");
        dynamic shell = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Windows Script Host could not be started.");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        shortcut.Description = "Added by PowerDesk StartupPilot";
        shortcut.Save();
        if (!File.Exists(shortcutPath))
            throw new IOException("The shortcut was not created.");
    }

    private static bool TryGetStartupFolderApprovedHive(string path, out RegistryHive hive)
    {
        var perUser  = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var allUsers = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        var normalized = Path.TrimEndingDirectorySeparator(path);
        if (perUser.Length > 0 && string.Equals(normalized, Path.TrimEndingDirectorySeparator(perUser), StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.CurrentUser;
            return true;
        }
        if (allUsers.Length > 0 && string.Equals(normalized, Path.TrimEndingDirectorySeparator(allUsers), StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.LocalMachine;
            return true;
        }
        hive = RegistryHive.CurrentUser;
        return false;
    }

    private StartupActionResult ToggleTask(StartupItem item, bool enable)
    {
        using var ts = new TaskService();
        using var task = ts.GetTask(item.Locator);
        if (task is null) return Fail("Task not found.");
        try
        {
            task.Enabled = enable;
            return Ok(enable ? "Enabled." : "Disabled.");
        }
        catch (UnauthorizedAccessException)
        {
            return Elevate("Toggling this task requires administrator.");
        }
        catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070005)
        {
            return Elevate("Toggling this task requires administrator.");
        }
    }

    public StartupActionResult SetServiceStartupType(StartupItem item, ServiceStartupType startupType)
    {
        if (item.Source != StartupSource.Service)
            return Fail("Item is not a service.");
        if (!IsAdmin())
            return Elevate("Editing service start type requires administrator.");

        var scStartType = startupType switch
        {
            ServiceStartupType.Automatic => "auto",
            ServiceStartupType.Manual    => "demand",
            ServiceStartupType.Disabled  => "disabled",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(scStartType))
            return Fail("Unknown service startup type.");

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add(item.Locator);
        psi.ArgumentList.Add("start=");
        psi.ArgumentList.Add(scStartType);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return Fail("Could not invoke sc.exe.");

            // Read both streams asynchronously *before* waiting, so a child process that fills
            // either pipe buffer can't deadlock us. The reads complete once the pipes close.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            bool exited = proc.WaitForExit(5000);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return Fail("sc.exe timed out.");
            }

            string outp = string.Empty, err = string.Empty;
            try { outp = stdoutTask.GetAwaiter().GetResult(); } catch { }
            try { err  = stderrTask.GetAwaiter().GetResult(); } catch { }

            if (proc.ExitCode != 0)
            {
                var msg = string.Concat(err, outp).Trim();
                if (msg.Length == 0) msg = $"sc.exe exit code {proc.ExitCode}.";
                return Fail("sc.exe failed: " + msg);
            }
            return Ok($"Service set to {StartupTypeLabel(startupType)}.");
        }
        catch (Exception ex)
        {
            _log.Error("sc.exe", ex);
            return Fail(ex.Message);
        }
    }

    private static string StartupTypeLabel(ServiceStartupType startupType) => startupType switch
    {
        ServiceStartupType.Automatic => "Automatic",
        ServiceStartupType.Manual    => "Manual",
        ServiceStartupType.Disabled  => "Disabled",
        _ => "Unknown",
    };

    private static void WriteStartupApprovedState(RegistryHive hive, string approvedSubkey, string valueName, bool enable)
    {
        using var baseKey = OpenBase(hive);
        using var key = baseKey.CreateSubKey($@"{StartupPilotLogic.StartupApprovedRoot}\{approvedSubkey}", writable: true)
            ?? throw new InvalidOperationException("Could not open StartupApproved key.");
        key.SetValue(valueName, StartupPilotLogic.BuildStartupApprovedBlob(enable), RegistryValueKind.Binary);
    }

    private static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
