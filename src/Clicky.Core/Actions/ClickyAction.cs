using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clicky.Core.Actions;

/// <summary>
/// One thing the model says, shows or does, in the shared action schema
/// (<c>Actions/actions.schema.json</c>). Every backend turns its output into these, so the rest of
/// the app never knows which backend answered. Coordinates are screenshot pixels; map them with
/// <see cref="Geometry.CoordinateMapper"/> before touching the screen.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SayAction), "say")]
[JsonDerivedType(typeof(PointAction), "point")]
[JsonDerivedType(typeof(HighlightAction), "highlight")]
[JsonDerivedType(typeof(DrawAction), "draw")]
[JsonDerivedType(typeof(StepAction), "step")]
[JsonDerivedType(typeof(ClickAction), "click")]
[JsonDerivedType(typeof(TypeAction), "type")]
[JsonDerivedType(typeof(KeyAction), "key")]
[JsonDerivedType(typeof(ScrollAction), "scroll")]
[JsonDerivedType(typeof(WaitAction), "wait")]
[JsonDerivedType(typeof(AskPermissionAction), "ask_permission")]
[JsonDerivedType(typeof(DoneAction), "done")]
[JsonDerivedType(typeof(OpenUrlAction), "open_url")]
[JsonDerivedType(typeof(UseToolAction), "use_tool")]
public abstract record ClickyAction
{
    /// <summary>The schema name of this action, e.g. "point".</summary>
    [JsonIgnore]
    public string Name => this switch
    {
        SayAction => "say",
        PointAction => "point",
        HighlightAction => "highlight",
        DrawAction => "draw",
        StepAction => "step",
        ClickAction => "click",
        TypeAction => "type",
        KeyAction => "key",
        ScrollAction => "scroll",
        WaitAction => "wait",
        AskPermissionAction => "ask_permission",
        DoneAction => "done",
        OpenUrlAction => "open_url",
        UseToolAction => "use_tool",
        _ => throw new InvalidOperationException($"Unknown action {GetType().Name}."),
    };

    /// <summary>True for actions that change something on the computer and so need the permission gate.</summary>
    [JsonIgnore]
    public bool ControlsComputer => this is ClickAction or TypeAction or KeyAction or ScrollAction or OpenUrlAction;

    /// <summary>
    /// True for actions the model expects a result from (the computer-use agent runs them and
    /// continues): computer control, connector tools and permission questions.
    /// </summary>
    [JsonIgnore]
    public bool NeedsResult => ControlsComputer || this is UseToolAction or AskPermissionAction;
}

public sealed record SayAction(string Text) : ClickyAction;

public sealed record PointAction(double X, double Y, string? Label = null) : ClickyAction;

/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
public sealed record HighlightAction(double X, double Y, double Width, double Height, string? Label = null) : ClickyAction;

public sealed record DrawPoint(double X, double Y);

[JsonConverter(typeof(JsonStringEnumConverter<DrawShape>))]
public enum DrawShape
{
    [JsonStringEnumMemberName("arrow")]
    Arrow,

    [JsonStringEnumMemberName("circle")]
    Circle,

    [JsonStringEnumMemberName("underline")]
    Underline,

    [JsonStringEnumMemberName("freehand")]
    Freehand,
}

[JsonConverter(typeof(JsonStringEnumConverter<DrawColor>))]
public enum DrawColor
{
    [JsonStringEnumMemberName("accent")]
    Accent,

    [JsonStringEnumMemberName("red")]
    Red,

    [JsonStringEnumMemberName("green")]
    Green,

    [JsonStringEnumMemberName("yellow")]
    Yellow,

    [JsonStringEnumMemberName("blue")]
    Blue,
}

public sealed record DrawAction(DrawShape Shape, IReadOnlyList<DrawPoint> Points, double? Radius = null, DrawColor? Color = null, string? Label = null) : ClickyAction
{
    public bool Equals(DrawAction? other) =>
        other is not null && Shape == other.Shape && Radius == other.Radius && Color == other.Color && Label == other.Label
        && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Shape, Points.Count, Radius, Color, Label);
}

/// <param name="X">Where the user should act, if the step has a place on screen.</param>
public sealed record StepAction(int Index, string Instruction, int? Total = null, double? X = null, double? Y = null) : ClickyAction;

[JsonConverter(typeof(JsonStringEnumConverter<MouseButton>))]
public enum MouseButton
{
    [JsonStringEnumMemberName("left")]
    Left,

    [JsonStringEnumMemberName("right")]
    Right,

    [JsonStringEnumMemberName("middle")]
    Middle,
}

/// <param name="Target">Name of the control, so UI Automation can invoke it without moving the pointer.</param>
public sealed record ClickAction(double X, double Y, MouseButton Button = MouseButton.Left, int Count = 1, string? Target = null) : ClickyAction;

public sealed record TypeAction(string Text) : ClickyAction;

/// <param name="Keys">A key or chord as the model wrote it, e.g. "Ctrl+S".</param>
/// <param name="Repeat">How many times to press it.</param>
public sealed record KeyAction(string Keys, int Repeat = 1) : ClickyAction;

[JsonConverter(typeof(JsonStringEnumConverter<ScrollDirection>))]
public enum ScrollDirection
{
    [JsonStringEnumMemberName("up")]
    Up,

    [JsonStringEnumMemberName("down")]
    Down,

    [JsonStringEnumMemberName("left")]
    Left,

    [JsonStringEnumMemberName("right")]
    Right,
}

/// <param name="Amount">Mouse-wheel notches.</param>
public sealed record ScrollAction(double X, double Y, ScrollDirection Direction, int Amount = 3) : ClickyAction;

public sealed record WaitAction(int Ms) : ClickyAction;

[JsonConverter(typeof(JsonStringEnumConverter<ActionRisk>))]
public enum ActionRisk
{
    [JsonStringEnumMemberName("normal")]
    Normal,

    [JsonStringEnumMemberName("irreversible")]
    Irreversible,
}

/// <param name="Action">What the model is about to do, in the user's terms.</param>
public sealed record AskPermissionAction(string Action, string? Reason = null, ActionRisk Risk = ActionRisk.Normal) : ClickyAction;

public sealed record DoneAction(string? Summary = null, bool Success = true) : ClickyAction;

/// <param name="Url">An absolute http or https address (checked by the parser).</param>
public sealed record OpenUrlAction(string Url) : ClickyAction;

/// <summary>A call to a connected app's tool (M16). On the API backend these arrive as real tool calls.</summary>
/// <param name="Tool">The model-facing tool name, e.g. "github__create_issue" ("name" in the schema).</param>
/// <param name="Input">The arguments, a JSON object.</param>
public sealed record UseToolAction([property: JsonPropertyName("name")] string Tool, JsonElement Input) : ClickyAction
{
    public bool Equals(UseToolAction? other) =>
        other is not null && Tool == other.Tool && JsonElement.DeepEquals(Input, other.Input);

    public override int GetHashCode() => HashCode.Combine(Tool, Input.ValueKind);
}
