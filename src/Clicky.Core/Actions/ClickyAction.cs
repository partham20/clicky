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
public abstract record ClickyAction
{
    /// <summary>The schema name of this action, e.g. "point".</summary>
    [JsonIgnore]
    public abstract string Name { get; }

    /// <summary>True for actions that change something on the computer and so need the permission gate.</summary>
    [JsonIgnore]
    public virtual bool ControlsComputer => false;
}

public sealed record SayAction(string Text) : ClickyAction
{
    public override string Name => "say";
}

public sealed record PointAction(double X, double Y, string? Label = null) : ClickyAction
{
    public override string Name => "point";
}

/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
public sealed record HighlightAction(double X, double Y, double Width, double Height, string? Label = null) : ClickyAction
{
    public override string Name => "highlight";
}

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
    public override string Name => "draw";

    public bool Equals(DrawAction? other) =>
        other is not null && Shape == other.Shape && Radius == other.Radius && Color == other.Color && Label == other.Label
        && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Shape, Points.Count, Radius, Color, Label);
}

/// <param name="X">Where the user should act, if the step has a place on screen.</param>
public sealed record StepAction(int Index, string Instruction, int? Total = null, double? X = null, double? Y = null) : ClickyAction
{
    public override string Name => "step";
}

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
public sealed record ClickAction(double X, double Y, MouseButton Button = MouseButton.Left, int Count = 1, string? Target = null) : ClickyAction
{
    public override string Name => "click";

    public override bool ControlsComputer => true;
}

public sealed record TypeAction(string Text) : ClickyAction
{
    public override string Name => "type";

    public override bool ControlsComputer => true;
}

/// <param name="Keys">A key or chord as the model wrote it, e.g. "Ctrl+S".</param>
public sealed record KeyAction(string Keys) : ClickyAction
{
    public override string Name => "key";

    public override bool ControlsComputer => true;
}

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
public sealed record ScrollAction(double X, double Y, ScrollDirection Direction, int Amount = 3) : ClickyAction
{
    public override string Name => "scroll";

    public override bool ControlsComputer => true;
}

public sealed record WaitAction(int Ms) : ClickyAction
{
    public override string Name => "wait";
}

[JsonConverter(typeof(JsonStringEnumConverter<ActionRisk>))]
public enum ActionRisk
{
    [JsonStringEnumMemberName("normal")]
    Normal,

    [JsonStringEnumMemberName("irreversible")]
    Irreversible,
}

/// <param name="Action">What the model is about to do, in the user's terms.</param>
public sealed record AskPermissionAction(string Action, string? Reason = null, ActionRisk Risk = ActionRisk.Normal) : ClickyAction
{
    public override string Name => "ask_permission";
}

public sealed record DoneAction(string? Summary = null, bool Success = true) : ClickyAction
{
    public override string Name => "done";
}
