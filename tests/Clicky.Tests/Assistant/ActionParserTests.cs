using Clicky.Core.Actions;

namespace Clicky.Tests.Assistant;

public sealed class ActionParserTests
{
    [Fact]
    public void Parses_a_point_with_type_anywhere_in_the_object()
    {
        var parsed = ActionParser.Parse("""{"x": 120, "y": 64.5, "label": "Share", "type": "point"}""");

        Assert.True(parsed.IsValid, parsed.Error);
        Assert.Equal(new PointAction(120, 64.5, "Share"), parsed.Action);
    }

    [Fact]
    public void Optional_fields_take_their_defaults()
    {
        Assert.Equal(new ClickAction(5, 6), ActionParser.Parse("""{"type":"click","x":5,"y":6}""").Action);
        Assert.Equal(new DoneAction(), ActionParser.Parse("""{"type":"done"}""").Action);
        Assert.Equal(new AskPermissionAction("Delete it"), ActionParser.Parse("""{"type":"ask_permission","action":"Delete it"}""").Action);
    }

    [Theory]
    [InlineData("""{"type":"point","x":5}""", "point", "y: is required")]
    [InlineData("""{"type":"point","x":"5","y":1}""", "point", "x: must be a number")]
    [InlineData("""{"type":"point","x":-3,"y":1}""", "point", "x: must be at least 0")]
    [InlineData("""{"type":"point","x":1,"y":1,"colour":"red"}""", "point", "colour: is not allowed")]
    [InlineData("""{"type":"teleport","x":1}""", "teleport", "unknown action 'teleport'")]
    [InlineData("""{"x":1}""", "?", "type is required")]
    [InlineData("""[1,2]""", "?", "an action must be a JSON object")]
    [InlineData("""{"type":"click","x":1,"y":1,"count":4}""", "click", "count: must be at most 3")]
    [InlineData("""{"type":"click","x":1,"y":1,"count":1.5}""", "click", "count: must be a whole number")]
    [InlineData("""{"type":"click","x":1,"y":1,"button":"thumb"}""", "click", "button: must be one of \"left\", \"right\", \"middle\"")]
    [InlineData("""{"type":"say","text":""}""", "say", "text: must not be empty")]
    [InlineData("""{"type":"say","text":"   "}""", "say", "text is blank")]
    [InlineData("""{"type":"wait","ms":50}""", "wait", "ms: must be at least 100")]
    [InlineData("""{"type":"draw","shape":"arrow","points":[{"x":1,"y":1}]}""", "draw", "arrow needs exactly 2 points")]
    [InlineData("""{"type":"draw","shape":"circle","points":[{"x":1,"y":1}]}""", "draw", "circle needs a radius")]
    [InlineData("""{"type":"draw","shape":"freehand","points":[{"x":1,"y":1}]}""", "draw", "freehand needs at least 2 points")]
    [InlineData("""{"type":"draw","shape":"arrow","points":[{"x":1},{"x":2,"y":2}]}""", "draw", "points[0].y: is required")]
    [InlineData("""{"type":"step","index":4,"total":3,"instruction":"Go"}""", "step", "index is past total")]
    [InlineData("""{"type":"step","index":1,"instruction":"Go","x":3}""", "step", "step needs both x and y, or neither")]
    public void Rejects_bad_actions_with_a_reason(string json, string name, string error)
    {
        var parsed = ActionParser.Parse(json);

        Assert.False(parsed.IsValid);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(error, parsed.Error);
    }

    [Fact]
    public void Broken_json_is_rejected_not_thrown()
    {
        var parsed = ActionParser.Parse("""{"type":"point","x":1,""");

        Assert.False(parsed.IsValid);
        Assert.StartsWith("not valid JSON", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_calls_take_the_name_from_the_tool_and_ignore_a_stray_type()
    {
        var parsed = ActionParser.ParseToolCall("highlight", """{"type":"point","x":1,"y":2,"width":3,"height":4}""");

        Assert.Equal(new HighlightAction(1, 2, 3, 4), parsed.Action);
    }

    [Fact]
    public void Tool_call_with_empty_or_broken_input_is_rejected()
    {
        Assert.Equal("x: is required", ActionParser.ParseToolCall("point", string.Empty).Error);
        Assert.StartsWith("input is not valid JSON", ActionParser.ParseToolCall("point", "{\"x\":").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_computer_actions_need_the_permission_gate()
    {
        Assert.True(new ClickAction(1, 1).ControlsComputer);
        Assert.True(new TypeAction("a").ControlsComputer);
        Assert.True(new KeyAction("Enter").ControlsComputer);
        Assert.True(new ScrollAction(1, 1, ScrollDirection.Up).ControlsComputer);
        Assert.False(new PointAction(1, 1).ControlsComputer);
        Assert.False(new AskPermissionAction("x").ControlsComputer);
        Assert.False(new WaitAction(100).ControlsComputer);
    }
}

public sealed class ActionBoundsTests
{
    [Fact]
    public void Points_inside_the_screenshot_pass_unchanged()
    {
        var result = ActionBounds.Check(new PointAction(1428, 803), 1429, 804);

        Assert.Equal(new PointAction(1428, 803), result.Action);
    }

    [Fact]
    public void Points_a_few_pixels_past_the_edge_are_pulled_onto_it()
    {
        Assert.Equal(new PointAction(1428, 0), ActionBounds.Check(new PointAction(1435, 0), 1429, 804).Action);
        Assert.Equal(new ClickAction(0, 803), ActionBounds.Check(new ClickAction(0, 810), 1429, 804).Action);
    }

    [Fact]
    public void Points_well_outside_are_rejected()
    {
        var result = ActionBounds.Check(new PointAction(1500, 10), 1429, 804);

        Assert.False(result.IsValid);
        Assert.Equal("(1500,10) is outside the 1429x804 screenshot", result.Error);
    }

    [Fact]
    public void Highlights_are_clipped_to_the_image()
    {
        var result = ActionBounds.Check(new HighlightAction(1400, 700, 100, 200, "Box"), 1429, 804);

        Assert.Equal(new HighlightAction(1400, 700, 29, 104, "Box"), result.Action);
    }

    [Fact]
    public void Drawings_reject_any_far_point_and_clamp_near_ones()
    {
        var near = new DrawAction(DrawShape.Arrow, [new DrawPoint(0, 0), new DrawPoint(105, 50)]);
        var far = new DrawAction(DrawShape.Arrow, [new DrawPoint(0, 0), new DrawPoint(300, 50)]);

        Assert.Equal(new DrawAction(DrawShape.Arrow, [new DrawPoint(0, 0), new DrawPoint(99, 50)]), ActionBounds.Check(near, 100, 100).Action);
        Assert.False(ActionBounds.Check(far, 100, 100).IsValid);
    }

    [Fact]
    public void Steps_without_a_place_and_non_spatial_actions_pass()
    {
        Assert.True(ActionBounds.Check(new StepAction(1, "Go"), 10, 10).IsValid);
        Assert.True(ActionBounds.Check(new KeyAction("Enter"), 10, 10).IsValid);
        Assert.False(ActionBounds.Check(new StepAction(1, "Go", X: 50, Y: 1), 10, 10).IsValid);
    }
}
