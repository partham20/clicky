using Clicky.Core;
using Clicky.Core.Assistant;
using Clicky.Core.Clickys;
using Clicky.Core.Routines;
using Clicky.Tests.Clickys;
using Microsoft.Extensions.Logging.Abstractions;
using static Clicky.Tests.Routines.RoutineScheduleTests;

namespace Clicky.Tests.Routines;

public sealed class ClickyServiceRunnerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-runner-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClickyModel _model = new();
    private readonly FakeRunner _unassigned = new();
    private readonly List<ClickyService> _services = [];
    private ClickyServiceRunner _runner = null!;

    private ClickyService Service => _services[0];

    public Task InitializeAsync()
    {
        _services.Add(new ClickyService(new ClickyPaths(_dir), _model, new ChatOnlyClickyActor(), NullLogger<ClickyService>.Instance));
        _runner = new ClickyServiceRunner(Service, _unassigned);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Service.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static RoutineRunRequest Request(string? clickyId) =>
        new("r1", "Morning briefing", "Brief me on today.", clickyId, Utc(2026, 1, 5, 8, 0), IsCatchUp: true, RoutineTrigger.CatchUp);

    [Fact]
    public async Task Lists_clickys_for_the_picker()
    {
        var desk = await Service.CreateAsync(new ClickyDraft("PDF Desk"));

        var clickys = await _runner.ListClickysAsync();

        Assert.Equal(new ClickyRef(desk.Id, "PDF Desk"), Assert.Single(clickys));
    }

    [Fact]
    public async Task Runs_the_prompt_in_the_clickys_chat_as_a_routine_turn()
    {
        var desk = await Service.CreateAsync(new ClickyDraft("PDF Desk"));
        _model.Default = Replies.Text("Three meetings today.");

        var result = await _runner.RunRoutineAsync(Request(desk.Id));

        Assert.True(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        var messages = await Service.GetMessagesAsync(desk.Id);
        Assert.Contains(messages, m => m.Role == ClickyMessageRole.Routine && m.Text.Contains("Brief me on today.", StringComparison.Ordinal));
        Assert.Empty(_unassigned.Calls);
    }

    [Fact]
    public async Task A_routine_without_a_clicky_goes_to_the_fallback()
    {
        var result = await _runner.RunRoutineAsync(Request(null));

        Assert.True(result.Succeeded);
        Assert.Single(_unassigned.Calls);
    }

    [Fact]
    public async Task A_deleted_clicky_is_a_failure_that_says_so()
    {
        var result = await _runner.RunRoutineAsync(Request("gone"));

        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Contains("no longer exists", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backend_failures_are_rethrown_for_the_engine()
    {
        var desk = await Service.CreateAsync(new ClickyDraft("PDF Desk"));
        _model.Then(new ClaudeBackendException(ClaudeFailure.Network, "Couldn't reach Claude."));

        var ex = await Assert.ThrowsAsync<ClaudeBackendException>(() => _runner.RunRoutineAsync(Request(desk.Id)));
        Assert.Equal(ClaudeFailure.Network, ex.Failure);
    }

    [Fact]
    public async Task Cancelling_throws_so_the_engine_can_tell_why()
    {
        var desk = await Service.CreateAsync(new ClickyDraft("PDF Desk"));
        var gate = new Gate();
        _model.Then(gate);
        using var cts = new CancellationTokenSource();

        var run = _runner.RunRoutineAsync(Request(desk.Id), cts.Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
