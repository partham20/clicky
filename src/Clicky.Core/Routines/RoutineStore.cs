using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Routines;

/// <summary>
/// Routines and their run history in their own SQLite file (%AppData%\Clicky\routines.db), apart
/// from the Clickys database so the two modules never migrate each other's tables. One connection,
/// used one call at a time on the thread pool, so callers on the UI thread never block on disk.
/// </summary>
public sealed class RoutineStore : IAsyncDisposable
{
    /// <summary>Runs kept per routine; older ones are pruned after each run.</summary>
    public const int KeepRunsPerRoutine = 50;

    /// <summary>Result text kept per run; longer output is cut (the Clicky's chat has the whole thing).</summary>
    public const int MaxOutputChars = 20_000;

    private const int SchemaVersion = 1;

    private readonly string _path;
    private readonly ILogger<RoutineStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public RoutineStore(string path, ILogger<RoutineStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = logger;
    }

    public string FilePath => _path;

    public Task<IReadOnlyList<Routine>> ListAsync(CancellationToken cancellationToken = default) => UseAsync<IReadOnlyList<Routine>>(db =>
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT * FROM routines ORDER BY created_at, id";
        using var reader = command.ExecuteReader();
        var routines = new List<Routine>();
        while (reader.Read())
        {
            if (ReadRoutine(reader) is { } routine)
            {
                routines.Add(routine);
            }
        }

        return routines;
    }, cancellationToken);

    public Task SaveAsync(Routine routine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routine);
        return UseAsync(db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO routines (id, name, prompt, clicky_id, schedule, enabled, paused_reason, consecutive_failures,
                                      created_at, updated_at, anchor_at, next_due_at, waiting_since, last_run_at, last_status, last_message)
                VALUES ($id, $name, $prompt, $clicky, $schedule, $enabled, $paused, $failures,
                        $created, $updated, $anchor, $next, $waiting, $lastRun, $lastStatus, $lastMessage)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, prompt = excluded.prompt, clicky_id = excluded.clicky_id, schedule = excluded.schedule,
                    enabled = excluded.enabled, paused_reason = excluded.paused_reason, consecutive_failures = excluded.consecutive_failures,
                    updated_at = excluded.updated_at, anchor_at = excluded.anchor_at, next_due_at = excluded.next_due_at,
                    waiting_since = excluded.waiting_since, last_run_at = excluded.last_run_at, last_status = excluded.last_status,
                    last_message = excluded.last_message
                """;
            command.Parameters.AddWithValue("$id", routine.Id);
            command.Parameters.AddWithValue("$name", routine.Name);
            command.Parameters.AddWithValue("$prompt", routine.Prompt);
            command.Parameters.AddWithValue("$clicky", Db(routine.ClickyId));
            command.Parameters.AddWithValue("$schedule", JsonSerializer.Serialize(routine.Schedule, RoutineJsonContext.Default.RoutineSchedule));
            command.Parameters.AddWithValue("$enabled", routine.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$paused", Db(routine.PausedReason));
            command.Parameters.AddWithValue("$failures", routine.ConsecutiveFailures);
            command.Parameters.AddWithValue("$created", Db(routine.CreatedAt));
            command.Parameters.AddWithValue("$updated", Db(routine.UpdatedAt));
            command.Parameters.AddWithValue("$anchor", Db(routine.AnchorAt));
            command.Parameters.AddWithValue("$next", Db(routine.NextDueAt));
            command.Parameters.AddWithValue("$waiting", Db(routine.WaitingSince));
            command.Parameters.AddWithValue("$lastRun", Db(routine.LastRunAt));
            command.Parameters.AddWithValue("$lastStatus", Db(routine.LastStatus?.ToString()));
            command.Parameters.AddWithValue("$lastMessage", Db(routine.LastMessage));
            command.ExecuteNonQuery();
            return true;
        }, cancellationToken);
    }

    /// <summary>Deletes a routine and its history. False if it wasn't there.</summary>
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => UseAsync(db =>
    {
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM routines WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }, cancellationToken);

    /// <summary>Adds a run to the history and returns its id.</summary>
    public Task<long> AddRunAsync(RoutineRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return UseAsync(db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO routine_runs (routine_id, scheduled_for, started_at, finished_at, status, trigger, message, output)
                VALUES ($routine, $scheduled, $started, $finished, $status, $trigger, $message, $output);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$routine", run.RoutineId);
            command.Parameters.AddWithValue("$scheduled", Db(run.ScheduledFor));
            command.Parameters.AddWithValue("$started", Db(run.StartedAt));
            command.Parameters.AddWithValue("$finished", Db(run.FinishedAt));
            command.Parameters.AddWithValue("$status", run.Status.ToString());
            command.Parameters.AddWithValue("$trigger", run.Trigger.ToString());
            command.Parameters.AddWithValue("$message", Db(run.Message));
            command.Parameters.AddWithValue("$output", Db(Cut(run.Output)));
            return (long)command.ExecuteScalar()!;
        }, cancellationToken);
    }

    /// <summary>Records how a run ended and prunes the routine's history to <see cref="KeepRunsPerRoutine"/>.</summary>
    public Task FinishRunAsync(long runId, string routineId, RoutineRunStatus status, DateTimeOffset finishedAt, string? message, string? output, CancellationToken cancellationToken = default) =>
        UseAsync(db =>
        {
            using var transaction = db.BeginTransaction();
            using (var update = db.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE routine_runs SET status = $status, finished_at = $finished, message = $message, output = $output WHERE id = $id";
                update.Parameters.AddWithValue("$status", status.ToString());
                update.Parameters.AddWithValue("$finished", Db(finishedAt));
                update.Parameters.AddWithValue("$message", Db(message));
                update.Parameters.AddWithValue("$output", Db(Cut(output)));
                update.Parameters.AddWithValue("$id", runId);
                update.ExecuteNonQuery();
            }

            Prune(db, transaction, routineId);
            transaction.Commit();
            return true;
        }, cancellationToken);

    /// <summary>The newest runs first.</summary>
    public Task<IReadOnlyList<RoutineRun>> ListRunsAsync(string routineId, int limit = KeepRunsPerRoutine, CancellationToken cancellationToken = default) =>
        UseAsync<IReadOnlyList<RoutineRun>>(db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT * FROM routine_runs WHERE routine_id = $routine ORDER BY id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$routine", routineId);
            command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            using var reader = command.ExecuteReader();
            var runs = new List<RoutineRun>();
            while (reader.Read())
            {
                runs.Add(new RoutineRun(
                    reader.GetInt64(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("routine_id")),
                    ReadTime(reader, "scheduled_for") ?? DateTimeOffset.MinValue,
                    ReadTime(reader, "started_at") ?? DateTimeOffset.MinValue,
                    ReadTime(reader, "finished_at"),
                    Enum.TryParse<RoutineRunStatus>(ReadText(reader, "status"), out var status) ? status : RoutineRunStatus.Failed,
                    Enum.TryParse<RoutineTrigger>(ReadText(reader, "trigger"), out var trigger) ? trigger : RoutineTrigger.Schedule,
                    ReadText(reader, "message"),
                    ReadText(reader, "output")));
            }

            return runs;
        }, cancellationToken);

    /// <summary>
    /// Runs still marked Running from a session that ended without finishing them (a crash, a
    /// power cut): they become Interrupted so the history doesn't show them running forever.
    /// </summary>
    public Task<int> CloseAbandonedRunsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => UseAsync(db =>
    {
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE routine_runs SET status = $status, finished_at = $now, message = COALESCE(message, 'Clicky closed before it finished.') WHERE status = 'Running'";
        command.Parameters.AddWithValue("$status", nameof(RoutineRunStatus.Interrupted));
        command.Parameters.AddWithValue("$now", Db(now));
        return command.ExecuteNonQuery();
    }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> UseAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Off the caller's thread: Microsoft.Data.Sqlite's async methods run synchronously.
            return await Task.Run(() => work(Open()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null)
        {
            return _connection;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        try
        {
            Migrate(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
        return connection;
    }

    private void Migrate(SqliteConnection db)
    {
        using (var pragma = db.CreateCommand())
        {
            // WAL: a reader (the history window) never waits on a run being recorded.
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 3000;";
            pragma.ExecuteNonQuery();
        }

        long version;
        using (var read = db.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version";
            version = (long)read.ExecuteScalar()!;
        }

        if (version >= SchemaVersion)
        {
            return;
        }

        using var transaction = db.BeginTransaction();
        using (var create = db.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = $"""
                CREATE TABLE IF NOT EXISTS routines (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    prompt TEXT NOT NULL,
                    clicky_id TEXT NULL,
                    schedule TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    paused_reason TEXT NULL,
                    consecutive_failures INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    anchor_at TEXT NOT NULL,
                    next_due_at TEXT NULL,
                    waiting_since TEXT NULL,
                    last_run_at TEXT NULL,
                    last_status TEXT NULL,
                    last_message TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS routine_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    routine_id TEXT NOT NULL REFERENCES routines(id) ON DELETE CASCADE,
                    scheduled_for TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    status TEXT NOT NULL,
                    trigger TEXT NOT NULL,
                    message TEXT NULL,
                    output TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS routine_runs_by_routine ON routine_runs (routine_id, id);
                PRAGMA user_version = {SchemaVersion};
                """;
            create.ExecuteNonQuery();
        }

        transaction.Commit();
        _logger.LogInformation("Routines database ready at {Path} (schema {Version})", _path, SchemaVersion);
    }

    private static void Prune(SqliteConnection db, SqliteTransaction transaction, string routineId)
    {
        using var prune = db.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = """
            DELETE FROM routine_runs WHERE routine_id = $routine AND id NOT IN
                (SELECT id FROM routine_runs WHERE routine_id = $routine ORDER BY id DESC LIMIT $keep)
            """;
        prune.Parameters.AddWithValue("$routine", routineId);
        prune.Parameters.AddWithValue("$keep", KeepRunsPerRoutine);
        prune.ExecuteNonQuery();
    }

    private Routine? ReadRoutine(SqliteDataReader reader)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        RoutineSchedule? schedule;
        try
        {
            schedule = JsonSerializer.Deserialize(reader.GetString(reader.GetOrdinal("schedule")), RoutineJsonContext.Default.RoutineSchedule);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Routine {Id} has an unreadable schedule; skipping it", id);
            return null;
        }

        if (schedule is null)
        {
            return null;
        }

        var created = ReadTime(reader, "created_at") ?? DateTimeOffset.UnixEpoch;
        return new Routine
        {
            Id = id,
            Name = reader.GetString(reader.GetOrdinal("name")),
            Prompt = reader.GetString(reader.GetOrdinal("prompt")),
            ClickyId = ReadText(reader, "clicky_id"),
            Schedule = schedule,
            Enabled = reader.GetInt64(reader.GetOrdinal("enabled")) != 0,
            PausedReason = ReadText(reader, "paused_reason"),
            ConsecutiveFailures = (int)reader.GetInt64(reader.GetOrdinal("consecutive_failures")),
            CreatedAt = created,
            UpdatedAt = ReadTime(reader, "updated_at") ?? created,
            AnchorAt = ReadTime(reader, "anchor_at") ?? created,
            NextDueAt = ReadTime(reader, "next_due_at"),
            WaitingSince = ReadTime(reader, "waiting_since"),
            LastRunAt = ReadTime(reader, "last_run_at"),
            LastStatus = Enum.TryParse<RoutineRunStatus>(ReadText(reader, "last_status"), out var status) ? status : null,
            LastMessage = ReadText(reader, "last_message"),
        };
    }

    private static string? ReadText(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadTime(SqliteDataReader reader, string column) =>
        ReadText(reader, column) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    private static object Db(string? value) => (object?)value ?? DBNull.Value;

    // Round-trip UTC text sorts correctly as a string.
    private static object Db(DateTimeOffset? value) => value is { } v ? v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;

    private static string? Cut(string? text) => text is { Length: > MaxOutputChars } ? text[..MaxOutputChars] + "\n…" : text;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RoutineSchedule))]
internal sealed partial class RoutineJsonContext : JsonSerializerContext;
