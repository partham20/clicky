using System.Windows.Threading;
using Clicky.Core.Assistant.ClaudeCode;
using Clicky.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Starts Claude Code once the app has settled, so the first question on the subscription backend
/// doesn't wait 2-6 s for it, and starts or releases it when settings.claude changes backend or
/// keepWarm (see <see cref="ClaudeCodeBackend.WarmUp"/>).
/// </summary>
internal sealed class ClaudeCodeStartup(
    ClaudeCodeBackend backend,
    Dispatcher dispatcher,
    ISettingsStore settings,
    ILogger<ClaudeCodeStartup> logger) : IHostedService, IDisposable
{
    /// <summary>After the UI goes idle, room for capture and speech-to-text to warm up first.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += OnSettingsChanged;
        _ = Task.Run(WarmUpAfterStartupAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        await _stopping.CancelAsync();
    }

    public void Dispose() => _stopping.Dispose();

    private async Task WarmUpAfterStartupAsync()
    {
        try
        {
            // Not from StartAsync itself: the UI thread is blocked until the host has started.
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(_stopping.Token).ConfigureAwait(false);
            await Task.Delay(Settle, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var claude = settings.Current.Claude;
        logger.LogInformation("Claude backend: {Backend}, keep Claude Code warm: {KeepWarm}", claude.Backend, claude.KeepWarm);
        backend.WarmUp();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var (before, after) = (e.Previous.Claude, e.Current.Claude);
        if (before.Backend != after.Backend || before.KeepWarm != after.KeepWarm)
        {
            backend.WarmUp();
        }
    }
}
