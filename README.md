# Clicky for Windows

An AI cursor buddy for Windows. It looks at your screen when you press a hotkey, talks back, points and draws, teaches, dictates, and runs computer-use agents and MCP-connected apps.

## Layout

| Project | Target | Role |
|---|---|---|
| `src/Clicky.App` | `net10.0-windows10.0.19041.0`, WPF | Composition root: Generic Host, tray, windows |
| `src/Clicky.Core` | `net10.0` | Settings, coordinate mapping, buddy state, hotkey gesture engine, audio conversion and speech gate, OpenAI speech-to-text, sentence streaming and the speech pipeline, OpenAI text-to-speech, and later prompts, action schema, backends, agent loop. No Windows APIs |
| `src/Clicky.Platform.Windows` | `net10.0-windows10.0.19041.0` | Single instance, start with Windows, per-monitor overlays, monitor and cursor info, keyboard hook, screen capture, microphone, Whisper, Credential Manager, speakers, Windows voices, call/share/Focus Assist detection, and later input, UIA |
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
- `speech`: `quietMode` (false; also in the tray menu), `engine` (`Windows` or `Cloud`), `windowsVoice` (empty uses the Windows default voice), `cloudModel` (`gpt-4o-mini-tts`), `cloudVoice` (`coral`), `rate` (1.0, 0.5 to 2), `volume` (1.0, on top of the Windows volume), `outputDevice` (empty follows the Windows default), `quietInCalls`, `quietWhenSharing`, `quietInFocusAssist` (all true), `ignoreMicApps` (apps that hold the mic without being in a call, e.g. `"NVIDIA Broadcast.exe"`). Also in Settings › Speech.
- `debug.saveAudio`: also write every Talk recording to `debug\audio\yyyyMMdd-HHmmss.wav` (16 kHz mono). Off by default; nothing else writes audio to disk.
- `memory\PROFILE.md` and `memory\VOLATILE.md`: what Clicky remembers about you (see Memory below). `memory`: `enabled` (true), `autoUpdate` (true), `idleMinutes` (3). Also in Settings › Memory.
- Speech models live in `%LocalAppData%\Clicky\models` (not roaming), downloaded on first use and checked against their SHA-256.
- The OpenAI key for cloud speech-to-text is in Windows Credential Manager as `Clicky/openai-api-key` (Settings › Voice, or the `OPENAI_API_KEY` environment variable for development), never in settings.json.
- `logs\clicky-YYYYMMDD.log`: daily rolling, 7 days kept, 20 MB cap per file. Level comes from `logging.minimumLevel`.

## Behaviour notes

- One instance per user per logon session (`Local\` mutex). A second launch asks the running copy to open Home (Settings with `--settings`) and exits with code 0.
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

Only Talk and Open Home do something yet; the others are logged until their milestones. Talk transcribes what you say (M3) and, until M6 sends it to Claude, says back what it heard (M4).

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

## Voice output (M4)

Replies are spoken sentence by sentence as their text arrives: the buddy shows Speaking, its glow follows the voice, and the sentence being said is its caption. Press Talk while it speaks and it stops (barge-in). Until M6 connects Claude, Talk says back what it heard, and Settings › Speech has a Test voice button.

- **Streaming.** Text is cut into sentences as it streams in (`SentenceSplitter`): at ". ", "! ", "? " and line breaks, but not at "3.14", "v1.2", "docs.claude.com", "e.g.", "Dr.", initials or "1." list numbers. Markdown, links, code blocks, table pipes and emoji are stripped before speaking (`SpeakableText`). Each sentence is synthesized while the one before it plays and queued behind it with a 180 ms pause, so there are no gaps. The look-ahead stops at about two sentences, so barge-in wastes little.
- **Windows voices** (default): WinRT `SpeechSynthesizer` with the voices installed in Windows, 15-30 ms a sentence once loaded at startup. Windows does not offer its newer natural Narrator voices to apps, so on most PCs these are the classic voices (David, Zira, Mark, Heera, Ravi, …). **Cloud**: OpenAI `gpt-4o-mini-tts`, natural voices, using the same Credential Manager key as cloud speech-to-text; it sends the reply text to OpenAI. The Claude API has no text-to-speech.
- **Speaker.** WASAPI shared mode in the device's own format, tagged as speech, opened per reply so new headphones apply to the next one. Opening it takes 250 ms to 1 s on Realtek drivers, so it is opened when Talk starts and kept for 15 s if no reply comes. Measured on an i7-13800H laptop: first sound about 30 ms after the first sentence is complete (about 180 ms at the speaker), and barge-in silences the voice within 45-60 ms, with a 15 ms fade so it doesn't click. A device unplugged mid-reply ends that reply with one log line and shows it as text.
- **Staying quiet.** Before each reply Clicky checks, in about 10 ms, whether to stay quiet. If so, the reply shows as a card by the pointer with a Copy button. The card doesn't take focus, stays out of screenshots and screen shares, and goes away after 8-45 s depending on length (not while the mouse is over it). The reasons, each logged:
  - Quiet mode is on (tray menu or Settings).
  - The speakers are muted, at zero, or missing (always).
  - **A call.** Windows has no "in a call" API. Clicky reads the microphone-in-use list Windows keeps for its privacy icon: another app holding the mic open almost always means a call (Teams, Zoom, Meet in a browser, Discord and Slack keep it open for the whole call, even when muted). An app that holds the mic for other reasons can go in `speech.ignoreMicApps`.
  - **Screen sharing.** There is no API at all. Most shares happen during a call, which already counts. On top of that, the sharing toolbars of Teams, Zoom, Chrome/Edge ("… is sharing your screen"), Firefox and Slack are recognized by their window titles. Anything else goes undetected; Quiet mode is the manual switch.
  - **Focus Assist / Do not disturb, presentations, full-screen apps and games.** `SHQueryUserNotificationState` (documented) for presentation mode, full screen and quiet time, plus the shell's own Focus Assist state (undocumented WNF) and Windows 11's Do not disturb setting. Each check fails open, to speaking, if Windows changes underneath it.
- **Overlays are not full-screen windows.** The shell treats a topmost window the size of the monitor as a full-screen app, so before M4 the buddy's overlays switched on Windows' automatic Focus Assist and reported the user as busy to every app. They now carry the shell's `NonRudeHWND` opt-out.
- **settings.json** saved as "UTF-8 with BOM" (Notepad and PowerShell can do that) used to count as corrupt; it now loads.

## Memory (M11)

Two Markdown files in `%AppData%\Clicky\memory`, both plain text you can edit in Settings › Memory or any editor:

- `PROFILE.md`: who you are, how you want answers ("How to answer"), habits and tools. Long-lived.
- `VOLATILE.md`: what you're working on now and open threads. Expected to churn.

How it flows (`Clicky.Core/Memory`, no Windows APIs):

- **Into prompts.** `IMemoryService.GetPromptAsync()` returns a `<memory>` block for the system prompt of voice, dictation and agent requests. Files are read per request and cached by timestamp and size, so edits from another editor apply on the next answer. Sections are never cut: if hand edits push memory past the prompt budget (9,500 characters), whole sections drop out and the block says how many. `VOLATILE.md` older than 14 days is marked as possibly out of date.
- **Updater pass.** `RecordTurn()` queues each exchange. After `idleMinutes` of quiet (or `EndConversationAsync()`), one request to the fast model (`IMemoryModel`) gets the queued turns, both files and the room left, and replies with JSON edits (`add`, `replace`, `remove`). A turn that sounds like feedback or a preference ("you talk too much", "from now on…", "remember that…", "forget…") runs the pass right away, so the next answer already follows it. A failed call keeps the turns (up to 40) for the next pass.
- **Merge rules** (`MemoryMerger`). Entries match ignoring case, spacing and end punctuation; an add that already exists is a no-op; replace/remove need the exact old entry, never a guess. Nothing is ever shortened: an entry over 280 characters, or an edit that would push `PROFILE.md` past 6,000 or `VOLATILE.md` past 3,000 characters, is refused whole (the model is told the room left so it consolidates instead). Anything that looks like a key, token or password is refused. At most 12 edits per pass. The merge runs against the files as they are on disk at that moment, so a hand edit made meanwhile is kept.
- **Undo and forget.** Settings › Memory can undo Clicky's last automatic change (this session, and only while the file hasn't changed since), forget one file or everything (resets to the empty template), and turn memory or automatic updates off.
- Writes are atomic (`.tmp` then move). Missing files are created from the templates.

Until a Claude backend registers `IMemoryModel`, memory is read into prompts but not updated automatically.

## Connectors (M16)

Settings › Connectors lets Claude use other apps through MCP servers, with the official C# MCP SDK (`ModelContextProtocol.Core`, client only). A **local program** is started by Clicky and spoken to over stdin/stdout (`npx`, `uvx`, `docker`, a full path). A **remote server** is reached over streamable HTTP, falling back to the older HTTP+SSE transport. Paste an `mcpServers` block from a README, or import `%AppData%\Claude\claude_desktop_config.json`.

- **Live.** Connectors live in `settings.connectors` and apply as soon as you save, hand edits included. Each enabled account is its own connection. A connection starts in the background at launch, lists its tools, follows `tools/list_changed`, and comes back on its own when the process exits or the network drops, after 1, 2, 5, 10, 30, then every 60 s. A server that never connects (wrong command) stops after 3 tries and shows why, with the last lines of its stderr. Retry tries again.
- **Tools for Claude.** `IConnectorTools` (Core) gives the backends and agent loop the tools of everything connected, named `<connector>__<tool>` (`<connector>-<account>__<tool>` when several accounts are on), always valid Messages API names of up to 64 characters. Each tool carries its JSON schema and the server's read-only/destructive hints. `NeedsConfirmation` is true for anything not declared read-only, and the permission gate runs on it. Calls never throw for a server problem; they come back as error results Claude can read. Results over 100,000 characters are cut. Tools can be switched off one by one.
- **Secrets.** API keys, secret environment values (`GITHUB_PERSONAL_ACCESS_TOKEN`, …) and OAuth tokens go into Credential Manager as `Clicky/mcp/<connector>/<account>/…`, never into settings.json. An imported server's `env` values are treated as secrets. One credential holds 1,280 characters and OAuth tokens are often longer, so values are split across `…#2`, `…#3` entries. Removing a connector or account deletes its entries.
- **OAuth.** MCP authorization: discovery from the server's 401 (protected-resource and authorization-server metadata), dynamic client registration, PKCE, and the RFC 8252 loopback redirect `http://127.0.0.1:<port>/callback` on a plain socket (no http.sys URL reservation, no firewall prompt). Background connects never open a browser. A server that needs a sign-in waits with a Sign in button, which opens the default browser and finishes when it redirects back (5-minute limit). Refresh tokens survive restarts, so you sign in once per account. Only `https:` authorization pages, or `http:` on loopback, are ever opened. For servers without dynamic registration, Advanced sign-in takes a client id and secret and a fixed redirect port.
- **Accounts.** A connector with credentials can have several accounts (Work, Personal), each with its own keys or sign-in. Tool descriptions say which account they act on.
- **Windows.** `npx` and other `.cmd` shims run through `cmd.exe` (the SDK does that). PATH is refreshed from the registry for each server, so Node or Python installed after Clicky started is found. Stdio servers get your profile folder as their working directory unless one is set. They are stopped (whole process tree) when Clicky quits; one Clicky crash can leave them running until they notice stdin closed.
- **Testing without Node.** `tests/Clicky.TestMcpServer` is a small stdio server (`echo`, `add`, `env`, `crash`, `grow`): command `dotnet`, arguments `<path>\Clicky.TestMcpServer.dll`.
