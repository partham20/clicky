using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Clickys;

/// <summary>
/// clickys.db: opening, schema migrations and recovery. The schema version lives in
/// PRAGMA user_version; each migration runs in its own transaction together with the version bump,
/// so a crash mid-upgrade leaves the previous version intact. An existing database is backed up
/// (clickys.db.v{N}.bak) before it is upgraded, and one that is not a database at all is moved
/// aside (clickys.db.corrupt-{time}) and started fresh rather than taking Home down.
/// </summary>
public sealed class ClickyDatabase
{
    public const string FileName = "clickys.db";

    /// <summary>
    /// Schema migrations in order: entry i takes the database from version i to i+1. Append only;
    /// never edit one that has shipped.
    /// </summary>
    public static IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE clickys (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            instructions TEXT NOT NULL DEFAULT '',
            notes TEXT NOT NULL DEFAULT '',
            color TEXT NOT NULL DEFAULT '#2F81F7',
            uses_computer INTEGER NOT NULL DEFAULT 0,
            max_steps INTEGER NOT NULL DEFAULT 30,
            unread INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            last_activity_at INTEGER NOT NULL
        );

        CREATE TABLE runs (
            id TEXT PRIMARY KEY,
            clicky_id TEXT NOT NULL REFERENCES clickys(id) ON DELETE CASCADE,
            trigger TEXT NOT NULL,
            prompt TEXT NOT NULL,
            status TEXT NOT NULL,
            queued_at INTEGER NOT NULL,
            started_at INTEGER,
            ended_at INTEGER,
            steps INTEGER NOT NULL DEFAULT 0,
            summary TEXT,
            error TEXT,
            model TEXT,
            input_tokens INTEGER NOT NULL DEFAULT 0,
            output_tokens INTEGER NOT NULL DEFAULT 0,
            cost_usd REAL,
            routine_id TEXT,
            routine_name TEXT,
            scheduled_for INTEGER
        );
        CREATE INDEX runs_by_clicky ON runs(clicky_id, queued_at DESC);
        CREATE INDEX runs_by_status ON runs(status);

        CREATE TABLE messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            clicky_id TEXT NOT NULL REFERENCES clickys(id) ON DELETE CASCADE,
            run_id TEXT REFERENCES runs(id) ON DELETE SET NULL,
            role TEXT NOT NULL,
            text TEXT NOT NULL,
            at INTEGER NOT NULL
        );
        CREATE INDEX messages_by_clicky ON messages(clicky_id, id);

        CREATE TABLE run_steps (
            run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            at INTEGER NOT NULL,
            kind TEXT NOT NULL,
            text TEXT NOT NULL,
            PRIMARY KEY (run_id, seq)
        ) WITHOUT ROWID;
        """,
    ];

    // SQLITE_CORRUPT and SQLITE_NOTADB.
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string _connectionString;

    private ClickyDatabase(string path, int version)
    {
        Path = path;
        Version = version;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>Schema version after opening.</summary>
    public int Version { get; }

    public static int LatestVersion => Migrations.Count;

    /// <summary>Opens (creating if needed) and migrates the database at <paramref name="path"/>.</summary>
    /// <exception cref="ClickyDatabaseException">It was written by a newer Clicky, or cannot be opened.</exception>
    public static Task<ClickyDatabase> OpenAsync(string path, ILogger logger, CancellationToken cancellationToken = default) =>
        OpenAsync(path, Migrations, logger, cancellationToken);

    /// <summary>Opens with a given migration list; tests use it to exercise upgrades.</summary>
    internal static async Task<ClickyDatabase> OpenAsync(string path, IReadOnlyList<string> migrations, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        try
        {
            return await MigrateAsync(path, migrations, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            var aside = MoveAside(path);
            logger.LogWarning(ex, "{Path} is damaged; moved it to {Aside} and starting a new one", path, aside);
            return await MigrateAsync(path, migrations, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw new ClickyDatabaseException($"Could not open {path}: {ex.Message}", ex);
        }
    }

    /// <summary>An open connection with foreign keys on. Dispose it when done; pooling keeps it cheap.</summary>
    public async Task<SqliteConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes pooled connections so the file can be moved or deleted (tests, uninstall).</summary>
    public static void ReleaseFiles() => SqliteConnection.ClearAllPools();

    private static async Task<ClickyDatabase> MigrateAsync(string path, IReadOnlyList<string> migrations, ILogger logger, CancellationToken cancellationToken)
    {
        var database = new ClickyDatabase(path, 0);
        await using var connection = await database.ConnectAsync(cancellationToken).ConfigureAwait(false);

        // WAL keeps Home's reads from waiting on a run's writes; the setting sticks to the file.
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

        if (version > migrations.Count)
        {
            throw new ClickyDatabaseException(
                $"{System.IO.Path.GetFileName(path)} was written by a newer version of Clicky (schema {version}; this one knows up to {migrations.Count}). Update Clicky to see your Clickys.");
        }

        if (version > 0 && version < migrations.Count)
        {
            var backup = $"{path}.v{version}.bak";
            await using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
            {
                await copy.OpenAsync(cancellationToken).ConfigureAwait(false);
                connection.BackupDatabase(copy);
            }

            logger.LogInformation("Backed up {Path} (schema {Version}) to {Backup} before upgrading", path, version, backup);
        }

        for (var next = version; next < migrations.Count; next++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, migrations[next], cancellationToken, transaction).ConfigureAwait(false);

            // PRAGMA takes no parameters; the value is our own integer.
            await ExecuteAsync(connection, $"PRAGMA user_version = {next + 1};", cancellationToken, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Clickys database {Path} migrated to schema {Version}", path, next + 1);
        }

        return new ClickyDatabase(path, migrations.Count);
    }

    private static string MoveAside(string path)
    {
        ReleaseFiles();
        var aside = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, aside, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(path + suffix))
            {
                File.Move(path + suffix, aside + suffix, overwrite: true);
            }
        }

        return aside;
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>clickys.db cannot be used. The message is written for the user.</summary>
public sealed class ClickyDatabaseException : Exception
{
    public ClickyDatabaseException()
    {
    }

    public ClickyDatabaseException(string message)
        : base(message)
    {
    }

    public ClickyDatabaseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
