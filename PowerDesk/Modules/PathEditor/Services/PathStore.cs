using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PowerDesk.Modules.PathEditor.Models;

namespace PowerDesk.Modules.PathEditor.Services;

/// <summary>Reads and writes the raw (unexpanded) PATH value for a scope.</summary>
internal interface IPathStore
{
    /// <summary>Raw PATH text with %VARS% left intact; empty when the value is absent.</summary>
    string Read(PathScope scope);

    /// <summary>Writes the PATH value as REG_EXPAND_SZ and notifies other processes.</summary>
    void Write(PathScope scope, string value);
}

/// <summary>
/// Registry-backed store. <see cref="Environment.GetEnvironmentVariable(string, EnvironmentVariableTarget)"/>
/// expands %SystemRoot%-style entries and <see cref="Environment.SetEnvironmentVariable(string, string, EnvironmentVariableTarget)"/>
/// writes plain REG_SZ, which silently destroys expandable entries; this class talks to the
/// registry directly to preserve them.
/// </summary>
internal sealed class RegistryPathStore : IPathStore
{
    private const string ValueName = "Path";
    private const string UserKeyPath = "Environment";
    private const string MachineKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public string Read(PathScope scope)
    {
        using var key = Open(scope, writable: false);
        if (key is null) return string.Empty;
        return key.GetValue(ValueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
    }

    public void Write(PathScope scope, string value)
    {
        using var key = Open(scope, writable: true)
            ?? throw new InvalidOperationException($"The {scope} environment registry key could not be opened for writing.");
        key.SetValue(ValueName, value ?? string.Empty, RegistryValueKind.ExpandString);
        key.Flush();
        BroadcastEnvironmentChange();
    }

    private static RegistryKey? Open(PathScope scope, bool writable)
    {
        if (scope == PathScope.Machine)
            return Registry.LocalMachine.OpenSubKey(MachineKeyPath, writable);
        var user = Registry.CurrentUser.OpenSubKey(UserKeyPath, writable);
        if (user is null && writable) user = Registry.CurrentUser.CreateSubKey(UserKeyPath, writable: true);
        return user;
    }

    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_NORMAL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);

    /// <summary>
    /// Tells Explorer and other top-level windows that the environment changed. A hung window
    /// cannot block us: SMTO_ABORTIFHUNG skips it and the per-window timeout is short.
    /// </summary>
    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment",
                SMTO_NORMAL | SMTO_ABORTIFHUNG, 1000, out _);
        }
        catch
        {
            // Best effort only: the registry write already succeeded.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}
