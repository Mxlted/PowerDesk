# PowerDesk

PowerDesk is a modular, local-first PC tool hub for Windows. It's a single polished app that can host many independent tools over time; the first two built-in tools are **WindowSizer** and **StartupPilot**.

- No account. No sign-in. No telemetry. No network calls.
- Everything runs locally.
- Default elevation is `asInvoker`. Tools that need administrator rights ask you to relaunch with elevation; they never silently demand it.

## Included tools

- **WindowSizer** — Resize, snap, pin, and center open windows by pixel; save size and layout presets; bind global hotkeys for the common actions.
- **StartupPilot** — Inspect and toggle everything Windows runs at sign-in: HKCU/HKLM `Run` and `RunOnce`, Startup folders (per-user and all-users), scheduled tasks with login/boot triggers, and services configured to start automatically. Tracks every change with an undo-able history and CSV export.

The shell also includes a Dashboard, Settings (theme, startup, tray, global hotkeys, data reset, log export), an About page, and a system tray icon.

## Requirements

- Windows 10 (build 1809 / 17763) or Windows 11
- For development: [.NET 9 SDK](https://dotnet.microsoft.com/) (`net9.0-windows`)

## Develop

```
cd PowerDesk
dotnet run
```

## Release publish (self-contained single-file)

```
dotnet publish PowerDesk/PowerDesk.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "../publish"
```

The output is a single `PowerDesk.exe` in `../publish/`. It does not need .NET installed on the target machine.

## Data storage

PowerDesk keeps everything under `%LocalAppData%\PowerDesk\`:

```
%LocalAppData%\PowerDesk\
  settings.json                          # shared app preferences (theme, tray, startup, global hotkeys, etc.)
  logs\powerdesk.log                     # rolling log file (2 MB cap)
  IconCache\                             # extracted icon thumbnails
  Modules\WindowSizer\settings.json      # size presets, layout presets, hotkey bindings, refresh interval
  Modules\StartupPilot\settings.json     # notes, pinned items, history, retention policy
```

All writes go through an atomic pattern: the new content is written to `<file>.tmp` and then `File.Replace`-d into place. On load, if the primary file is missing or unreadable, PowerDesk attempts to recover from the `.tmp` before falling back to defaults.

## Permissions and elevation

PowerDesk starts unelevated. Most of WindowSizer and the read-only parts of StartupPilot work without administrator rights.

Administrator is required for:

- Editing HKLM `Run`/`RunOnce` registry values
- Moving shortcuts into/out of the all-users Startup folder
- Toggling services between Automatic and Disabled
- Enabling/disabling scheduled tasks owned by other users or `SYSTEM`

If an action needs elevation, PowerDesk surfaces a clear message and offers a "Relaunch as administrator" button. It never crashes when an operation is denied.

## Architecture

PowerDesk's shell is a thin WPF host. Each feature is a self-contained **module** that implements `IPowerDeskModule` and is registered via `ModuleRegistry` at startup:

- `Core/` — shared services (storage, logging, theme, navigation, permissions, icons, tray, status, recent actions)
- `Modules/<Name>/` — a feature module with its own `Models/`, `Services/`, `ViewModels/`, `Views/`, and a single `<Name>Module.cs` entry point
- `Resources/` — themes (Dark, Light, OLED Dark) and flat modern control styles
- `Shared/` — converters and reusable UI bits
- `Views/` — Dashboard, Settings, About (shell pages)

Adding a new tool only requires a new `Modules/<Name>/<Name>Module.cs` and registering it in `App.OnStartup`.

## No telemetry / local-only

PowerDesk does not make network calls. It does not collect or transmit usage data. Logs and settings stay on your machine.
