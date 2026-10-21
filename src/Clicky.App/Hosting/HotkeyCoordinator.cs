using System.Windows.Threading;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;
using Clicky.Core.Hotkeys;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// What each hotkey does. M2 wires Talk to the buddy and a screenshot, and OpenHome to Home; the
/// rest are logged until their milestones (voice M3/M4, text mode and dictation later).
/// </summary>
/// <remarks>Events arrive on the hotkey pump thread; nothing here blocks it or the UI thread.</remarks>
internal sealed class HotkeyCoordinator(
    IHotkeyService hotkeys,
    ICaptureService capture,
    IBuddyPresence presence,
    ISettingsStore settings,
    IAppShell shell,
    Dispatcher dispatcher,
    ILogger<HotkeyCoordinator> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        hotkeys.Triggered += OnHotkey;

        // Device creation and codec loading cost ~1 s once; pay it now rather than on the first Talk.
        _ = Task.Run(() => capture.WarmUpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        hotkeys.Triggered -= OnHotkey;
        await _stopping.CancelAsync();
    }

    public void Dispose() => _stopping.Dispose();

    private void OnHotkey(object? sender, HotkeyEvent e)
    {
        switch (e.Action, e.Phase)
        {
            case (HotkeyAction.Talk, HotkeyPhase.Started):
                presence.SetState(BuddyState.Listening);

                // The pointer where the user was looking when they pressed, not where it is when the capture runs.
                PixelPoint? cursor = CursorReader.TryGet(out var position, out _) ? position : null;
                _ = CaptureForTalkAsync(new CaptureTarget(settings.Current.Capture.Mode, cursor));
                break;

            case (HotkeyAction.Talk, HotkeyPhase.Ended or HotkeyPhase.Cancelled):
                presence.SetState(BuddyState.Idle);
                break;

            case (HotkeyAction.OpenHome, HotkeyPhase.Fired):
                dispatcher.InvokeAsync(shell.ShowHome);
                break;

            default:
                logger.LogInformation("Hotkey {Action} {Phase} (not wired up yet)", e.Action, e.Phase);
                break;
        }
    }

    private async Task CaptureForTalkAsync(CaptureTarget target)
    {
        try
        {
            var result = await capture.CaptureAsync(target, _stopping.Token).ConfigureAwait(false);
            var geometry = result.Geometry;
            var source = result.Monitor is { } monitor ? $"{monitor.DeviceName} scale {monitor.Scale:0.##}" : $"{target.Mode} {geometry.Source.Width}x{geometry.Source.Height}";

            if (target.CursorPosition is { } cursor)
            {
                var shot = CoordinateMapper.ToScreenshot(cursor, geometry);
                var back = CoordinateMapper.ToPhysical(shot, geometry);
                logger.LogInformation(
                    "Talk capture {Width}x{Height} from {Source} in {Ms:0} ms; cursor ({X},{Y}) -> ({U:0.0},{V:0.0}) -> ({BackX},{BackY})",
                    geometry.ImageWidth,
                    geometry.ImageHeight,
                    source,
                    result.Elapsed.TotalMilliseconds,
                    cursor.X,
                    cursor.Y,
                    shot.X,
                    shot.Y,
                    back.X,
                    back.Y);
            }
            else
            {
                logger.LogInformation("Talk capture {Width}x{Height} from {Source} in {Ms:0} ms", geometry.ImageWidth, geometry.ImageHeight, source, result.Elapsed.TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (CaptureException ex)
        {
            logger.LogWarning(ex, "Talk capture failed");
        }
    }
}
