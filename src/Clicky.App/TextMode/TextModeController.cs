using System.Globalization;
using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core.Assistant;
using Clicky.Core.Capture;
using Clicky.Core.Documents;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Core.Talk;
using Clicky.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Clicky.App.TextMode;

/// <summary>
/// Text mode (M10): double-tap Ctrl opens a box by the pointer for a typed question. The question
/// goes through the same Talk loop as a spoken one (<see cref="TalkAnswerer"/>), with the
/// screenshot taken as the box opened, the files dropped or pasted on the box, and the document of
/// the app that was in front when it is ticked or asked about ("summarize this"). The answer
/// streams into the box (and is also spoken with settings.text.speakReplies); its marks go on
/// screen as usual. The box stays open for follow-ups in the same conversation.
/// </summary>
/// <remarks>
/// Documents are read as they are added and fitted to settings.text.documentTokenBudget when the
/// question goes (see <see cref="DocumentFitter"/>); each goes once, then stays in the
/// conversation. Call from the UI thread, except <see cref="Toggle"/>, which is safe from any.
/// </remarks>
internal sealed class TextModeController(
    TalkLoop talk,
    TalkAnswerer answerer,
    ReplyPresenter replies,
    ICaptureService capture,
    IWindowDocumentReader windowDocuments,
    IImageLoader images,
    ISettingsStore settings,
    Dispatcher dispatcher,
    ILogger<TextModeController> logger) : IDisposable
{
    /// <summary>How long a question waits for files still being read before it goes without them.</summary>
    private static readonly TimeSpan ReadingWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CaptureWait = TimeSpan.FromSeconds(3);

    private readonly List<PendingAttachment> _attachments = [];
    private TextModeBox? _box;
    private nint _previousWindow;
    private PixelPoint? _cursor;
    private Task<CaptureResult?>? _openCapture;
    private Task _windowRead = Task.CompletedTask;
    private CancellationTokenSource? _session;
    private CancellationTokenSource? _answer;

    /// <summary>The box is showing. UI thread.</summary>
    public bool IsOpen => _box is not null;

    /// <summary>
    /// Double-tap Ctrl: opens the box, or focuses it if it is open but behind, or closes it if it is
    /// open and focused. The foreground window and pointer are read now, before anything moves.
    /// </summary>
    public void Toggle()
    {
        var foreground = ForegroundRights.Foreground;
        PixelPoint? cursor = Platform.Windows.Input.CursorReader.TryGet(out var position, out _) ? position : null;
        dispatcher.InvokeAsync(() =>
        {
            if (_box is { } box)
            {
                if (ForegroundRights.Foreground == box.Handle)
                {
                    Close();
                }
                else
                {
                    ForegroundRights.Activate(box.Handle);
                    box.FocusInput();
                }

                return;
            }

            Open(foreground, cursor, []);
        });
    }

    /// <summary>Opens the box (if needed) with these files on it: files dropped on the buddy.</summary>
    public void OpenWithFiles(IReadOnlyList<string> files)
    {
        dispatcher.VerifyAccess();
        if (_box is { } box)
        {
            AddFiles(files);
            ForegroundRights.Activate(box.Handle);
            return;
        }

        PixelPoint? cursor = Platform.Windows.Input.CursorReader.TryGet(out var position, out _) ? position : null;
        Open(ForegroundRights.Foreground, cursor, files);
    }

    public void Dispose()
    {
        _answer?.Cancel();
        _session?.Cancel();
        _session?.Dispose();
    }

    private void Open(nint foreground, PixelPoint? cursor, IReadOnlyList<string> files)
    {
        var text = settings.Current.Text;
        _previousWindow = ForegroundRights.IsOwnWindow(foreground) ? 0 : foreground;
        _cursor = cursor;
        _session = new CancellationTokenSource();
        var token = _session.Token;

        // Stops a spoken answer, clears old marks, and starts Claude Code warming up while the user types.
        talk.Begin();
        _openCapture = text.IncludeScreenshot ? CaptureAsync(token) : null;

        var box = new TextModeBox(cursor ?? default, logger);
        box.AskRequested += (_, question) => _ = AskAsync(question);
        box.StopRequested += (_, _) => _answer?.Cancel();
        box.CloseRequested += (_, _) => Close();
        box.FilesDropped += (_, dropped) => AddFiles(dropped);
        box.ImagePasted += (_, png) => AddPicture("pasted picture.png", png);
        box.Deactivated += (_, _) =>
        {
            if (ReferenceEquals(_box, box) && box.IsEmpty)
            {
                Close(restoreFocus: false);
            }
        };
        box.Closed += (_, _) =>
        {
            if (ReferenceEquals(_box, box))
            {
                Reset();
            }
        };
        _box = box;
        box.Show();
        if (!ForegroundRights.Activate(box.Handle))
        {
            logger.LogInformation("Text mode: Windows didn't let the box take focus; click it to type");
        }

        logger.LogInformation("Text mode: opened over {Window:X}", _previousWindow);
        AddFiles(files);
        if (text.OfferWindowDocument && _previousWindow != 0)
        {
            _windowRead = OfferWindowDocumentAsync(_previousWindow, token);
        }
    }

    private void Close(bool restoreFocus = true)
    {
        var box = _box;
        if (box is null)
        {
            return;
        }

        var wasInFront = ForegroundRights.Foreground == box.Handle;
        Reset();
        box.Close();
        if (restoreFocus && wasInFront)
        {
            ForegroundRights.Restore(_previousWindow);
        }
    }

    private void Reset()
    {
        _answer?.Cancel();
        _session?.Cancel();
        _session?.Dispose();
        _session = null;
        _box = null;
        _attachments.Clear();
        _openCapture = null;
        _windowRead = Task.CompletedTask;
    }

    private void AddFiles(IReadOnlyList<string> files)
    {
        if (_box is not { } box || _session is not { } session)
        {
            return;
        }

        foreach (var path in files)
        {
            if (_attachments.Any(a => !a.IsWindowDocument && a.State != PendingState.Sent && string.Equals(a.Name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var pending = PendingAttachment.ForFile(path);
            _attachments.Add(pending);
            box.AddChip(pending, Remove);
            if (Directory.Exists(path))
            {
                pending.Fail("folders can't be attached; drop the files in it");
                continue;
            }

            var kind = DocumentFileReader.Classify(path);
            var tooMany = kind == DocumentFileKind.Image
                ? Unsent(a => a.Image is not null || a.State == PendingState.Reading) > DocumentAttachments.MaxImages
                : Unsent(a => a.Document is not null || a.State == PendingState.Reading) > DocumentAttachments.MaxDocuments;
            if (tooMany)
            {
                pending.Fail(kind == DocumentFileKind.Image
                    ? $"at most {DocumentAttachments.MaxImages} pictures per question"
                    : $"at most {DocumentAttachments.MaxDocuments} documents per question");
                continue;
            }

            _ = LoadAsync(pending, path, kind, session.Token);
        }
    }

    private int Unsent(Func<PendingAttachment, bool> predicate) =>
        _attachments.Count(a => a.State != PendingState.Sent && a.State != PendingState.Failed && !a.IsWindowDocument && predicate(a));

    private void AddPicture(string name, byte[] png)
    {
        if (_box is not { } box)
        {
            return;
        }

        var pending = PendingAttachment.ForFile(name);
        _attachments.Add(pending);
        box.AddChip(pending, Remove);
        pending.SetImage(RequestAttachment.Picture(name, png, "image/png"));
    }

    private void Remove(PendingAttachment pending)
    {
        _attachments.Remove(pending);
        _box?.RemoveChip(pending);
    }

    private async Task LoadAsync(PendingAttachment pending, string path, DocumentFileKind kind, CancellationToken cancellationToken)
    {
        try
        {
            if (kind == DocumentFileKind.Image)
            {
                pending.SetImage(await images.LoadAsync(path, cancellationToken).ConfigureAwait(true));
            }
            else
            {
                var document = await Task.Run(() => DocumentFileReader.Read(path, cancellationToken), cancellationToken).ConfigureAwait(true);
                pending.SetDocument(document);
                logger.LogInformation(
                    "Text mode: read {Name}: {Chars} chars, about {Tokens} tokens{Warning}",
                    document.Name,
                    document.CharCount,
                    document.EstimatedTokens,
                    document.Warning is null ? string.Empty : $" ({document.Warning})");
            }
        }
        catch (DocumentException ex)
        {
            logger.LogInformation(ex, "Text mode: could not read {Name}", pending.Name);
            pending.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            pending.Fail("cancelled");
        }
    }

    private async Task OfferWindowDocumentAsync(nint window, CancellationToken cancellationToken)
    {
        DocumentContent? document;
        try
        {
            document = await windowDocuments.ReadAsync(window, DocumentFileReader.MaxChars, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "Text mode: reading the window's document failed");
            return;
        }

        if (document is null || _box is not { } box || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var pending = PendingAttachment.ForWindow(document);
        _attachments.Add(pending);
        box.AddChip(pending, Remove);
    }

    private async Task AskAsync(string question)
    {
        if (_box is not { } box || _session is not { } session || _answer is not null)
        {
            return;
        }

        var text = settings.Current.Text;
        var answer = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        _answer = answer;
        var wording = question.Length > 0 ? question : "Have a look at what I attached.";
        try
        {
            if (DocumentAttachments.RefersToOpenDocument(wording))
            {
                // "Summarize this" right after opening: the window's document may still be on its way.
                if (!_windowRead.IsCompleted)
                {
                    box.BeginAnswer(wording, "Reading the document…");
                    await _windowRead.WaitAsync(answer.Token).ConfigureAwait(true);
                }

                foreach (var window in _attachments.Where(a => a.IsWindowDocument))
                {
                    window.IncludeForQuestion();
                }
            }

            var going = _attachments.Where(a => a.Included && a.State is PendingState.Reading or PendingState.Ready).ToList();
            box.BeginAnswer(wording, going.Any(a => a.State == PendingState.Reading) ? "Reading the files first…" : null);
            await WaitForReadingAsync(going, answer.Token).ConfigureAwait(true);

            var documents = going.Where(a => a.State == PendingState.Ready && a.Document is not null).ToList();
            var pictures = going.Where(a => a.State == PendingState.Ready && a.Image is not null).ToList();
            var plan = await Task.Run(
                () => DocumentAttachments.Build([.. documents.Select(d => d.Document!)], [.. pictures.Select(p => p.Image!)], text.DocumentTokenBudget, wording),
                answer.Token).ConfigureAwait(true);
            LogPlan(plan);

            // The first question uses the screenshot taken as the box opened; follow-ups look again.
            var screenshot = _openCapture ?? (text.IncludeScreenshot ? CaptureAsync(answer.Token) : null);
            _openCapture = null;
            var shot = screenshot is null ? null : await WaitForCaptureAsync(screenshot).ConfigureAwait(true);

            talk.Begin();
            var result = await answerer.AskAsync(
                new TalkQuestion(wording, RequestSource.Typed, shot, _cursor, Attachments: plan.Attachments),
                () => new BoxReply(box, text.SpeakReplies ? replies.Begin() : null),
                answer.Token).ConfigureAwait(true);

            // Build takes at most MaxDocuments; any beyond that stay on the box for the next question.
            for (var i = 0; i < Math.Min(documents.Count, plan.Documents.Count); i++)
            {
                documents[i].MarkSent(plan.Documents[i].Coverage);
            }

            foreach (var picture in pictures)
            {
                picture.MarkSent(null);
            }

            if (!ReferenceEquals(_box, box))
            {
                return;
            }

            switch (result.Outcome)
            {
                case TalkOutcome.Failed:
                    box.EndAnswer(result.Problem ?? "Claude couldn't answer that.", null);
                    break;
                case TalkOutcome.Cancelled:
                    box.EndAnswer(null, "Stopped.");
                    break;
                default:
                    box.EndAnswer(null, Coverage(plan, result));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_box, box))
            {
                box.EndAnswer(null, "Stopped.");
            }
        }
        finally
        {
            if (ReferenceEquals(_answer, answer))
            {
                _answer = null;
            }

            answer.Dispose();
        }
    }

    private static async Task WaitForReadingAsync(IReadOnlyList<PendingAttachment> attachments, CancellationToken cancellationToken)
    {
        var reading = attachments.Where(a => a.State == PendingState.Reading).Select(a => a.Loaded).ToList();
        if (reading.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(reading).WaitAsync(ReadingWait, cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            // Goes without the slow ones; their chips keep reading for the next question.
        }
    }

    /// <summary>For the box: what the answer was based on, when not everything fit or the answer was only marks.</summary>
    private static string? Coverage(AttachmentPlan plan, TalkResult result)
    {
        var notes = new List<string>();
        foreach (var fitted in plan.Documents.Where(d => d.IsPartial))
        {
            notes.Add(string.Create(CultureInfo.CurrentCulture, $"{fitted.Attachment.Name} was too long to send whole; Claude saw about {fitted.Coverage:P0} of it, picked for this question."));
        }

        if (result.SpokenText.Length == 0 && result.Marks > 0)
        {
            notes.Add("Clicky pointed it out on screen.");
        }

        return notes.Count == 0 ? null : string.Join(' ', notes);
    }

    private void LogPlan(AttachmentPlan plan)
    {
        foreach (var fitted in plan.Documents)
        {
            logger.LogInformation(
                "Text mode: sending {Name}: about {Included} of {Total} tokens{Partial}",
                fitted.Attachment.Name,
                fitted.IncludedTokens,
                fitted.TotalTokens,
                fitted.IsPartial ? " (partial)" : string.Empty);
        }
    }

    /// <summary>Never throws: without a screenshot, the question still goes.</summary>
    private async Task<CaptureResult?> CaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await capture.CaptureAsync(new CaptureTarget(settings.Current.Capture.Mode, _cursor), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Text mode capture failed; asking without a screenshot");
            return null;
        }
    }

    private async Task<CaptureResult?> WaitForCaptureAsync(Task<CaptureResult?> screenshot)
    {
        try
        {
            return await screenshot.WaitAsync(CaptureWait).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Text mode capture took over {Seconds} s; asking without a screenshot", CaptureWait.TotalSeconds);
            return null;
        }
    }

    /// <summary>The answer's words into the box, and also to the speaker when typed answers are read aloud.</summary>
    private sealed class BoxReply(TextModeBox box, ITalkReply? spoken) : ITalkReply
    {
        private int _done;

        public void Append(string text)
        {
            spoken?.Append(text);
            box.Dispatcher.InvokeAsync(() => box.AppendAnswer(text));
        }

        public void Complete()
        {
            Interlocked.Exchange(ref _done, 1);
            spoken?.Complete();
        }

        public bool Interrupt()
        {
            spoken?.Interrupt();
            return Interlocked.Exchange(ref _done, 1) == 0;
        }
    }
}
