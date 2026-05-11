# PowerDesk

A modular, local-first PC tool hub for Windows - inspired by Microsoft PowerToys, designed to grow one tool at a time.

The first release ships with two built-in tools:

- **[WindowSizer](https://github.com/Mxlted/WindowSizer)** - resize, snap, pin, center, and reposition windows with pixel precision. Saved size presets, layout presets, multi-monitor support, and global hotkeys.
- **[StartupPilot](https://github.com/Mxlted/StartupPilot)** - see and control everything Windows runs at sign-in: registry `Run` keys, Startup folders, scheduled tasks, and auto-start services. Per-source filters, impact estimates, orphan detection, change history with undo, and CSV export.

PowerDesk runs entirely on your machine. No account, no sign-in, no telemetry, no network calls.

---

## Beta warning - read this first

PowerDesk is **in active beta**. It directly edits Windows registry keys, Startup folders, scheduled tasks, and service start types. Used carelessly, those changes can:

- Stop drivers, vendor utilities, or input devices from loading at boot
- Disable services your apps or games rely on
- Leave your machine in a state that takes Task Manager or `services.msc` to recover from

**Do not disable anything if you don't know what it is.** When in doubt, look the item up before toggling it. If you're not comfortable with the consequences of changing what Windows runs at startup, this tool is not for you yet.

PowerDesk's change history (StartupPilot → History tab) lets you undo the most recent action. Use it.

---

## Install

1. Go to the [Releases page](https://github.com/Mxlted/PowerDesk/releases).
2. Download `PowerDesk.exe` from the latest release.
3. Double-click to run. No installer, no .NET runtime needed - it's a single self-contained file.

PowerDesk starts unelevated. If you toggle something that needs administrator rights (HKLM registry, services, all-users startup folder), it'll tell you and offer to relaunch with elevation.

### Requirements

- Windows 10 (build 1809 / 17763) or Windows 11
- 64-bit (x64)

---

## What you get

- Flat, modern, dark/light/OLED-dark themed UI
- Tray icon with quick actions and minimize-to-tray
- Dashboard with module status, recent activity, and health indicators
- Global hotkeys for window snapping (defaults: `Ctrl+Alt+Arrow`, `Ctrl+Alt+C`, `Ctrl+Alt+M`)
- Local data only - settings live in `%LocalAppData%\PowerDesk\`

---

## Build from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/).

```
git clone https://github.com/Mxlted/PowerDesk.git
cd PowerDesk/PowerDesk
dotnet run
```

Build a single-file self-contained release:

```
dotnet publish PowerDesk/PowerDesk.csproj -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "../publish"
```

The output is `../publish/PowerDesk.exe`.

---

## Data and privacy

Everything PowerDesk stores lives under `%LocalAppData%\PowerDesk\`:

```
settings.json                          shared app preferences
logs\powerdesk.log                     rolling log (2 MB cap)
Modules\WindowSizer\settings.json      size presets, layout presets, hotkeys
Modules\StartupPilot\settings.json     notes, pinned items, history
```

No data is ever sent off your machine. To wipe everything: Settings → "Reset all data", or delete the folder above.

---

## License

PowerDesk is released under the [MIT License](LICENSE). Use it, fork it, modify it, ship it - just keep the copyright notice.

---

## Contributing

PowerDesk is built to grow. Each tool is a self-contained module under `PowerDesk/Modules/<Name>/` implementing a single `IPowerDeskModule` interface. New tools can be added without touching the shell.

Bug reports and ideas: open an issue.
