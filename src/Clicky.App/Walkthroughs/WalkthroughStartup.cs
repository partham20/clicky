using System.Windows.Threading;
using Clicky.Core.Skills;
using Clicky.Core.Walkthroughs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Walkthroughs;

/// <summary>
/// Sets up the skills folder (with its README) at startup, and shows the step card while a walkthrough
/// runs: created when one starts, updated as it goes, and gone a moment after it ends.
/// </summary>
internal sealed class WalkthroughStartup(
    IWalkthroughs walkthroughs,
    AppSkillLibrary skills,
    Dispatcher dispatcher,
    ILogger<WalkthroughStartup> logger) : IHostedService
{
    private static readonly TimeSpan FinishedCardTime = TimeSpan.FromSeconds(2.5);

    private WalkthroughCard? _card;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        walkthroughs.Changed += OnChanged;
        _ = Task.Run(
            () =>
            {
                skills.EnsureCreated();
                logger.LogInformation("App skills folder {Directory}: {Count} skill(s)", skills.Directory, skills.All.Count);
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        walkthroughs.Changed -= OnChanged;
        dispatcher.InvokeAsync(() => Close(null));
        return Task.CompletedTask;
    }

    // Changed comes from the engine's loop, in order; InvokeAsync keeps that order.
    private void OnChanged(object? sender, WalkthroughChangedEventArgs e) => dispatcher.InvokeAsync(() => Apply(e.Snapshot));

    private void Apply(WalkthroughSnapshot snapshot)
    {
        try
        {
            if (!snapshot.IsActive)
            {
                if (_card?.Id != snapshot.Id)
                {
                    return;
                }

                if (snapshot.End is WalkthroughEnd.Cancelled or WalkthroughEnd.Replaced)
                {
                    Close(snapshot.Id);
                    return;
                }

                _card.Update(snapshot);
                var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = FinishedCardTime };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Close(snapshot.Id);
                };
                timer.Start();
                return;
            }

            if (_card is null || _card.Id != snapshot.Id)
            {
                Close(null);
                _card = new WalkthroughCard(snapshot.Id, walkthroughs, logger);
                _card.Update(snapshot);
                _card.Show();
                return;
            }

            _card.Update(snapshot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Walkthrough card failed");
        }
    }

    /// <param name="id">Close only the card for this walkthrough; null closes whatever is showing.</param>
    private void Close(Guid? id)
    {
        if (_card is { } card && (id is null || card.Id == id))
        {
            _card = null;
            card.Close();
        }
    }
}
