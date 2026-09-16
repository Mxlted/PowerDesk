using System.Diagnostics;
using Microsoft.Win32;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Models;

namespace PowerDesk.Core.Services;

/// <summary>
/// Owns PowerDesk's own "Run at Windows startup" registration (HKCU\...\Run\PowerDesk).
/// <para>
/// The registry is the source of truth, not <see cref="AppSettings.RunAtWindowsStartup"/>: the user can
/// remove or disable the entry from Task Manager, Autoruns or StartupPilot itself, and PowerDesk is a
/// portable exe that gets moved or renamed. <see cref="Sync"/> reconciles the two at launch so the
/// Settings checkbox tells the truth and a moved exe keeps starting with Windows.
/// </para>
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "PowerDesk";

    /// <summary>Full path of the running executable, or null when it cannot be determined.</summary>
    public static string? CurrentExePath
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(path)) path = Process.GetCurrentProcess().MainModule?.FileName;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch { return null; }
        }
    }

    /// <summary>The command written to the Run key: the exe path, quoted so spaces survive.</summary>
    public static string BuildCommand(string exePath) => $"\"{exePath}\"";

    /// <summary>
    /// True when a Run command launches <paramref name="exePath"/>. Accepts the quoted form PowerDesk
    /// writes as well as an unquoted path (which some tools produce), compared case-insensitively.
    /// </summary>
    public static bool CommandMatchesExe(string? command, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(exePath)) return false;
        var trimmed = command.Trim();
        var head = trimmed;
        if (trimmed.Length >= 2 && trimmed[0] == '"')
        {
            var close = trimmed.IndexOf('"', 1);
            head = close > 1 ? trimmed[1..close] : trimmed[1..];
        }
        return string.Equals(head.Trim(), exePath.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, exePath.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes Explorer's StartupApproved blob: byte 0 is 0x02/0x06 when enabled and 0x03/0x07 when
    /// disabled (the low bit is what matters). Null when there is no usable record.
    /// </summary>
    public static bool? ParseApprovedBlob(byte[]? data)
    {
        if (data is null || data.Length == 0 || data[0] == 0) return null;
        return (data[0] & 1) == 0;
    }

    /// <summary>The command currently registered, or null when PowerDesk is not in the Run key.</summary>
    public static string? ReadCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// True when PowerDesk will actually start with Windows: a Run value exists and Task Manager has not
    /// marked it disabled.
    /// </summary>
    public static bool IsEnabled()
    {
        if (ReadCommand() is null) return false;
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: false);
            return ParseApprovedBlob(approved?.GetValue(ValueName) as byte[]) ?? true;
        }
        catch { return true; }
    }

    /// <summary>
    /// Writes or removes the Run value. Enabling also clears any Task Manager "disabled" mark so the
    /// entry really runs; disabling removes both records. Throws on registry failure.
    /// </summary>
    public static void SetEnabled(bool enable)
    {
        var exe = CurrentExePath ?? throw new InvalidOperationException("Could not determine PowerDesk's exe path.");
        using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the Run key.");
        if (enable)
        {
            run.SetValue(ValueName, BuildCommand(exe), RegistryValueKind.String);
            try
            {
                using var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedKeyPath, writable: true);
                // 0x02 + zero FILETIME is exactly what Task Manager writes for "Enabled".
                approved?.SetValue(ValueName, new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }
            catch { /* the Run value alone is enough; approval state is best-effort */ }
        }
        else
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
            try
            {
                using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch { }
        }
    }

    /// <summary>
    /// Reconciles the saved preference with the registry at launch:
    /// a registration that points at an old location is re-pointed at the running exe, and the
    /// preference is updated to reflect whether the entry actually exists and is enabled.
    /// Returns true when <paramref name="settings"/> was changed and should be saved.
    /// </summary>
    public static bool Sync(AppSettings settings, ILogger log)
    {
        var command = ReadCommand();
        if (command is not null)
        {
            var exe = CurrentExePath;
            if (exe is not null && !CommandMatchesExe(command, exe))
            {
                try
                {
                    using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                    run?.SetValue(ValueName, BuildCommand(exe), RegistryValueKind.String);
                    log.Info($"Startup entry re-pointed from {command} to {exe}.");
                }
                catch (Exception ex)
                {
                    log.Warn($"Could not update the startup entry path: {ex.Message}");
                }
            }
        }

        var enabled = IsEnabled();
        if (settings.RunAtWindowsStartup == enabled) return false;
        log.Info($"Run-at-startup preference corrected to {enabled} to match the registry.");
        settings.RunAtWindowsStartup = enabled;
        return true;
    }
}
