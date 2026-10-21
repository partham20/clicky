using Microsoft.Extensions.Logging;

namespace Clicky.Core.Dictation;

/// <summary>
/// After an insertion, reads the field back every <see cref="PollInterval"/> for
/// <see cref="Window"/> and teaches the personal vocabulary any word the user fixed by hand.
/// Only the latest insertion is watched: a new one (or a new dictation) ends the previous watch,
/// whose last snapshot still counts.
/// </summary>
public sealed class CorrectionWatcher(PersonalVocabulary vocabulary, ILogger<CorrectionWatcher> logger, TimeProvider? time = null)
{
    /// <summary>The plan's "edits made within ~20 s of insertion".</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _current;

    /// <summary>Starts watching <paramref name="probe"/>, ending any earlier watch. Returns the watch, for tests.</summary>
    public Task Watch(IInsertedTextProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref _current, cancel)?.Cancel();
        return Task.Run(() => RunAsync(probe, cancel), CancellationToken.None);
    }

    /// <summary>Ends the current watch, learning from what it has seen so far.</summary>
    public void Stop() => Interlocked.Exchange(ref _current, null)?.Cancel();

    private async Task RunAsync(IInsertedTextProbe probe, CancellationTokenSource cancel)
    {
        Correction? found = null;
        try
        {
            var started = _time.GetTimestamp();
            while (_time.GetElapsedTime(started) < Window)
            {
                await Task.Delay(PollInterval, _time, cancel.Token).ConfigureAwait(false);
                var text = await probe.ReadAsync(cancel.Token).ConfigureAwait(false);
                if (text is null)
                {
                    break;
                }

                // The latest state wins: a fix that was itself undone is no fix.
                found = CorrectionLearner.Find(probe.TextAfterInsert, probe.InsertStart, probe.InsertLength, text);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "Dictation: could not read the field back");
        }
        finally
        {
            Interlocked.CompareExchange(ref _current, null, cancel);
            cancel.Dispose();
        }

        if (found is not null)
        {
            vocabulary.Learn(found.Heard, found.Written);
        }
    }
}
