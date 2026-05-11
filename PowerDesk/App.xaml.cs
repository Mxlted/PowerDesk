using System;
using System.Threading.Tasks;
using System.Windows;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Models;
using PowerDesk.Core.Navigation;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Core.Theming;
using PowerDesk.Modules.StartupPilot;
using PowerDesk.Modules.WindowSizer;
using Application = System.Windows.Application;

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
    public ModuleRegistry Modules { get; } = new();
    public AppSettings Settings { get; private set; } = new();
    public TrayIconService Tray { get; private set; } = null!;

    public WindowSizerModule? WindowSizerModule { get; private set; }
    public StartupPilotModule? StartupPilotModule { get; private set; }

    public MainWindow? Shell { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        StartupPilotModule = new StartupPilotModule(Logger, Storage, Status, RecentActions, Icons, Permissions);
        Modules.Register(WindowSizerModule);
        Modules.Register(StartupPilotModule);

        foreach (var m in Modules.Modules)
        {
            try { await m.InitializeAsync(); }
            catch (Exception ex) { Logger.Error($"Module init failed: {m.Id}", ex); }
        }

        Shell = new MainWindow();
        Shell.Closed += async (_, _) =>
        {
            foreach (var m in Modules.Modules)
            {
                try { await m.ShutdownAsync(); } catch (Exception ex) { Logger.Error($"Module shutdown: {m.Id}", ex); }
            }
            await Storage.SaveAsync(Core.Services.PathService.SettingsFile, Settings);
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

        if (Settings.StartMinimized)
        {
            Shell.WindowState = WindowState.Minimized;
            Shell.ShowInTaskbar = !Settings.MinimizeToTrayOnClose;
            if (Settings.MinimizeToTrayOnClose) Shell.Hide();
        }
        else
        {
            Shell.Show();
        }
    }

    public void ShowShell()
    {
        if (Shell is null) return;
        if (!Shell.IsVisible) Shell.Show();
        if (Shell.WindowState == WindowState.Minimized) Shell.WindowState = WindowState.Normal;
        Shell.Activate();
        Shell.Topmost = true;
        Shell.Topmost = false;
    }

    public async Task SaveSettingsAsync()
        => await Storage.SaveAsync(Core.Services.PathService.SettingsFile, Settings);
}
