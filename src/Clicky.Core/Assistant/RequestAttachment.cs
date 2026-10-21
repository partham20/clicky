using Clicky.Core.Documents;

namespace Clicky.Core.Assistant;

public enum AttachmentKind
{
    /// <summary>Text the model reads: a file's or a window's document, already fitted to the budget.</summary>
    Document,

    /// <summary>A picture the user dropped (not the screenshot, which has its own slot).</summary>
    Image,
}

/// <summary>
/// Something the user gave Clicky to read along with the question. Both backends send it as its
/// own content block before the screenshot and the question, and it stays in the conversation
/// (so follow-ups can refer back to it) until <see cref="HistoryLimits.MaxAttachmentTokens"/>
/// pushes it out.
/// </summary>
/// <param name="Name">Shown to the model and on the chip: "report.pdf", "Quarterly plan - Word".</param>
/// <param name="Text">The document text (only <see cref="AttachmentKind.Document"/>); null once history has dropped it.</param>
/// <param name="Image">The encoded image (only <see cref="AttachmentKind.Image"/>); null once history has dropped it.</param>
/// <param name="MediaType">image/png, image/jpeg, image/gif or image/webp.</param>
/// <param name="Note">For the model: what was left out of a document that did not fit, or why it is empty.</param>
/// <param name="Origin">"file" or "window", for the model and the log.</param>
public sealed record RequestAttachment(
    string Name,
    AttachmentKind Kind,
    string? Text = null,
    byte[]? Image = null,
    string? MediaType = null,
    string? Note = null,
    string Origin = RequestAttachment.FileOrigin)
{
    public const string FileOrigin = "file";
    public const string WindowOrigin = "window";

    /// <summary>What an image costs, about the same as a screenshot.</summary>
    public const int ImageTokens = 1_600;

    /// <summary>False once history has dropped the content to save room; only the name is sent then.</summary>
    public bool HasContent => Kind == AttachmentKind.Document ? Text is not null : Image is not null;

    /// <summary>Rough size in tokens of what is sent.</summary>
    public int EstimatedTokens => !HasContent ? 0 : Kind == AttachmentKind.Image ? ImageTokens : TokenEstimate.Of(Text) + TokenEstimate.Of(Note);

    public static RequestAttachment Document(string name, string text, string? note = null, string origin = FileOrigin) =>
        new(name, AttachmentKind.Document, text, Note: note, Origin: origin);

    public static RequestAttachment Picture(string name, byte[] image, string mediaType) =>
        new(name, AttachmentKind.Image, Image: image, MediaType: mediaType);

    /// <summary>The same attachment without its content, kept so the model knows it was there.</summary>
    public RequestAttachment WithoutContent() => this with { Text = null, Image = null, Note = null };
}
