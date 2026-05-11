using System;
using System.IO;

namespace PowerDesk.Core.Services;

/// <summary>
/// Resolves canonical PowerDesk data locations under %LocalAppData%\PowerDesk\.
/// All paths are guaranteed to exist after a call.
/// </summary>
public static class PathService
{
    public static string Root { get; } = EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerDesk"));

    public static string LogsDir { get; } = EnsureDir(Path.Combine(Root, "logs"));
    public static string LogFile { get; } = Path.Combine(LogsDir, "powerdesk.log");
    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    public static string ModulesDir { get; } = EnsureDir(Path.Combine(Root, "Modules"));

    public static string ModuleDir(string moduleId)
        => EnsureDir(Path.Combine(ModulesDir, moduleId));

    public static string ModuleSettingsFile(string moduleId)
        => Path.Combine(ModuleDir(moduleId), "settings.json");

    public static string IconCacheDir { get; } = EnsureDir(Path.Combine(Root, "IconCache"));

    private static string EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* surface via logger when callers use it */ }
        return path;
    }
}
