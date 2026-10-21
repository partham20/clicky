namespace Clicky.Core.Settings;

public interface ISettingsStore
{
    /// <summary>Latest known-good settings. Safe to read from any thread.</summary>
    ClickySettings Current { get; }

    /// <summary>
    /// Raised on a thread-pool thread after <see cref="Current"/> changes, whether from
    /// <see cref="UpdateAsync"/> or an external edit to the file. Never raised for no-op changes.
    /// UI subscribers must marshal to the dispatcher themselves.
    /// </summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Applies <paramref name="mutate"/> to the current value and persists it atomically.</summary>
    Task UpdateAsync(Func<ClickySettings, ClickySettings> mutate, CancellationToken cancellationToken = default);
}

public sealed class SettingsChangedEventArgs(ClickySettings previous, ClickySettings current, SettingsChangeSource source) : EventArgs
{
    public ClickySettings Previous { get; } = previous;

    public ClickySettings Current { get; } = current;

    public SettingsChangeSource Source { get; } = source;
}

public enum SettingsChangeSource
{
    /// <summary>Changed through <see cref="ISettingsStore.UpdateAsync"/>.</summary>
    App,

    /// <summary>settings.json was edited outside the app and reloaded.</summary>
    File,
}
