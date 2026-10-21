using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Capture;
using Clicky.Core.Connectors;
using Clicky.Core.Geometry;
using Clicky.Core.Memory;
using Clicky.Core.Settings;
using Clicky.Core.Talk;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Agent;

/// <summary>
/// The computer-use agent loop (M13). When a reply asks to act (click, type, keys, scroll, open a
/// page, call a connected app's tool, or ask permission), it runs those actions behind the
/// permission gate, takes a fresh screenshot, sends the results back and keeps going until the model
/// says done, the user stops it, or the step budget runs out.
/// </summary>
/// <remarks>
/// <para>
/// Works the same on both backends: tool results by id on the API, a results block in the text for
/// action tags. One run per conversation at a time (the backends allow one reply at a time anyway).
/// </para>
/// <para>
/// The gate: the first action in a conversation is asked about (Allow once / Always for this
/// conversation / Not now); anything that sends, deletes, buys or can't be undone is asked about
/// every time; read-only connector tools never are. With "Always", the first action of each new run
/// waits out a short countdown on the card so the user can cancel before anything happens.
/// </para>
/// </remarks>
public sealed class ComputerAgent(
    IClaudeAssistant assistant,
    IComputerControl computer,
    ICaptureService capture,
    IAgentPresenter presenter,
    ISettingsStore settings,
    ILogger<ComputerAgent> logger,
    IConnectorTools? connectors = null,
    IMemoryService? memory = null,
    TimeProvider? time = null) : IComputerAgent
{
    /// <summary>After acting, how long the screen gets to settle before the next screenshot.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>Connector output beyond this is cut, so one chatty tool can't fill the context.</summary>
    public const int MaxToolResultChars = 20_000;

    public const string ScreenshotResult = "A new screenshot of the screen as it is now is attached to this message, after the results.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConditionalWeakTable<Conversation, PermissionGate> _gates = [];

    private enum ItemKind
    {
        Show,
        Screenshot,
        Wait,
        Done,
        Rejected,
        Question,
        Control,
        Tool,

        /// <summary>A call the run's caller answers (<see cref="AgentRunOptions.Tools"/>).</summary>
        Own,
    }

    private enum StepEnd
    {
        Continue,
        Done,

        /// <summary>The user said not now: tell the model, let it answer, act no further.</summary>
        Declined,

        /// <summary>The user cancelled the countdown: stop quietly.</summary>
        Cancelled,
    }

    /// <summary>What the user has allowed in <paramref name="conversation"/>. A new conversation starts with nothing allowed.</summary>
    public PermissionGate GateFor(Conversation conversation) => _gates.GetValue(conversation, _ => new PermissionGate());

    /// <summary>
    /// True when <paramref name="completed"/> wants something done and its results back: computer
    /// control, a connector tool, a permission question, a toolset call, or a fix-up of a rejected call.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the agent's instance contract (the Talk loop calls it through an interface).")]
    public bool WantsToAct(ReplyCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.Stop is ReplyStop.Refused or ReplyStop.MaxTokens)
        {
            return false;
        }

        return completed.Actions.Any(a => a.NeedsResult)
            || completed.Calls.Any(c => c.Call.ToolsetName is not null || (!c.Call.Accepted && c.Call.Name is not ("point" or "highlight" or "draw" or "step")));
    }

    /// <summary>Runs the rest of the task for the user watching (card, countdown, prompts).</summary>
    public IAsyncEnumerable<ReplyEvent> ContinueAsync(Conversation conversation, ReplyCompleted completed, RequestScreenshot? screenshot, CancellationToken cancellationToken = default) =>
        ContinueAsync(conversation, completed, screenshot, AgentRunOptions.Default, cancellationToken);

    /// <summary>
    /// Runs the rest of the task: executes <paramref name="completed"/>'s actions, then step after
    /// step until done. Yields every step's reply events, each step opened by
    /// <see cref="ReplyScreenshotTaken"/> when a new screenshot was taken. Returns quietly when the
    /// user presses Stop; throws <see cref="OperationCanceledException"/> only for
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="screenshot">The screenshot <paramref name="completed"/>'s coordinates refer to.</param>
    /// <exception cref="ClaudeBackendException">A step's request failed.</exception>
    public async IAsyncEnumerable<ReplyEvent> ContinueAsync(
        Conversation conversation,
        ReplyCompleted completed,
        RequestScreenshot? screenshot,
        AgentRunOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(options);
        if (!WantsToAct(completed))
        {
            yield break;
        }

        var agentSettings = settings.Current.Agent;
        var maxSteps = Math.Clamp(options.MaxSteps ?? agentSettings.MaxSteps, 1, AgentSettings.MaxMaxSteps);
        var task = TaskText(conversation);
        using var card = options.Unattended ? null : presenter.Begin(task, screenshot?.Geometry.Source);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, card?.StopRequested ?? CancellationToken.None);
        var run = new Run(GateFor(conversation), card, options.Unattended ? options.Permissions : card, options.Unattended, options.PreApproved, agentSettings, options.Tools);
        var spoken = new StringBuilder();
        var reply = completed;
        var shot = screenshot;
        var step = 0;
        string? summary = null;
        var ending = "Something went wrong.";
        var success = false;
        logger.LogInformation("Agent run started ({MaxSteps} steps max, {Mode})", maxSteps, options.Unattended ? "unattended" : "attended");
        try
        {
            while (true)
            {
                step++;
                StepResult result;
                try
                {
                    result = await ExecuteStepAsync(run, reply, shot, step, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    conversation.KeepUnsentResults(run.Partial);
                    (ending, success) = ("Stopped.", true);
                    logger.LogInformation("Agent stopped by the user during step {Step}", step);
                    break;
                }

                if (result.End == StepEnd.Done)
                {
                    conversation.KeepUnsentResults(result.Results);
                    (summary, ending, success) = (result.Summary, result.Summary ?? "Done.", result.Success);
                    break;
                }

                if (result.End == StepEnd.Cancelled)
                {
                    conversation.KeepUnsentResults(result.Results);
                    (ending, success) = ("Cancelled.", true);
                    break;
                }

                var last = result.End == StepEnd.Declined || step >= maxSteps;
                var instruction = result.End == StepEnd.Declined
                    ? "The user said not now. Acknowledge it in a few words and don't act again unless they ask."
                    : step >= maxSteps
                        ? $"That was the last step allowed ({maxSteps}). Stop here: say briefly what's done and what's left, and use done."
                        : null;

                RequestScreenshot? next;
                try
                {
                    await Task.Delay(result.Waited ? TimeSpan.FromMilliseconds(150) : SettleDelay, _time, stop.Token).ConfigureAwait(false);
                    next = await CaptureAsync(shot, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    conversation.KeepUnsentResults(result.Results);
                    (ending, success) = ("Stopped.", true);
                    break;
                }

                if (next is not null)
                {
                    shot = next;
                    yield return new ReplyScreenshotTaken(next);
                }

                var text = instruction ?? (next is null
                    ? "Clicky couldn't take a new screenshot. Carry on if you can without one, or tell the user."
                    : "Carry on with the task, or use done when it's finished.");
                var request = new ClaudeRequest(text, RequestSource.Agent, next, Step: new AgentStep(step, maxSteps, result.Results), Tools: options.Tools?.Offered);

                ReplyCompleted? answered = null;
                var stoppedMidReply = false;
                var events = assistant.ReplyAsync(conversation, request, stop.Token).GetAsyncEnumerator(stop.Token);
                try
                {
                    while (true)
                    {
                        ReplyEvent replyEvent;
                        try
                        {
                            if (!await events.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            replyEvent = events.Current;
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            stoppedMidReply = true;
                            break;
                        }

                        switch (replyEvent)
                        {
                            case ReplyText said:
                                spoken.Append(said.Text);
                                break;
                            case ReplyCompleted done:
                                answered = done;
                                break;
                        }

                        yield return replyEvent;
                    }
                }
                finally
                {
                    await events.DisposeAsync().ConfigureAwait(false);
                }

                if (stoppedMidReply)
                {
                    (ending, success) = ("Stopped.", true);
                    break;
                }

                if (last || answered is null || !WantsToAct(answered))
                {
                    summary = answered?.Actions.OfType<DoneAction>().LastOrDefault()?.Summary;
                    (ending, success) = result.End == StepEnd.Declined
                        ? ("Okay, not doing that.", true)
                        : step >= maxSteps && answered is not null && WantsToAct(answered)
                            ? ($"Stopped after {maxSteps} steps.", false)
                            : (summary ?? "Done.", answered?.Actions.OfType<DoneAction>().LastOrDefault()?.Success ?? true);
                    break;
                }

                reply = answered;
            }
        }
        finally
        {
            logger.LogInformation("Agent run ended after {Steps} steps: {Ending}", step, ending);
            card?.Finish(ending, success);
            if (memory is not null && task.Length > 0 && !options.Unattended)
            {
                var said = (summary ?? spoken.ToString()).Trim();
                memory.RecordTurn(new MemoryTurn(task, said.Length > 500 ? said[..500] + "…" : said.Length > 0 ? said : ending, _time.GetUtcNow(), MemoryTurnMode.Agent));
            }
        }
    }

    /// <summary>The user's words from the turn that started the run, without the screen context line.</summary>
    internal static string TaskText(Conversation conversation)
    {
        var turns = conversation.Turns;
        var turn = turns.LastOrDefault(t => !t.UserText.Contains("[Agent step ", StringComparison.Ordinal));
        if (turn is null)
        {
            return string.Empty;
        }

        var lines = turn.UserText.Split('\n').Where(l => !l.StartsWith('[')).Select(l => l.Trim());
        var text = string.Join(' ', lines).Trim();
        foreach (var prefix in new[] { "The user said (speech-to-text): ", "The user typed: " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
            }
        }

        return text.Length > 200 ? text[..200] + "…" : text;
    }

    internal static string Describe(ClickyAction action, ControlTarget target, ConnectorTool? tool = null)
    {
        var where = target.App is { Length: > 0 } app ? $" in {app}" : string.Empty;
        return action switch
        {
            ClickAction click => $"{ClickVerb(click)} {(Name(target.Name ?? click.Target) is { } name ? $"\"{name}\"" : "there")}{where}",
            TypeAction type => $"Type \"{Short(type.Text.ReplaceLineEndings(" ⏎ "), 60)}\"{where}",
            KeyAction key => $"Press {key.Keys}{(key.Repeat > 1 ? string.Create(CultureInfo.InvariantCulture, $" {key.Repeat} times") : string.Empty)}{where}",
            ScrollAction scroll => $"Scroll {scroll.Direction.ToString().ToLowerInvariant()}{where}",
            OpenUrlAction open => $"Open {(Uri.TryCreate(open.Url, UriKind.Absolute, out var uri) ? uri.Host : open.Url)}",
            UseToolAction use => tool is null
                ? $"Use {use.Tool}"
                : $"Use {tool.Title ?? tool.ServerToolName} in {tool.ConnectorName}{(string.IsNullOrWhiteSpace(tool.AccountLabel) ? string.Empty : $" ({tool.AccountLabel})")}",
            _ => action.Name,
        };
    }

    private static string ClickVerb(ClickAction click) => (click.Button, click.Count) switch
    {
        (MouseButton.Right, _) => "Right-click",
        (MouseButton.Middle, _) => "Middle-click",
        (_, 2) => "Double-click",
        (_, >= 3) => "Triple-click",
        _ => "Click",
    };

    private static string? Name(string? name) => string.IsNullOrWhiteSpace(name) ? null : Short(name.Trim(), 50);

    private static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static PixelPoint? PointOf(ClickyAction action, ScreenshotGeometry? geometry) => (action, geometry) switch
    {
        (ClickAction c, { } g) => CoordinateMapper.ToPhysical(new ScreenshotPoint(c.X, c.Y), g),
        (ScrollAction s, { } g) => CoordinateMapper.ToPhysical(new ScreenshotPoint(s.X, s.Y), g),
        _ => null,
    };

    private static PixelPoint Centre(PixelRect rect) => new(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

    /// <summary>What the step has to deal with, in order: every tool call (API), or the actions that want results (tags).</summary>
    private static List<(ToolCallRecord? Call, ClickyAction? Action)> Items(ReplyCompleted reply, IRunTools? tools)
    {
        if (reply.Calls.Count > 0)
        {
            return [.. reply.Calls.Select(c => ((ToolCallRecord?)c.Call, c.Action))];
        }

        return [.. reply.Actions.Where(a => a.NeedsResult || a is WaitAction or DoneAction || tools?.Answers(a) == true).Select(a => ((ToolCallRecord?)null, (ClickyAction?)a))];
    }

    private async Task<StepResult> ExecuteStepAsync(Run run, ReplyCompleted reply, RequestScreenshot? shot, int step, CancellationToken cancellationToken)
    {
        var geometry = shot?.Geometry;
        var approvedAhead = run.Gate.TakeNextStepApproval();
        var plans = new List<Planned>();
        foreach (var (call, action) in Items(reply, run.Tools))
        {
            plans.Add(await PlanAsync(run, call, action, geometry, approvedAhead, cancellationToken).ConfigureAwait(false));
        }

        var results = new List<ToolOutcome>();
        run.Partial = results;
        var acting = plans.Where(p => p.Kind is ItemKind.Control or ItemKind.Tool && p.Refusal is null).ToList();

        // One prompt for everything in the step that needs asking (the model's own questions are separate).
        var asked = false;
        var mouseThisStep = false;
        var toAsk = plans.Where(p => p.NeedsAsk).ToList();
        if (toAsk.Count > 0)
        {
            asked = true;
            var prompt = BuildPrompt(toAsk);
            var answer = await AskAsync(run, prompt, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Permission for {Count} actions ({Irreversible}): {Answer}", toAsk.Count, prompt.Irreversible ? "irreversible" : "normal", answer);
            if (answer == PermissionAnswer.NotNow)
            {
                var refusal = run.Prompter is null ? "Not done: nobody is there to approve it right now." : "Not done: the user said not now.";
                foreach (var plan in plans)
                {
                    results.Add(plan.Kind is ItemKind.Control or ItemKind.Tool or ItemKind.Question or ItemKind.Own
                        ? new ToolOutcome(plan.Call?.Id, plan.Name, refusal, IsError: true)
                        : Passive(plan));
                }

                return new StepResult(results, StepEnd.Declined);
            }

            if (answer == PermissionAnswer.AlwaysThisConversation)
            {
                run.Gate.Remember(prompt.Kind == PermissionKind.Connector ? PermissionKind.Connector : PermissionKind.Control, toAsk.Where(p => p.Tool is not null).Select(p => p.Tool!.Name));
            }
        }

        // Acting without having asked: give the user a moment to cancel, once per run.
        var seconds = run.Settings.CancelWindowSeconds;
        if (!asked && !approvedAhead && !run.CountedDown && run.Card is { } card && seconds > 0 && acting.Any(p => p.Kind == ItemKind.Control || p.Tool?.ReadOnly == false))
        {
            run.CountedDown = true;
            var first = acting.First();
            if (!await card.CountdownAsync(first.Description, TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false))
            {
                foreach (var plan in plans)
                {
                    results.Add(plan.Kind is ItemKind.Control or ItemKind.Tool or ItemKind.Own
                        ? new ToolOutcome(plan.Call?.Id, plan.Name, "Not done: the user cancelled before it ran.", IsError: true)
                        : Passive(plan));
                }

                return new StepResult(results, StepEnd.Cancelled);
            }
        }

        if (acting.Count > 0)
        {
            run.CountedDown = true;
        }

        var failed = false;
        var declined = false;
        var waited = false;
        DoneAction? done = null;
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((failed || declined) && plan.Kind is ItemKind.Control or ItemKind.Tool or ItemKind.Question or ItemKind.Own)
            {
                results.Add(new ToolOutcome(plan.Call?.Id, plan.Name, declined ? "Not executed: the user said not now." : ComputerToolset.SkippedAfterFailure, IsError: true));
                continue;
            }

            switch (plan.Kind)
            {
                case ItemKind.Show or ItemKind.Screenshot:
                    results.Add(Passive(plan));
                    break;
                case ItemKind.Done:
                    done = (DoneAction)plan.Action!;
                    results.Add(Passive(plan));
                    break;
                case ItemKind.Wait:
                    var ms = ((WaitAction)plan.Action!).Ms;
                    await Task.Delay(TimeSpan.FromMilliseconds(ms), _time, cancellationToken).ConfigureAwait(false);
                    waited = true;
                    results.Add(new ToolOutcome(plan.Call?.Id, plan.Name, string.Create(CultureInfo.InvariantCulture, $"Waited {ms} ms.")));
                    break;
                case ItemKind.Rejected:
                    failed = true;
                    results.Add(new ToolOutcome(plan.Call?.Id, plan.Name, $"Rejected: {plan.Refusal}. Nothing was done.", IsError: true));
                    break;
                case ItemKind.Question:
                    var question = (AskPermissionAction)plan.Action!;
                    var answer = await AskAsync(run, new PermissionPrompt($"{question.Action}?", question.Reason, PermissionKind.Question, question.Risk == ActionRisk.Irreversible), cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("The model asked permission ({Risk}): {Answer}", question.Risk, answer);
                    if (answer == PermissionAnswer.NotNow)
                    {
                        declined = true;
                        results.Add(new ToolOutcome(plan.Call?.Id, plan.Name, run.Prompter is null ? "Nobody is there to approve it right now. Don't do it." : "The user said not now. Don't do it."));
                    }
                    else
                    {
                        run.Gate.ApproveNextStep();
                        if (answer == PermissionAnswer.AlwaysThisConversation)
                        {
                            run.Gate.Remember(PermissionKind.Question);
                        }

                        results.Add(new ToolOutcome(plan.Call?.Id, plan.Name, "The user said yes. Go ahead."));
                    }

                    break;
                case ItemKind.Control or ItemKind.Tool when plan.Refusal is not null:
                    failed = true;
                    results.Add(new ToolOutcome(plan.Call?.Id, plan.Name, plan.Refusal, IsError: true));
                    break;
                case ItemKind.Control:
                    run.Card?.ShowStep(step, plan.Description);
                    if (PointOf(plan.Action!, geometry) is { } point)
                    {
                        run.Card?.Avoid(point);
                    }

                    var outcome = await computer.ExecuteAsync(plan.Action!, geometry, new ControlOptions(run.Gate.AlwaysMouse || mouseThisStep), cancellationToken).ConfigureAwait(false);
                    if (outcome.Status == ControlStatus.NeedsMouse)
                    {
                        var mouse = await AskAsync(
                            run,
                            new PermissionPrompt($"Let Clicky use your mouse to {char.ToLowerInvariant(plan.Description[0])}{plan.Description[1..]}?", "It can't press this one directly, so the pointer will move there and back.", PermissionKind.Mouse, plan.Risk.Irreversible),
                            cancellationToken).ConfigureAwait(false);
                        if (mouse == PermissionAnswer.NotNow)
                        {
                            outcome = ControlOutcome.Failed("Not done: the user didn't let Clicky move the mouse. Try the keyboard instead, or ask the user to do it.");
                        }
                        else
                        {
                            mouseThisStep = true;
                            if (mouse == PermissionAnswer.AlwaysThisConversation)
                            {
                                run.Gate.Remember(PermissionKind.Mouse);
                            }

                            outcome = await computer.ExecuteAsync(plan.Action!, geometry, new ControlOptions(AllowMouse: true), cancellationToken).ConfigureAwait(false);
                        }
                    }

                    logger.LogInformation("Agent step {Step}: {Action} -> {Status}: {Detail}", step, plan.Name, outcome.Status, outcome.Detail);
                    failed = outcome.Status != ControlStatus.Done;
                    results.Add(new ToolOutcome(plan.Call?.Id, plan.Name, outcome.Detail, failed));
                    break;
                case ItemKind.Own:
                    // The caller's tools do their own asking (the routine card).
                    var own = await run.Tools!.CallAsync(plan.Action!, plan.Call?.Id, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Agent step {Step}: {Action} handed back -> {Result}", step, plan.Name, own.IsError ? "error" : "ok");
                    failed = own.IsError;
                    results.Add(own with { ToolCallId = plan.Call?.Id });
                    break;
                case ItemKind.Tool:
                    run.Card?.ShowStep(step, plan.Description);
                    var use = (UseToolAction)plan.Action!;
                    var toolResult = await connectors!.CallAsync(use.Tool, use.Input, cancellationToken).ConfigureAwait(false);
                    var text = toolResult.ToText();
                    if (text.Length > MaxToolResultChars)
                    {
                        text = text[..MaxToolResultChars] + "\n…(cut short)";
                    }

                    logger.LogInformation("Agent step {Step}: tool {Tool} -> {Result}", step, use.Tool, toolResult.IsError ? "error" : "ok");
                    failed = toolResult.IsError;
                    results.Add(new ToolOutcome(plan.Call?.Id, plan.Name, text.Length > 0 ? text : toolResult.IsError ? "The tool failed without saying why." : "Done (no output).", toolResult.IsError));
                    break;
            }
        }

        return declined
            ? new StepResult(results, StepEnd.Declined)
            : done is not null
                ? new StepResult(results, StepEnd.Done, done.Summary, done.Success, waited)
                : new StepResult(results, StepEnd.Continue, Waited: waited);
    }

    private async Task<Planned> PlanAsync(Run run, ToolCallRecord? call, ClickyAction? action, ScreenshotGeometry? geometry, bool approvedAhead, CancellationToken cancellationToken)
    {
        var name = call?.Name ?? action?.Name ?? "?";
        if (call is { Accepted: false })
        {
            return new Planned(call, action, name, ItemKind.Rejected, name, Risk.Normal, Refusal: call.Error ?? "invalid input");
        }

        if (action is not null && run.Tools?.Answers(action) == true)
        {
            return new Planned(call, action, name, ItemKind.Own, Describe(action, ControlTarget.Unknown), Risk.Normal);
        }

        switch (action)
        {
            case null when call?.ToolsetName is not null:
                return new Planned(call, null, name, ItemKind.Screenshot, name, Risk.Normal);
            case null:
                return new Planned(call, null, name, ItemKind.Rejected, name, Risk.Normal, Refusal: "unknown tool");
            case WaitAction:
                return new Planned(call, action, name, ItemKind.Wait, name, Risk.Normal);
            case DoneAction:
                return new Planned(call, action, name, ItemKind.Done, name, Risk.Normal);
            case AskPermissionAction:
                return new Planned(call, action, name, ItemKind.Question, name, Risk.Normal);
            case UseToolAction use:
                var tool = connectors?.Tools.FirstOrDefault(t => t.Name == use.Tool);
                if (tool is null)
                {
                    return new Planned(call, action, name, ItemKind.Tool, use.Tool, Risk.Normal, Refusal: $"No connected tool is called {use.Tool} right now. Use one from the current list, or tell the user.");
                }

                var toolRisk = RiskClassifier.Classify(tool);
                var askTool = tool.NeedsConfirmation && !(approvedAhead || (!toolRisk.Irreversible && (run.PreApproved || run.Gate.AlwaysTool(tool.Name))));
                return new Planned(call, action, name, ItemKind.Tool, Describe(action, ControlTarget.Unknown, tool), toolRisk, askTool, Tool: tool);
            case { ControlsComputer: true }:
                if (!run.Settings.Enabled)
                {
                    return new Planned(call, action, name, ItemKind.Control, name, Risk.Normal, Refusal: "Not done: acting on the computer is switched off in Clicky's settings. Tell the user.");
                }

                // The card is clickable, so it must not be what the action (or its inspection) hits.
                if (PointOf(action, geometry) is { } point)
                {
                    run.Card?.Avoid(point);
                }

                ControlTarget target;
                try
                {
                    target = await computer.InspectAsync(action, geometry, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Could not inspect the target of {Action}", name);
                    target = ControlTarget.Unknown;
                }

                var description = Describe(action, target);
                if (target.Elevated)
                {
                    return new Planned(call, action, name, ItemKind.Control, description, Risk.Normal, Refusal: $"Not done: {target.App ?? "that window"} runs as administrator, and Windows doesn't let Clicky reach it. Ask the user to do this step.");
                }

                var risk = RiskClassifier.Classify(action, target);
                var ask = !approvedAhead && (risk.Irreversible || !(run.PreApproved || run.Gate.AlwaysControl));
                return new Planned(call, action, name, ItemKind.Control, description, risk, ask);
            default:
                return new Planned(call, action, name, ItemKind.Show, name, Risk.Normal);
        }
    }

    private static PermissionPrompt BuildPrompt(List<Planned> items)
    {
        var irreversible = items.Any(p => p.Risk.Irreversible);
        var kind = items.All(p => p.Kind == ItemKind.Tool) ? PermissionKind.Connector : PermissionKind.Control;
        var detail = new StringBuilder();
        string question;
        if (items.Count == 1)
        {
            question = $"{items[0].Description}?";
            if (items[0].Action is UseToolAction use)
            {
                detail.Append(Short(use.Input.GetRawText(), 300));
            }
        }
        else
        {
            question = "Let Clicky do this?";
            for (var i = 0; i < items.Count; i++)
            {
                detail.Append(CultureInfo.InvariantCulture, $"{i + 1}. {items[i].Description}\n");
            }
        }

        foreach (var why in items.Where(p => p.Risk.Irreversible).Select(p => p.Risk.Why).Distinct())
        {
            detail.Append(CultureInfo.InvariantCulture, $"\nThis can't be undone: {why}.");
        }

        var text = detail.ToString().Trim();
        return new PermissionPrompt(question, text.Length > 0 ? text : null, kind, irreversible);
    }

    private static ToolOutcome Passive(Planned plan) => plan.Kind switch
    {
        ItemKind.Screenshot => new ToolOutcome(plan.Call?.Id, plan.Name, ScreenshotResult),
        ItemKind.Done => new ToolOutcome(plan.Call?.Id, plan.Name, "Noted."),
        ItemKind.Rejected => new ToolOutcome(plan.Call?.Id, plan.Name, $"Rejected: {plan.Refusal}. Nothing was done.", IsError: true),
        _ => new ToolOutcome(plan.Call?.Id, plan.Name, DefaultToolResults.Shown),
    };

    private static async Task<PermissionAnswer> AskAsync(Run run, PermissionPrompt prompt, CancellationToken cancellationToken)
    {
        if (run.Prompter is null)
        {
            return PermissionAnswer.NotNow;
        }

        var answer = await run.Prompter.AskAsync(prompt, cancellationToken).ConfigureAwait(false);

        // Belt and braces: an irreversible action is never "always".
        return prompt.Irreversible && answer == PermissionAnswer.AlwaysThisConversation ? PermissionAnswer.AllowOnce : answer;
    }

    private async Task<RequestScreenshot?> CaptureAsync(RequestScreenshot? previous, CancellationToken cancellationToken)
    {
        // The same monitor as before, wherever the user's pointer has gone.
        PixelPoint? at = previous is null ? null : Centre(previous.Geometry.Source);
        try
        {
            var result = await capture.CaptureAsync(new CaptureTarget(settings.Current.Capture.Mode, at), cancellationToken).ConfigureAwait(false);
            return new RequestScreenshot(result.Png, result.Geometry);
        }
        catch (CaptureException ex)
        {
            logger.LogWarning(ex, "Agent could not take a screenshot");
            return null;
        }
    }

    private sealed class Run(PermissionGate gate, IAgentCard? card, IPermissionPrompter? prompter, bool unattended, bool preApproved, AgentSettings settings, IRunTools? tools)
    {
        public PermissionGate Gate { get; } = gate;

        public IAgentCard? Card { get; } = card;

        /// <summary>Null when unattended without a prompter: everything that needs asking is refused.</summary>
        public IPermissionPrompter? Prompter { get; } = prompter;

        public bool Unattended { get; } = unattended;

        /// <summary>Ordinary actions need no question (see <see cref="AgentRunOptions.PreApproved"/>).</summary>
        public bool PreApproved { get; } = preApproved;

        public AgentSettings Settings { get; } = settings;

        /// <summary>Calls the caller answers itself.</summary>
        public IRunTools? Tools { get; } = tools;

        public bool CountedDown { get; set; }

        /// <summary>The current step's results so far, kept if the user stops mid-step.</summary>
        public IReadOnlyList<ToolOutcome> Partial { get; set; } = [];
    }

    private sealed record Planned(
        ToolCallRecord? Call,
        ClickyAction? Action,
        string Name,
        ItemKind Kind,
        string Description,
        Risk Risk,
        bool NeedsAsk = false,
        string? Refusal = null,
        ConnectorTool? Tool = null);

    private sealed record StepResult(IReadOnlyList<ToolOutcome> Results, StepEnd End, string? Summary = null, bool Success = true, bool Waited = false);
}
