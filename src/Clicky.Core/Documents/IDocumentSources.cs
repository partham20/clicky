using Clicky.Core.Assistant;

namespace Clicky.Core.Documents;

/// <summary>Turns a picture file into an image attachment Claude accepts (Windows: WIC, in Platform.Windows).</summary>
public interface IImageLoader
{
    /// <summary>
    /// Decodes the file and, if needed, scales it down to the screenshot caps and re-encodes it as
    /// PNG or JPEG, so any size or format the user drops is accepted. Never writes to disk.
    /// </summary>
    /// <exception cref="DocumentException">Not a picture, or damaged.</exception>
    Task<RequestAttachment> LoadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Reads the document open in an app window (Windows: UI Automation's TextPattern).</summary>
public interface IWindowDocumentReader
{
    /// <summary>
    /// The text of the main document in <paramref name="window"/> (the window that was in front
    /// when the user asked): a Word document, a web page, a PDF in a viewer, an editor's file.
    /// Null when the window has none or doesn't expose it. Never throws for the app's sake: a hung
    /// or uncooperative app gives null after a short timeout.
    /// </summary>
    /// <param name="maxChars">Stop reading after this many characters (the result's warning says so).</param>
    Task<DocumentContent?> ReadAsync(nint window, int maxChars, CancellationToken cancellationToken = default);
}
