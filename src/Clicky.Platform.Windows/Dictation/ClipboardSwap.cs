using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Dictation;

/// <summary>
/// The clipboard side of pasting dictation: keeps a copy of whatever the user had on the
/// clipboard, puts the dictated text there (marked so clipboard history and cloud clipboard skip
/// it), and afterwards puts the user's content back unless something else replaced it meanwhile.
/// </summary>
/// <remarks>
/// <para>Clipboard calls run on the UI thread, which owns a message-only window: SetClipboardData
/// fails without an owner window, and the owner must pump messages or apps that empty the
/// clipboard hang waiting for it. Each call is a few milliseconds; a clipboard held open by
/// another app is retried without blocking.</para>
/// <para>Only memory formats are kept (text, HTML, RTF, DIB images, files, app formats). GDI handles
/// (CF_BITMAP, metafiles, palettes) are not copied; Windows rebuilds CF_BITMAP from CF_DIB.
/// Anything bigger than <see cref="MaxSnapshotBytes"/> in total is not restored.</para>
/// </remarks>
public sealed class ClipboardSwap : IDisposable
{
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(20);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly uint _excludeFromMonitoring;
    private readonly uint _canIncludeInHistory;
    private readonly uint _canUploadToCloud;
    private HwndSource? _owner;

    public ClipboardSwap(Dispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _excludeFromMonitoring = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _canIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        _canUploadToCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
    }

    /// <summary>
    /// Saves the current clipboard and puts <paramref name="text"/> on it. Returns the saved content
    /// and the clipboard sequence number after the change, or null if the clipboard could not be opened.
    /// </summary>
    /// <param name="private">Keep the text out of clipboard history and cloud clipboard (true while pasting).</param>
    public async Task<ClipboardSnapshot?> PutTextAsync(string text, bool @private, bool keepSnapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return await WithClipboardAsync(
            () =>
            {
                var snapshot = keepSnapshot ? Snapshot() : null;
                EmptyClipboard();
                SetUnicodeText(text);
                if (@private)
                {
                    SetBytes(_excludeFromMonitoring, [0]);
                    SetBytes(_canIncludeInHistory, [0, 0, 0, 0]);
                    SetBytes(_canUploadToCloud, [0, 0, 0, 0]);
                }

                return snapshot ?? ClipboardSnapshot.Empty;
            },
            cancellationToken).ConfigureAwait(false) is { } saved
            ? saved with { Sequence = GetClipboardSequenceNumber() }
            : null;
    }

    /// <summary>
    /// Puts <paramref name="snapshot"/> back, unless the clipboard changed since
    /// <see cref="ClipboardSnapshot.Sequence"/> (the user or an app copied something new).
    /// </summary>
    public async Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TooLarge)
        {
            _logger.LogInformation("Dictation: the clipboard held more than {Mb} MB, so it was not put back after pasting", MaxSnapshotBytes / (1024 * 1024));
            return;
        }

        if (GetClipboardSequenceNumber() != snapshot.Sequence)
        {
            _logger.LogDebug("Dictation: the clipboard changed while pasting; leaving it alone");
            return;
        }

        await WithClipboardAsync(
            () =>
            {
                EmptyClipboard();
                foreach (var (format, data) in snapshot.Formats)
                {
                    SetBytes(format, data);
                }

                return snapshot;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _dispatcher.Invoke(() =>
    {
        _owner?.Dispose();
        _owner = null;
    });

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
                return new ClipboardSnapshot([], 0, TooLarge: true);
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

        return new ClipboardSnapshot(formats, 0, TooLarge: false);
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
                _owner ??= new HwndSource(new HwndSourceParameters("Clicky clipboard") { ParentWindow = new nint(-3) /* HWND_MESSAGE */ });
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
public sealed record ClipboardSnapshot(IReadOnlyList<(uint Format, byte[] Data)> Formats, uint Sequence, bool TooLarge)
{
    public static ClipboardSnapshot Empty { get; } = new([], 0, TooLarge: false);
}
