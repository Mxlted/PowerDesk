# PowerDesk

A modular, local-first PC tool hub for Windows - inspired by Microsoft PowerToys, designed to grow one tool at a time.

Current beta release: **v0.3.0**.

PowerDesk currently includes these built-in tools:

- **[WindowSizer](https://github.com/Mxlted/WindowSizer)** - resize, snap, pin, center, and reposition windows with pixel precision. Saved size presets, layout presets, multi-monitor support, and global hotkeys.
- **[StartupPilot](https://github.com/Mxlted/StartupPilot)** - see and control everything Windows runs at sign-in: registry `Run` keys, Startup folders, scheduled tasks, and services. Startup entries show Task Manager-style enabled/disabled state, while services live in their own section with Automatic, Manual, and Disabled startup type controls. Per-source filters, impact estimates, orphan detection, change history with undo, and CSV export are included.
- **MonitorDesk** - inspect monitor bounds, work areas, primary display state, virtual desktop layout, and saved monitor layouts with editable display positions.
- **DnsDesk** - inspect adapter DNS state, switch common DNS profiles, apply IPv4 and IPv6 resolvers when the selected adapter supports them, and flush the DNS resolver cache.
- **HashDesk** - compute SHA256, SHA1, and MD5 checksums for files or text without sending anything anywhere.
- **HostProfiles** - save and apply Windows hosts-file profiles with timestamped backups.
- **ColorPicker** - pick colors from a dialog or themed screen picker, sample pixels, and copy HEX/RGB/HSL values.
- **FileLockFinder** - find processes locking a file or folder using Windows Restart Manager, drag in targets, then inspect or stop lock owners with confirmation.
- **PathEditor** - edit User or Machine PATH entries with duplicate detection, asynchronous missing-folder validation, backups, and environment-change broadcast.

PowerDesk runs entirely on your machine. No account, no sign-in, no telemetry, no network calls.

---

## Beta warning - read this first

PowerDesk is **in active beta**. Some tools directly edit Windows registry keys, Startup folders, scheduled tasks, service start types, DNS settings, the hosts file, and PATH. Used carelessly, those changes can:

- Stop drivers, vendor utilities, or input devices from loading at boot
- Disable or misconfigure services your apps or games rely on
- Break name resolution by setting bad DNS servers or hosts-file entries
- Break developer tools or app launchers by removing PATH entries
- Stop a process that still has unsaved work
- Leave your machine in a state that takes Task Manager or `services.msc` to recover from

**Do not change anything if you don't know what it is.** When in doubt, look the item up before toggling, applying, removing, or stopping it. If you're not comfortable with the consequences of changing low-level Windows settings, this tool is not for you yet.

PowerDesk includes guardrails where possible: StartupPilot has change history with undo, HostProfiles and PathEditor keep backups, admin-only actions are gated, and destructive actions ask for confirmation.

---

## Install

1. Go to the [Releases page](https://github.com/Mxlted/PowerDesk/releases).
2. Download `PowerDesk.exe` from the latest release.
3. Double-click to run. No installer, no .NET runtime needed - it's a single self-contained file.

PowerDesk starts unelevated. If you change something that needs administrator rights (HKLM registry, services, all-users startup folder, Machine PATH, hosts file, DNS settings), it'll tell you and offer to relaunch with elevation.

### Requirements

- Windows 10 (build 1809 / 17763) or Windows 11
- 64-bit (x64)

---

## What you get

- Flat, modern, dark/light/OLED-dark themed UI
- Dark/OLED-aware native title bar, including elevated windows
- Tray icon with quick actions and minimize-to-tray
- Dashboard with module status, recent activity, and health indicators
- Single-instance guard to avoid settings conflicts
- Global hotkeys for window snapping (defaults: `Ctrl+Alt+Arrow`, `Ctrl+Alt+C`, `Ctrl+Alt+M`)
- Saved monitor layouts and editable display positions
- Admin-aware utilities for startup control, DNS, hosts profiles, file locks, and PATH
- IPv4/IPv6 DNS profile handling that skips IPv6 writes when the adapter has no usable IPv6 route
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
dotnet publish PowerDesk.csproj -c Release -r win-x64 --self-contained ^
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
Modules\MonitorDesk\settings.json      saved monitor layouts
Modules\HostProfiles\settings.json     saved hosts-file profiles
Modules\HostProfiles\hosts-backup-*    hosts-file backups
Modules\PathEditor\settings.json       PATH backup history
```

No data is ever sent off your machine. To wipe everything: Settings → "Reset all data", or delete the folder above.

---

## License

PowerDesk is released under the [MIT License](LICENSE)

---

## Contributing

PowerDesk is built to grow. Each tool is a self-contained module under `PowerDesk/Modules/<Name>/` implementing a single `IPowerDeskModule` interface. New tools can be added without touching the shell.

Bug reports and ideas: open an issue.