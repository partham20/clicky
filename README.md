# Clicky for Windows

An AI cursor buddy for Windows. It looks at your screen when you press a hotkey, talks back, points and draws, teaches, dictates, and runs computer-use agents and MCP-connected apps.

## Layout

| Project | Target | Role |
|---|---|---|
| `src/Clicky.App` | `net10.0-windows10.0.19041.0`, WPF | Composition root: Generic Host, tray, windows |
| `src/Clicky.Core` | `net10.0` | Settings, and later prompts, action schema, backends, agent loop. No Windows APIs |
| `src/Clicky.Platform.Windows` | `net10.0-windows10.0.19041.0` | Single instance, start with Windows, and later hotkeys, capture, overlay, input, UIA |
| `tests/Clicky.Tests` | `net10.0` | xUnit |

Windows 10 2004 (build 19041) is the floor because overlays need `WDA_EXCLUDEFROMCAPTURE`.

## Build

```powershell
dotnet build Clicky.sln
dotnet test Clicky.sln
dotnet run --project src/Clicky.App
```

Package versions are pinned centrally in `Directory.Packages.props`. Warnings are errors.

## Data

Everything lives in `%AppData%\Clicky` (override with the `CLICKY_DATA_DIR` environment variable):

- `settings.json`: hand-editable (comments and trailing commas allowed). Edits apply live. A file that is corrupt at startup is moved to `settings.json.<timestamp>.bad` and defaults are written. An invalid edit while running is ignored and the last good settings stay active.
- `logs\clicky-YYYYMMDD.log`: daily rolling, 7 days kept, 20 MB cap per file. Level comes from `logging.minimumLevel`.

## Behaviour notes

- One instance per user per logon session (`Local\` mutex). A second launch asks the running copy to open Home and exits with code 0.
- Start with Windows writes `HKCU\...\Run`. If you turn Clicky off in Task Manager's Startup tab, Clicky respects that and switches its own setting off. Under a Velopack install the entry points at the root launcher stub, so updates don't break it.
