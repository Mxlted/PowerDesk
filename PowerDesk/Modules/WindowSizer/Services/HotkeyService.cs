using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using PowerDesk.Core.Logging;
using PowerDesk.Modules.WindowSizer.Models;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;

namespace PowerDesk.Modules.WindowSizer.Services;

/// <summary>
/// Wraps RegisterHotKey/WM_HOTKEY. A hidden message window owns the registrations.
/// The service raises <see cref="HotkeyPressed"/> on the UI thread.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly ILogger _log;
    private HwndSource? _source;
    private readonly Dictionary<int, HotkeyBinding> _idToBinding = new();
    private int _nextId = 0x9000;
    private bool _disposed;

    public event EventHandler<HotkeyBinding>? HotkeyPressed;

    public HotkeyService(ILogger log) => _log = log;

    public void Initialize()
    {
        if (_source is not null) return;
        _source = new HwndSource(0, 0, 0, 0, 0, "PowerDesk.HotkeyHost", IntPtr.Zero);
        _source.AddHook(WndProc);
    }

    public bool RegisterAll(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        if (_source is null) Initialize();
        bool allOk = true;
        foreach (var b in bindings)
        {
            if (!b.Enabled || b.VirtualKey == 0 || b.Modifiers == 0) continue;
            int id = _nextId++;
            try
            {
                if (RegisterHotKey(_source!.Handle, id, b.Modifiers | MOD_NOREPEAT, b.VirtualKey))
                {
                    _idToBinding[id] = b;
                }
                else
                {
                    allOk = false;
                    _log.Warn($"Hotkey register failed: {b.DisplayText} ({b.ActionLabel})");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                _log.Error($"Hotkey register threw for {b.DisplayText}", ex);
            }
        }
        return allOk;
    }

    public void UnregisterAll()
    {
        if (_source is null) return;
        foreach (var id in _idToBinding.Keys)
        {
            try { UnregisterHotKey(_source.Handle, id); } catch { }
        }
        _idToBinding.Clear();
    }

    public int ActiveCount => _idToBinding.Count;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_idToBinding.TryGetValue(id, out var binding))
            {
                handled = true;
                try { HotkeyPressed?.Invoke(this, binding); }
                catch (Exception ex) { _log.Error("Hotkey handler", ex); }
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { UnregisterAll(); } catch { }
        _source?.Dispose();
        _source = null;
    }
}
