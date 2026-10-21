namespace Clicky.Core.Documents;

/// <summary>A natural piece of a document: a page of a PDF, a slide, or the whole text of a plain file.</summary>
/// <param name="Number">Page or slide number (1-based); null for documents without pages.</param>
public sealed record DocumentSection(string Text, int? Number = null);

/// <summary>
/// A document's text as read, before it is fitted to the request budget by <see cref="DocumentFitter"/>.
/// </summary>
/// <param name="Name">File name, or the window's title.</param>
/// <param name="Origin">"file" or "window" (see <see cref="Assistant.RequestAttachment"/>).</param>
/// <param name="Sections">The text, in order.</param>
/// <param name="Unit">What <see cref="DocumentSection.Number"/> counts: "page" or "slide"; null when sections have no numbers.</param>
/// <param name="TotalUnits">Pages or slides in the whole document, also those not read.</param>
/// <param name="Warning">For the user and the model: why the text may be incomplete ("looks scanned", "only the first 2,000 pages were read").</param>
public sealed record DocumentContent(
    string Name,
    string Origin,
    IReadOnlyList<DocumentSection> Sections,
    string? Unit = null,
    int? TotalUnits = null,
    string? Warning = null)
{
    public bool IsEmpty => Sections.All(s => string.IsNullOrWhiteSpace(s.Text));

    public int CharCount => Sections.Sum(s => s.Text.Length);

    public int EstimatedTokens => Sections.Sum(s => TokenEstimate.Of(s.Text));

    /// <summary>A rough word count for the chip ("12,400 words").</summary>
    public int WordCount => Sections.Sum(s => s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
}

/// <summary>A document could not be read. <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class DocumentException : Exception
{
    public DocumentException()
    {
    }

    public DocumentException(string message)
        : base(message)
    {
    }

    public DocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
