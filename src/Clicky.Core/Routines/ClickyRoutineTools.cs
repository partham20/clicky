using System.Text.Json;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Clickys;
using Clicky.Core.Connectors;

namespace Clicky.Core.Routines;

/// <summary>
/// Gives a Clicky's chat (M14) the routine tools for its own routines: create, list, pause, run now
/// and delete, plus Claude's <see cref="RoutineAction"/> as another way to create one. Creating and
/// deleting ask the user on the agent card. A routine run is unattended: it is offered only list and
/// pause, and anything else is refused.
/// </summary>
/// <param name="tools">Lazy because the engine runs routines through the Clicky service that uses these tools.</param>
public sealed class ClickyRoutineTools(Lazy<RoutineTools> tools) : IClickyTools
{
    public const string ConnectorId = "clicky-routines";

    private static readonly IReadOnlyList<ConnectorTool> s_attended = [.. RoutineTools.For(attended: true).Select(ToConnectorTool)];
    private static readonly IReadOnlyList<ConnectorTool> s_unattended = [.. RoutineTools.For(attended: false).Select(ToConnectorTool)];

    /// <summary>Anyone but a routine run can be asked: a chat message, a retry, a voice command.</summary>
    public static bool IsAttended(ClickyRunTrigger trigger) => trigger != ClickyRunTrigger.Routine;

    public IReadOnlyList<ConnectorTool> ToolsFor(ClickyAgent clicky, ClickyRunTrigger trigger) => IsAttended(trigger) ? s_attended : s_unattended;

    public bool Answers(ClickyAction action) => action is RoutineAction || (action is UseToolAction use && RoutineTools.IsRoutineTool(use.Tool));

    public async Task<ToolOutcome> CallAsync(ClickyAgent clicky, ClickyRunTrigger trigger, ClickyAction action, string? toolCallId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clicky);
        var (name, input, outcomeName) = action switch
        {
            UseToolAction use => (use.Tool, use.Input, use.Tool),
            RoutineAction routine => (RoutineTools.Create, CreateInput(routine), routine.Name),
            _ => throw new ArgumentException($"Not a routine tool call: {action?.Name}.", nameof(action)),
        };

        var result = await tools.Value.CallAsync(name, input, new RoutineToolContext(clicky.Id, IsAttended(trigger)), cancellationToken).ConfigureAwait(false);
        return new ToolOutcome(toolCallId, outcomeName, result.Text, result.IsError);
    }

    private static JsonElement CreateInput(RoutineAction routine) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["name"] = routine.Title,
            ["prompt"] = routine.Prompt,
            ["schedule"] = routine.Schedule,
        });

    private static ConnectorTool ToConnectorTool(RoutineTool tool) => new(
        tool.Name,
        tool.Description,
        tool.InputSchema,
        ConnectorId,
        "Routines",
        AccountId: string.Empty,
        AccountLabel: string.Empty,
        ServerToolName: tool.Name,
        Title: null,
        ReadOnly: tool.Name == RoutineTools.List,
        Destructive: tool.Name == RoutineTools.Delete,
        OpenWorld: false);
}
