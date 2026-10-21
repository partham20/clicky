using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Clicky.Core.Clickys;

/// <summary>
/// Rows of clickys.db as records. No business rules here (see <see cref="ClickyService"/>); every
/// method opens a pooled connection, so the store is thread-safe. Times are stored as Unix
/// milliseconds, enums by name.
/// </summary>
public sealed class ClickyStore(ClickyDatabase database)
{
    private const string ClickyColumns = "id, name, instructions, notes, color, uses_computer, max_steps, unread, created_at, updated_at, last_activity_at";

    private const string RunColumns =
        "id, clicky_id, trigger, prompt, status, queued_at, started_at, ended_at, steps, summary, error, model, " +
        "input_tokens, output_tokens, cost_usd, routine_id, routine_name, scheduled_for";

    public ClickyDatabase Database { get; } = database;

    public async Task<IReadOnlyList<ClickyAgent>> ListClickysAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, $"SELECT {ClickyColumns} FROM clickys ORDER BY last_activity_at DESC, name COLLATE NOCASE;");
        return await ReadAllAsync(command, ReadClicky, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClickyAgent?> GetClickyAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, $"SELECT {ClickyColumns} FROM clickys WHERE id = $id;", ("$id", id));
        var found = await ReadAllAsync(command, ReadClicky, cancellationToken).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    public async Task InsertClickyAsync(ClickyAgent clicky, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            $"INSERT INTO clickys ({ClickyColumns}) VALUES ($id, $name, $instructions, $notes, $color, $computer, $steps, $unread, $created, $updated, $activity);",
            ("$id", clicky.Id),
            ("$name", clicky.Name),
            ("$instructions", clicky.Instructions),
            ("$notes", clicky.Notes),
            ("$color", clicky.Color),
            ("$computer", clicky.UsesComputer),
            ("$steps", clicky.MaxSteps),
            ("$unread", clicky.Unread),
            ("$created", Time(clicky.CreatedAt)),
            ("$updated", Time(clicky.UpdatedAt)),
            ("$activity", Time(clicky.LastActivityAt)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>False if there is no such Clicky.</returns>
    public async Task<bool> UpdateClickyAsync(string id, ClickyDraft draft, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            "UPDATE clickys SET name = $name, instructions = $instructions, notes = $notes, color = $color, uses_computer = $computer, " +
            "max_steps = $steps, updated_at = $now WHERE id = $id;",
            ("$id", id),
            ("$name", draft.Name),
            ("$instructions", draft.Instructions),
            ("$notes", draft.Notes),
            ("$color", draft.Color),
            ("$computer", draft.UsesComputer),
            ("$steps", draft.MaxSteps),
            ("$now", Time(now)));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Deletes the Clicky with its chat, runs and activity (cascade).</summary>
    public async Task<bool> DeleteClickyAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, "DELETE FROM clickys WHERE id = $id;", ("$id", id));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task MarkReadAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, "UPDATE clickys SET unread = 0 WHERE id = $id AND unread <> 0;", ("$id", id));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a chat message and moves the Clicky to the top of Home; replies also count as unread.</summary>
    public async Task<ClickyMessage> AddMessageAsync(string clickyId, string? runId, ClickyMessageRole role, string text, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long id;
        await using (var insert = Command(
            connection,
            "INSERT INTO messages (clicky_id, run_id, role, text, at) VALUES ($clicky, $run, $role, $text, $at) RETURNING id;",
            ("$clicky", clickyId),
            ("$run", runId),
            ("$role", role.ToString()),
            ("$text", text),
            ("$at", Time(at))))
        {
            insert.Transaction = transaction;
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await using (var touch = Command(
            connection,
            "UPDATE clickys SET last_activity_at = MAX(last_activity_at, $at), unread = unread + $unread WHERE id = $clicky;",
            ("$clicky", clickyId),
            ("$at", Time(at)),
            ("$unread", role is ClickyMessageRole.Assistant or ClickyMessageRole.System ? 1 : 0)))
        {
            touch.Transaction = transaction;
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ClickyMessage(id, clickyId, runId, role, text, at);
    }

    /// <summary>The newest <paramref name="limit"/> messages, oldest first.</summary>
    public async Task<IReadOnlyList<ClickyMessage>> GetMessagesAsync(string clickyId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            "SELECT id, clicky_id, run_id, role, text, at FROM " +
            "(SELECT * FROM messages WHERE clicky_id = $clicky ORDER BY id DESC LIMIT $limit) ORDER BY id;",
            ("$clicky", clickyId),
            ("$limit", Math.Max(0, limit)));
        return await ReadAllAsync(
            command,
            r => new ClickyMessage(r.GetInt64(0), r.GetString(1), NullableString(r, 2), Parse<ClickyMessageRole>(r.GetString(3)), r.GetString(4), FromTime(r.GetInt64(5))),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task InsertRunAsync(ClickyRun run, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            $"INSERT INTO runs ({RunColumns}) VALUES ($id, $clicky, $trigger, $prompt, $status, $queued, $started, $ended, $steps, $summary, $error, $model, " +
            "$input, $output, $cost, $routine, $routineName, $scheduled);",
            RunParameters(run));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes every mutable column of <paramref name="run"/>.</summary>
    public async Task UpdateRunAsync(ClickyRun run, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            "UPDATE runs SET status = $status, started_at = $started, ended_at = $ended, steps = $steps, summary = $summary, error = $error, " +
            "model = $model, input_tokens = $input, output_tokens = $output, cost_usd = $cost WHERE id = $id;",
            RunParameters(run));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClickyRun?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, $"SELECT {RunColumns} FROM runs WHERE id = $id;", ("$id", runId));
        var found = await ReadAllAsync(command, ReadRun, cancellationToken).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>The newest <paramref name="limit"/> runs of a Clicky, newest first.</summary>
    public async Task<IReadOnlyList<ClickyRun>> GetRunsAsync(string clickyId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            $"SELECT {RunColumns} FROM runs WHERE clicky_id = $clicky ORDER BY queued_at DESC, rowid DESC LIMIT $limit;",
            ("$clicky", clickyId),
            ("$limit", Math.Max(0, limit)));
        return await ReadAllAsync(command, ReadRun, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks every run still queued, running or paused as interrupted (Clicky quit or crashed).</summary>
    /// <returns>The runs that were changed.</returns>
    public async Task<IReadOnlyList<ClickyRun>> InterruptActiveRunsAsync(DateTimeOffset now, string error, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            "UPDATE runs SET status = $interrupted, ended_at = $now, error = COALESCE(error, $error) " +
            $"WHERE status IN ('Queued', 'Running', 'Paused') RETURNING {RunColumns};",
            ("$interrupted", nameof(ClickyRunStatus.Interrupted)),
            ("$now", Time(now)),
            ("$error", error));
        return await ReadAllAsync(command, ReadRun, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends a line to a run's activity log.</summary>
    public async Task<ClickyRunStep> AddStepAsync(string runId, DateTimeOffset at, ClickyStepKind kind, string text, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            "INSERT INTO run_steps (run_id, seq, at, kind, text) " +
            "SELECT $run, COALESCE(MAX(seq), 0) + 1, $at, $kind, $text FROM run_steps WHERE run_id = $run RETURNING seq;",
            ("$run", runId),
            ("$at", Time(at)),
            ("$kind", kind.ToString()),
            ("$text", text));
        var seq = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new ClickyRunStep(runId, seq, at, kind, text);
    }

    public async Task<IReadOnlyList<ClickyRunStep>> GetStepsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await Database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, "SELECT run_id, seq, at, kind, text FROM run_steps WHERE run_id = $run ORDER BY seq;", ("$run", runId));
        return await ReadAllAsync(
            command,
            r => new ClickyRunStep(r.GetString(0), r.GetInt32(1), FromTime(r.GetInt64(2)), Parse<ClickyStepKind>(r.GetString(3)), r.GetString(4)),
            cancellationToken).ConfigureAwait(false);
    }

    internal static long Time(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromTime(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static (string, object?)[] RunParameters(ClickyRun run) =>
    [
        ("$id", run.Id),
        ("$clicky", run.ClickyId),
        ("$trigger", run.Trigger.ToString()),
        ("$prompt", run.Prompt),
        ("$status", run.Status.ToString()),
        ("$queued", Time(run.QueuedAt)),
        ("$started", run.StartedAt is { } started ? Time(started) : null),
        ("$ended", run.EndedAt is { } ended ? Time(ended) : null),
        ("$steps", run.Steps),
        ("$summary", run.Summary),
        ("$error", run.Error),
        ("$model", run.Model),
        ("$input", run.InputTokens),
        ("$output", run.OutputTokens),
        ("$cost", run.CostUsd is { } cost ? (double)cost : null),
        ("$routine", run.RoutineId),
        ("$routineName", run.RoutineName),
        ("$scheduled", run.ScheduledFor is { } scheduled ? Time(scheduled) : null),
    ];

    private static ClickyAgent ReadClicky(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetInt64(5) != 0,
        r.GetInt32(6),
        r.GetInt32(7),
        FromTime(r.GetInt64(8)),
        FromTime(r.GetInt64(9)),
        FromTime(r.GetInt64(10)));

    private static ClickyRun ReadRun(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        Parse<ClickyRunTrigger>(r.GetString(2)),
        r.GetString(3),
        Parse<ClickyRunStatus>(r.GetString(4)),
        FromTime(r.GetInt64(5)),
        NullableTime(r, 6),
        NullableTime(r, 7),
        r.GetInt32(8),
        NullableString(r, 9),
        NullableString(r, 10),
        NullableString(r, 11),
        r.GetInt64(12),
        r.GetInt64(13),
        r.IsDBNull(14) ? null : (decimal)r.GetDouble(14),
        NullableString(r, 15),
        NullableString(r, 16),
        NullableTime(r, 17));

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? NullableTime(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : FromTime(r.GetInt64(i));

    // Unknown names (a newer Clicky wrote them) read as the first value rather than failing the whole list.
    private static T Parse<T>(string value)
        where T : struct, Enum => Enum.TryParse<T>(value, ignoreCase: false, out var parsed) ? parsed : default;

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(SqliteCommand command, Func<SqliteDataReader, T> read, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(read(reader));
        }

        return results;
    }
}
