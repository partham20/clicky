using System.Text.Json;
using Clicky.Core.Capture;
using Clicky.Core.Hotkeys;
using Clicky.Core.Settings;

namespace Clicky.Tests.Settings;

public sealed class HotkeyAndCaptureSettingsTests
{
    [Fact]
    public void Defaults_match_the_build_plan()
    {
        var bindings = ClickySettings.Default.Hotkeys.ToBindings(out var problems);

        Assert.Empty(problems);
        Assert.Equal(
            [
                new HotkeyBinding(HotkeyAction.Talk, HotkeyGesture.Hold(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)),
                new HotkeyBinding(HotkeyAction.TextMode, HotkeyGesture.DoubleTap(HotkeyModifiers.Ctrl)),
                new HotkeyBinding(HotkeyAction.Dictation, HotkeyGesture.Hold(HotkeyModifiers.Ctrl | HotkeyModifiers.Win)),
                new HotkeyBinding(HotkeyAction.DictationHandsFree, HotkeyGesture.DoubleTap(HotkeyModifiers.Ctrl | HotkeyModifiers.Win)),
                new HotkeyBinding(HotkeyAction.OpenHome, HotkeyGesture.Press(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'A')),
            ],
            bindings);

        var alwaysOn = Assert.Single(ClickySettings.Default.Hotkeys.Bindings, b => b.Action == HotkeyAction.AlwaysOnVoice);
        Assert.False(alwaysOn.Enabled);
        Assert.Equal("triple Ctrl", alwaysOn.Gesture);

        Assert.Equal(CaptureMode.MonitorUnderCursor, ClickySettings.Default.Capture.Mode);
        Assert.Equal(1568, ClickySettings.Default.Capture.MaxLongEdge);
        Assert.Equal(1_150_000, ClickySettings.Default.Capture.MaxPixels);
        Assert.False(ClickySettings.Default.Capture.IncludeCursor);
        Assert.False(ClickySettings.Default.Debug.SaveCaptures);
    }

    [Fact]
    public void Settings_compare_by_binding_content_not_list_identity()
    {
        var a = ClickySettings.Default with { Hotkeys = new HotkeySettings { Bindings = [.. HotkeySettings.DefaultBindings] } };
        var b = ClickySettings.Default.Normalize();

        Assert.Equal(ClickySettings.Default, a);
        Assert.Equal(ClickySettings.Default, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var changed = a with { Hotkeys = new HotkeySettings { Bindings = [.. HotkeySettings.DefaultBindings.Select(x => x with { Enabled = false })] } };
        Assert.NotEqual(a, changed);
    }

    [Fact]
    public void Normalize_repairs_nulls_unknown_actions_and_missing_actions()
    {
        var broken = ClickySettings.Default with
        {
            Hotkeys = new HotkeySettings
            {
                Bindings =
                [
                    null!,
                    new HotkeyBindingSettings { Action = HotkeyAction.Talk, Gesture = "Ctrl+Shift" },
                    new HotkeyBindingSettings { Action = (HotkeyAction)99, Gesture = "Ctrl+Alt" },
                    new HotkeyBindingSettings { Action = HotkeyAction.OpenHome, Gesture = null! },
                ],
            },
            Capture = null!,
            Debug = null!,
        };

        var normalized = broken.Normalize();

        var bindings = normalized.Hotkeys.Bindings;
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, bindings.Count);
        Assert.Equal("Ctrl+Shift", bindings[0].Gesture);
        Assert.Equal(HotkeyAction.OpenHome, bindings[1].Action);
        Assert.Equal(string.Empty, bindings[1].Gesture);
        Assert.Equal(new CaptureSettings(), normalized.Capture);
        Assert.Equal(new DebugSettings(), normalized.Debug);

        var nullList = (ClickySettings.Default with { Hotkeys = new HotkeySettings { Bindings = null! } }).Normalize();
        Assert.Equal(ClickySettings.Default.Hotkeys, nullList.Hotkeys);
    }

    [Theory]
    [InlineData(10, 1, CaptureSettings.MinMaxLongEdge, CaptureSettings.MinMaxPixels)]
    [InlineData(100_000, int.MaxValue, CaptureSettings.MaxMaxLongEdge, CaptureSettings.MaxMaxPixels)]
    [InlineData(1000, 500_000, 1000, 500_000)]
    public void Normalize_clamps_capture_caps(int longEdge, int pixels, int expectedLongEdge, int expectedPixels)
    {
        var normalized = (ClickySettings.Default with { Capture = new CaptureSettings { MaxLongEdge = longEdge, MaxPixels = pixels, Mode = (CaptureMode)42 } }).Normalize();

        Assert.Equal(expectedLongEdge, normalized.Capture.MaxLongEdge);
        Assert.Equal(expectedPixels, normalized.Capture.MaxPixels);
        Assert.Equal(CaptureMode.MonitorUnderCursor, normalized.Capture.Mode);
    }

    [Fact]
    public void Bad_bindings_are_reported_and_skipped_not_fatal()
    {
        var settings = new HotkeySettings
        {
            Bindings =
            [
                new HotkeyBindingSettings { Action = HotkeyAction.Talk, Gesture = "Ctrl+Alt" },
                new HotkeyBindingSettings { Action = HotkeyAction.TextMode, Gesture = "Ctlr+Alt" },
                new HotkeyBindingSettings { Action = HotkeyAction.Dictation, Gesture = "Shift" },
                new HotkeyBindingSettings { Action = HotkeyAction.OpenHome, Gesture = "Alt+Ctrl" },
                new HotkeyBindingSettings { Action = HotkeyAction.AlwaysOnVoice, Gesture = "Ctrl+Alt+V", Enabled = false },
                new HotkeyBindingSettings { Action = HotkeyAction.DictationHandsFree, Gesture = "" },
            ],
        };

        var bindings = settings.ToBindings(out var problems);

        Assert.Equal(HotkeyAction.Talk, Assert.Single(bindings).Action);
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("TextMode", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("two modifiers", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("already used by Talk", StringComparison.Ordinal));
    }

    [Fact]
    public void Properties_missing_from_the_file_keep_their_defaults()
    {
        var json = """
            {
              "logging": {},
              "overlay": { "hideFloatingCursor": true },
              "hotkeys": { "bindings": [ { "action": "OpenHome", "gesture": "Ctrl+Alt+H" } ] },
              "capture": { "includeCursor": true },
              "claude": { "backend": "Subscription" },
            }
            """;

        var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.ClickySettings)!.Normalize();

        Assert.Equal(LogLevelSetting.Information, loaded.Logging.MinimumLevel);
        Assert.True(loaded.Overlay.HideFloatingCursor);
        Assert.Equal(40, loaded.Overlay.FollowSmoothingMs);
        Assert.True(loaded.Hotkeys.Bindings[0].Enabled);
        Assert.Equal(CaptureSettings.DefaultMaxLongEdge, loaded.Capture.MaxLongEdge);
        Assert.Equal(CaptureSettings.DefaultMaxPixels, loaded.Capture.MaxPixels);
        Assert.True(loaded.Capture.IncludeCursor);
        Assert.Equal(ScreenshotFormat.Jpeg, loaded.Capture.Format);
        Assert.Equal(CaptureSettings.DefaultJpegQuality, loaded.Capture.JpegQuality);
        Assert.True(loaded.Claude.KeepWarm);
        Assert.False(loaded.Claude.FastThinking);
    }

    [Fact]
    public void Json_round_trips_and_reads_as_hand_written()
    {
        var json = """
            {
              // Hand edited
              "hotkeys": {
                "bindings": [
                  { "action": "Talk", "gesture": "hold Ctrl+Space" },
                  { "action": "OpenHome", "gesture": "Ctrl+Shift+H", "enabled": false },
                ],
              },
              "capture": { "mode": "AllMonitors", "maxLongEdge": 1024, "includeCursor": true },
              "debug": { "saveCaptures": true },
            }
            """;

        var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.ClickySettings)!.Normalize();

        Assert.Equal("hold Ctrl+Space", loaded.Hotkeys.Bindings[0].Gesture);
        Assert.True(loaded.Hotkeys.Bindings[0].Enabled);
        Assert.False(loaded.Hotkeys.Bindings[1].Enabled);
        Assert.Equal(CaptureMode.AllMonitors, loaded.Capture.Mode);
        Assert.Equal(1024, loaded.Capture.MaxLongEdge);
        Assert.Equal(CaptureSettings.DefaultMaxPixels, loaded.Capture.MaxPixels);
        Assert.True(loaded.Capture.IncludeCursor);
        Assert.True(loaded.Debug.SaveCaptures);

        var written = JsonSerializer.Serialize(loaded, SettingsJsonContext.ReadableClickySettings);
        Assert.Contains("\"gesture\": \"hold Ctrl+Space\"", written, StringComparison.Ordinal);
        Assert.Equal(loaded, JsonSerializer.Deserialize(written, SettingsJsonContext.Default.ClickySettings)!.Normalize());
    }
}
