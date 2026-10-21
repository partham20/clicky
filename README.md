# Clicky for Windows

An AI cursor buddy for Windows. It looks at your screen when you press a hotkey, talks back, points and draws, teaches, dictates, and runs computer-use agents and MCP-connected apps.

## Layout

| Project | Target | Role |
|---|---|---|
| `src/Clicky.App` | `net10.0-windows10.0.19041.0`, WPF | Composition root: Generic Host, tray, windows |
| `src/Clicky.Core` | `net10.0` | Settings, coordinate mapping, buddy state, hotkey gesture engine, audio conversion and speech gate, OpenAI speech-to-text, and later prompts, action schema, backends, agent loop. No Windows APIs |
| `src/Clicky.Platform.Windows` | `net10.0-windows10.0.19041.0` | Single instance, start with Windows, per-monitor overlays, monitor and cursor info, keyboard hook, screen capture, microphone, Whisper, Credential Manager, and later input, UIA |
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
- `voice`: `engine` (`Local` or `Cloud`), `language` (`en`, `hi`, … or `auto`), `localModel` (`large-v3-turbo-q5_0`, used on the GPU), `cpuModel` (`small.en-q5_1`), `useGpu` (true; applies on restart), `cloudModel` (`gpt-4o-mini-transcribe`), `inputDevice` (empty follows the Windows default), `minSpeechMs` (300), `maxRecordingSeconds` (120). Also in Settings › Voice.
- `debug.saveCaptures`: also write every screenshot to `debug\captures\yyyyMMdd-HHmmss.png`, with a red crosshair where the pointer maps to. Off by default; nothing else writes screenshots to disk.
- `debug.saveAudio`: also write every Talk recording to `debugudio\yyyyMMdd-HHmmss.wav` (16 kHz mono). Off by default; nothing else writes audio to disk.
- Speech models live in `%LocalAppData%\Clicky\models` (not roaming), downloaded on first use and checked against their SHA-256.
- The OpenAI key for cloud speech-to-text is in Windows Credential Manager as `Clicky/openai-api-key` (Settings › Voice, or the `OPENAI_API_KEY` environment variable for development), never in settings.json.
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

Only Talk and Open Home do something yet; the others are logged until their milestones. Talk transcribes what you say (M3) and shows it beside the buddy; sending it to Claude comes in M6.

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

## Voice input (M3)

Hold Talk, speak, release: the buddy listens (its disc follows your voice), thinks while the speech is transcribed, and shows what it heard in a bubble beside it.

- **Mic opens on key-down.** The gesture engine announces a hold as `Pending` the moment its keys are down, 200 ms before it arms, and the mic opens then, so the first word is never clipped. A Pending that never arms (Ctrl+Alt+A, a quick tap) closes the mic and throws the audio away. Between Talks the mic is closed, so the Windows mic-in-use icon only shows while you hold.
- **Capture.** WASAPI shared mode in the device's own mix format (NAudio 3 `WasapiRecorder`, 50 ms buffers), converted in managed code to 16 kHz mono with a band-limited resampler. Each Talk opens the endpoint fresh, so plugging in a headset applies to the next Talk.
- **Speech gate.** An energy gate measures the recording against its own noise floor, needs at least `minSpeechMs` of speech, and trims silence to 250 ms either side. Silence never reaches Whisper, which would turn it into "Thank you." Whisper's subtitle habits (`[BLANK_AUDIO]`, `(music)`, "Thanks for watching!") are removed; short fillers such as "Thank you." are only dropped when there was barely any speech.
- **Local engine.** Whisper.net (whisper.cpp) through Vulkan, on the first discrete GPU (on a laptop Vulkan lists the integrated one first), with flash attention: `large-v3-turbo-q5_0` takes about 0.55 s for a 1.5-6 s question on an RTX A1000. Without a usable GPU it runs `small.en-q5_1` on the CPU with the audio context cut to the clip, about 0.4-0.6 s on a 14-core i7. The model loads and warms up at startup; the very first run on a machine also compiles GPU pipelines, which can take 20-45 s once.
- **Cloud engine.** OpenAI's transcription endpoint, 16-bit WAV upload, 20 s timeout. The Claude API has no speech-to-text, so this is a second provider.
- **Problems.** Mic blocked in Windows privacy settings, no mic, a mic unplugged mid-sentence (what was heard is still transcribed), a model still downloading, or a bad API key: each shows in the bubble, as a tray notification (at most once per 10 minutes per message) and in the log.
- **Bluetooth headsets** switch to their hands-free profile while the mic is open, so their audio drops to call quality during Talk. That is Windows; the laptop's own mic avoids it.
