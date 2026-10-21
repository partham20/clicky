using System.Text.RegularExpressions;
using Clicky.Core.Assistant;

namespace Clicky.Core.Documents;

/// <summary>The attachments for one question, and how much of each document made it in (for the chips and the log).</summary>
public sealed record AttachmentPlan(IReadOnlyList<RequestAttachment> Attachments, IReadOnlyList<FittedDocument> Documents)
{
    public static AttachmentPlan Empty { get; } = new([], []);

    public int EstimatedTokens => Attachments.Sum(a => a.EstimatedTokens);
}

/// <summary>Puts together what goes with a typed question: documents fitted to the budget, then pictures.</summary>
public static partial class DocumentAttachments
{
    /// <summary>More than this many documents in one question is refused at the box.</summary>
    public const int MaxDocuments = 10;

    /// <summary>More than this many pictures (plus the screenshot) is refused at the box.</summary>
    public const int MaxImages = 5;

    /// <param name="budgetTokens">settings.text.documentTokenBudget: all documents together.</param>
    public static AttachmentPlan Build(IReadOnlyList<DocumentContent> documents, IReadOnlyList<RequestAttachment> images, int budgetTokens, string? question)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(images);
        if (documents.Count == 0 && images.Count == 0)
        {
            return AttachmentPlan.Empty;
        }

        var fitted = DocumentFitter.FitAll([.. documents.Take(MaxDocuments)], budgetTokens, question);
        return new([.. fitted.Select(f => f.Attachment), .. images.Take(MaxImages)], fitted);
    }

    /// <summary>
    /// Whether a question is about the document on screen ("summarize this", "what does this page
    /// say about refunds"), so the open window's document goes with it without the user ticking it.
    /// </summary>
    public static bool RefersToOpenDocument(string? question) =>
        !string.IsNullOrWhiteSpace(question) && OpenDocumentPattern().IsMatch(question);

    [GeneratedRegex(
        @"\b(this|the|that|my|current|open)\s+(whole\s+|entire\s+|full\s+)?(document|doc|page|article|paper|pdf|file|report|email|mail|thread|chapter|essay|post|contract|spec|datasheet|manual|text|story|letter|transcript|slides?|deck|presentation|code)\b" +
        @"|\b(summari[sz]e|tl;?dr|proofread)\b(?!\s+(the\s+)?(screen|window|picture|image|screenshot))" +
        @"|\b(read|go through|look through)\s+(this|it)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenDocumentPattern();
}
