using System.Collections.Concurrent;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    // Acceptance bar from the M0 check: an external edit is live within ~1 s.
    private static readonly TimeSpan ReloadDeadline = TimeSpan.FromSeconds(1);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));
    private readonly List<JsonSettingsStore> _stores = [];

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Open_without_file_writes_defaults()
    {
        var store = Open();

        Assert.Equal(ClickySettings.Default, store.Current);
        Assert.True(File.Exists(SettingsPath));
        Assert.Contains("\"startWithWindows\": false", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public async Task Update_round_trips_through_a_fresh_store()
    {
        var first = Open();
        await first.UpdateAsync(s => s with
        {
            General = s.General with { StartWithWindows = true },
            Logging = s.Logging with { MinimumLevel = LogLevelSetting.Debug },
        });
        first.Dispose();

        var second = Open();

        Assert.True(second.Current.General.StartWithWindows);
        Assert.Equal(LogLevelSetting.Debug, second.Current.Logging.MinimumLevel);
    }

    [Fact]
    public void Corrupt_file_at_startup_is_quarantined_and_defaults_restored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"general\": { \"startWithWindows\": tru");

        var store = Open();

        Assert.Equal(ClickySettings.Default, store.Current);
        var bad = Assert.Single(Directory.GetFiles(_dir, "settings.json.*.bad"));
        Assert.Equal("{ \"general\": { \"startWithWindows\": tru", File.ReadAllText(bad));
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Hand_edited_file_with_comments_trailing_commas_and_nulls_loads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              // turned on by hand
              "general": { "startWithWindows": true, },
              "logging": null,
            }
            """);

        var store = Open();

        Assert.True(store.Current.General.StartWithWindows);
        Assert.Equal(new LoggingSettings(), store.Current.Logging);
        Assert.Empty(Directory.GetFiles(_dir, "*.bad"));
    }

    [Fact]
    public void A_file_saved_as_utf8_with_bom_loads_instead_of_being_quarantined()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "general": { "startWithWindows": true } }""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var store = Open();

        Assert.True(store.Current.General.StartWithWindows);
        Assert.Empty(Directory.GetFiles(_dir, "*.bad"));
    }

    [Fact]
    public void M0_file_without_overlay_section_gets_overlay_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 1, "general": { "startWithWindows": true }, "overlay": null }""");

        var store = Open();

        Assert.Equal(new OverlaySettings(), store.Current.Overlay);
        Assert.False(store.Current.Overlay.HideFloatingCursor);
        Assert.Equal(40, store.Current.Overlay.FollowSmoothingMs);
    }

    [Fact]
    public async Task External_edit_is_applied_within_deadline()
    {
        var store = Open();
        var changed = NextChange(store);

        File.WriteAllText(SettingsPath, """{ "general": { "startWithWindows": true } }""");

        var e = await changed.WaitAsync(ReloadDeadline);
        Assert.Equal(SettingsChangeSource.File, e.Source);
        Assert.False(e.Previous.General.StartWithWindows);
        Assert.True(store.Current.General.StartWithWindows);
    }

    [Fact]
    public async Task Rename_based_save_is_picked_up()
    {
        var store = Open();
        var changed = NextChange(store);

        // VS Code / many editors: write a temp file, then rename it over the target.
        var temp = Path.Combine(_dir, "settings.json~edit");
        File.WriteAllText(temp, """{ "logging": { "minimumLevel": "Warning" } }""");
        File.Move(temp, SettingsPath, overwrite: true);

        await changed.WaitAsync(ReloadDeadline);
        Assert.Equal(LogLevelSetting.Warning, store.Current.Logging.MinimumLevel);
    }

    [Fact]
    public async Task Invalid_external_edit_keeps_last_good_settings_and_leaves_file_alone()
    {
        var store = Open();
        await store.UpdateAsync(s => s with { General = s.General with { StartWithWindows = true } });
        var events = Record(store);

        const string halfTyped = """{ "general": { "startWithWindows": fal""";
        File.WriteAllText(SettingsPath, halfTyped);
        await Task.Delay(Debounce * 4);

        Assert.True(store.Current.General.StartWithWindows);
        Assert.Empty(events);
        Assert.Equal(halfTyped, File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.bad"));
    }

    [Fact]
    public async Task Burst_of_external_writes_is_debounced_to_one_reload_with_the_final_value()
    {
        var store = Open();
        var events = Record(store);

        foreach (var level in new[] { "Verbose", "Debug", "Information", "Warning", "Error" })
        {
            File.WriteAllText(SettingsPath, $$"""{ "logging": { "minimumLevel": "{{level}}" } }""");
            await Task.Delay(10);
        }

        await Task.Delay(Debounce * 4);

        var e = Assert.Single(events);
        Assert.Equal(LogLevelSetting.Error, e.Current.Logging.MinimumLevel);
        Assert.Equal(LogLevelSetting.Error, store.Current.Logging.MinimumLevel);
    }

    [Fact]
    public async Task Own_write_raises_once_and_does_not_echo_as_a_file_change()
    {
        var store = Open();
        var events = Record(store);

        await store.UpdateAsync(s => s with { General = s.General with { StartWithWindows = true } });
        await Task.Delay(Debounce * 4);

        var e = Assert.Single(events);
        Assert.Equal(SettingsChangeSource.App, e.Source);
    }

    [Fact]
    public async Task No_op_update_neither_writes_nor_raises()
    {
        var store = Open();
        var events = Record(store);
        var writtenAt = File.GetLastWriteTimeUtc(SettingsPath);

        await store.UpdateAsync(s => s with { });
        await Task.Delay(Debounce * 2);

        Assert.Empty(events);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(SettingsPath));
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_block_other_subscribers()
    {
        var store = Open();
        store.Changed += (_, _) => throw new InvalidOperationException("boom");
        var changed = NextChange(store);

        await store.UpdateAsync(s => s with { General = s.General with { StartWithWindows = true } });

        Assert.True((await changed.WaitAsync(ReloadDeadline)).Current.General.StartWithWindows);
    }

    private JsonSettingsStore Open()
    {
        var store = JsonSettingsStore.Open(
            SettingsPath,
            NullLogger<JsonSettingsStore>.Instance,
            new SettingsStoreOptions { ReloadDebounce = Debounce });
        _stores.Add(store);
        return store;
    }

    private static Task<SettingsChangedEventArgs> NextChange(ISettingsStore store)
    {
        var tcs = new TaskCompletionSource<SettingsChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, e) => tcs.TrySetResult(e);
        return tcs.Task;
    }

    private static ConcurrentQueue<SettingsChangedEventArgs> Record(ISettingsStore store)
    {
        var events = new ConcurrentQueue<SettingsChangedEventArgs>();
        store.Changed += (_, e) => events.Enqueue(e);
        return events;
    }
}
