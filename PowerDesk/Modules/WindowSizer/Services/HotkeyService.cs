using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Services;
using PowerDesk.Modules.WindowSizer.Models;
using static PowerDesk.Modules.WindowSizer.Services.NativeMethods;

namespace PowerDesk.Modules.WindowSizer.Services;

/// <summary>Outcome of a RegisterAll pass. <see cref="Failures"/> carries a human-readable reason per binding.</summary>
public sealed class HotkeyRegistrationResult
{
    public int Registered { get; init; }
    public IReadOnlyList<(HotkeyBinding Binding, string Reason)> Failures { get; init; } = Array.Empty<(HotkeyBinding, string)>();
    public bool AllOk => Failures.Count == 0;
}

/// <summary>
/// Wraps RegisterHotKey/WM_HOTKEY. A hidden message-only window owns the registrations.
/// RegisterHotKey binds to the calling thread, so every OS call is marshalled onto the UI thread that created the
/// window. The service raises <see cref="HotkeyPressed"/> on the UI thread.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int FirstId = 0x9000; // application hotkey ids must be in 0x0000..0xBFFF

    private readonly ILogger _log;
    private HwndSource? _source;
    private readonly Dictionary<int, HotkeyBinding> _idToBinding = new();
    private int _nextId = FirstId;
    private bool _disposed;

    public event EventHandler<HotkeyBinding>? HotkeyPressed;

    public HotkeyService(ILogger log) => _log = log;

    public int ActiveCount => _idToBinding.Count;

    public void Initialize()
    {
        if (_disposed) return;
        UiDispatcher.Invoke(() =>
        {
            if (_source is not null) return;
            try
            {
                var p = new HwndSourceParameters("PowerDesk.HotkeyHost")
                {
                    ParentWindow = HWND_MESSAGE,
                    WindowStyle = 0,
                    ExtendedWindowStyle = 0,
                    PositionX = 0, PositionY = 0, Width = 0, Height = 0,
                };
                _source = new HwndSource(p);
                _source.AddHook(WndProc);
            }
            catch (Exception ex)
            {
                // Fall back to a hidden (never shown) top-level window so hotkeys still work.
                _log.Warn($"Message-only hotkey host failed ({ex.Message}); using hidden top-level window.");
                try
                {
                    _source = new HwndSource(0, 0, 0, 0, 0, "PowerDesk.HotkeyHost", IntPtr.Zero);
                    _source.AddHook(WndProc);
                }
                catch (Exception ex2)
                {
                    _log.Error("Hotkey host window could not be created", ex2);
                    _source = null;
                }
            }
        });
    }

    public HotkeyRegistrationResult RegisterAll(IEnumerable<HotkeyBinding> bindings)
    {
        if (_disposed) return new HotkeyRegistrationResult();
        var snapshot = new List<HotkeyBinding>(bindings);
        HotkeyRegistrationResult result = new();
        UiDispatcher.Invoke(() => result = RegisterAllCore(snapshot));
        return result;
    }

    private HotkeyRegistrationResult RegisterAllCore(List<HotkeyBinding> bindings)
    {
        UnregisterAllCore();
        if (_source is null) Initialize();
        if (_source is null || _source.Handle == IntPtr.Zero)
        {
            var reason = "hotkey host window unavailable";
            var all = new List<(HotkeyBinding, string)>();
            foreach (var b in WindowSizerLogic.PlanRegistrations(bindings).ToRegister) all.Add((b, reason));
            return new HotkeyRegistrationResult { Failures = all };
        }

        var plan = WindowSizerLogic.PlanRegistrations(bindings);
        var failures = new List<(HotkeyBinding, string)>();
        foreach (var dup in plan.Duplicates)
        {
            failures.Add((dup, "duplicate of another binding"));
            _log.Warn($"Hotkey skipped (duplicate chord): {dup.DisplayText} ({dup.ActionLabel})");
        }

        foreach (var b in plan.ToRegister)
        {
            int id = _nextId++;
            try
            {
                var mods = WindowSizerLogic.NormalizeModifiers(b.Modifiers) | MOD_NOREPEAT;
                if (RegisterHotKey(_source.Handle, id, mods, b.VirtualKey))
                {
                    _idToBinding[id] = b;
                }
                else
                {
                    var reason = WindowSizerLogic.DescribeRegisterFailure(Marshal.GetLastWin32Error());
                    failures.Add((b, reason));
                    _log.Warn($"Hotkey register failed: {b.DisplayText} ({b.ActionLabel}): {reason}");
                }
            }
            catch (Exception ex)
            {
                failures.Add((b, ex.Message));
                _log.Error($"Hotkey register threw for {b.DisplayText}", ex);
            }
        }
        return new HotkeyRegistrationResult { Registered = _idToBinding.Count, Failures = failures };
    }

    public void UnregisterAll()
    {
        if (_source is null) { _idToBinding.Clear(); return; }
        try { UiDispatcher.Invoke(UnregisterAllCore); }
        catch (Exception ex) { _log.Warn($"Hotkey unregister failed: {ex.Message}"); }
    }

    private void UnregisterAllCore()
    {
        if (_source is not null && _source.Handle != IntPtr.Zero)
        {
            foreach (var id in _idToBinding.Keys)
            {
                try { UnregisterHotKey(_source.Handle, id); } catch { }
            }
        }
        _idToBinding.Clear();
        _nextId = FirstId; // ids are free again; never let them creep past 0xBFFF over a long session
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = unchecked((int)wParam.ToInt64());
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
        try
        {
            UiDispatcher.Invoke(() =>
            {
                try { UnregisterAllCore(); } catch { }
                if (_source is not null)
                {
                    try { _source.RemoveHook(WndProc); } catch { }
                    try { _source.Dispose(); } catch { }
                    _source = null;
                }
            });
        }
        catch (Exception ex) { _log.Warn($"Hotkey service dispose: {ex.Message}"); }
        HotkeyPressed = null;
    }
}
