namespace Clicky.Core.Settings;

/// <summary>settings.text: the typed-question box (double-tap Ctrl) and the documents sent with it.</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record TextModeSettings(
    bool SpeakReplies = false,
    bool IncludeScreenshot = true,
    bool OfferWindowDocument = true,
    int DocumentTokenBudget = TextModeSettings.DefaultDocumentTokenBudget)
{
    /// <summary>About 240,000 characters: a 100-page report goes whole.</summary>
    public const int DefaultDocumentTokenBudget = 60_000;
    public const int MinDocumentTokenBudget = 2_000;
    public const int MaxDocumentTokenBudget = 400_000;

    /// <summary>Also read typed answers aloud. Off: they appear in the box only.</summary>
    public bool SpeakReplies { get; init; } = SpeakReplies;

    /// <summary>Send a screenshot (taken as the box opens) with typed questions.</summary>
    public bool IncludeScreenshot { get; init; } = IncludeScreenshot;

    /// <summary>Read the document in the app that was in front (UI Automation) and offer it as an attachment.</summary>
    public bool OfferWindowDocument { get; init; } = OfferWindowDocument;

    /// <summary>Tokens of document text one question may carry, all documents together; longer ones are cut to the relevant parts.</summary>
    public int DocumentTokenBudget { get; init; } = DocumentTokenBudget;

    public TextModeSettings Normalize() => this with
    {
        DocumentTokenBudget = Math.Clamp(DocumentTokenBudget, MinDocumentTokenBudget, MaxDocumentTokenBudget),
    };
}
