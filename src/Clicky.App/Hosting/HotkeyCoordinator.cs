using System.Windows.Threading;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;
using Clicky.Core.Hotkeys;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// What each hotkey does. Talk opens the mic the moment its keys go down (Pending), which also
/// stops Clicky if it is speaking (barge-in); it shows the buddy listening and takes a screenshot
/// once it arms, and on release turns the speech into text. Until M6 sends that to Claude, Clicky
/// says back what it heard. OpenHome opens Home. The rest are logged until their milestones (text
/// mode M10, dictation M12).
/// </summary>
/// <remarks>Events arrive on the hotkey pump thread, one at a time; nothing here blocks it or the UI thread.</remarks>
internal sealed class HotkeyCoordinator(
    IHotkeyService hotkeys,
    ICaptureService capture,
    VoiceInput voice,
    VoiceOutput speech,
    ReplyPresenter replies,
    IBuddyPresence presence,
    ISettingsStore settings,
    IAppShell shell,
    IUserNotifier notifier,
    Dispatcher dispatcher,
    ILogger<HotkeyCoordinator> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan ProblemCaptionTime = TimeSpan.FromSeconds(8);

    private readonly CancellationTokenSource _stopping = new();

    // Hotkey thread only.
    private VoiceSession? _talk;

    // Also read on the capture thread (level) and by completions on the thread pool.
    private volatile bool _talkArmed;
    private int _talkGeneration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        hotkeys.Triggered += OnHotkey;

        // Device creation and codec loading cost ~1 s once; pay it now rather than on the first Talk.
        _ = Task.Run(() => capture.WarmUpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        hotkeys.Triggered -= OnHotkey;
        await _stopping.CancelAsync();
        _talk?.Cancel();
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
                PixelPoint? cursor = CursorReader.TryGet(out var position, out _) ? position : null;
                _ = CaptureForTalkAsync(new CaptureTarget(settings.Current.Capture.Mode, cursor));
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
                _ = FinishTalkAsync(session, Volatile.Read(ref _talkGeneration));
                break;

            case (HotkeyAction.OpenHome, HotkeyPhase.Fired):
                dispatcher.InvokeAsync(shell.ShowHome);
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

    private async Task FinishTalkAsync(VoiceSession session, int generation)
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
                // M6 sends this, with the screenshot, to Claude. Until then Clicky says back what it heard.
                replies.Speak($"You said: {result.Text}");
                break;

            case VoiceOutcome.Failed:
                presence.SetState(BuddyState.Idle);
                var problem = result.Problem ?? "Something went wrong with speech input.";
                ShowCaption(problem, ProblemCaptionTime);
                notifier.Notify(result.MicrophoneProblem is null ? "Clicky couldn't transcribe that" : "Clicky can't hear you", problem, warning: true);
                break;

            default:
                presence.SetState(BuddyState.Idle);
                break;
        }
    }

    private void ShowCaption(string text, TimeSpan duration)
    {
        presence.SetCaption(text);
        _ = ClearCaptionAsync(presence.Caption, duration);
    }

    private async Task ClearCaptionAsync(string? caption, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (caption is not null && string.Equals(presence.Caption, caption, StringComparison.Ordinal))
        {
            presence.SetCaption(null);
        }
    }

    private async Task CaptureForTalkAsync(CaptureTarget target)
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
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (CaptureException ex)
        {
            logger.LogWarning(ex, "Talk capture failed");
        }
    }
}
