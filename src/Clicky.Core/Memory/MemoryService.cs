using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Memory;

/// <summary>
/// Reads memory into prompts and runs the updater pass. Turns queue up until the conversation has been
/// quiet for settings.memory.idleMinutes, or until a turn looks like feedback (<see cref="FeedbackCue"/>);
/// then one pass sends the queued turns and the current files to <see cref="IMemoryModel"/> and merges
/// the proposed edits through <see cref="MemoryStore"/>. One pass at a time; turns that arrive during a
/// pass wait for the next. A failed model call keeps its turns queued (bounded) for the next pass.
/// </summary>
public sealed class MemoryService : IMemoryService, IDisposable
{
    /// <summary>Most turns one pass looks at; older queued turns are dropped first.</summary>
    public const int MaxQueuedTurns = 40;

    private readonly MemoryStore _store;
    private readonly ISettingsStore _settings;
    private readonly IMemoryModel? _model;
    private readonly TimeProvider _time;
    private readonly ILogger<MemoryService> _logger;
    private readonly object _lock = new();
    private readonly List<MemoryTurn> _queue = [];
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly CancellationTokenSource _disposing = new();
    private readonly CancellationToken _stopping;
    private readonly ITimer _idle;
    private bool _queueHasFeedback;
    private int _warnedNoModel;
    private volatile bool _disposed;

    /// <param name="model">Null until a Claude backend provides one; memory is then read but not updated.</param>
    public MemoryService(MemoryStore store, ISettingsStore settings, ILogger<MemoryService> logger, IMemoryModel? model = null, TimeProvider? time = null)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
        _model = model;
        _time = time ?? TimeProvider.System;
        _stopping = _disposing.Token;
        _idle = _time.CreateTimer(_ => _ = RunPassAsync(false, _stopping), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<MemoryUpdatedEventArgs>? Updated;

    public MemoryStore Store => _store;

    /// <summary>Turns waiting for the next updater pass.</summary>
    public int QueuedTurns
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    private MemorySettings Settings => _settings.Current.Memory;

    public async Task<string> GetPromptAsync(CancellationToken cancellationToken = default)
    {
        if (!Settings.Enabled)
        {
            return string.Empty;
        }

        try
        {
            var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            return MemoryPrompt.Build(snapshot, _store.Limits, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An answer without memory beats no answer.
            _logger.LogWarning(ex, "Could not read memory; answering without it");
            return string.Empty;
        }
    }

    public void RecordTurn(MemoryTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (_disposed || !Settings.Enabled || !Settings.AutoUpdate || string.IsNullOrWhiteSpace(turn.User))
        {
            return;
        }

        if (_model is null)
        {
            if (Interlocked.Exchange(ref _warnedNoModel, 1) == 0)
            {
                _logger.LogInformation("Memory updates wait for a Claude backend; memory is read-only until then");
            }

            return;
        }

        var feedback = FeedbackCue.IsFeedback(turn.User);
        lock (_lock)
        {
            _queue.Add(turn);
            if (_queue.Count > MaxQueuedTurns)
            {
                _queue.RemoveRange(0, _queue.Count - MaxQueuedTurns);
            }

            _queueHasFeedback |= feedback;
        }

        if (feedback)
        {
            _logger.LogDebug("Feedback heard; updating memory now");
            _idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _ = RunPassAsync(true, _stopping);
        }
        else
        {
            _idle.Change(TimeSpan.FromMinutes(Settings.IdleMinutes), Timeout.InfiniteTimeSpan);
        }
    }

    public Task EndConversationAsync(CancellationToken cancellationToken = default)
    {
        _idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return RunPassAsync(false, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idle.Dispose();
        _disposing.Cancel();
        _disposing.Dispose();
    }

    /// <summary>Runs one pass over everything queued. Never throws except for cancellation by the caller.</summary>
    internal async Task<MergeResult?> RunPassAsync(bool fromFeedback, CancellationToken cancellationToken)
    {
        if (_model is null || _disposed)
        {
            return null;
        }

        try
        {
            await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        MemoryTurn[] turns = [];
        try
        {
            bool hadFeedback;
            lock (_lock)
            {
                turns = [.. _queue];
                hadFeedback = _queueHasFeedback;
                _queue.Clear();
                _queueHasFeedback = false;
            }

            if (turns.Length == 0 || !Settings.Enabled || !Settings.AutoUpdate)
            {
                return null;
            }

            var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var user = MemoryUpdaterPrompt.User(snapshot.Memory, turns, _store.Limits, _time.GetUtcNow());
            var started = _time.GetTimestamp();
            var reply = await _model.CompleteAsync(MemoryUpdaterPrompt.System, user, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<MemoryEdit> edits;
            try
            {
                edits = MemoryEditParser.Parse(reply);
            }
            catch (FormatException ex)
            {
                // Not retried: the same turns would most likely confuse it the same way.
                _logger.LogWarning(ex, "Memory update reply was unusable: {Reply}", reply.Length > 300 ? reply[..300] : reply);
                return null;
            }

            var result = await _store.ApplyAsync(edits, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Memory pass over {Turns} turns in {Ms:0} ms: {Applied} applied, {Rejected} refused",
                turns.Length,
                _time.GetElapsedTime(started).TotalMilliseconds,
                result.Applied.Count,
                result.Rejected.Count);
            foreach (var edit in result.Applied)
            {
                _logger.LogDebug("Memory: {Edit}", edit);
            }

            foreach (var rejected in result.Rejected)
            {
                _logger.LogDebug("Memory refused ({Reason}): {Edit}", rejected.Reason, rejected.Edit);
            }

            if (result.Applied.Count > 0)
            {
                RaiseUpdated(result, fromFeedback || hadFeedback);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            Requeue(turns);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Backend down, network gone, file locked: try these turns again with the next pass.
            _logger.LogWarning(ex, "Memory update failed; will retry with the next conversation");
            Requeue(turns);
            return null;
        }
        finally
        {
            _pass.Release();
        }
    }

    private void Requeue(MemoryTurn[] turns)
    {
        if (turns.Length == 0 || _disposed)
        {
            return;
        }

        lock (_lock)
        {
            _queue.InsertRange(0, turns);
            if (_queue.Count > MaxQueuedTurns)
            {
                _queue.RemoveRange(0, _queue.Count - MaxQueuedTurns);
            }
        }
    }

    private void RaiseUpdated(MergeResult result, bool fromFeedback)
    {
        try
        {
            Updated?.Invoke(this, new MemoryUpdatedEventArgs(result, fromFeedback));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "A memory update handler failed");
        }
    }
}
