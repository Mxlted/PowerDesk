using System;
using System.Diagnostics;
using System.Security.Principal;

namespace PowerDesk.Core.Permissions;

public sealed class PermissionService
{
    public bool IsAdministrator
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                var p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
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
