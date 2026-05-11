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
}

/// <summary>
/// Applies enable/disable to startup items using the same conventions Task Manager uses where possible:
/// registry value renamed with leading '!', shortcuts moved to a Disabled subfolder, tasks toggled,
/// services flipped between Automatic and Disabled via sc.exe.
/// </summary>
public sealed class StartupController
{
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
                StartupSource.Service       => ToggleService(item, enable),
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

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(keyPath, writable: true);
        if (key is null) return new StartupActionResult { Success = false, Message = "Registry key not found." };

        var value = key.GetValue(rawName);
        if (value is null) return new StartupActionResult { Success = false, Message = "Registry value not found." };

        var newName = enable ? rawName.TrimStart('!') : ("!" + rawName.TrimStart('!'));
        if (newName == rawName) return new StartupActionResult { Success = true, Message = "Already in desired state." };

        key.SetValue(newName, value);
        key.DeleteValue(rawName, throwOnMissingValue: false);
        return new StartupActionResult { Success = true, Message = enable ? "Enabled." : "Disabled." };
    }

    private StartupActionResult ToggleStartupFolder(StartupItem item, bool enable)
    {
        var lnk = item.Locator;
        if (string.IsNullOrEmpty(lnk)) return new StartupActionResult { Success = false, Message = "Empty shortcut path." };
        var dir = Path.GetDirectoryName(lnk) ?? string.Empty;
        if (string.IsNullOrEmpty(dir)) return new StartupActionResult { Success = false, Message = "No directory for shortcut." };

        bool isDisabled = string.Equals(Path.GetFileName(dir), "Disabled", StringComparison.OrdinalIgnoreCase);

        if (enable && isDisabled)
        {
            var parent = Directory.GetParent(dir)?.FullName ?? dir;
            var dest = Path.Combine(parent, Path.GetFileName(lnk));
            File.Move(lnk, dest, overwrite: true);
            return new StartupActionResult { Success = true, Message = "Enabled." };
        }
        if (!enable && !isDisabled)
        {
            var disabledDir = Path.Combine(dir, "Disabled");
            Directory.CreateDirectory(disabledDir);
            var dest = Path.Combine(disabledDir, Path.GetFileName(lnk));
            File.Move(lnk, dest, overwrite: true);
            return new StartupActionResult { Success = true, Message = "Disabled." };
        }
        return new StartupActionResult { Success = true, Message = "Already in desired state." };
    }

    private StartupActionResult ToggleTask(StartupItem item, bool enable)
    {
        using var ts = new TaskService();
        var task = ts.GetTask(item.Locator);
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

    private StartupActionResult ToggleService(StartupItem item, bool enable)
    {
        if (!IsAdmin())
            return new StartupActionResult { Success = false, NeedsElevation = true, Message = "Editing service start type requires administrator." };

        var args = $"config \"{item.Locator}\" start= {(enable ? "auto" : "disabled")}";
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return new StartupActionResult { Success = false, Message = "Could not invoke sc.exe." };
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                var outp = proc.StandardOutput.ReadToEnd();
                return new StartupActionResult { Success = false, Message = $"sc.exe failed: {err}{outp}".Trim() };
            }
            return new StartupActionResult { Success = true, Message = enable ? "Service set to Automatic." : "Service Disabled." };
        }
        catch (Exception ex)
        {
            _log.Error("sc.exe", ex);
            return new StartupActionResult { Success = false, Message = ex.Message };
        }
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
