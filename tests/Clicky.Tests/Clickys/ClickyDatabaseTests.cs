using Clicky.Core.Clickys;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Clickys;

public sealed class ClickyDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, ClickyDatabase.FileName);

    public void Dispose()
    {
        ClickyDatabase.ReleaseFiles();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Fresh_open_creates_the_latest_schema()
    {
        var database = await ClickyDatabase.OpenAsync(DbPath, NullLogger.Instance);

        Assert.Equal(ClickyDatabase.LatestVersion, database.Version);
        Assert.Equal(Path.GetFullPath(DbPath), database.Path);
        Assert.Equal(ClickyDatabase.LatestVersion, await UserVersionAsync(database));
        Assert.Equal(
            ["clickys", "messages", "run_steps", "runs"],
            await ScalarsAsync(database, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"));
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    [Fact]
    public async Task Reopening_is_a_no_op_and_keeps_data()
    {
        var first = await ClickyDatabase.OpenAsync(DbPath, NullLogger.Instance);
        var store = new ClickyStore(first);
        var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
        await store.InsertClickyAsync(new ClickyAgent("ab12", "Kept", "", "", ClickyDraft.DefaultColor, false, 30, 0, now, now, now));

        var second = await ClickyDatabase.OpenAsync(DbPath, NullLogger.Instance);

        Assert.Equal(ClickyDatabase.LatestVersion, second.Version);
        Assert.Equal("Kept", (await new ClickyStore(second).GetClickyAsync("ab12"))?.Name);
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public async Task A_database_from_a_newer_clicky_is_refused()
    {
        await SetUserVersionAsync(ClickyDatabase.LatestVersion + 1);

        var ex = await Assert.ThrowsAsync<ClickyDatabaseException>(() => ClickyDatabase.OpenAsync(DbPath, NullLogger.Instance));

        Assert.Contains("newer version of Clicky", ex.Message, StringComparison.Ordinal);

        // Left alone for the newer Clicky: not moved aside, not downgraded.
        Assert.True(File.Exists(DbPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
        ClickyDatabase.ReleaseFiles();
        Assert.Equal(ClickyDatabase.LatestVersion + 1, await ReadUserVersionAsync());
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_moved_aside_and_replaced()
    {
        Directory.CreateDirectory(_dir);
        var garbage = string.Concat(Enumerable.Repeat("this is definitely not an SQLite file. ", 200));
        await File.WriteAllTextAsync(DbPath, garbage);

        var database = await ClickyDatabase.OpenAsync(DbPath, NullLogger.Instance);

        Assert.Equal(ClickyDatabase.LatestVersion, database.Version);
        var aside = Assert.Single(Directory.GetFiles(_dir, ClickyDatabase.FileName + ".corrupt-*"), f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal));
        Assert.Equal(garbage, await File.ReadAllTextAsync(aside));

        // The fresh database works.
        var store = new ClickyStore(database);
        var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
        await store.InsertClickyAsync(new ClickyAgent("cd34", "Fresh", "", "", ClickyDraft.DefaultColor, false, 30, 0, now, now, now));
        Assert.Single(await store.ListClickysAsync());
    }

    [Fact]
    public async Task Upgrading_backs_up_the_old_version_and_keeps_data()
    {
        string[] v1 = ["CREATE TABLE things (id INTEGER PRIMARY KEY, name TEXT NOT NULL);"];
        string[] v2 = [v1[0], "ALTER TABLE things ADD COLUMN colour TEXT NOT NULL DEFAULT 'blue';"];

        var old = await ClickyDatabase.OpenAsync(DbPath, v1, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(1, old.Version);
        await ExecuteAsync(old, "INSERT INTO things (name) VALUES ('kettle');");

        var upgraded = await ClickyDatabase.OpenAsync(DbPath, v2, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, upgraded.Version);
        Assert.Equal(2, await UserVersionAsync(upgraded));
        Assert.Equal(["kettle|blue"], await ScalarsAsync(upgraded, "SELECT name || '|' || colour FROM things;"));

        var backup = DbPath + ".v1.bak";
        Assert.True(File.Exists(backup));
        await using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
        await copy.OpenAsync();
        await using var command = copy.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT name FROM things;";
        Assert.Equal("kettle", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Blank_path_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ClickyDatabase.OpenAsync(" ", NullLogger.Instance));
    }

    private static async Task<long> UserVersionAsync(ClickyDatabase database)
    {
        await using var connection = await database.ConnectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> ScalarsAsync(ClickyDatabase database, string sql)
    {
        await using var connection = await database.ConnectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task ExecuteAsync(ClickyDatabase database, string sql)
    {
        await using var connection = await database.ConnectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetUserVersionAsync(int version)
    {
        Directory.CreateDirectory(_dir);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE future (x); PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ReadUserVersionAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
