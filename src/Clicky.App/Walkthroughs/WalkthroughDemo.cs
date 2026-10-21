using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Core.Walkthroughs;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Walkthroughs;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Walkthroughs;

/// <summary>
/// A four-step walkthrough of a small practice window, with scripted steps instead of Claude, for
/// checking on real hardware (tray › Walkthrough demo) that clicks, keyboard focus, going off track,
/// Back, Skip and Done all behave. Everything except where the steps come from is the real thing:
/// the engine, the mouse and accessibility hooks, the step marks and the card.
/// </summary>
public sealed class WalkthroughDemo(IWalkthroughs walkthroughs, SwitchableWalkthroughTurns turns, ILogger<WalkthroughDemo> logger)
{
    private const int StepCount = 4;

    private Window? _window;
    private Button? _open;
    private Button? _options;
    private Button? _save;
    private IDisposable? _scripted;
    private IDisposable? _exempt;
    private Guid? _walkthrough;

    /// <summary>Opens the practice window and starts; brings the window back if the demo is running. UI thread.</summary>
    public void Play()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        _open = new Button { Content = "Open", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 12, 0) };
        _options = new Button { Content = "Options", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 12, 0) };
        _save = new Button { Content = "Save", Padding = new Thickness(18, 6, 18, 6) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 14) };
        row.Children.Add(_open);
        row.Children.Add(_options);
        row.Children.Add(_save);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "A practice window for walkthroughs. Follow the steps Clicky points at; try clicking elsewhere, Back and Skip too.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(row);
        panel.Children.Add(new TextBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left });

        var window = new Window
        {
            Title = "Clicky walkthrough demo",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = panel,
        };
        window.SourceInitialized += (_, _) => _exempt = WindowsStepWatcher.Exempt(new WindowInteropHelper(window).Handle);
        window.ContentRendered += (_, _) => Start();
        window.Closed += (_, _) => Stop(cancel: true);
        walkthroughs.Changed += OnChanged;
        _window = window;
        window.Show();
    }

    private void Start()
    {
        _scripted = turns.Use(new ScriptedTurns(this));
        if (StepFor(1) is not { } step)
        {
            return;
        }

        ScriptedReply.Send(walkthroughs, step.Geometry, step.Action);
        logger.LogInformation("Walkthrough demo started");
    }

    private void OnChanged(object? sender, WalkthroughChangedEventArgs e)
    {
        var snapshot = e.Snapshot;
        _window?.Dispatcher.InvokeAsync(() =>
        {
            if (_window is null)
            {
                return;
            }

            _walkthrough ??= snapshot.Id;
            if (snapshot.Id == _walkthrough && !snapshot.IsActive)
            {
                logger.LogInformation("Walkthrough demo finished: {End}", snapshot.End);
                Stop(cancel: false);
            }
        });
    }

    private void Stop(bool cancel)
    {
        if (_window is null)
        {
            return;
        }

        walkthroughs.Changed -= OnChanged;
        if (cancel && _walkthrough is { } id && walkthroughs.Current?.Id == id)
        {
            walkthroughs.Cancel();
        }

        _scripted?.Dispose();
        _scripted = null;
        _exempt?.Dispose();
        _exempt = null;
        _walkthrough = null;
        var window = _window;
        _window = null;
        if (!cancel)
        {
            window.Close();
        }
    }

    /// <summary>Step <paramref name="index"/> of the script, pointing at the window as it is now. UI thread.</summary>
    private (StepAction Action, ScreenshotGeometry Geometry)? StepFor(int index)
    {
        if (_window is null || _open is null || _options is null || _save is null)
        {
            return null;
        }

        // The screenshot a default capture of the window's monitor would have.
        var centre = _window.PointToScreen(new Point(_window.ActualWidth / 2, _window.ActualHeight / 2));
        var monitor = CoordinateMapper.NearestMonitor(DisplayMonitors.GetAll(), new PixelPoint((int)centre.X, (int)centre.Y));
        if (monitor is null)
        {
            return null;
        }

        var (width, height) = CoordinateMapper.DownscaleSize(monitor.Bounds.Width, monitor.Bounds.Height, CaptureSettings.DefaultMaxLongEdge, CaptureSettings.DefaultMaxPixels);
        var geometry = new ScreenshotGeometry(monitor.Bounds, width, height);

        ScreenshotPoint At(FrameworkElement element)
        {
            // PointToScreen gives physical pixels in a per-monitor-aware process.
            var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            return CoordinateMapper.ToScreenshot(new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y)), geometry);
        }

        StepAction Pointing(string instruction, FrameworkElement element)
        {
            var at = At(element);
            return new StepAction(index, instruction, StepCount, at.X, at.Y);
        }

        StepAction? step = index switch
        {
            1 => Pointing("Click Open", _open),
            2 => Pointing("Now click Options", _options),
            3 => Pointing("Use the keyboard this time: press Tab until Save is highlighted", _save),
            4 => new StepAction(4, "Type your name in the box, then press Done on this card", StepCount),
            _ => null,
        };
        return step is null ? null : (step, geometry);
    }

    /// <summary>Answers the engine's "what next?" from the script, through the same reply events Claude's answers use.</summary>
    private sealed class ScriptedTurns(WalkthroughDemo demo) : IWalkthroughTurns
    {
        public async Task RunAsync(WalkthroughTurn turn, IWalkthroughs walkthroughs, CancellationToken cancellationToken)
        {
            // About as long as Claude takes, so the card's "Looking at your screen…" shows.
            await Task.Delay(TimeSpan.FromSeconds(0.6), cancellationToken).ConfigureAwait(false);
            var next = turn.Reason == WalkthroughTurnReason.OffTrack ? turn.StepIndex : turn.StepIndex + 1;
            var window = demo._window;
            if (window is null)
            {
                return;
            }

            var step = await window.Dispatcher.InvokeAsync(() => demo.StepFor(next));
            if (step is { } s)
            {
                ScriptedReply.Send(walkthroughs, s.Geometry, s.Action);
            }
            else
            {
                ScriptedReply.Send(walkthroughs, null, new SayAction("That's the demo done."), new DoneAction("Demo finished"));
            }
        }
    }
}

/// <summary>Sends one whole reply the way a talk loop passes it on: started, the actions, completed.</summary>
internal static class ScriptedReply
{
    public static void Send(IWalkthroughs walkthroughs, ScreenshotGeometry? geometry, params ClickyAction[] actions)
    {
        walkthroughs.Observe(new ReplyStarted(ClaudeBackendKind.Api, "demo", ModelTier.Fast, "demo"), geometry);
        foreach (var action in actions)
        {
            walkthroughs.Observe(action is SayAction say ? new ReplyText(say.Text) : new ReplyAction(action), geometry);
        }

        walkthroughs.Observe(new ReplyCompleted(ReplyStop.EndTurn, actions, ReplyUsage.None, null, TimeSpan.Zero, "demo"), geometry);
    }
}
