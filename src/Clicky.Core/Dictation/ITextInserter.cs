using Clicky.Core.Settings;

namespace Clicky.Core.Dictation;

/// <summary>How dictated text reached the app.</summary>
public enum InsertionMethod
{
    /// <summary>Nothing was inserted; see <see cref="InsertionResult.Problem"/>.</summary>
    None,

    /// <summary>UI Automation set the field's value directly (native fields only).</summary>
    ValuePattern,

    /// <summary>Typed as Unicode keystrokes, independent of the keyboard layout.</summary>
    Typed,

    /// <summary>Pasted through the clipboard, which was then put back as it was.</summary>
    Pasted,

    /// <summary>Could not be inserted, so it was left on the clipboard for the user to paste.</summary>
    Copied,
}

/// <param name="Text">What to insert, already cleaned.</param>
/// <param name="PreviousChar">
/// The last character Clicky inserted in this dictation, for spacing between pieces when the field
/// cannot be read; null for the first piece.
/// </param>
/// <param name="Settings">Terminal and paste app lists.</param>
public sealed record InsertionRequest(string Text, char? PreviousChar, DictationSettings Settings);

/// <param name="Method">How it went in.</param>
/// <param name="Text">Exactly what was inserted (spacing added, line breaks collapsed in terminals).</param>
/// <param name="App">Target process, for logs.</param>
/// <param name="IsTerminal">The target was a terminal, so line breaks were collapsed.</param>
/// <param name="Problem">User-facing reason when <see cref="Method"/> is None or Copied.</param>
/// <param name="Probe">Reads the field back later to learn corrections; null when the field cannot be read.</param>
public sealed record InsertionResult(
    InsertionMethod Method,
    string Text,
    string App,
    bool IsTerminal,
    string? Problem,
    IInsertedTextProbe? Probe = null);

/// <summary>Reads back the field Clicky inserted into, so hand corrections can be learned.</summary>
public interface IInsertedTextProbe
{
    /// <summary>The whole field right after insertion.</summary>
    string TextAfterInsert { get; }

    /// <summary>Where the inserted text starts in <see cref="TextAfterInsert"/>.</summary>
    int InsertStart { get; }

    int InsertLength { get; }

    /// <summary>The field's text now, or null if it is gone or can no longer be read.</summary>
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Puts dictated text into whatever has keyboard focus.</summary>
public interface ITextInserter
{
    /// <summary>Inserts at the caret of the focused field. Never throws for an app that refuses; see the result.</summary>
    Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken cancellationToken = default);
}
