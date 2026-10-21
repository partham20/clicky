using Clicky.Core.Actions;
using Clicky.Core.Geometry;
using Clicky.Core.Skills;
using Clicky.Core.Walkthroughs;

namespace Clicky.Tests.Walkthroughs;

public sealed class StepJudgeTests
{
    private static readonly WalkthroughOptions Options = WalkthroughOptions.Default;
    private static readonly PixelRect Monitor = new(0, 0, 2000, 1000);
    private static readonly StepTarget Target = new(new PixelPoint(500, 300), 24, Monitor, "EXCEL");

    [Theory]
    [InlineData(500, 300, StepVerdict.Done)]
    [InlineData(524, 300, StepVerdict.Done)]
    [InlineData(516, 316, StepVerdict.Done)]
    [InlineData(517, 317, StepVerdict.Stray)]
    [InlineData(525, 300, StepVerdict.Stray)]
    [InlineData(1500, 900, StepVerdict.Stray)]
    public void Clicks_count_within_the_tolerance(int x, int y, StepVerdict expected) =>
        Assert.Equal(expected, StepJudge.Judge(Target, new PointerClicked(new PixelPoint(x, y), MouseButton.Left), Options));

    [Fact]
    public void A_click_on_the_edge_of_the_element_at_the_spot_counts()
    {
        var button = new PixelRect(440, 280, 600, 320);
        Assert.Equal(StepVerdict.Done, StepJudge.Judge(Target, new PointerClicked(new PixelPoint(590, 315), MouseButton.Left, button), Options));
    }

    [Fact]
    public void A_click_in_a_big_pane_that_holds_the_spot_does_not()
    {
        var pane = new PixelRect(0, 100, 1900, 990);
        Assert.Equal(StepVerdict.Stray, StepJudge.Judge(Target, new PointerClicked(new PixelPoint(1500, 900), MouseButton.Left, pane), Options));
    }

    [Fact]
    public void Clicks_on_Clicky_and_steps_without_a_spot_are_ignored()
    {
        Assert.Equal(StepVerdict.Ignore, StepJudge.Judge(Target, new PointerClicked(new PixelPoint(1500, 900), MouseButton.Left, OnClicky: true), Options));
        Assert.Equal(StepVerdict.Ignore, StepJudge.Judge(null, new PointerClicked(new PixelPoint(500, 300), MouseButton.Left), Options));
        Assert.Equal(StepVerdict.Ignore, StepJudge.Judge(null, new ForegroundChanged(new ForegroundApp("chrome.exe", "x")), Options));
    }

    [Fact]
    public void Element_events_at_the_spot_are_done_and_presses_elsewhere_are_off_track()
    {
        var atSpot = new PixelRect(480, 290, 520, 310);
        var elsewhere = new PixelRect(1400, 880, 1480, 920);
        Assert.Equal(StepVerdict.Done, StepJudge.Judge(Target, new ElementEvent(ElementEventKind.Focus, atSpot), Options));
        Assert.Equal(StepVerdict.Done, StepJudge.Judge(Target, new ElementEvent(ElementEventKind.Select, atSpot), Options));
        Assert.Equal(StepVerdict.Ignore, StepJudge.Judge(Target, new ElementEvent(ElementEventKind.MenuOpened, atSpot), Options));
        Assert.Equal(StepVerdict.OffTrack, StepJudge.Judge(Target, new ElementEvent(ElementEventKind.Invoke, elsewhere, "Delete"), Options));
        Assert.Equal(StepVerdict.Ignore, StepJudge.Judge(Target, new ElementEvent(ElementEventKind.Focus, elsewhere), Options));
    }

    [Theory]
    [InlineData("excel.exe", StepVerdict.Ignore)]
    [InlineData("explorer.exe", StepVerdict.Ignore)]
    [InlineData("SearchHost.exe", StepVerdict.Ignore)]
    [InlineData("chrome.exe", StepVerdict.OffTrack)]
    public void Another_app_coming_to_the_front_is_off_track(string process, StepVerdict expected) =>
        Assert.Equal(expected, StepJudge.Judge(Target, new ForegroundChanged(new ForegroundApp(process, "title")), Options));

    [Fact]
    public void Tolerance_follows_the_monitor_scale_and_the_screenshot_downscale()
    {
        // 1080p at 100%, not downscaled: 24 DIP = 24 px beats 10 screenshot px.
        Assert.Equal(24, StepJudge.Tolerance(1.0, new ScreenshotGeometry(new PixelRect(0, 0, 1920, 1080), 1920, 1080), Options));

        // 4K at 150%: 36 px from DPI.
        Assert.Equal(36, StepJudge.Tolerance(1.5, new ScreenshotGeometry(new PixelRect(0, 0, 3840, 2160), 3840, 2160), Options));

        // 4K at 100% sent at 1568 wide: 10 screenshot px is ~24.5 physical px.
        Assert.Equal(25, StepJudge.Tolerance(1.0, new ScreenshotGeometry(new PixelRect(0, 0, 3840, 2160), 1568, 882), Options));
    }
}
