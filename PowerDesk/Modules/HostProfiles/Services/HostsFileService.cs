using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PowerDesk.Modules.HostProfiles.Services;

/// <summary>
/// The only place that touches a hosts file on disk. The path is injectable so tests can point it at a temp file.
/// </summary>
internal sealed class HostsFileService
{
    public string HostsPath { get; }

    public HostsFileService(string? hostsPath = null)
    {
        HostsPath = string.IsNullOrWhiteSpace(hostsPath) ? HostProfilesLogic.DefaultHostsPath : hostsPath;
    }

    public bool Exists => File.Exists(HostsPath);

    /// <summary>Reads and decodes the hosts file; an absent file reads as empty.</summary>
    public string Read()
    {
        if (!File.Exists(HostsPath)) return string.Empty;
        return HostProfilesLogic.Decode(File.ReadAllBytes(HostsPath));
    }

    /// <summary>
    /// Backs up the current file (if any) into <paramref name="backupDirectory"/>, then writes the normalized content
    /// as UTF-8 without BOM with CRLF line endings. A read-only attribute is cleared for the write and restored after.
    /// Returns the backup path, or null when there was nothing to back up.
    /// </summary>
    public string? WriteWithBackup(string? content, string backupDirectory, DateTime timestamp)
    {
        var normalized = HostProfilesLogic.NormalizeForWrite(content);
        string? backupPath = null;

        if (File.Exists(HostsPath))
        {
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory, HostProfilesLogic.BackupFileName(timestamp));
            File.Copy(HostsPath, backupPath, overwrite: true);
        }

        FileAttributes? original = null;
        if (File.Exists(HostsPath))
        {
            var attributes = File.GetAttributes(HostsPath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                original = attributes;
                File.SetAttributes(HostsPath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        try
        {
            File.WriteAllText(HostsPath, normalized, HostProfilesLogic.WriteEncoding);
        }
        finally
        {
            if (original is not null)
            {
                try { File.SetAttributes(HostsPath, original.Value); } catch { }
            }
        }
        return backupPath;
    }

    /// <summary>Flushes the Windows DNS resolver cache so new hosts entries take effect immediately.</summary>
    public static bool FlushDns()
    {
        try { return DnsFlushResolverCache(); }
        catch { return false; }
    }

    [DllImport("dnsapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
