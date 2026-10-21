using Clicky.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Memory;

public sealed class MemorySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("{ \"memory\": null }")]
    [InlineData("{ \"memory\": { } }")]
    [InlineData("{ }")]
    public void Missing_memory_settings_read_as_defaults(string json)
    {
        var memory = Load(json).Memory;

        Assert.True(memory.Enabled);
        Assert.True(memory.AutoUpdate);
        Assert.Equal(MemorySettings.DefaultIdleMinutes, memory.IdleMinutes);
    }

    [Fact]
    public void Idle_minutes_are_clamped()
    {
        Assert.Equal(1, Load("{ \"memory\": { \"idleMinutes\": 0 } }").Memory.IdleMinutes);
        Assert.Equal(MemorySettings.MaxIdleMinutes, Load("{ \"memory\": { \"idleMinutes\": 9999, \"enabled\": false } }").Memory.IdleMinutes);
    }

    private ClickySettings Load(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        using var store = JsonSettingsStore.Open(path, NullLogger<JsonSettingsStore>.Instance);
        return store.Current;
    }
}
