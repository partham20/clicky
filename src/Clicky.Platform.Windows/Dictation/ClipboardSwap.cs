using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Dictation;

/// <summary>How a paste through the clipboard went.</summary>
public enum PasteOutcome
{
    /// <summary>The app read the text, and the user's clipboard was put back.</summary>
    Pasted,

    /// <summary>The app never asked for the text; it is left on the clipboard for the user.</summary>
    NotRead,

    /// <summary>Another app kept the clipboard open; nothing changed.</summary>
    Busy,
}

/// <summary>
/// The clipboard side of pasting dictation: keeps a copy of whatever the user had on the
/// clipboard, offers the dictated text (marked so clipboard history and cloud clipboard skip it),
/// and puts the user's content back once the target app has read the text.
/// </summary>
/// <remarks>
/// <para>The text is offered with delayed rendering: Windows asks Clicky for it (WM_RENDERFORMAT)
/// when the target pastes, so the restore waits for that instead of guessing how long the app
/// takes. Restoring on a timer would make a slow app paste the user's old clipboard instead of
/// the dictation. An app that never asks within <see cref="RenderTimeout"/> keeps the text on the
/// clipboard, and the user is told to paste it.</para>
/// <para>Clipboard calls run on the UI thread, which owns a message-only window: SetClipboardData
/// fails without an owner window, and the owner must pump messages to answer render requests.
/// Each call is a few milliseconds; a clipboard held open by another app is retried without
/// blocking.</para>
/// <para>Only memory formats are kept (text, HTML, RTF, DIB images, files, app formats). GDI handles
/// (CF_BITMAP, metafiles, palettes) are not copied; Windows rebuilds CF_BITMAP from CF_DIB.
/// Anything bigger than <see cref="MaxSnapshotBytes"/> in total is not restored.</para>
/// </remarks>
public sealed class ClipboardSwap : IDisposable
{
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;

    public static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(3);

    private const int WmRenderFormat = 0x0305;
    private const int WmRenderAllFormats = 0x0306;
    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(20);

    // After the first format is read, an app may ask for more (CF_TEXT is made from ours).
    private static readonly TimeSpan AfterRender = TimeSpan.FromMilliseconds(150);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly uint _excludeFromMonitoring;
    private readonly uint _canIncludeInHistory;
    private readonly uint _canUploadToCloud;
    private HwndSource? _owner;

    // UI thread only.
    private string? _offered;
    private TaskCompletionSource? _rendered;

    public ClipboardSwap(Dispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _excludeFromMonitoring = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _canIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        _canUploadToCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard for the user to paste. False if the clipboard was busy.</summary>
    public async Task<bool> CopyAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return await WithClipboardAsync(
            () =>
            {
                EmptyClipboard();
                SetUnicodeText(text);
                return string.Empty;
            },
            cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Offers <paramref name="text"/> on the clipboard, calls <paramref name="sendPaste"/> (which
    /// presses the app's paste keys), and once the app has read the text puts the user's clipboard back.
    /// </summary>
    public async Task<PasteOutcome> PasteAsync(string text, Action sendPaste, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sendPaste);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = await WithClipboardAsync(
            () =>
            {
                var saved = Snapshot();
                EmptyClipboard();
                _offered = text;
                _rendered = rendered;

                // Delayed rendering: no data now, Windows asks for it when someone pastes.
                SetClipboardData(CfUnicodeText, 0);
                SetBytes(_excludeFromMonitoring, [0]);
                SetBytes(_canIncludeInHistory, [0, 0, 0, 0]);
                SetBytes(_canUploadToCloud, [0, 0, 0, 0]);
                return saved;
            },
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return PasteOutcome.Busy;
        }

        sendPaste();
        var read = await Task.WhenAny(rendered.Task, Task.Delay(RenderTimeout, cancellationToken)).ConfigureAwait(false) == rendered.Task;
        if (read)
        {
            await Task.Delay(AfterRender, cancellationToken).ConfigureAwait(false);
        }

        await WithClipboardAsync(
            () =>
            {
                if (GetClipboardOwner() != _owner!.Handle)
                {
                    // The user or an app copied something new meanwhile; leave it.
                    _logger.LogDebug("Dictation: the clipboard changed while pasting; leaving it alone");
                }
                else if (!read)
                {
                    // Make the text real so it can still be pasted once Clicky stops offering it.
                    EmptyClipboard();
                    SetUnicodeText(text);
                }
                else if (snapshot.TooLarge)
                {
                    _logger.LogInformation("Dictation: the clipboard held more than {Mb} MB, so it was not put back after pasting", MaxSnapshotBytes / (1024 * 1024));
                }
                else
                {
                    EmptyClipboard();
                    foreach (var (format, data) in snapshot.Formats)
                    {
                        SetBytes(format, data);
                    }
                }

                _offered = null;
                _rendered = null;
                return snapshot;
            },
            CancellationToken.None).ConfigureAwait(false);

        return read ? PasteOutcome.Pasted : PasteOutcome.NotRead;
    }

    public void Dispose() => _dispatcher.Invoke(() =>
    {
        _owner?.Dispose();
        _owner = null;
    });

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmRenderFormat when (uint)wParam == CfUnicodeText && _offered is { } text:
                // Inside WM_RENDERFORMAT the clipboard is already open for the asking app.
                SetUnicodeText(text);
                _rendered?.TrySetResult();
                handled = true;
                break;

            case WmRenderAllFormats when _offered is { } text:
                // Clicky is closing with the text still offered.
                if (OpenClipboard(hwnd))
                {
                    if (GetClipboardOwner() == hwnd)
                    {
                        SetUnicodeText(text);
                    }

                    CloseClipboard();
                }

                handled = true;
                break;
        }

        return 0;
    }

    private static unsafe ClipboardSnapshot Snapshot()
    {
        var formats = new List<(uint, byte[])>();
        long total = 0;
        for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
        {
            if (IsHandleFormat(format))
            {
                continue;
            }

            var handle = GetClipboardData(format);
            if (handle == 0)
            {
                continue;
            }

            var size = (long)GlobalSize(handle);
            total += size;
            if (total > MaxSnapshotBytes)
            {
                return new ClipboardSnapshot([], TooLarge: true);
            }

            var pointer = GlobalLock(handle);
            if (pointer == 0)
            {
                continue;
            }

            try
            {
                formats.Add((format, new ReadOnlySpan<byte>((void*)pointer, (int)size).ToArray()));
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        return new ClipboardSnapshot(formats, TooLarge: false);
    }

    /// <summary>Formats whose data is a GDI or owner handle, not memory that can be copied.</summary>
    private static bool IsHandleFormat(uint format) =>
        format is 2 /* CF_BITMAP */ or 3 /* CF_METAFILEPICT */ or 9 /* CF_PALETTE */ or 14 /* CF_ENHMETAFILE */
            or 0x80 /* CF_OWNERDISPLAY */ or 0x82 /* CF_DSPBITMAP */ or 0x83 /* CF_DSPMETAFILEPICT */ or 0x8E /* CF_DSPENHMETAFILE */
        || format is >= 0x200 and <= 0x3FF; // CF_PRIVATEFIRST..CF_GDIOBJLAST: app-defined handles

    private static void SetUnicodeText(string text)
    {
        var bytes = new byte[(text.Length + 1) * sizeof(char)];
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
        SetBytes(CfUnicodeText, bytes);
    }

    private static unsafe void SetBytes(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable, (nuint)Math.Max(1, data.Length));
        if (handle == 0)
        {
            return;
        }

        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            GlobalFree(handle);
            return;
        }

        data.CopyTo(new Span<byte>((void*)pointer, data.Length));
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == 0)
        {
            // Ownership passes to the system only on success.
            GlobalFree(handle);
        }
    }

    private async Task<T?> WithClipboardAsync<T>(Func<T> work, CancellationToken cancellationToken)
        where T : class
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            var (opened, result) = await _dispatcher.InvokeAsync(() =>
            {
                if (_owner is null)
                {
                    _owner = new HwndSource(new HwndSourceParameters("Clicky clipboard") { ParentWindow = new nint(-3) /* HWND_MESSAGE */ });
                    _owner.AddHook(WndProc);
                }

                if (!OpenClipboard(_owner.Handle))
                {
                    return (false, (T?)null);
                }

                try
                {
                    return (true, work());
                }
                finally
                {
                    CloseClipboard();
                }
            });

            if (opened)
            {
                return result;
            }

            await Task.Delay(OpenRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("Dictation: another app kept the clipboard open");
        return null;
    }
}

/// <summary>What was on the clipboard before Clicky pasted.</summary>
internal sealed record ClipboardSnapshot(IReadOnlyList<(uint Format, byte[] Data)> Formats, bool TooLarge);
