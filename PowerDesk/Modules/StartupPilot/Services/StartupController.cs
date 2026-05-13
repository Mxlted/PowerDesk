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
/// registry value renamed with leading '!', shortcuts moved to a Disabled subfolder, tasks toggled,
/// services flipped between Automatic, Manual, and Disabled via sc.exe.
/// </summary>
public sealed class StartupController
{
    private const string StartupApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private readonly ILogger _log;

    public StartupController(ILogger log) => _log = log;

    public StartupActionResult Toggle(StartupItem item, bool enable)
    {
        if (item.Enabled == enable)
            return new StartupActionResult { Success = true, Message = "No change." };
        try
        {
            return item.Source switch
            {
                StartupSource.Registry      => ToggleRegistry(item, enable),
                StartupSource.StartupFolder => ToggleStartupFolder(item, enable),
                StartupSource.TaskScheduler => ToggleTask(item, enable),
                StartupSource.Service       => SetServiceStartupType(item, enable ? ServiceStartupType.Automatic : ServiceStartupType.Disabled),
                _ => new StartupActionResult { Success = false, Message = "Unknown source." },
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Administrator privileges required." };
        }
        catch (System.Security.SecurityException)
        {
            return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Administrator privileges required." };
        }
        catch (Exception ex)
        {
            _log.Error($"Toggle '{item.Name}'", ex);
            return new StartupActionResult { Success = false, Message = ex.Message };
        }
    }

    private StartupActionResult ToggleRegistry(StartupItem item, bool enable)
    {
        // Locator format: "{Hive}|{path}|{rawValueName}"
        var parts = item.Locator.Split('|', 3);
        if (parts.Length != 3) return new StartupActionResult { Success = false, Message = "Malformed registry locator." };
        if (!Enum.TryParse<RegistryHive>(parts[0], out var hive))
            return new StartupActionResult { Success = false, Message = "Unknown hive." };
        var keyPath = parts[1];
        var rawName = parts[2];

        if (hive == RegistryHive.LocalMachine && !IsAdmin())
            return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Editing HKLM requires administrator." };

        using var baseKey = RegistryKey.OpenBaseKey(hive,
            hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
        using var key = baseKey.OpenSubKey(keyPath, writable: true);
        if (key is null) return new StartupActionResult { Success = false, Message = "Registry key not found." };

        var value = key.GetValue(rawName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return new StartupActionResult { Success = false, Message = "Registry value not found." };
        var valueKind = key.GetValueKind(rawName);
        var approvedSubkey = StartupApprovedSubkeyForRegistryPath(keyPath);
        if (approvedSubkey is not null)
        {
            var approvedName = rawName.TrimStart('!');
            var updatedRawName = rawName;
            if (enable && rawName.StartsWith("!", StringComparison.Ordinal))
            {
                if (Array.Exists(key.GetValueNames(), name => string.Equals(name, approvedName, StringComparison.Ordinal)))
                    return new StartupActionResult
                    {
                        Success = false,
                        Message = $"A registry value named '{approvedName}' already exists; refusing to overwrite it.",
                    };
                key.SetValue(approvedName, value, valueKind);
                key.DeleteValue(rawName, throwOnMissingValue: false);
                updatedRawName = approvedName;
            }

            WriteStartupApprovedState(hive, approvedSubkey, approvedName, enable);
            return new StartupActionResult
            {
                Success = true,
                Message = enable ? "Enabled." : "Disabled.",
                UpdatedLocator = $"{hive}|{keyPath}|{updatedRawName}",
            };
        }

        var newName = enable ? rawName.TrimStart('!') : ("!" + rawName.TrimStart('!'));
        if (newName == rawName) return new StartupActionResult { Success = true, Message = "Already in desired state." };
        if (Array.Exists(key.GetValueNames(), name => string.Equals(name, newName, StringComparison.Ordinal)))
            return new StartupActionResult
            {
                Success = false,
                Message = $"A registry value named '{newName}' already exists; refusing to overwrite it.",
            };

        key.SetValue(newName, value, valueKind);
        key.DeleteValue(rawName, throwOnMissingValue: false);
        return new StartupActionResult
        {
            Success = true,
            Message = enable ? "Enabled." : "Disabled.",
            UpdatedLocator = $"{hive}|{keyPath}|{newName}",
        };
    }

    private StartupActionResult ToggleStartupFolder(StartupItem item, bool enable)
    {
        var lnk = item.Locator;
        if (string.IsNullOrEmpty(lnk)) return new StartupActionResult { Success = false, Message = "Empty shortcut path." };
        var dir = Path.GetDirectoryName(lnk) ?? string.Empty;
        if (string.IsNullOrEmpty(dir)) return new StartupActionResult { Success = false, Message = "No directory for shortcut." };
        if (!File.Exists(lnk)) return new StartupActionResult { Success = false, Message = "Shortcut not found." };

        bool isDisabled = string.Equals(Path.GetFileName(dir), "Disabled", StringComparison.OrdinalIgnoreCase);

        if (enable && isDisabled)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrEmpty(parent) || !IsKnownStartupFolder(parent))
                return new StartupActionResult { Success = false, Message = "Disabled shortcut isn't inside a known startup folder; refusing to move." };
            if (!TryGetStartupFolderApprovedHive(parent, out var hive))
                return new StartupActionResult { Success = false, Message = "Unknown startup folder scope." };
            if (hive == RegistryHive.LocalMachine && !IsAdmin())
                return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Editing all-users startup approval requires administrator." };
            var dest = Path.Combine(parent, Path.GetFileName(lnk));
            if (File.Exists(dest))
                return new StartupActionResult { Success = false, Message = "A shortcut with that name already exists; refusing to overwrite it." };
            File.Move(lnk, dest);
            WriteStartupApprovedState(hive, "StartupFolder", Path.GetFileName(dest), enable: true);
            return new StartupActionResult { Success = true, Message = "Enabled.", UpdatedLocator = dest };
        }
        if (!enable && !isDisabled)
        {
            if (!IsKnownStartupFolder(dir))
                return new StartupActionResult { Success = false, Message = "Shortcut isn't inside a known startup folder; refusing to move." };
            if (!TryGetStartupFolderApprovedHive(dir, out var hive))
                return new StartupActionResult { Success = false, Message = "Unknown startup folder scope." };
            if (hive == RegistryHive.LocalMachine && !IsAdmin())
                return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Editing all-users startup approval requires administrator." };
            WriteStartupApprovedState(hive, "StartupFolder", Path.GetFileName(lnk), enable: false);
            return new StartupActionResult { Success = true, Message = "Disabled.", UpdatedLocator = lnk };
        }
        if (enable && !isDisabled)
        {
            if (!TryGetStartupFolderApprovedHive(dir, out var hive))
                return new StartupActionResult { Success = false, Message = "Unknown startup folder scope." };
            if (hive == RegistryHive.LocalMachine && !IsAdmin())
                return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Editing all-users startup approval requires administrator." };
            WriteStartupApprovedState(hive, "StartupFolder", Path.GetFileName(lnk), enable: true);
            return new StartupActionResult { Success = true, Message = "Enabled.", UpdatedLocator = lnk };
        }
        return new StartupActionResult { Success = true, Message = "Already in desired state.", UpdatedLocator = lnk };
    }

    private static bool IsKnownStartupFolder(string path)
    {
        var perUser  = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var allUsers = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        return string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(perUser),  StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(allUsers), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetStartupFolderApprovedHive(string path, out RegistryHive hive)
    {
        var perUser  = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var allUsers = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        var normalized = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(perUser), StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.CurrentUser;
            return true;
        }
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(allUsers), StringComparison.OrdinalIgnoreCase))
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
        if (task is null) return new StartupActionResult { Success = false, Message = "Task not found." };
        try
        {
            task.Enabled = enable;
            return new StartupActionResult { Success = true, Message = enable ? "Enabled." : "Disabled." };
        }
        catch (UnauthorizedAccessException)
        {
            return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Toggling this task requires administrator." };
        }
    }

    public StartupActionResult SetServiceStartupType(StartupItem item, ServiceStartupType startupType)
    {
        if (item.Source != StartupSource.Service)
            return new StartupActionResult { Success = false, Message = "Item is not a service." };
        if (!IsAdmin())
            return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Editing service start type requires administrator." };

        var scStartType = startupType switch
        {
            ServiceStartupType.Automatic => "auto",
            ServiceStartupType.Manual    => "demand",
            ServiceStartupType.Disabled  => "disabled",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(scStartType))
            return new StartupActionResult { Success = false, Message = "Unknown service startup type." };

        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
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
            if (proc is null) return new StartupActionResult { Success = false, Message = "Could not invoke sc.exe." };

            // Read both streams asynchronously *before* waiting, so a child process that fills
            // either pipe buffer can't deadlock us. WhenAll completes after the process exits.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            bool exited = proc.WaitForExit(5000);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new StartupActionResult { Success = false, Message = "sc.exe timed out." };
            }

            // The streams complete once the process exits and the pipes close.
            string outp = string.Empty, err = string.Empty;
            try { outp = stdoutTask.GetAwaiter().GetResult(); } catch { }
            try { err  = stderrTask.GetAwaiter().GetResult(); } catch { }

            if (proc.ExitCode != 0)
            {
                var msg = string.Concat(err, outp).Trim();
                if (msg.Length == 0) msg = $"sc.exe exit code {proc.ExitCode}.";
                return new StartupActionResult { Success = false, Message = "sc.exe failed: " + msg };
            }
            return new StartupActionResult { Success = true, Message = $"Service set to {StartupTypeLabel(startupType)}." };
        }
        catch (Exception ex)
        {
            _log.Error("sc.exe", ex);
            return new StartupActionResult { Success = false, Message = ex.Message };
        }
    }

    private static string StartupTypeLabel(ServiceStartupType startupType) => startupType switch
    {
        ServiceStartupType.Automatic => "Automatic",
        ServiceStartupType.Manual    => "Manual",
        ServiceStartupType.Disabled  => "Disabled",
        _ => "Unknown",
    };

    private static string? StartupApprovedSubkeyForRegistryPath(string runKeyPath)
    {
        if (runKeyPath.Equals(@"Software\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase))
            return "Run";
        if (runKeyPath.Equals(@"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase))
            return "Run32";
        return null;
    }

    private static void WriteStartupApprovedState(RegistryHive hive, string approvedSubkey, string valueName, bool enable)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive,
            hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
        using var key = baseKey.CreateSubKey($@"{StartupApprovedRoot}\{approvedSubkey}", writable: true)
            ?? throw new InvalidOperationException("Could not open StartupApproved key.");

        var data = new byte[12];
        BitConverter.GetBytes(enable ? 2 : 3).CopyTo(data, 0);
        if (!enable)
            BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(valueName, data, RegistryValueKind.Binary);
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
