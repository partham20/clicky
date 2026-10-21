using Clicky.Core.Walkthroughs;

namespace Clicky.App.Walkthroughs;

/// <summary>
/// The walkthrough engine's <see cref="IWalkthroughTurns"/>: the stock one (Claude), unless something
/// has taken over for a while, like the tray's walkthrough demo with its scripted steps.
/// </summary>
public sealed class SwitchableWalkthroughTurns(AssistantWalkthroughTurns stock) : IWalkthroughTurns
{
    private IWalkthroughTurns? _override;

    /// <summary>Runs every turn through <paramref name="turns"/> until the result is disposed.</summary>
    public IDisposable Use(IWalkthroughTurns turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        Volatile.Write(ref _override, turns);
        return new Restore(this, turns);
    }

    public Task RunAsync(WalkthroughTurn turn, IWalkthroughs walkthroughs, CancellationToken cancellationToken) =>
        (Volatile.Read(ref _override) ?? stock).RunAsync(turn, walkthroughs, cancellationToken);

    private sealed class Restore(SwitchableWalkthroughTurns owner, IWalkthroughTurns turns) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref owner._override, null, turns);
    }
}
