using System.Windows.Threading;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;
using Clicky.Core.Hotkeys;
using Clicky.Core.Assistant;
using Clicky.Core.Settings;
using Clicky.Core.Talk;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// What each hotkey does. Talk opens the mic the moment its keys go down (Pending), which also
/// stops Clicky if it is speaking or still answering (barge-in), clears its marks and gets Claude
/// ready; it shows the buddy listening and takes a screenshot once it arms, and on release turns
/// the speech into text and asks Claude with the screenshot (<see cref="TalkAnswerer"/>). OpenHome
/// opens Home. The rest are logged until their milestones (text mode M10). Dictation is
/// DictationCoordinator's.
/// </summary>
/// <remarks>Events arrive on the hotkey pump thread, one at a time; nothing here blocks it or the UI thread.</remarks>
internal sealed class HotkeyCoordinator(
    IHotkeyService hotkeys,
    ICaptureService capture,
    VoiceInput voice,
    VoiceOutput speech,
    TalkLoop talkLoop,
    TalkAnswerer answerer,
    IBuddyPresence presence,
    ISettingsStore settings,
    IAppShell shell,
    IUserNotifier notifier,
    Dispatcher dispatcher,
    ILogger<HotkeyCoordinator> logger) : IHostedService, IDisposable
{
    /// <summary>
    /// How long a transcript waits for its screenshot (normally ready ~150 ms after Talk arms, long
    /// before speech is text) before asking without one.
    /// </summary>
    private static readonly TimeSpan CaptureWait = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stopping = new();

    // Hotkey thread only.
    private VoiceSession? _talk;
    private Task<CaptureResult?> _capture = Task.FromResult<CaptureResult?>(null);
    private PixelPoint? _cursor;

    // Also read on the capture thread (level) and by completions on the thread pool.
    private volatile bool _talkArmed;
    private int _talkGeneration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The capture path warms up in StartupWarmUp, one GPU user at a time.
        hotkeys.Triggered += OnHotkey;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        hotkeys.Triggered -= OnHotkey;
        await _stopping.CancelAsync();
        _talk?.Cancel();
        talkLoop.Cancel();
    }

    public void Dispose() => _stopping.Dispose();

    private void OnHotkey(object? sender, HotkeyEvent e)
    {
        switch (e.Action, e.Phase)
        {
            case (HotkeyAction.Talk, HotkeyPhase.Pending):
                if (speech.Interrupt())
                {
                    logger.LogInformation("Talk: barge-in, stopped speaking");
                }

                // Also stops an answer still streaming, clears the marks and starts Claude warming up.
                talkLoop.Begin();
                StartListening();
                break;

            case (HotkeyAction.Talk, HotkeyPhase.Started):
                Interlocked.Increment(ref _talkGeneration);
                _talkArmed = true;
                StartListening();
                presence.SetCaption(null);
                presence.SetState(BuddyState.Listening);

                // A reply follows; opening the speaker takes 250 ms or more, so start now.
                speech.Prepare();

                // The pointer where the user was looking when they pressed, not where it is when the capture runs.
                _cursor = CursorReader.TryGet(out var position, out _) ? position : null;
                _capture = CaptureForTalkAsync(new CaptureTarget(settings.Current.Capture.Mode, _cursor));
                break;

            case (HotkeyAction.Talk, HotkeyPhase.Cancelled):
                _talk?.Cancel();
                _talk = null;
                if (_talkArmed)
                {
                    _talkArmed = false;
                    presence.SetLevel(0);
                    presence.SetState(BuddyState.Idle);
                }

                break;

            case (HotkeyAction.Talk, HotkeyPhase.Ended):
                var session = _talk;
                _talk = null;
                _talkArmed = false;
                presence.SetLevel(0);
                if (session is null)
                {
                    presence.SetState(BuddyState.Idle);
                    break;
                }

                presence.SetState(BuddyState.Thinking);
                _ = FinishTalkAsync(session, Volatile.Read(ref _talkGeneration), _capture, _cursor);
                break;

            case (HotkeyAction.OpenHome, HotkeyPhase.Fired):
                dispatcher.InvokeAsync(shell.ShowHome);
                break;

            case (HotkeyAction.Dictation or HotkeyAction.DictationHandsFree, _):
                // DictationCoordinator.
                break;

            case (_, HotkeyPhase.Pending or HotkeyPhase.Cancelled):
                // Only Talk does work before a hold arms; the others are logged once they start.
                break;

            default:
                logger.LogInformation("Hotkey {Action} {Phase} (not wired up yet)", e.Action, e.Phase);
                break;
        }
    }

    private void StartListening()
    {
        if (_talk is not null)
        {
            return;
        }

        var session = voice.Start();
        session.LevelChanged += level =>
        {
            if (_talkArmed)
            {
                presence.SetLevel(level);
            }
        };
        _talk = session;
    }

    private async Task FinishTalkAsync(VoiceSession session, int generation, Task<CaptureResult?> screenshot, PixelPoint? cursor)
    {
        VoiceResult result;
        try
        {
            result = await session.CompleteAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            session.Dispose();
        }

        // A newer Talk owns the buddy now; this one's result only goes to the log.
        if (generation != Volatile.Read(ref _talkGeneration) || _talkArmed)
        {
            return;
        }

        switch (result.Outcome)
        {
            case VoiceOutcome.Transcribed:
                var shot = await WaitForCaptureAsync(screenshot).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _talkGeneration) || _talkArmed)
                {
                    return;
                }

                await answerer.AskAsync(new TalkQuestion(result.Text, RequestSource.Voice, shot, cursor), _stopping.Token).ConfigureAwait(false);
                break;

            case VoiceOutcome.Failed:
                presence.SetState(BuddyState.Idle);
                var problem = result.Problem ?? "Something went wrong with speech input.";
                answerer.ShowCaption(problem, TalkAnswerer.ProblemCaptionTime);
                notifier.Notify(result.MicrophoneProblem is null ? "Clicky couldn't transcribe that" : "Clicky can't hear you", problem, warning: true);
                break;

            default:
                presence.SetState(BuddyState.Idle);
                break;
        }
    }

    private async Task<CaptureResult?> WaitForCaptureAsync(Task<CaptureResult?> screenshot)
    {
        try
        {
            return await screenshot.WaitAsync(CaptureWait, _stopping.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Talk capture took over {Seconds} s; asking without a screenshot", CaptureWait.TotalSeconds);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Never throws: without a screenshot, Talk still answers what it can.</summary>
    private async Task<CaptureResult?> CaptureForTalkAsync(CaptureTarget target)
    {
        try
        {
            var result = await capture.CaptureAsync(target, _stopping.Token).ConfigureAwait(false);
            var geometry = result.Geometry;
            var source = result.Monitor is { } monitor ? $"{monitor.DeviceName} scale {monitor.Scale:0.##}" : $"{target.Mode} {geometry.Source.Width}x{geometry.Source.Height}";

            if (target.CursorPosition is { } cursor)
            {
                var shot = CoordinateMapper.ToScreenshot(cursor, geometry);
                var back = CoordinateMapper.ToPhysical(shot, geometry);
                logger.LogInformation(
                    "Talk capture {Width}x{Height} from {Source} in {Ms:0} ms; cursor ({X},{Y}) -> ({U:0.0},{V:0.0}) -> ({BackX},{BackY})",
                    geometry.ImageWidth,
                    geometry.ImageHeight,
                    source,
                    result.Elapsed.TotalMilliseconds,
                    cursor.X,
                    cursor.Y,
                    shot.X,
                    shot.Y,
                    back.X,
                    back.Y);
            }
            else
            {
                logger.LogInformation("Talk capture {Width}x{Height} from {Source} in {Ms:0} ms", geometry.ImageWidth, geometry.ImageHeight, source, result.Elapsed.TotalMilliseconds);
            }

            return result;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Talk capture failed; asking without a screenshot");
            return null;
        }
    }
}
