namespace Clicky.App.Views.Home;

/// <summary>Which Clicky the user is looking at in Home right now, so its replies don't also pop a notification.</summary>
internal sealed class HomeViewState
{
    private volatile string? _watching;

    /// <summary>The Clicky shown in Home while Home is the active window; null otherwise.</summary>
    public string? Watching
    {
        get => _watching;
        set => _watching = value;
    }
}
