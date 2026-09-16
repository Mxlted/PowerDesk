using System;
using System.Diagnostics;
using System.Security.Principal;
using PowerDesk.Core.Services;

namespace PowerDesk.Core.Permissions;

public sealed class PermissionService
{
    // Token elevation does not change for the lifetime of the process, so we resolve once.
    private readonly Lazy<bool> _isAdmin = new(ComputeIsAdministrator, isThreadSafe: true);

    public bool IsAdministrator => _isAdmin.Value;

    private static bool ComputeIsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var p = new WindowsPrincipal(id);
            return p.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Optional shell-provided relaunch routine. The shell installs one that releases the single-instance
    /// lock before spawning, so the elevated copy never sees "PowerDesk is already running".
    /// </summary>
    public Func<bool>? RelaunchHandler { get; set; }

    /// <summary>
    /// Optional shell-provided routine that closes this instance once an elevated copy has been started.
    /// </summary>
    public Action? ShutdownHandler { get; set; }

    /// <summary>
    /// The single "Relaunch as administrator" flow every module and the Settings page share: reports
    /// when elevation is unnecessary or was cancelled, and otherwise hands off to the elevated copy
    /// by closing this instance.
    /// </summary>
    public void RequestElevation(StatusService status)
    {
        if (IsAdministrator)
        {
            status.Set("Already running as administrator.", StatusKind.Info);
            return;
        }
        if (!TryRelaunchAsAdmin())
        {
            status.Set("Elevation cancelled.", StatusKind.Warning);
            return;
        }
        ShutdownHandler?.Invoke();
    }

    /// <summary>
    /// Relaunches PowerDesk with the runas verb. Returns true if a process was started; false on cancel or error.
    /// Callers should close the current shell when this returns true.
    /// </summary>
    public bool TryRelaunchAsAdmin(string? args = null)
    {
        if (RelaunchHandler is { } handler)
        {
            try { return handler(); }
            catch { return false; }
        }
        try
        {
            var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return false;
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = args ?? string.Empty,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
