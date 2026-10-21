using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Clicky.Core.Routines;

namespace Clicky.App.Views;

/// <summary>A routine's recent runs; picking one shows what it said or what went wrong.</summary>
internal sealed class RoutineHistoryWindow : Window
{
    private readonly RoutineEngine _engine;
    private readonly Routine _routine;
    private readonly ListBox _runs = new() { Height = 220, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _output = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 200,
        MaxHeight = 400,
    };

    public RoutineHistoryWindow(RoutineEngine engine, Routine routine)
    {
        _engine = engine;
        _routine = routine;
        Title = $"{routine.Name}: history";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"Last {RoutineStore.KeepRunsPerRoutine} runs, newest first.", Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_runs);
        panel.Children.Add(_output);
        Content = panel;

        _runs.SelectionChanged += (_, _) => ShowRun(_runs.SelectedItem is ListBoxItem { Tag: RoutineRun run } ? run : null);
        _engine.RunFinished += OnRunFinished;
        Closed += (_, _) => _engine.RunFinished -= OnRunFinished;
        Loaded += async (_, _) => await LoadAsync();
    }

    private static string Describe(RoutineRun run)
    {
        var local = run.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var status = run.Status switch
        {
            RoutineRunStatus.Running => "Running",
            RoutineRunStatus.Succeeded => "Done",
            RoutineRunStatus.Failed => "Failed",
            RoutineRunStatus.Skipped => "Skipped",
            RoutineRunStatus.Interrupted => "Interrupted",
            RoutineRunStatus.Cancelled => "Stopped",
            _ => run.Status.ToString(),
        };
        var trigger = run.Trigger switch
        {
            RoutineTrigger.CatchUp => " (caught up)",
            RoutineTrigger.Manual => " (Run now)",
            RoutineTrigger.Startup => " (at startup)",
            _ => string.Empty,
        };
        return $"{local}  {status}{trigger}";
    }

    private void OnRunFinished(object? sender, RoutineRunFinishedEventArgs e)
    {
        if (e.Routine.Id == _routine.Id)
        {
            Dispatcher.InvokeAsync(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<RoutineRun> runs;
        try
        {
            runs = await _engine.ListRunsAsync(_routine.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _output.Text = $"Couldn't read the history: {ex.Message}";
            return;
        }

        var selected = _runs.SelectedItem is ListBoxItem { Tag: RoutineRun current } ? current.Id : (long?)null;
        _runs.Items.Clear();
        foreach (var run in runs)
        {
            var item = new ListBoxItem { Content = Describe(run), Tag = run, ToolTip = run.Message };
            _runs.Items.Add(item);
            if (run.Id == selected)
            {
                _runs.SelectedItem = item;
            }
        }

        if (_runs.SelectedItem is null && _runs.Items.Count > 0)
        {
            _runs.SelectedIndex = 0;
        }
        else if (runs.Count == 0)
        {
            _output.Text = "It hasn't run yet.";
        }
    }

    private void ShowRun(RoutineRun? run)
    {
        if (run is null)
        {
            _output.Text = string.Empty;
            return;
        }

        var scheduled = run.ScheduledFor.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var header = run.Trigger == RoutineTrigger.Manual ? string.Empty : $"Scheduled for {scheduled}.\n";
        var body = run.Output ?? run.Message ?? (run.Status == RoutineRunStatus.Running ? "Running…" : string.Empty);
        if (run.Output is not null && run.Message is not null && run.Status != RoutineRunStatus.Succeeded)
        {
            body = $"{run.Message}\n\n{run.Output}";
        }

        _output.Text = header + body;
    }
}
