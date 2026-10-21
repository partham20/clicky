using System.Windows.Threading;
using Clicky.App.Suggestions;
using Clicky.Core.Assistant;
using Clicky.Core.Buddy;
using Clicky.Core.Settings;
using Clicky.Core.Suggestions;
using Clicky.Core.Talk;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>The tray's "Today's ideas".</summary>
public interface ITodaysIdeas
{
    /// <summary>Researches now if today hasn't been, then shows the card. Any thread; returns at once.</summary>
    void ShowToday();
}

/// <summary>
/// Runs the morning suggestions (M17): asks <see cref="SuggestionEngine"/> once a minute whether
/// to research or show the card, shows <see cref="SuggestionCard"/>, and hands "Do it" to the Talk
/// loop as a typed request (the computer-use agent picks it up from there when it is installed).
/// </summary>
internal sealed class SuggestionCoordinator(
    SuggestionEngine engine,
    IQuietDetector quiet,
    ISettingsStore settings,
    IBuddyPresence presence,
    TalkLoop talk,
    TalkAnswerer answerer,
    IUserNotifier notifier,
    Dispatcher dispatcher,
    ILogger<SuggestionCoordinator> logger) : BackgroundService, ITodaysIdeas, ISuggestionCardHost
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>Lets startup (speech models, connectors) settle before the first look.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(45);

    private SuggestionCard? _card;
    private int _finding;

    public void ShowToday()
    {
        if (Interlocked.Exchange(ref _finding, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!engine.Open(engine.Today).Any())
                {
                    notifier.Notify("Clicky", "Looking for ideas… this takes a minute or two.");
                }

                var found = await engine.FindNowAsync(CancellationToken.None).ConfigureAwait(false);
                if (found.Ideas.Count == 0)
                {
                    notifier.Notify("No ideas right now", found.Problem ?? "Nothing new worth suggesting today. Clicky looks again tomorrow morning.", warning: found.Problem is not null);
                    return;
                }

                await ShowAsync(found.Ideas, "Today's ideas").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "Suggestions: could not show today's ideas");
            }
            finally
            {
                Volatile.Write(ref _finding, 0);
            }
        });
    }

    void ISuggestionCardHost.DoIt(Suggestion suggestion)
    {
        engine.Decide(suggestion.Id, SuggestionDecision.Approved);
        _ = Task.Run(() => RunAsync(suggestion));
    }

    void ISuggestionCardHost.Next(Suggestion suggestion) => engine.Decide(suggestion.Id, SuggestionDecision.Passed);

    async Task<Suggestion?> ISuggestionCardHost.AdjustAsync(Suggestion suggestion, string request)
    {
        try
        {
            var revised = await Task.Run(() => engine.AdjustAsync(suggestion.Id, request, CancellationToken.None)).ConfigureAwait(true);
            if (revised is null)
            {
                notifier.Notify("Couldn't adjust that idea", "Claude's answer didn't make sense, so the idea stays as it was.", warning: true);
            }

            return revised;
        }
        catch (ClaudeBackendException ex)
        {
            notifier.Notify("Couldn't adjust that idea", ex.Message, warning: true);
            return null;
        }
    }

    void ISuggestionCardHost.Closed(Suggestion current, IReadOnlyList<Suggestion> unseen) =>
        engine.Decide(current.Id, SuggestionDecision.Declined);

    void ISuggestionCardHost.TimedOut(IReadOnlyList<Suggestion> remaining) =>
        engine.MarkIgnored(remaining.Select(s => s.Id));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TickInterval);
            do
            {
                try
                {
                    if (Volatile.Read(ref _card) is null && await engine.TickAsync(Presence, stoppingToken).ConfigureAwait(false))
                    {
                        var ideas = engine.Open(engine.Today).Where(s => s.Decision == SuggestionDecision.Pending).ToList();
                        if (ideas.Count > 0)
                        {
                            await ShowAsync(ideas, "Good morning").ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.LogError(ex, "Suggestions: the morning check failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private SuggestionPresence Presence() => new(
        UserIdle.Get(),
        quiet.Check(settings.Current.Speech).Reasons,
        presence.State != BuddyState.Idle);

    private async Task ShowAsync(IReadOnlyList<Suggestion> ideas, string greeting)
    {
        var day = ideas[0].Day;
        await dispatcher.InvokeAsync(() =>
        {
            if (_card is not null)
            {
                return;
            }

            var card = new SuggestionCard(ideas, greeting, this, logger);
            card.Closed += (_, _) => Volatile.Write(ref _card, null);
            Volatile.Write(ref _card, card);
            card.Show();
            logger.LogInformation("Suggestions: card up with {Count} ideas", ideas.Count);
        });
        engine.MarkShown(day);
    }

    private async Task RunAsync(Suggestion suggestion)
    {
        try
        {
            talk.Begin();
            var question = new TalkQuestion($"From this morning's suggestions, please do this for me: {suggestion.Task}", RequestSource.Typed);
            var result = await answerer.AskAsync(question).ConfigureAwait(false);
            logger.LogInformation("Suggestions: \"{Title}\" handed to Talk: {Outcome}", suggestion.Title, result.Outcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Suggestions: could not start \"{Title}\"", suggestion.Title);
            notifier.Notify("Clicky couldn't start that", ex.Message, warning: true);
        }
    }
}
