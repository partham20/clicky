using Clicky.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Updates;

public sealed class UpdateSettingsTests : IDisposable
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
    [InlineData("{ \"updates\": null }")]
    [InlineData("{ \"updates\": { } }")]
    [InlineData("{ }")]
    public void Missing_update_settings_read_as_defaults(string json)
    {
        var updates = Load(json).Updates;

        Assert.True(updates.AutoCheck);
        Assert.True(updates.AutoDownload);
        Assert.Equal(string.Empty, updates.FeedUrl);
        Assert.False(updates.Prerelease);
    }

    [Fact]
    public void Feed_url_is_trimmed()
    {
        var updates = Load("{ \"updates\": { \"feedUrl\": \"  C:\\\\releases \", \"autoDownload\": false } }").Updates;

        Assert.Equal(@"C:\releases", updates.FeedUrl);
        Assert.False(updates.AutoDownload);
        Assert.True(updates.AutoCheck);
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
