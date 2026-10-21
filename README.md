# Clicky for Windows

An AI cursor buddy for Windows. It looks at your screen when you press a hotkey, talks back, points and draws, teaches, dictates, and runs computer-use agents and MCP-connected apps.

## Layout

| Project | Target | Role |
|---|---|---|
| `src/Clicky.App` | `net10.0-windows10.0.19041.0`, WPF | Composition root: Generic Host, tray, windows |
| `src/Clicky.Core` | `net10.0` | Settings, coordinate mapping, buddy state, hotkey gesture engine, and later prompts, action schema, backends, agent loop. No Windows APIs |
| `src/Clicky.Platform.Windows` | `net10.0-windows10.0.19041.0` | Single instance, start with Windows, per-monitor overlays, monitor and cursor info, keyboard hook, screen capture, and later input, UIA |
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
- `overlay.hideFloatingCursor` (also in the tray menu): the buddy stops following the pointer and docks to Home. `overlay.followSmoothingMs` (default 40, 0 to 300) sets how loosely it trails the pointer; 0 pins it.
- `hotkeys.bindings`: one entry per action, e.g. `{ "action": "Talk", "gesture": "Ctrl+Alt", "enabled": true }`. Also editable in Settings. A binding that can't be read is logged and skipped; the rest keep working.
- `capture`: `mode` (`MonitorUnderCursor`, `ForegroundWindow`, `AllMonitors`), `maxLongEdge` (1568), `maxPixels` (1150000), `includeCursor` (false).
- `debug.saveCaptures`: also write every screenshot to `debug\captures\yyyyMMdd-HHmmss.png`, with a red crosshair where the pointer maps to. Off by default; nothing else writes screenshots to disk.
- `logs\clicky-YYYYMMDD.log`: daily rolling, 7 days kept, 20 MB cap per file. Level comes from `logging.minimumLevel`.

## Behaviour notes

- One instance per user per logon session (`Local\` mutex). A second launch asks the running copy to open Home and exits with code 0.
- Start with Windows writes `HKCU\...\Run`. If you turn Clicky off in Task Manager's Startup tab, Clicky respects that and switches its own setting off. Under a Velopack install the entry points at the root launcher stub, so updates don't break it.
- The cursor buddy lives on one transparent overlay per monitor. The overlays are click-through, never take focus, stay out of Alt+Tab and the taskbar, and are excluded from screen capture. They are rebuilt when monitors are added, removed, moved or rescaled.
- While the pointer is still, the buddy costs a ~30 Hz cursor poll and nothing else. Per-frame work runs only while it is moving or animating a state.

## Hotkeys and capture (M2)

| Action | Default | Notes |
|---|---|---|
| Talk | Hold Ctrl+Alt | Buddy listens and a screenshot is taken while held |
| Type a question | Double-tap Ctrl | |
| Dictation | Hold Ctrl+Win | |
| Hands-free dictation | Double-tap Ctrl+Win | |
| Open Home | Ctrl+Alt+A | |
| Always-on voice | Triple-tap Ctrl | Off by default |

Only Talk and Open Home do something yet; the others are logged until their milestones.

Gesture text in settings: modifiers alone are a hold (`Ctrl+Alt`), modifiers plus a key are a press (`Ctrl+Alt+A`), and `double`/`triple` prefixes are taps (`double Ctrl`). `hold Ctrl+Space` holds a key. A modifier-only hold needs two modifiers, and a press needs a modifier unless the key is F13-F24, so a binding can't take over normal typing.

How it works: a `WH_KEYBOARD_LL` hook on its own thread feeds `GestureEngine` (Core, unit tested on a fake clock). The callback only translates the event and asks the engine; events leave through a channel, so nothing slow ever runs inside the hook.

- **Timing.** A modifier-only hold arms after 200 ms with exactly its modifiers down and no other key pressed. A tap is a press-and-release within 250 ms with nothing else in between; taps count up while each starts within 300 ms of the last release. Any other key cancels: Ctrl+Alt+A opens Home and never starts Talk, and Ctrl+C is never a tap.
- **Triple-tap latency.** When a chord has both a double and a triple binding, the double has to wait out the 300 ms window to rule out a third tap. With always-on voice off (the default), double-tap Ctrl fires the moment you release the second tap.
- **AltGr.** On layouts with AltGr, Windows sends a fake left Ctrl (scan code 0x21D) before right Alt. The engine ignores it, so AltGr is plain Alt to us and typing `@` or `€` never looks like Ctrl+Alt.
- **Start menu.** Releasing Win after Ctrl+Win would open Start. When a Win gesture of ours owns the release, the hook swallows the Win key-up and injects VK 0xFF (unassigned) down/up followed by the Win key-up in one `SendInput`, so the shell sees "another key was pressed" and stays closed. Win on its own and Win shortcuts like Win+D are left alone.
- **Injected input is ignored.** Keys from `SendInput` (remote tools, AutoHotkey, on-screen keyboards, our own mask key) never trigger or block a gesture.
- **Elevated windows (UIPI).** While an administrator window has focus, Windows sends its keystrokes to no lower-integrity hook, so hotkeys do nothing there. The log says so once per window. Key-ups can also go missing when focus moves to such a window or to the secure desktop. While a hold is active, the held keys are checked against `GetAsyncKeyState` every 100 ms, and again on every focus change, so a hold can't get stuck. Session lock/unlock resets everything.
- **Recording.** The shortcut box in Settings records through the same hook, which pauses all hotkeys and swallows keys only while a Clicky window has focus, so Win can be recorded without opening Start.

Capture uses Windows.Graphics.Capture from the 19041 projection (no extra packages): one session per screenshot, first frame only, then closed. The D3D device and codecs are created at startup off the UI thread; after that a monitor capture plus PNG encode takes about 120-150 ms.

- **Size caps.** Images are downscaled (Fant) to fit both `maxLongEdge` 1568 and `maxPixels` 1.15 MP, never upscaled. A 4K monitor becomes 1429x804.
- **Coordinates.** `ScreenshotGeometry` records the captured rectangle in physical pixels and the image size. `CoordinateMapper.ToScreenshot` and `ToPhysical` convert between them. A physical pixel survives the round trip exactly (tested for every pixel at 100-175% layouts with negative origins and odd sizes). An integer screenshot pixel maps to the centre of the screen area it was shrunk from.
- **Overlays.** The buddy's overlays use `WDA_EXCLUDEFROMCAPTURE`, so the buddy is never in a screenshot.
- **Border.** On Windows 11, Clicky asks for borderless capture, so no yellow frame flashes. Windows 10 always shows it briefly.
- **HDR.** Frames are BGRA8; on an HDR monitor Windows tone-maps them to SDR, which can look washed out. The log records the capture format.
