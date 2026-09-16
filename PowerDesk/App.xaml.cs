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
        Permissions.ShutdownHandler = () => Shell?.ForceClose();

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
        // WPF calls Shutdown() straight after this event and tears the dispatcher down before the
        // async Closed handler below gets to its first continuation, so on log-off / restart the
        // window placement, last page and any unsaved module state were lost. Persist synchronously here.
        SessionEnding += (_, _) => PersistBeforeSessionEnds();

        Logger.Info("PowerDesk starting.");

        Storage = new JsonStorageService(Logger);
        Icons = new IconService(Logger);

        Settings = await Storage.LoadAsync(PathService.SettingsFile, () => new AppSettings());
        ThemeService.Apply(Settings.Theme);

        // A portable exe gets moved and renamed; keep the Run entry pointing at wherever we are now,
        // and let the saved preference follow whatever Task Manager / other tools did to the entry.
        try
        {
            if (StartupRegistration.Sync(Settings, Logger))
                await SaveSettingsAsync();
        }
        catch (Exception ex) { Logger.Warn($"Startup registration sync: {ex.Message}"); }

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

        Shell = new MainWindow();
        Shell.Closed += async (_, _) =>
        {
            if (!_skipShutdownPersistence)
            {
                foreach (var m in Modules.Modules)
                {
                    try { await m.ShutdownAsync(); } catch (Exception ex) { Logger.Error($"Module shutdown: {m.Id}", ex); }
                }
                await SaveSettingsAsync();
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
        Tray.Initialize(Modules.Modules.Select(m => (m.Id, m.DisplayName)).ToList());
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
            // The window never fires StateChanged/IsVisibleChanged in this path, so tell WindowSizer
            // explicitly that nobody is looking; otherwise it polls every window every 2 seconds for nothing.
            WindowSizerModule.ViewModel.OnShellVisibilityChanged(false);
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

        // Initialize modules only now that the shell is on screen. Previously the window did not
        // appear until the startup scan, network adapter enumeration and PATH read had all finished,
        // which read as a slow launch. Modules are independent by design so they load concurrently;
        // a failure in one is logged and never blocks the others.
        Status.Set("Loading tools…");
        await Task.WhenAll(Modules.Modules.Select(InitializeModuleAsync));
        Logger.Info("All modules initialized.");
    }

    private async Task InitializeModuleAsync(IPowerDeskModule module)
    {
        try { await module.InitializeAsync(); }
        catch (Exception ex) { Logger.Error($"Module init failed: {module.Id}", ex); }
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

    /// <summary>
    /// Saves everything the normal Closed path would, but without yielding to a dispatcher that is
    /// about to be shut down. Module shutdowns are awaited by pumping a nested message loop (their
    /// continuations need the UI thread), each under a short budget so a hung module cannot make
    /// Windows kill us before the app settings are written.
    /// </summary>
    private void PersistBeforeSessionEnds()
    {
        if (_skipShutdownPersistence) return;
        _skipShutdownPersistence = true;
        Logger.Info("Windows session ending; persisting state.");
        try
        {
            Shell?.SavePlacement();
            foreach (var m in Modules.Modules)
            {
                try { WaitWithPump(m.ShutdownAsync(), TimeSpan.FromMilliseconds(1500)); }
                catch (Exception ex) { Logger.Error($"Module shutdown (session end): {m.Id}", ex); }
            }
            // JsonStorageService awaits with ConfigureAwait(false) throughout, so blocking here cannot deadlock.
            SaveSettingsAsync().GetAwaiter().GetResult();
            Logger.Info("State persisted for session end.");
        }
        catch (Exception ex)
        {
            Logger.Error("Session-end persistence", ex);
        }
    }

    /// <summary>Waits for a task on the UI thread while still dispatching messages, giving up after <paramref name="timeout"/>.</summary>
    private static void WaitWithPump(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        var timer = new System.Windows.Threading.DispatcherTimer(
            timeout, System.Windows.Threading.DispatcherPriority.Send,
            (_, _) => frame.Continue = false,
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        timer.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (task.IsCompleted) task.GetAwaiter().GetResult();
    }

    public Task<bool> SaveSettingsAsync()
        => Storage.SaveAsync(PathService.SettingsFile, Settings);

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
