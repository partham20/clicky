using System.Threading.Channels;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using Clicky.App.Buddy;
using Clicky.Core.Assistant;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Dictation;
using Clicky.Core.Hotkeys;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Dictation;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Dictation: hold Ctrl+Win and speak, and the text goes into the focused app on release; or
/// double-tap Ctrl+Win to dictate hands-free, with each sentence typed as it is recognised, until
/// the double-tap (or a hold) comes again.
/// </summary>
/// <remarks>
/// <para>The mic opens on the hold's Pending, so the first word is never lost. A hold that is
/// interrupted after it armed (another key, a session lock) keeps what was said: it is shown in a
/// card with a Copy button instead of being typed into whatever has focus by then. Everything said
/// is in the dictation history either way.</para>
/// <para>Insertions run one at a time, in order, on their own loop. After each one, the field is
/// watched for hand corrections to learn (<see cref="CorrectionWatcher"/>).</para>
/// <para>Hotkey events arrive one at a time on the hotkey pump thread; nothing here blocks it.</para>
/// </remarks>
internal sealed class DictationCoordinator(
    IHotkeyService hotkeys,
    DictationInput dictation,
    ITextInserter inserter,
    CorrectionWatcher corrections,
    DictationHistory history,
    ScreenDrafter drafter,
    ICaptureService capture,
    VoiceOutput speech,
    IBuddyPresence presence,
    ISettingsStore settings,
    IUserNotifier notifier,
    Dispatcher dispatcher,
    ILogger<DictationCoordinator> logger) : IHostedService, IDisposable
{
    private const string ListeningCaption = "Dictating…";
    private const string HandsFreeCaption = "Dictating hands-free. Double-tap Ctrl+Win to stop.";
    private const string DraftingCaption = "Writing a reply…";
    private const int PreviewChars = 90;
    private static readonly TimeSpan ProblemCaptionTime = TimeSpan.FromSeconds(8);

    private readonly CancellationTokenSource _stopping = new();
    private readonly Channel<InsertJob> _inserts = Channel.CreateUnbounded<InsertJob>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _insertLoop;

    // Hotkey thread only (and LimitReached, which posts back through the same paths under _gate).
    private readonly Lock _gate = new();
    private DictationSession? _session;
    private bool _armed;
    private int _generation;

    // Hands-free sessions whose first words asked for a draft: their pieces are not typed.
    private readonly ConditionalWeakTable<DictationSession, object> _drafting = [];

    // Insert loop only.
    private char? _previousChar;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        hotkeys.Triggered += OnHotkey;
        _insertLoop = Task.Run(InsertLoopAsync, CancellationToken.None);
        var keepDays = settings.Current.Dictation.KeepHistoryDays;
        _ = Task.Run(() => history.Prune(keepDays), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        hotkeys.Triggered -= OnHotkey;
        lock (_gate)
        {
            _session?.Cancel();
            _session = null;
        }

        corrections.Stop();
        _inserts.Writer.TryComplete();
        await _stopping.CancelAsync();
        if (_insertLoop is { } loop)
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _stopping.Dispose();

    private void OnHotkey(object? sender, HotkeyEvent e)
    {
        if (e.Action is not (HotkeyAction.Dictation or HotkeyAction.DictationHandsFree))
        {
            return;
        }

        lock (_gate)
        {
            var handsFree = _session is { Mode: DictationMode.HandsFree } && _armed;
            switch (e.Action, e.Phase)
            {
                // While hands-free runs, the taps of the double-tap that will stop it come through
                // as Pending/Cancelled holds; they must not touch the running session.
                case (HotkeyAction.Dictation, HotkeyPhase.Pending or HotkeyPhase.Cancelled) when handsFree:
                    break;

                case (HotkeyAction.Dictation, HotkeyPhase.Started) when handsFree:
                case (HotkeyAction.DictationHandsFree, HotkeyPhase.Fired) when handsFree:
                    Finish(insert: true);
                    break;

                case (HotkeyAction.Dictation, HotkeyPhase.Pending):
                    Open(DictationMode.Hold);
                    break;

                case (HotkeyAction.Dictation, HotkeyPhase.Started):
                    Open(DictationMode.Hold);
                    Arm(ListeningCaption);
                    break;

                case (HotkeyAction.Dictation, HotkeyPhase.Ended):
                    Finish(insert: true);
                    break;

                case (HotkeyAction.Dictation, HotkeyPhase.Cancelled):
                    if (_armed)
                    {
                        // Interrupted: keep the words, but focus may be anywhere now.
                        Finish(insert: false);
                    }
                    else
                    {
                        _session?.Cancel();
                        _session = null;
                    }

                    break;

                case (HotkeyAction.DictationHandsFree, HotkeyPhase.Fired):
                    _session?.Cancel();
                    _session = null;
                    Open(DictationMode.HandsFree);
                    Arm(HandsFreeCaption);
                    break;
            }
        }
    }

    // Under _gate.
    private void Open(DictationMode mode)
    {
        if (_session is not null)
        {
            return;
        }

        if (speech.Interrupt())
        {
            logger.LogInformation("Dictation: stopped speaking");
        }

        var session = dictation.Start(mode);
        var generation = ++_generation;
        session.LevelChanged += level =>
        {
            if (_armed && ReferenceEquals(_session, session))
            {
                presence.SetLevel(level);
            }
        };
        session.PieceReady += piece => OnPiece(session, piece);
        session.LimitReached += () =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                {
                    Finish(insert: true);
                }
            }
        };
        _session = session;
        _ = WatchOpenAsync(session, generation);
    }

    // Under _gate.
    private void Arm(string caption)
    {
        _armed = true;
        corrections.Stop();
        _previousChar = null;
        presence.SetState(BuddyState.Listening);
        presence.SetCaption(caption);
    }

    // Under _gate.
    private void Finish(bool insert)
    {
        var session = _session;
        var wasArmed = _armed;
        _session = null;
        _armed = false;
        presence.SetLevel(0);
        if (session is null || !wasArmed)
        {
            session?.Cancel();
            ResetBuddy();
            return;
        }

        presence.SetState(BuddyState.Thinking);
        _ = CompleteAsync(session, insert);
    }

    private void OnPiece(DictationSession session, DictationPiece piece)
    {
        var settingsNow = settings.Current.Dictation;
        if (piece.Index == 0 && ScreenDrafter.FindRequest(piece.Text) is not null)
        {
            _drafting.TryAdd(session, new object());
        }

        if (session.Mode == DictationMode.HandsFree && settingsNow.TypeWhileSpeaking && !_drafting.TryGetValue(session, out _))
        {
            _inserts.Writer.TryWrite(new InsertJob(piece.Text, Insert: true));
        }

        // A live preview of what was heard, beside the buddy.
        if (ReferenceEquals(Volatile.Read(ref _session), session))
        {
            var text = session.TextSoFar.ReplaceLineEndings(" ");
            presence.SetCaption(text.Length <= PreviewChars ? text : "…" + text[^PreviewChars..].TrimStart());
        }
    }

    private async Task WatchOpenAsync(DictationSession session, int generation)
    {
        var problem = await session.Opened.ConfigureAwait(false);
        if (problem is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_session, session) || generation != _generation)
            {
                return;
            }

            _session = null;
            _armed = false;
        }

        session.Cancel();
        ResetBuddy();
        ShowCaption(problem.Message, ProblemCaptionTime);
        notifier.Notify("Clicky can't hear you", problem.Message, warning: true);
    }

    private async Task CompleteAsync(DictationSession session, bool insert)
    {
        DictationResult result;
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

        var drafting = _drafting.TryGetValue(session, out _);
        var typedAlready = session.Mode == DictationMode.HandsFree && settings.Current.Dictation.TypeWhileSpeaking && !drafting;
        if (result.Text.Length > 0 && !typedAlready)
        {
            _inserts.Writer.TryWrite(new InsertJob(result.Text, insert) { Draft = insert && drafting });
        }

        if (result.Problem is { } problem)
        {
            ShowCaption(problem, ProblemCaptionTime);
            if (result.Text.Length == 0)
            {
                notifier.Notify(result.MicrophoneProblem is null ? "Clicky couldn't transcribe that" : "Clicky can't hear you", problem, warning: true);
            }
        }

        _inserts.Writer.TryWrite(InsertJob.Done);
    }

    private async Task InsertLoopAsync()
    {
        await foreach (var job in _inserts.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (job.IsDone)
            {
                lock (_gate)
                {
                    if (_session is null)
                    {
                        ResetBuddy();
                    }
                }

                continue;
            }

            if (!job.Insert)
            {
                ShowCard(job.Text, "Dictation was interrupted, so it wasn't typed. Here's what you said:");
                continue;
            }

            try
            {
                var text = job.Text;
                if (job.Draft)
                {
                    text = await DraftAsync(job.Text).ConfigureAwait(false);
                    if (text.Length == 0)
                    {
                        continue;
                    }
                }

                var dictationSettings = settings.Current.Dictation;
                var result = await inserter.InsertAsync(new InsertionRequest(text, _previousChar, dictationSettings), _stopping.Token).ConfigureAwait(false);
                switch (result.Method)
                {
                    case InsertionMethod.ValuePattern or InsertionMethod.Typed or InsertionMethod.Pasted:
                        _previousChar = result.Text.Length > 0 ? result.Text[^1] : _previousChar;
                        if (result.Probe is { } probe && dictationSettings.LearnCorrections)
                        {
                            _ = corrections.Watch(probe);
                        }

                        break;

                    case InsertionMethod.None when result.Problem is null:
                        // Nothing left to type (a "new paragraph" in a terminal).
                        break;

                    case InsertionMethod.Copied:
                        ShowCard(result.Text, result.Problem ?? "On the clipboard.");
                        break;

                    default:
                        ShowCard(result.Text, result.Problem ?? "Couldn't type this. Here's what you said:");
                        break;
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "Dictation: inserting failed");
                ShowCard(job.Text, "Couldn't type this. Here's what you said:");
            }
        }
    }

    /// <summary>"Type a reply to this": Claude reads the screen and writes the reply. Empty if it couldn't.</summary>
    private async Task<string> DraftAsync(string request)
    {
        presence.SetState(BuddyState.Thinking);
        presence.SetCaption(DraftingCaption);
        RequestScreenshot? screenshot = null;
        try
        {
            var mode = settings.Current.Capture.Mode;
            var shot = await capture.CaptureAsync(new CaptureTarget(mode, CursorReader.TryGet(out var cursor, out _) ? cursor : null), _stopping.Token).ConfigureAwait(false);
            screenshot = new RequestScreenshot(shot.Png, shot.Geometry);
        }
        catch (CaptureException ex)
        {
            logger.LogWarning(ex, "Dictation draft: no screenshot; drafting from the words alone");
        }

        try
        {
            var draft = await drafter.DraftAsync(request, screenshot, WindowsTextInserter.ForegroundAppName(), _stopping.Token).ConfigureAwait(false);
            if (draft.Length == 0)
            {
                ShowCaption("Claude didn't write a reply. Try again with a bit more detail.", ProblemCaptionTime);
            }
            else if (presence.Caption == DraftingCaption)
            {
                presence.SetCaption(null);
            }

            return draft;
        }
        catch (ClaudeBackendException ex)
        {
            logger.LogWarning(ex, "Dictation draft failed ({Failure})", ex.Failure);
            ShowCaption(ex.Message, ProblemCaptionTime);
            ShowCard(request, $"Couldn't write the reply: {ex.Message} Here's what you said:");
            return string.Empty;
        }
    }

    private void ResetBuddy()
    {
        if (presence.State is BuddyState.Listening or BuddyState.Thinking)
        {
            presence.SetState(BuddyState.Idle);
        }

        presence.SetLevel(0);
        presence.SetCaption(null);
    }

    private void ShowCard(string text, string header) =>
        dispatcher.InvokeAsync(() => new ReplyCard(text.Trim(), reason: null, logger, header).Show());

    private void ShowCaption(string text, TimeSpan duration)
    {
        presence.SetCaption(text);
        _ = ClearCaptionAsync(text, duration);
    }

    private async Task ClearCaptionAsync(string caption, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (string.Equals(presence.Caption, caption, StringComparison.Ordinal))
        {
            presence.SetCaption(null);
        }
    }

    private sealed record InsertJob(string Text, bool Insert)
    {
        public static InsertJob Done { get; } = new(string.Empty, Insert: false) { IsDone = true };

        public bool IsDone { get; init; }

        /// <summary>The text is a drafting request, not text to type.</summary>
        public bool Draft { get; init; }
    }
}
