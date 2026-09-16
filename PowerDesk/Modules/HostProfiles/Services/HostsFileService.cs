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
    /// as UTF-8 without BOM with CRLF line endings. The content is staged in a sibling temp file and swapped into
    /// place, so a failure mid-write (disk full, antivirus grabbing the handle) can never leave Windows with a
    /// truncated hosts file. A read-only attribute is cleared for the swap and restored after.
    /// Returns the backup path, or null when there was nothing to back up.
    /// </summary>
    public string? WriteWithBackup(string? content, string backupDirectory, DateTime timestamp)
    {
        var normalized = HostProfilesLogic.NormalizeForWrite(content);
        string? backupPath = null;
        var exists = File.Exists(HostsPath);

        if (exists)
        {
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory, HostProfilesLogic.BackupFileName(timestamp));
            File.Copy(HostsPath, backupPath, overwrite: true);
        }

        FileAttributes? original = null;
        if (exists)
        {
            var attributes = File.GetAttributes(HostsPath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                original = attributes;
                File.SetAttributes(HostsPath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        var temp = HostsPath + ".powerdesk.tmp";
        try
        {
            File.WriteAllText(temp, normalized, HostProfilesLogic.WriteEncoding);
            if (exists)
            {
                // ReplaceFile keeps the original's identity (creation time, DACL, streams) and is atomic on NTFS.
                try { File.Replace(temp, HostsPath, destinationBackupFileName: null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(temp, HostsPath, overwrite: true); }
            }
            else
            {
                File.Move(temp, HostsPath);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
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
