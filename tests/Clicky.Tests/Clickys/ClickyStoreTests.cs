using Clicky.Core.Clickys;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Clickys;

public sealed class ClickyStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ClickyDatabase.ReleaseFiles();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Clicky_round_trips_every_field()
    {
        var store = await OpenAsync();
        var clicky = new ClickyAgent(
            "0123456789abcdef0123456789abcdef",
            "Inbox helper",
            "Sort my mail.",
            "Boss is Dana.",
            "#ABCDEF",
            UsesComputer: true,
            MaxSteps: 42,
            Unread: 3,
            CreatedAt: Start,
            UpdatedAt: Start.AddMinutes(1),
            LastActivityAt: Start.AddMinutes(2));

        await store.InsertClickyAsync(clicky);

        Assert.Equal(clicky, await store.GetClickyAsync(clicky.Id));
        Assert.Equal([clicky], await store.ListClickysAsync());
        Assert.Null(await store.GetClickyAsync("ffff"));

        var draft = new ClickyDraft("Renamed", "New instructions", "New notes", "#000000", UsesComputer: false, MaxSteps: 7);
        Assert.True(await store.UpdateClickyAsync(clicky.Id, draft, Start.AddHours(1)));
        Assert.False(await store.UpdateClickyAsync("ffff", draft, Start.AddHours(1)));

        var updated = await store.GetClickyAsync(clicky.Id);
        Assert.Equal(
            clicky with { Name = "Renamed", Instructions = "New instructions", Notes = "New notes", Color = "#000000", UsesComputer = false, MaxSteps = 7, UpdatedAt = Start.AddHours(1) },
            updated);
    }

    [Fact]
    public async Task Clickys_list_by_last_activity()
    {
        var store = await OpenAsync();
        var quiet = await AddClickyAsync(store, "aaaa", "Quiet");
        var busy = await AddClickyAsync(store, "bbbb", "Busy");

        await store.AddMessageAsync(busy.Id, null, ClickyMessageRole.User, "hello", Start.AddMinutes(5));

        Assert.Equal(["Busy", "Quiet"], (await store.ListClickysAsync()).Select(c => c.Name));
        Assert.Equal(Start.AddMinutes(5), (await store.GetClickyAsync(busy.Id))!.LastActivityAt);
        Assert.Equal(Start, (await store.GetClickyAsync(quiet.Id))!.LastActivityAt);
    }

    [Fact]
    public async Task Run_round_trips_every_field()
    {
        var store = await OpenAsync();
        var clicky = await AddClickyAsync(store, "aaaa", "Runner");
        var run = new ClickyRun(
            "run1",
            clicky.Id,
            ClickyRunTrigger.Routine,
            "Check the build",
            ClickyRunStatus.Running,
            Start,
            StartedAt: Start.AddSeconds(1),
            Steps: 2,
            Model: "m",
            InputTokens: 100,
            OutputTokens: 20,
            CostUsd: 0.25m,
            RoutineId: "r1",
            RoutineName: "Nightly",
            ScheduledFor: Start.AddMinutes(-1));

        await store.InsertRunAsync(run);
        Assert.Equal(run, await store.GetRunAsync("run1"));

        var finished = run with { Status = ClickyRunStatus.Failed, EndedAt = Start.AddSeconds(9), Summary = "s", Error = "e", Steps = 3, InputTokens = 150, OutputTokens = 30, CostUsd = 0.5m };
        await store.UpdateRunAsync(finished);

        Assert.Equal(finished, await store.GetRunAsync("run1"));
        Assert.Equal(TimeSpan.FromSeconds(8), finished.Duration);
        Assert.False(finished.IsActive);
        Assert.Null(await store.GetRunAsync("nope"));
    }

    [Fact]
    public async Task Runs_list_newest_first_up_to_the_limit()
    {
        var store = await OpenAsync();
        var clicky = await AddClickyAsync(store, "aaaa", "Runner");
        for (var i = 0; i < 4; i++)
        {
            await store.InsertRunAsync(new ClickyRun($"run{i}", clicky.Id, ClickyRunTrigger.Chat, $"p{i}", ClickyRunStatus.Completed, Start.AddMinutes(i)));
        }

        Assert.Equal(["run3", "run2", "run1"], (await store.GetRunsAsync(clicky.Id, 3)).Select(r => r.Id));
    }

    [Fact]
    public async Task Deleting_a_clicky_removes_its_messages_runs_and_steps()
    {
        var store = await OpenAsync();
        var gone = await AddClickyAsync(store, "aaaa", "Gone");
        var kept = await AddClickyAsync(store, "bbbb", "Kept");
        foreach (var clicky in new[] { gone, kept })
        {
            var runId = "run-" + clicky.Id;
            await store.InsertRunAsync(new ClickyRun(runId, clicky.Id, ClickyRunTrigger.Chat, "hi", ClickyRunStatus.Completed, Start));
            await store.AddMessageAsync(clicky.Id, runId, ClickyMessageRole.User, "hi", Start);
            await store.AddStepAsync(runId, Start, ClickyStepKind.Request, "Asked Claude");
        }

        Assert.True(await store.DeleteClickyAsync(gone.Id));
        Assert.False(await store.DeleteClickyAsync(gone.Id));

        Assert.Null(await store.GetClickyAsync(gone.Id));
        Assert.Empty(await store.GetMessagesAsync(gone.Id, 10));
        Assert.Empty(await store.GetRunsAsync(gone.Id, 10));
        Assert.Null(await store.GetRunAsync("run-aaaa"));
        Assert.Empty(await store.GetStepsAsync("run-aaaa"));

        Assert.Single(await store.GetMessagesAsync(kept.Id, 10));
        Assert.NotNull(await store.GetRunAsync("run-bbbb"));
        Assert.Single(await store.GetStepsAsync("run-bbbb"));
    }

    [Fact]
    public async Task Messages_return_the_newest_n_oldest_first()
    {
        var store = await OpenAsync();
        var clicky = await AddClickyAsync(store, "aaaa", "Chatty");
        for (var i = 1; i <= 5; i++)
        {
            await store.AddMessageAsync(clicky.Id, null, ClickyMessageRole.User, $"m{i}", Start.AddSeconds(i));
        }

        var newest = await store.GetMessagesAsync(clicky.Id, 3);

        Assert.Equal(["m3", "m4", "m5"], newest.Select(m => m.Text));
        Assert.True(newest[0].Id < newest[1].Id && newest[1].Id < newest[2].Id);
        Assert.Equal(Start.AddSeconds(3), newest[0].At);
        Assert.Empty(await store.GetMessagesAsync(clicky.Id, 0));
        Assert.Empty(await store.GetMessagesAsync(clicky.Id, -1));
    }

    [Fact]
    public async Task Message_keeps_its_run_and_role()
    {
        var store = await OpenAsync();
        var clicky = await AddClickyAsync(store, "aaaa", "Chatty");
        await store.InsertRunAsync(new ClickyRun("run1", clicky.Id, ClickyRunTrigger.Routine, "p", ClickyRunStatus.Completed, Start));

        var added = await store.AddMessageAsync(clicky.Id, "run1", ClickyMessageRole.Routine, "Routine prompt", Start);

        Assert.Equal(added, Assert.Single(await store.GetMessagesAsync(clicky.Id, 10)));
        Assert.Equal("run1", added.RunId);
        Assert.Equal(ClickyMessageRole.Routine, added.Role);
    }

    [Fact]
    public async Task Replies_and_notes_count_as_unread_until_marked_read()
    {
        var store = await OpenAsync();
        var clicky = await AddClickyAsync(store, "aaaa", "Chatty");

        await store.AddMessageAsync(clicky.Id, null, ClickyMessageRole.User, "q", Start);
        await store.AddMessageAsync(clicky.Id, null, ClickyMessageRole.Routine, "r", Start);
        Assert.Equal(0, (await store.GetClickyAsync(clicky.Id))!.Unread);

        await store.AddMessageAsync(clicky.Id, null, ClickyMessageRole.Assistant, "a", Start);
        await store.AddMessageAsync(clicky.Id, null, ClickyMessageRole.System, "s", Start);
        Assert.Equal(2, (await store.GetClickyAsync(clicky.Id))!.Unread);

        await store.MarkReadAsync(clicky.Id);
        Assert.Equal(0, (await store.GetClickyAsync(clicky.Id))!.Unread);

        await store.AddMessageAsync(clicky.Id, null, ClickyMessageRole.Assistant, "again", Start);
        Assert.Equal(1, (await store.GetClickyAsync(clicky.Id))!.Unread);
    }

    [Fact]
    public async Task Step_numbers_count_up_per_run()
    {
        var store = await OpenAsync();
        var clicky = await AddClickyAsync(store, "aaaa", "Stepper");
        await store.InsertRunAsync(new ClickyRun("one", clicky.Id, ClickyRunTrigger.Chat, "p", ClickyRunStatus.Running, Start));
        await store.InsertRunAsync(new ClickyRun("two", clicky.Id, ClickyRunTrigger.Chat, "p", ClickyRunStatus.Running, Start));

        Assert.Equal(1, (await store.AddStepAsync("one", Start, ClickyStepKind.Request, "a")).Seq);
        Assert.Equal(2, (await store.AddStepAsync("one", Start, ClickyStepKind.Text, "b")).Seq);
        Assert.Equal(1, (await store.AddStepAsync("two", Start, ClickyStepKind.Request, "c")).Seq);
        Assert.Equal(3, (await store.AddStepAsync("one", Start.AddSeconds(1), ClickyStepKind.Control, "d")).Seq);

        Assert.Equal(
            [
                new ClickyRunStep("one", 1, Start, ClickyStepKind.Request, "a"),
                new ClickyRunStep("one", 2, Start, ClickyStepKind.Text, "b"),
                new ClickyRunStep("one", 3, Start.AddSeconds(1), ClickyStepKind.Control, "d"),
            ],
            await store.GetStepsAsync("one"));
    }

    [Fact]
    public async Task Interrupting_marks_only_active_runs()
    {
        var store = await OpenAsync();
        var clicky = await AddClickyAsync(store, "aaaa", "Runner");
        foreach (var status in Enum.GetValues<ClickyRunStatus>())
        {
            await store.InsertRunAsync(new ClickyRun(status.ToString(), clicky.Id, ClickyRunTrigger.Chat, "p", status, Start, Error: status == ClickyRunStatus.Paused ? "earlier" : null));
        }

        var changed = await store.InterruptActiveRunsAsync(Start.AddHours(1), "Clicky closed.");

        Assert.Equal(["Paused", "Queued", "Running"], changed.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.All(changed, r => Assert.Equal(ClickyRunStatus.Interrupted, r.Status));
        Assert.All(changed, r => Assert.Equal(Start.AddHours(1), r.EndedAt));

        Assert.Equal("Clicky closed.", (await store.GetRunAsync("Running"))!.Error);
        Assert.Equal("earlier", (await store.GetRunAsync("Paused"))!.Error);
        Assert.Equal(ClickyRunStatus.Completed, (await store.GetRunAsync("Completed"))!.Status);
        Assert.Equal(ClickyRunStatus.Cancelled, (await store.GetRunAsync("Cancelled"))!.Status);
        Assert.Null((await store.GetRunAsync("Completed"))!.EndedAt);

        Assert.Empty(await store.InterruptActiveRunsAsync(Start.AddHours(2), "again"));
    }

    private static async Task<ClickyAgent> AddClickyAsync(ClickyStore store, string id, string name)
    {
        var clicky = new ClickyAgent(id, name, "", "", ClickyDraft.DefaultColor, false, ClickyDraft.DefaultMaxSteps, 0, Start, Start, Start);
        await store.InsertClickyAsync(clicky);
        return clicky;
    }

    private async Task<ClickyStore> OpenAsync() =>
        new(await ClickyDatabase.OpenAsync(Path.Combine(_dir, ClickyDatabase.FileName), NullLogger.Instance));
}
