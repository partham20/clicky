using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Clicky.Core.Routines;

/// <summary>A routine tool as Claude sees it, ready for a Messages API "tools" array.</summary>
/// <param name="NeedsConfirmation">Run the permission gate first (deleting can't be undone).</param>
public sealed record RoutineTool(string Name, string Description, JsonElement InputSchema, bool NeedsConfirmation);

public sealed record RoutineToolResult(bool IsError, string Text);

/// <summary>Who is calling a routine tool.</summary>
/// <param name="ClickyId">
/// The Clicky whose chat the call came from: new routines belong to it, and it sees and changes only
/// its own. Null for the main Clicky, which sees every routine.
/// </param>
/// <param name="Attended">
/// The user is there to answer (a chat message, not a routine run). Unattended calls can only list
/// and pause: creating, deleting and running routines needs someone to ask.
/// </param>
/// <param name="Near">Where the user is (physical pixels), so the confirmation card shows on that monitor.</param>
public sealed record RoutineToolContext(string? ClickyId, bool Attended, Geometry.PixelRect? Near = null);

/// <summary>
/// "Do this every morning" by conversation: tools a chat or agent loop offers so Claude can create,
/// list, pause, run and delete routines. The schedule is structured; Claude turns "every morning"
/// into daily at 08:00 using the guidance in the descriptions. Creating and deleting ask the user
/// first through <see cref="RoutineConfirmer"/>; without one they are refused.
/// </summary>
public sealed class RoutineTools(RoutineEngine engine, TimeProvider? time = null, Func<TimeZoneInfo>? zone = null, RoutineConfirmer? confirmer = null)
{
    public const string Create = "create_routine";
    public const string List = "list_routines";
    public const string Pause = "pause_routine";
    public const string RunNow = "run_routine_now";
    public const string Delete = "delete_routine";

    private const string RoutineRef = """{ "type": "string", "description": "The routine's id or exact name, from list_routines." }""";

    private static readonly string ScheduleSchema = """
        {
          "type": "object",
          "description": "When it runs, in the user's local time. Guidance: 'every morning' = daily 08:00, 'every evening' = daily 18:00, 'every weekday' = weekdays, 'every few hours' = every 180 minutes from 09:00 until 21:00, 'every hour' = every 60 minutes. Ask only if the user gave no hint at all.",
          "properties": {
            "kind": { "type": "string", "enum": ["once", "daily", "weekdays", "weekly", "every", "monthly", "cron", "startup"] },
            "time": { "type": "string", "description": "HH:mm, 24-hour local time (once, daily, weekdays, weekly, monthly)." },
            "date": { "type": "string", "description": "once: local date, YYYY-MM-DD." },
            "days": { "type": "array", "items": { "type": "string", "enum": ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] }, "description": "weekly, or to limit 'every' to some days." },
            "every_minutes": { "type": "integer", "minimum": 5, "maximum": 10080, "description": "every: minutes between runs." },
            "from": { "type": "string", "description": "every: HH:mm of the first run each day (optional)." },
            "until": { "type": "string", "description": "every: no runs at or after this HH:mm (optional)." },
            "day_of_month": { "type": "integer", "minimum": 1, "maximum": 31, "description": "monthly: 31 means the last day." },
            "cron": { "type": "string", "description": "cron: five fields, minute hour day-of-month month day-of-week, local time. Only when nothing else fits." }
          },
          "required": ["kind"]
        }
        """;

    private static readonly IReadOnlyList<RoutineTool> s_definitions =
    [
        Tool(
            Create,
            "Create a routine: something Clicky does on a schedule, such as \"brief me every morning\" or \"check my inbox every few hours\". " +
            "Routines run while Clicky is open, catch up once after sleep, and put their results in the Clicky's chat without speaking. " +
            "Use it when the user asks for something to happen repeatedly or at a later time. The user is asked to save it, and the result says whether they did.",
            $$"""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "Short name, e.g. \"Morning briefing\"." },
                "prompt": { "type": "string", "description": "What to do each time, written as a complete instruction that makes sense on its own later." },
                "schedule": {{ScheduleSchema}}
              },
              "required": ["name", "prompt", "schedule"]
            }
            """,
            needsConfirmation: true),
        Tool(List, "List the user's routines with their schedule, status and next run.", """{ "type": "object", "properties": {} }""", needsConfirmation: false),
        Tool(
            Pause,
            "Pause or resume a routine.",
            $$"""{ "type": "object", "properties": { "routine": {{RoutineRef}}, "paused": { "type": "boolean" } }, "required": ["routine", "paused"] }""",
            needsConfirmation: false),
        Tool(RunNow, "Run a routine right now, without changing its schedule.", $$"""{ "type": "object", "properties": { "routine": {{RoutineRef}} }, "required": ["routine"] }""", needsConfirmation: false),
        Tool(Delete, "Delete a routine and its history. Can't be undone; the user is asked first.", $$"""{ "type": "object", "properties": { "routine": {{RoutineRef}} }, "required": ["routine"] }""", needsConfirmation: true),
    ];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<TimeZoneInfo> _zone = zone ?? (() => TimeZoneInfo.Local);

    public static IReadOnlyList<RoutineTool> Definitions => s_definitions;

    public static bool IsRoutineTool(string name) => s_definitions.Any(t => t.Name == name);

    /// <summary>The tools to offer: all of them when the user is there, otherwise only list and pause.</summary>
    public static IReadOnlyList<RoutineTool> For(bool attended) =>
        attended ? s_definitions : [.. s_definitions.Where(t => !NeedsUser(t.Name))];

    /// <summary>Runs a tool call. Problems come back as error results for Claude; only cancellation throws.</summary>
    public async Task<RoutineToolResult> CallAsync(string name, JsonElement input, RoutineToolContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Attended && NeedsUser(name))
        {
            return Error("Nobody is here to ask, so routines can't be created, deleted or run from here. Suggest it to the user in your reply instead.");
        }

        try
        {
            return name switch
            {
                Create => await CreateAsync(input, context, cancellationToken).ConfigureAwait(false),
                List => ListRoutines(context),
                Pause => await PauseAsync(input, context, cancellationToken).ConfigureAwait(false),
                RunNow => await RunAsync(input, context, cancellationToken).ConfigureAwait(false),
                Delete => await DeleteAsync(input, context, cancellationToken).ConfigureAwait(false),
                _ => Error($"Unknown tool {name}."),
            };
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>Reads the tool's schedule object. Throws <see cref="ArgumentException"/> with a message for Claude.</summary>
    public static RoutineSchedule ParseSchedule(JsonElement schedule)
    {
        if (schedule.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("schedule must be an object.");
        }

        var kind = String(schedule, "kind")?.ToLowerInvariant();
        RoutineSchedule result = kind switch
        {
            "once" => RoutineSchedule.Once(Date(schedule).ToDateTime(Time(schedule, "time", required: true)!.Value)),
            "daily" => RoutineSchedule.Daily(Time(schedule, "time", required: true)!.Value, Days(schedule) ?? RoutineDays.Every),
            "weekdays" => RoutineSchedule.Weekdays(Time(schedule, "time", required: true)!.Value),
            "weekly" => RoutineSchedule.Daily(Time(schedule, "time", required: true)!.Value, Days(schedule) ?? throw new ArgumentException("weekly needs days.")),
            "every" => RoutineSchedule.Every(
                TimeSpan.FromMinutes(Int(schedule, "every_minutes") ?? throw new ArgumentException("every needs every_minutes.")),
                Time(schedule, "from", required: false),
                Time(schedule, "until", required: false),
                Days(schedule) ?? RoutineDays.Every),
            "monthly" => RoutineSchedule.Monthly(Int(schedule, "day_of_month") ?? throw new ArgumentException("monthly needs day_of_month."), Time(schedule, "time", required: true)!.Value),
            "cron" => RoutineSchedule.FromCron(String(schedule, "cron") ?? throw new ArgumentException("cron needs a cron expression.")),
            "startup" => RoutineSchedule.AtStartup(),
            _ => throw new ArgumentException("schedule.kind must be once, daily, weekdays, weekly, every, monthly, cron or startup."),
        };

        return result.Validate() is { } error ? throw new ArgumentException(error) : result;
    }

    /// <summary>Creating and deleting ask the user; running now from a routine could keep a routine running itself.</summary>
    private static bool NeedsUser(string name) => name is Create or Delete or RunNow;

    private async Task<RoutineToolResult> CreateAsync(JsonElement input, RoutineToolContext context, CancellationToken cancellationToken)
    {
        var name = String(input, "name") ?? throw new ArgumentException("name is required.");
        var prompt = String(input, "prompt") ?? throw new ArgumentException("prompt is required.");
        var schedule = ParseSchedule(input.TryGetProperty("schedule", out var s) ? s : default);
        if (schedule.Kind == RoutineScheduleKind.Once && schedule.NextAfter(_time.GetUtcNow(), _zone(), _time.GetUtcNow()) is null)
        {
            throw new ArgumentException("That time has already passed.");
        }

        if (confirmer is null)
        {
            return Error("Clicky can't ask the user to save a routine from here, so nothing was saved.");
        }

        var draft = new RoutineDraft(name, prompt, schedule, context.ClickyId);
        using var card = await confirmer.ConfirmCreateAsync(draft, context.Near, cancellationToken).ConfigureAwait(false);
        if (!card.Approved)
        {
            return new RoutineToolResult(false, $"The user chose not to save the routine \"{name}\". Nothing was set up.");
        }

        Routine routine;
        try
        {
            routine = await engine.CreateAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            card.Finish($"Couldn't save it: {ex.Message}", success: false);
            throw;
        }

        card.Finish(confirmer.SavedMessage(routine), success: true);
        var next = routine.NextDueAt is { } at ? $" Next run: {RoutineFormat.When(at, _time.GetUtcNow(), _zone(), CultureInfo.InvariantCulture)}." : string.Empty;
        return new RoutineToolResult(false, $"The user saved the routine \"{routine.Name}\" (id {routine.Id}): {routine.Schedule.Describe(CultureInfo.InvariantCulture)}.{next} It runs while Clicky is open and its results arrive in the chat.");
    }

    private RoutineToolResult ListRoutines(RoutineToolContext context)
    {
        var routines = Visible(context);
        if (routines.Count == 0)
        {
            return new RoutineToolResult(false, "No routines yet.");
        }

        var text = new StringBuilder();
        foreach (var routine in routines)
        {
            text.Append(CultureInfo.InvariantCulture, $"- \"{routine.Name}\" (id {routine.Id}): {routine.Schedule.Describe(CultureInfo.InvariantCulture)}. {engine.GetStatus(routine.Id).Text}.");
            if (routine.LastRunAt is { } last)
            {
                text.Append(CultureInfo.InvariantCulture, $" Last run {RoutineFormat.When(last, _time.GetUtcNow(), _zone(), CultureInfo.InvariantCulture)}: {routine.LastStatus}");
                if (routine.LastMessage is { } message)
                {
                    text.Append(" (").Append(message).Append(')');
                }

                text.Append('.');
            }

            text.Append(" Does: ").Append(routine.Prompt.ReplaceLineEndings(" ")).Append('\n');
        }

        return new RoutineToolResult(false, text.ToString().TrimEnd());
    }

    private async Task<RoutineToolResult> PauseAsync(JsonElement input, RoutineToolContext context, CancellationToken cancellationToken)
    {
        var routine = Resolve(input, context);
        var paused = input.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True;
        await engine.SetEnabledAsync(routine.Id, !paused, cancellationToken).ConfigureAwait(false);
        return new RoutineToolResult(false, paused ? $"Paused \"{routine.Name}\"." : $"Resumed \"{routine.Name}\". {engine.GetStatus(routine.Id).Text}.");
    }

    private async Task<RoutineToolResult> RunAsync(JsonElement input, RoutineToolContext context, CancellationToken cancellationToken)
    {
        var routine = Resolve(input, context);
        await engine.RunNowAsync(routine.Id, cancellationToken).ConfigureAwait(false);
        return new RoutineToolResult(false, $"Started \"{routine.Name}\"; its result will arrive in the chat.");
    }

    private async Task<RoutineToolResult> DeleteAsync(JsonElement input, RoutineToolContext context, CancellationToken cancellationToken)
    {
        var routine = Resolve(input, context);
        if (confirmer is null)
        {
            return Error("Clicky can't ask the user to delete a routine from here, so nothing was deleted.");
        }

        using var card = await confirmer.ConfirmDeleteAsync(routine, context.Near, cancellationToken).ConfigureAwait(false);
        if (!card.Approved)
        {
            return new RoutineToolResult(false, $"The user chose to keep \"{routine.Name}\". Nothing was deleted.");
        }

        await engine.DeleteAsync(routine.Id, cancellationToken).ConfigureAwait(false);
        card.Finish($"Deleted \u201C{routine.Name}\u201D.", success: true);
        return new RoutineToolResult(false, $"Deleted \"{routine.Name}\".");
    }

    /// <summary>The routines this caller may see and change.</summary>
    private List<Routine> Visible(RoutineToolContext context) =>
        [.. engine.Routines.Where(r => context.ClickyId is null || string.Equals(r.ClickyId, context.ClickyId, StringComparison.Ordinal))];

    private Routine Resolve(JsonElement input, RoutineToolContext context)
    {
        var key = String(input, "routine") ?? throw new ArgumentException("routine is required.");
        var visible = Visible(context);
        if (visible.FirstOrDefault(r => r.Id == key) is { } byId)
        {
            return byId;
        }

        var byName = visible.Where(r => string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new KeyNotFoundException($"No routine called \"{key}\". Use list_routines to see them."),
            _ => throw new ArgumentException($"Several routines are called \"{key}\"; use the id from list_routines."),
        };
    }

    private static RoutineTool Tool(string name, string description, string schema, bool needsConfirmation)
    {
        using var document = JsonDocument.Parse(schema);
        return new RoutineTool(name, description, document.RootElement.Clone(), needsConfirmation);
    }

    private static RoutineToolResult Error(string message) => new(true, message);

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static TimeOnly? Time(JsonElement element, string property, bool required)
    {
        var text = String(element, property);
        if (text is null)
        {
            return required ? throw new ArgumentException($"{property} is required (HH:mm).") : null;
        }

        return TimeOnly.TryParseExact(text, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new ArgumentException($"{property} must be HH:mm, 24-hour: \"{text}\".");
    }

    private static DateOnly Date(JsonElement element)
    {
        var text = String(element, "date") ?? throw new ArgumentException("once needs a date (YYYY-MM-DD).");
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"date must be YYYY-MM-DD: \"{text}\".");
    }

    private static RoutineDays? Days(JsonElement element)
    {
        if (!element.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array || days.GetArrayLength() == 0)
        {
            return null;
        }

        var result = RoutineDays.None;
        foreach (var day in days.EnumerateArray())
        {
            result |= (day.GetString() ?? string.Empty).ToLowerInvariant() switch
            {
                "sun" or "sunday" => RoutineDays.Sunday,
                "mon" or "monday" => RoutineDays.Monday,
                "tue" or "tuesday" => RoutineDays.Tuesday,
                "wed" or "wednesday" => RoutineDays.Wednesday,
                "thu" or "thursday" => RoutineDays.Thursday,
                "fri" or "friday" => RoutineDays.Friday,
                "sat" or "saturday" => RoutineDays.Saturday,
                var other => throw new ArgumentException($"Unknown day \"{other}\"; use mon, tue, wed, thu, fri, sat, sun."),
            };
        }

        return result;
    }
}
