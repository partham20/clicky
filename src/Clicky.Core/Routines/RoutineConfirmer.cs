using System.Globalization;
using Clicky.Core.Agent;
using Clicky.Core.Geometry;

namespace Clicky.Core.Routines;

/// <summary>
/// Asks the user before a routine is saved or deleted on Claude's word, on the agent card (the same
/// card the permission gate uses): Save routine / Delete routine or Not now, never Always.
/// </summary>
public sealed class RoutineConfirmer(IAgentPresenter presenter, TimeProvider? time = null, Func<TimeZoneInfo>? zone = null)
{
    public const string CreateTask = "Setting up a routine";
    public const string DeleteTask = "Deleting a routine";
    public const string SaveLabel = "Save routine";
    public const string DeleteLabel = "Delete routine";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<TimeZoneInfo> _zone = zone ?? (() => TimeZoneInfo.Local);

    /// <summary>"Save the routine …?" with its schedule, next run and prompt.</summary>
    /// <param name="near">Where the user was asking (physical pixels), so the card shows on that monitor.</param>
    /// <returns>The answer; when approved, the card is still up for <see cref="RoutineCard.Finish"/>.</returns>
    public Task<RoutineCard> ConfirmCreateAsync(RoutineDraft draft, PixelRect? near, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var culture = CultureInfo.CurrentCulture;
        var now = _time.GetUtcNow();
        var when = draft.Schedule.Describe(culture);
        var next = draft.Schedule.NextAfter(now, _zone(), now) is { } at ? $" Next run {RoutineFormat.When(at, now, _zone(), culture)}." : string.Empty;
        var question = new PermissionPrompt(
            $"Save the routine “{draft.Name}”?",
            $"{when}.{next}\nEach time: {draft.Prompt}",
            PermissionKind.Routine,
            Irreversible: false,
            AllowLabel: SaveLabel);
        return AskAsync(CreateTask, $"{draft.Name}: {Lower(when, culture)}", question, near, cancellationToken);
    }

    /// <summary>"Delete the routine …?", marked as something that can't be undone.</summary>
    public Task<RoutineCard> ConfirmDeleteAsync(Routine routine, PixelRect? near, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routine);
        var when = routine.Schedule.Describe(CultureInfo.CurrentCulture);
        var question = new PermissionPrompt(
            $"Delete the routine “{routine.Name}”?",
            $"{when}. Its history goes too.",
            PermissionKind.Routine,
            Irreversible: true,
            AllowLabel: DeleteLabel);
        return AskAsync(DeleteTask, routine.Name, question, near, cancellationToken);
    }

    /// <summary>Saved, with when it runs next, for the card.</summary>
    public string SavedMessage(Routine routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        var next = routine.NextDueAt is { } due ? $" Next run {RoutineFormat.When(due, _time.GetUtcNow(), _zone(), CultureInfo.CurrentCulture)}." : string.Empty;
        return $"Saved “{routine.Name}”.{next} It's in Settings › Agents.";
    }

    private static string Lower(string text, CultureInfo culture) => text.Length == 0 ? text : char.ToLower(text[0], culture) + text[1..];

    private async Task<RoutineCard> AskAsync(string task, string step, PermissionPrompt question, PixelRect? near, CancellationToken cancellationToken)
    {
        var card = presenter.Begin(task, near);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, card.StopRequested);
            PermissionAnswer answer;
            try
            {
                card.ShowStep(0, step);
                answer = await card.AskAsync(question, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                answer = PermissionAnswer.NotNow; // Stop on the card.
            }

            if (answer == PermissionAnswer.NotNow)
            {
                card.Dispose();
                return new RoutineCard(null);
            }

            return new RoutineCard(card);
        }
        catch
        {
            card.Dispose();
            throw;
        }
    }
}

/// <summary>The user's answer. Dispose closes the card if <see cref="Finish"/> wasn't called.</summary>
public sealed class RoutineCard : IDisposable
{
    private readonly IAgentCard? _card;

    internal RoutineCard(IAgentCard? card) => _card = card;

    public bool Approved => _card is not null;

    /// <summary>Shows how it went for a moment, then closes.</summary>
    public void Finish(string message, bool success) => _card?.Finish(message, success);

    public void Dispose() => _card?.Dispose();
}
