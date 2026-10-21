using System.Globalization;
using Clicky.Core.Assistant;
using Clicky.Core.Documents;

namespace Clicky.App.TextMode;

internal enum PendingState
{
    /// <summary>Being read (a big PDF can take a few seconds).</summary>
    Reading,

    Ready,

    /// <summary>Could not be read; <see cref="PendingAttachment.Problem"/> says why.</summary>
    Failed,

    /// <summary>Went with a question; it stays in the conversation, so it isn't sent again.</summary>
    Sent,
}

/// <summary>A file or window document on the box, as its chip shows it. UI thread only.</summary>
internal sealed class PendingAttachment
{
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PendingAttachment(string name, bool isWindowDocument)
    {
        Name = name;
        IsWindowDocument = isWindowDocument;
    }

    public event EventHandler? Changed;

    public string Name { get; }

    /// <summary>The document of the app that was in front, offered rather than added: it goes only when included.</summary>
    public bool IsWindowDocument { get; }

    public PendingState State { get; private set; } = PendingState.Reading;

    public DocumentContent? Document { get; private set; }

    public RequestAttachment? Image { get; private set; }

    public string? Problem { get; private set; }

    /// <summary>Goes with the next question. Files are always included; the window document when ticked (or asked about).</summary>
    public bool Included { get; private set; }

    /// <summary>The user ticked or unticked the window document themselves, so the question no longer decides.</summary>
    public bool ChosenByUser { get; private set; }

    /// <summary>Done reading (ready or failed).</summary>
    public Task Loaded => _loaded.Task;

    /// <summary>How much of the document the last question carried (1 when it went whole).</summary>
    public double? SentCoverage { get; private set; }

    public static PendingAttachment ForFile(string path) => new(Path.GetFileName(path), isWindowDocument: false) { Included = true };

    public static PendingAttachment ForWindow(DocumentContent document)
    {
        var pending = new PendingAttachment(document.Name, isWindowDocument: true);
        pending.SetDocument(document);
        return pending;
    }

    /// <summary>One line for the chip: "12,400 words", "picture", "reading…", the problem.</summary>
    public string Summary => State switch
    {
        PendingState.Reading => "reading…",
        PendingState.Failed => Problem ?? "can't read it",
        PendingState.Sent when SentCoverage is { } coverage and < 0.995 => string.Create(CultureInfo.CurrentCulture, $"sent, about {coverage:P0}"),
        PendingState.Sent => "sent",
        _ when Image is not null => "picture",
        _ when Document is { } document => document.IsEmpty
            ? document.Warning ?? "no text"
            : string.Create(CultureInfo.CurrentCulture, $"{document.WordCount:N0} words{(document.Unit is { } unit && document.TotalUnits is { } units ? $", {units} {unit}{(units == 1 ? string.Empty : "s")}" : string.Empty)}"),
        _ => string.Empty,
    };

    public void SetDocument(DocumentContent document)
    {
        Document = document;
        State = PendingState.Ready;
        _loaded.TrySetResult();
        OnChanged();
    }

    public void SetImage(RequestAttachment image)
    {
        Image = image;
        State = PendingState.Ready;
        _loaded.TrySetResult();
        OnChanged();
    }

    public void Fail(string problem)
    {
        Problem = problem;
        State = PendingState.Failed;
        Included = false;
        _loaded.TrySetResult();
        OnChanged();
    }

    public void Choose(bool included)
    {
        ChosenByUser = true;
        Included = included && State == PendingState.Ready;
        OnChanged();
    }

    /// <summary>Includes the window document because the question is about it, unless the user already decided.</summary>
    public void IncludeForQuestion()
    {
        if (!ChosenByUser && State == PendingState.Ready)
        {
            Included = true;
            OnChanged();
        }
    }

    public void MarkSent(double? coverage)
    {
        State = PendingState.Sent;
        SentCoverage = coverage;
        Included = false;
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
