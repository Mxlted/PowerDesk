using System;
using System.Diagnostics;
using System.Security.Principal;

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
    /// Relaunches PowerDesk with the runas verb. Returns true if a process was started; false on cancel or error.
    /// </summary>
    public bool TryRelaunchAsAdmin(string? args = null)
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
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
