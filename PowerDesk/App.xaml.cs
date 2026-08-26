using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Models;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Core.Theming;
using PowerDesk.Modules.ColorPicker;
using PowerDesk.Modules.DnsDesk;
using PowerDesk.Modules.FileLockFinder;
using PowerDesk.Modules.HashDesk;
using PowerDesk.Modules.HostProfiles;
using PowerDesk.Modules.MonitorDesk;
using PowerDesk.Modules.PathEditor;
using PowerDesk.Modules.StartupPilot;
using PowerDesk.Modules.WindowSizer;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace PowerDesk;

public partial class App : Application
{
    public static App Instance => (App)Current;

    public ILogger Logger { get; } = new FileLogger();
    public JsonStorageService Storage { get; private set; } = null!;
    public ThemeService ThemeService { get; } = new();
    public PermissionService Permissions { get; } = new();
    public StatusService Status { get; } = new();
    public RecentActionsService RecentActions { get; } = new();
    public IconService Icons { get; private set; } = null!;
    public IConfirmationService Confirm { get; } = new ConfirmationService();
    public ModuleRegistry Modules { get; } = new();
    public AppSettings Settings { get; private set; } = new();
    public TrayIconService Tray { get; private set; } = null!;

    public WindowSizerModule? WindowSizerModule { get; private set; }
    public StartupPilotModule? StartupPilotModule { get; private set; }

    public MainWindow? Shell { get; private set; }
    private bool _skipShutdownPersistence;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;

    private const string InstanceMutexName = @"Local\PowerDesk.SingleInstance";
    private const string ActivationEventName = @"Local\PowerDesk.Activate";
    /// <summary>Passed by a relaunch (elevation / reset) so the new process waits for the old one to release the instance lock.</summary>
    public const string RelaunchArg = "--relaunched";

    protected override async void OnStartup(StartupEventArgs e)
    {
        var relaunched = e.Args.Any(a => string.Equals(a, RelaunchArg, StringComparison.OrdinalIgnoreCase));
        if (!TryAcquireSingleInstance(relaunched))
        {
            // Ask the running instance to bring its window forward; only fall back to a dialog if that fails
            // (e.g. the other instance is elevated and we cannot open its event).
            if (!TrySignalExistingInstance())
            {
                MessageBox.Show(
                    "PowerDesk is already running. Use the existing window or tray icon.",
                    "PowerDesk",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        StartActivationListener();
        Permissions.RelaunchHandler = () => RelaunchSelf(elevated: true);

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("UI exception (handled)", args.Exception);
            Status.Set("An unexpected error occurred. See logs for details.", StatusKind.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.Error("Domain exception", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Logger.Info("PowerDesk starting.");

        Storage = new JsonStorageService(Logger);
        Icons = new IconService(Logger);

        Settings = await Storage.LoadAsync(Core.Services.PathService.SettingsFile, () => new AppSettings());
        ThemeService.Apply(Settings.Theme);

        // Register feature modules. Add more here as the hub grows.
        WindowSizerModule = new WindowSizerModule(Logger, Storage, Status, RecentActions, Icons, Settings);
        StartupPilotModule = new StartupPilotModule(Logger, Storage, Status, RecentActions, Icons, Permissions, Confirm);
        Modules.Register(WindowSizerModule);
        Modules.Register(StartupPilotModule);
        Modules.Register(new MonitorDeskModule(Logger, Storage, Status, RecentActions));
        Modules.Register(new DnsDeskModule(Logger, Status, RecentActions, Permissions));
        Modules.Register(new HashDeskModule(Logger, Status, RecentActions));
        Modules.Register(new HostProfilesModule(Logger, Storage, Status, RecentActions, Permissions, Confirm));
        Modules.Register(new ColorPickerModule(Logger, Status));
        Modules.Register(new FileLockFinderModule(Logger, Status, RecentActions, Permissions, Confirm));
        Modules.Register(new PathEditorModule(Logger, Storage, Status, RecentActions, Permissions, Confirm));

        foreach (var m in Modules.Modules)
        {
            try { await m.InitializeAsync(); }
            catch (Exception ex) { Logger.Error($"Module init failed: {m.Id}", ex); }
        }

        Shell = new MainWindow();
        Shell.Closed += async (_, _) =>
        {
            if (!_skipShutdownPersistence)
            {
                foreach (var m in Modules.Modules)
                {
                    try { await m.ShutdownAsync(); } catch (Exception ex) { Logger.Error($"Module shutdown: {m.Id}", ex); }
                }
                await Storage.SaveAsync(Core.Services.PathService.SettingsFile, Settings);
            }
            else
            {
                Logger.Info("PowerDesk shutdown persistence skipped.");
            }
            Tray?.Dispose();
            Logger.Info("PowerDesk exited.");
            Shutdown();
        };

        // System tray
        Tray = new TrayIconService(Logger);
        Tray.Initialize();
        Tray.ShowRequested += (_, _) => ShowShell();
        Tray.ExitRequested += (_, _) => Shell?.ForceClose();
        Tray.OpenModuleRequested += (_, id) => { ShowShell(); Shell?.NavigateTo(id); };
        Tray.RescanStartupRequested += async (_, _) =>
        {
            if (StartupPilotModule is { } sp) await sp.ViewModel.RescanAsync();
        };
        Tray.SnapForegroundLeftRequested  += (_, _) => WindowSizerModule?.ViewModel?.InvokeForegroundSnap(true);
        Tray.SnapForegroundRightRequested += (_, _) => WindowSizerModule?.ViewModel?.InvokeForegroundSnap(false);

        if (Settings.StartMinimized && Settings.MinimizeToTrayOnClose)
        {
            // Stay hidden; the tray icon is the way back in. Nothing to Show() yet.
            Shell.ShowInTaskbar = false;
            Tray.ShowBalloon("PowerDesk started in the tray", "Click the tray icon to open it.");
        }
        else if (Settings.StartMinimized)
        {
            // Minimized but still reachable from the taskbar: the window must be shown for that.
            Shell.WindowState = WindowState.Minimized;
            Shell.Show();
        }
        else
        {
            Shell.Show();
        }
    }

    public void ShowShell()
    {
        if (Shell is null) return;
        Shell.ShowInTaskbar = true;
        if (!Shell.IsVisible) Shell.Show();
        if (Shell.WindowState == WindowState.Minimized) Shell.WindowState = WindowState.Normal;
        Shell.Activate();
        Shell.Topmost = true;
        Shell.Topmost = false;
        Shell.Focus();
    }

    public void SkipShutdownPersistenceOnce() => _skipShutdownPersistence = true;

    public async Task<bool> SaveSettingsAsync()
        => await Storage.SaveAsync(Core.Services.PathService.SettingsFile, Settings);

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    /// <summary>
    /// Relaunches PowerDesk (optionally elevated) and closes this instance. The instance lock is released
    /// BEFORE the new process starts so the newcomer never sees "already running", and the newcomer is told
    /// to retry the lock briefly in case shutdown persistence is still in flight here.
    /// Returns true when a new process was started (the caller should then close the shell).
    /// </summary>
    public bool RelaunchSelf(bool elevated)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;
        ReleaseSingleInstance();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Arguments = RelaunchArg,
            };
            if (elevated) psi.Verb = "runas";
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Relaunch failed or was cancelled: {ex.Message}");
            // Re-acquire so this instance keeps behaving as the single instance.
            TryAcquireSingleInstance(relaunched: false);
            StartActivationListener();
            return false;
        }
    }

    private bool TryAcquireSingleInstance(bool relaunched)
    {
        // A relaunched instance may start while the previous one is still finishing its shutdown; give it
        // a few seconds to let go before deciding another copy is genuinely running.
        var deadline = DateTime.UtcNow + (relaunched ? TimeSpan.FromSeconds(8) : TimeSpan.Zero);
        while (true)
        {
            try
            {
                var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
                if (createdNew)
                {
                    _singleInstanceMutex = mutex;
                    return true;
                }
                // Someone else owns it. Wait briefly for it to be released (abandoned mutexes count as acquired).
                var wait = deadline - DateTime.UtcNow;
                if (wait <= TimeSpan.Zero) { mutex.Dispose(); return false; }
                try
                {
                    if (mutex.WaitOne(wait)) { _singleInstanceMutex = mutex; return true; }
                }
                catch (AbandonedMutexException) { _singleInstanceMutex = mutex; return true; }
                mutex.Dispose();
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Mutex exists but was created by a differently-privileged process we cannot open: treat as running.
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                // Kernel object problems should never keep the app from starting.
                Logger.Warn($"Single-instance mutex unavailable: {ex.Message}");
                return true;
            }
        }
    }

    private void ReleaseSingleInstance()
    {
        try { _activationWait?.Unregister(null); } catch { }
        _activationWait = null;
        try { _activationEvent?.Dispose(); } catch { }
        _activationEvent = null;
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        _singleInstanceMutex = null;
    }

    private void StartActivationListener()
    {
        if (_activationEvent is not null) return;
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                (_, _) => Dispatcher.BeginInvoke(ShowShell),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Activation listener unavailable: {ex.Message}");
        }
    }

    private static bool TrySignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var evt))
            {
                using (evt) return evt.Set();
            }
        }
        catch { }
        return false;
    }
}
