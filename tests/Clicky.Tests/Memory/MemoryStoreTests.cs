using Clicky.Core.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Memory;

public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"), "memory");
    private readonly MemoryStore _store;

    public MemoryStoreTests() => _store = new MemoryStore(_dir, NullLogger<MemoryStore>.Instance);

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task First_read_creates_both_files_from_templates()
    {
        var snapshot = await _store.ReadAsync();

        Assert.True(snapshot.Memory.Profile.IsEmpty);
        Assert.Equal(MemoryTemplates.Empty(MemoryFile.Profile).ToMarkdown(), File.ReadAllText(Path.Combine(_dir, "PROFILE.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "VOLATILE.md")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Edits_made_in_another_editor_are_picked_up()
    {
        await _store.ReadAsync();
        var path = _store.PathOf(MemoryFile.Profile);
        File.WriteAllText(path, "## About me\r\n- Edited in Notepad.\r\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var snapshot = await _store.ReadAsync();

        Assert.Equal("Edited in Notepad.", snapshot.Memory.Profile.Find("About me")!.Entries.Single().Text);
        Assert.DoesNotContain('\r', snapshot.ProfileText);
    }

    [Fact]
    public async Task Apply_merges_into_the_file_on_disk_and_can_be_undone()
    {
        await _store.SaveTextAsync(MemoryFile.Profile, "# Profile\n\n## How to answer\n- Explain in detail.\n");
        var before = File.ReadAllText(_store.PathOf(MemoryFile.Profile));
        var events = new List<MemoryChangedEventArgs>();
        _store.Changed += (_, e) => events.Add(e);

        var result = await _store.ApplyAsync([new MemoryEdit(MemoryEditKind.Replace, MemoryFile.Profile, "How to answer", "Explain in detail", "Keep answers short.")]);

        Assert.Single(result.Applied);
        Assert.Equal("# Profile\n\n## How to answer\n- Keep answers short.\n", File.ReadAllText(_store.PathOf(MemoryFile.Profile)));
        Assert.Equal(MemoryChangeSource.Updater, Assert.Single(events).Source);
        Assert.True(_store.CanUndo);

        Assert.True(await _store.UndoLastUpdateAsync());
        Assert.Equal(before, File.ReadAllText(_store.PathOf(MemoryFile.Profile)));
        Assert.False(_store.CanUndo);
    }

    [Fact]
    public async Task Undo_refuses_to_clobber_a_later_hand_edit()
    {
        await _store.ApplyAsync([new MemoryEdit(MemoryEditKind.Add, MemoryFile.Volatile, "Current project", Text: "M11.")]);
        await _store.SaveTextAsync(MemoryFile.Volatile, "## Current project\n- M11, edited by hand.\n");

        Assert.False(await _store.UndoLastUpdateAsync());
        Assert.Contains("edited by hand", File.ReadAllText(_store.PathOf(MemoryFile.Volatile)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_op_pass_writes_nothing()
    {
        await _store.ReadAsync();
        var written = File.GetLastWriteTimeUtc(_store.PathOf(MemoryFile.Profile));

        var result = await _store.ApplyAsync([]);

        Assert.Empty(result.Applied);
        Assert.False(_store.CanUndo);
        Assert.Equal(written, File.GetLastWriteTimeUtc(_store.PathOf(MemoryFile.Profile)));
    }

    [Fact]
    public async Task Forget_resets_to_templates_and_drops_undo()
    {
        await _store.ApplyAsync([new MemoryEdit(MemoryEditKind.Add, MemoryFile.Profile, "About me", Text: "Secret-ish fact.")]);

        await _store.ForgetAsync();

        var snapshot = await _store.ReadAsync();
        Assert.True(snapshot.Memory.Profile.IsEmpty);
        Assert.True(snapshot.Memory.Volatile.IsEmpty);
        Assert.False(_store.CanUndo);
    }
}
