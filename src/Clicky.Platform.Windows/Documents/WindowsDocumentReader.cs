using System.Runtime.InteropServices;
using Clicky.Core.Assistant;
using Clicky.Core.Documents;
using Clicky.Platform.Windows.Input;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Documents;

/// <summary>
/// Reads the document open in an app through UI Automation's TextPattern: Word, Edge and Chrome
/// pages, PDF viewers, Notepad, editors with accessibility on. It takes the focused element if it
/// is a document or a multi-line edit, else the window's largest document element, and reads
/// <see cref="TextPattern"/>'s whole document range.
/// </summary>
/// <remarks>
/// <para>Password fields are never read, nor are elevated windows (UIPI refuses anyway).</para>
/// <para>Chrome and Electron build their accessibility tree only once a UI Automation client asks,
/// so the very first read of such a window can come back empty; the box offers it again next time.</para>
/// <para>A hung app must not hang text mode: every call has a UIA transaction timeout and the
/// whole read gives up after <see cref="ReadTimeout"/>.</para>
/// </remarks>
public sealed class WindowsDocumentReader(ILogger<WindowsDocumentReader> logger) : IWindowDocumentReader, IDisposable
{
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Less text than this is a text box, not a document.</summary>
    public const int MinDocumentChars = 200;

    // Searching a big web page's tree for documents costs; only this many are compared.
    private const int MaxCandidates = 8;

    private readonly Lazy<UIA3Automation> _automation = new(() => new UIA3Automation
    {
        ConnectionTimeout = TimeSpan.FromSeconds(2),
        TransactionTimeout = TimeSpan.FromSeconds(4),
    });

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DocumentContent?> ReadAsync(nint window, int maxChars, CancellationToken cancellationToken = default)
    {
        window = window == 0 ? 0 : GetAncestor(window, GaRoot);
        if (window == 0 || !IsWindow(window))
        {
            return null;
        }

        if (ProcessElevation.IsWindowElevated(window, out var process) && !ProcessElevation.IsCurrentProcessElevated)
        {
            logger.LogInformation("Text mode: {App} runs as administrator, so its document can't be read", process);
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // UIA is fine on the thread pool (MTA); a dedicated thread keeps a stuck provider off it.
            var read = Task.Factory.StartNew(() => Read(window, process, maxChars), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return await read.WaitAsync(ReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogInformation("Text mode: {App} took over {Seconds} s to give its document; skipped", process, ReadTimeout.TotalSeconds);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_automation.IsValueCreated)
        {
            _automation.Value.Dispose();
        }

        _gate.Dispose();
    }

    private DocumentContent? Read(nint window, string process, int maxChars)
    {
        var started = Environment.TickCount64;
        try
        {
            var automation = _automation.Value;
            var root = automation.FromHandle(window);
            var element = Pick(automation, root, window);
            if (element is null)
            {
                logger.LogDebug("Text mode: no document element in {App}", process);
                return null;
            }

            var text = element.Patterns.Text.Pattern.DocumentRange.GetText(maxChars + 1) ?? string.Empty;
            var cut = text.Length > maxChars;
            if (cut)
            {
                text = text[..maxChars];
            }

            text = DocumentFileReader.Tidy(text);
            if (text.Length < MinDocumentChars)
            {
                logger.LogDebug("Text mode: {App}'s document has only {Chars} chars; not offered", process, text.Length);
                return null;
            }

            var name = DocumentName(ProcessElevation.GetWindowTitle(window), process);
            logger.LogInformation(
                "Text mode: read {Chars} chars ({Type}) from {App} in {Ms} ms{Cut}",
                text.Length,
                element.Properties.ControlType.ValueOrDefault,
                process,
                Environment.TickCount64 - started,
                cut ? ", cut" : string.Empty);
            return new DocumentContent(
                name,
                RequestAttachment.WindowOrigin,
                [new DocumentSection(text)],
                Warning: cut ? "Only the start of this document was read; it is very long." : null);
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Text mode: could not read the document in {App}", process);
            return null;
        }
    }

    /// <summary>The focused element when it is a document (or a multi-line edit) in this window, else the window's biggest document.</summary>
    private static AutomationElement? Pick(UIA3Automation automation, AutomationElement root, nint window)
    {
        try
        {
            var focused = automation.FocusedElement();
            if (focused is not null && IsReadable(focused) && BelongsTo(focused, window)
                && focused.Properties.ControlType.ValueOrDefault is ControlType.Document or ControlType.Edit)
            {
                return focused;
            }
        }
        catch (Exception ex) when (ex is COMException or TimeoutException)
        {
            // Some apps refuse focus queries; search the window instead.
        }

        var documents = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Document));
        AutomationElement? best = null;
        var bestArea = 0.0;
        foreach (var candidate in documents.Take(MaxCandidates))
        {
            if (!IsReadable(candidate) || candidate.Properties.IsOffscreen.ValueOrDefault)
            {
                continue;
            }

            var bounds = candidate.Properties.BoundingRectangle.ValueOrDefault;
            var area = bounds.Width * bounds.Height;
            if (area > bestArea)
            {
                best = candidate;
                bestArea = area;
            }
        }

        return best;
    }

    private static bool IsReadable(AutomationElement element) =>
        !element.Properties.IsPassword.ValueOrDefault && element.Patterns.Text.IsSupported;

    private static bool BelongsTo(AutomationElement element, nint window)
    {
        var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle != 0)
        {
            return GetAncestor(handle, GaRoot) == window;
        }

        // Windowless elements (web content): compare processes.
        GetWindowThreadProcessId(window, out var windowProcess);
        return element.Properties.ProcessId.ValueOrDefault == (int)windowProcess;
    }

    /// <summary>"Plan.docx - Word" stays as is; an empty title becomes the app's name.</summary>
    private static string DocumentName(string title, string process)
    {
        title = title.Trim();
        if (title.Length == 0)
        {
            return Path.GetFileNameWithoutExtension(process);
        }

        return title.Length <= 80 ? title : string.Concat(title.AsSpan(0, 79), "…");
    }
}
