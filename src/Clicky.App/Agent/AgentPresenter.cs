using System.Windows.Threading;
using Clicky.Core.Agent;
using Clicky.Core.Geometry;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Agent;

/// <summary>Shows agent runs on an <see cref="AgentCard"/>, marshalling every call to the UI thread.</summary>
internal sealed class AgentPresenter(Dispatcher dispatcher, ILogger<AgentPresenter> logger) : IAgentPresenter
{
    private static readonly TimeSpan s_finishShown = TimeSpan.FromSeconds(4);

    public IAgentCard Begin(string task, PixelRect? near)
    {
        var card = dispatcher.Invoke(() =>
        {
            var window = new AgentCard(task, near, logger);
            window.Show();
            return window;
        });
        return new Handle(card, dispatcher);
    }

    private sealed class Handle : IAgentCard
    {
        private readonly AgentCard _card;
        private readonly Dispatcher _dispatcher;
        private readonly CancellationTokenSource _stop = new();
        private int _closed;

        public Handle(AgentCard card, Dispatcher dispatcher)
        {
            _card = card;
            _dispatcher = dispatcher;
            card.StopClicked += () =>
            {
                if (Volatile.Read(ref _closed) == 0)
                {
                    _stop.Cancel();
                }
            };
        }

        public CancellationToken StopRequested => _stop.Token;

        public void ShowStep(int number, string text) => Post(() => _card.ShowStep(number > 0 ? $"Step {number}: {text}" : text));

        public async Task<bool> CountdownAsync(string upcoming, TimeSpan duration, CancellationToken cancellationToken)
        {
            var countdown = await _dispatcher.InvokeAsync(() => _card.StartCountdown(upcoming, duration)).Task.ConfigureAwait(false);
            using (cancellationToken.Register(() => Post(() => _card.EndCountdown(goAhead: false))))
            {
                var goAhead = await countdown.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return goAhead;
            }
        }

        public async Task<PermissionAnswer> AskAsync(PermissionPrompt prompt, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            if (Volatile.Read(ref _closed) != 0)
            {
                return PermissionAnswer.NotNow;
            }

            var answer = await _dispatcher.InvokeAsync(() => _card.Ask(prompt)).Task.ConfigureAwait(false);
            using (cancellationToken.Register(() => Post(() => _card.Answer(PermissionAnswer.NotNow))))
            {
                var result = await answer.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
        }

        public void Avoid(PixelPoint point)
        {
            // Synchronous: the card must be out of the way before the click is aimed.
            if (Volatile.Read(ref _closed) == 0)
            {
                _dispatcher.Invoke(() => _card.Avoid(point), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1));
            }
        }

        public void Finish(string message, bool success)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            Post(() =>
            {
                _card.ShowFinished(message, success);
                var timer = new DispatcherTimer { Interval = s_finishShown };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _card.Close();
                };
                timer.Start();
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Post(_card.Close);
            }

            _stop.Dispose();
        }

        private void Post(Action action) => _dispatcher.InvokeAsync(action);
    }
}
