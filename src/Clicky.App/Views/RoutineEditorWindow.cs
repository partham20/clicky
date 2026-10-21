using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Clicky.Core.Routines;

namespace Clicky.App.Views;

/// <summary>Add or edit one routine: what to do, which Clicky does it, and when, with a live preview of the next run.</summary>
internal sealed class RoutineEditorWindow : Window
{
    private static readonly (string Label, Repeat Repeat)[] Repeats =
    [
        ("Every day", Repeat.Daily),
        ("Every weekday", Repeat.Weekdays),
        ("On certain days", Repeat.Days),
        ("Every few minutes or hours", Repeat.Interval),
        ("Once", Repeat.Once),
        ("Monthly", Repeat.Monthly),
        ("When Clicky starts", Repeat.Startup),
        ("Cron expression (advanced)", Repeat.Cron),
    ];

    private readonly TextBox _name = Field();
    private readonly ComboBox _clicky = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinLines = 3,
        MaxLines = 10,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 0, 0, 4),
    };

    private readonly ComboBox _repeat = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox _time = Field("8:00");
    private readonly DatePicker _date = new() { Margin = new Thickness(0, 0, 0, 4), SelectedDate = DateTime.Today.AddDays(1) };
    private readonly CheckBox[] _days = [.. Enum.GetValues<DayOfWeek>().Select(d => new CheckBox { Content = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d), Tag = d, Margin = new Thickness(0, 0, 10, 0) })];
    private readonly TextBox _every = Field("3");
    private readonly ComboBox _unit = new() { Width = 100, Margin = new Thickness(6, 0, 0, 4) };
    private readonly TextBox _from = Field(string.Empty);
    private readonly TextBox _until = Field(string.Empty);
    private readonly TextBox _dayOfMonth = Field("1");
    private readonly TextBox _cron = Field("0 8 * * 1-5");
    private readonly StackPanel _timeRow = new();
    private readonly StackPanel _dateRow = new();
    private readonly StackPanel _daysRow = new();
    private readonly StackPanel _intervalRow = new();
    private readonly StackPanel _monthlyRow = new();
    private readonly StackPanel _cronRow = new();
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.8 };
    private readonly Button _save = new() { Content = "Save", IsDefault = true, Padding = new Thickness(16, 2, 16, 2), Margin = new Thickness(0, 0, 8, 0) };

    public RoutineEditorWindow(Routine? original, IReadOnlyList<ClickyRef> clickys)
    {
        Title = original is null ? "New routine" : $"Edit {original.Name}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _clicky.Items.Add(new ComboBoxItem { Content = "Clicky", Tag = null });
        foreach (var clicky in clickys)
        {
            _clicky.Items.Add(new ComboBoxItem { Content = clicky.Name, Tag = clicky.Id });
        }

        foreach (var (label, repeat) in Repeats)
        {
            _repeat.Items.Add(new ComboBoxItem { Content = label, Tag = repeat });
        }

        _unit.Items.Add(new ComboBoxItem { Content = "minutes", Tag = 1 });
        _unit.Items.Add(new ComboBoxItem { Content = "hours", Tag = 60 });
        _unit.SelectedIndex = 1;

        Content = new ScrollViewer { Content = Build(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Load(original);

        _repeat.SelectionChanged += (_, _) => Refresh();
        _unit.SelectionChanged += (_, _) => Refresh();
        _date.SelectedDateChanged += (_, _) => Refresh();
        foreach (var box in new[] { _time, _every, _from, _until, _dayOfMonth, _cron })
        {
            box.TextChanged += (_, _) => Refresh();
        }

        foreach (var day in _days)
        {
            day.Click += (_, _) => Refresh();
        }

        Refresh();
        Loaded += (_, _) => _name.Focus();
    }

    private enum Repeat
    {
        Daily,
        Weekdays,
        Days,
        Interval,
        Once,
        Monthly,
        Startup,
        Cron,
    }

    /// <summary>The edited routine, valid when ShowDialog returned true.</summary>
    public RoutineDraft? Result { get; private set; }

    private static TextBox Field(string text = "") => new() { Text = text, Margin = new Thickness(0, 0, 0, 4) };

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 8, 0, 2) };

    private static TextBlock Hint(string text) => new() { Text = text, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };

    private static string Format(TimeOnly time) => time.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"8", "8:30", "08:30", "8 AM", "20:00": whatever the user means, or null.</summary>
    private static TimeOnly? ParseTime(string text, bool required)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return required ? throw new FormatException("Enter a time, like 8:00.") : null;
        }

        if (TimeOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var time) ||
            TimeOnly.TryParseExact(text, ["H", "HH", "Hmm", "HHmm", "h tt", "htt", "h:mm tt", "h:mmtt"], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time))
        {
            return time;
        }

        throw new FormatException($"\"{text}\" isn't a time. Try 8:00 or 18:30.");
    }

    private StackPanel Build()
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Label("Name"));
        panel.Children.Add(_name);
        panel.Children.Add(Label("What should Clicky do each time?"));
        panel.Children.Add(_prompt);
        panel.Children.Add(Hint("Write it as you'd ask: \"Summarize my unread email and today's calendar.\" It runs later with nobody watching, so make it complete."));
        panel.Children.Add(Label("Which Clicky"));
        panel.Children.Add(_clicky);
        panel.Children.Add(Label("Repeat"));
        panel.Children.Add(_repeat);

        _timeRow.Children.Add(Label("At"));
        _timeRow.Children.Add(_time);
        panel.Children.Add(_timeRow);

        _dateRow.Children.Add(Label("On"));
        _dateRow.Children.Add(_date);
        panel.Children.Add(_dateRow);

        _daysRow.Children.Add(Label("Days"));
        var days = new WrapPanel();
        foreach (var day in RotateToFirstDay(_days))
        {
            days.Children.Add(day);
        }

        _daysRow.Children.Add(days);
        panel.Children.Add(_daysRow);

        _intervalRow.Children.Add(Label("Every"));
        var every = new DockPanel();
        DockPanel.SetDock(_unit, Dock.Right);
        every.Children.Add(_unit);
        every.Children.Add(_every);
        _intervalRow.Children.Add(every);
        var window = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        window.ColumnDefinitions.Add(new ColumnDefinition());
        window.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        window.ColumnDefinitions.Add(new ColumnDefinition());
        var from = new StackPanel { Children = { Hint("From (optional)"), _from } };
        var until = new StackPanel { Children = { Hint("Until (optional)"), _until } };
        Grid.SetColumn(until, 2);
        window.Children.Add(from);
        window.Children.Add(until);
        _intervalRow.Children.Add(window);
        _intervalRow.Children.Add(Hint("Leave From and Until empty to run around the clock. Days below limit it to some days."));
        panel.Children.Add(_intervalRow);

        _monthlyRow.Children.Add(Label("Day of the month (31 = last day)"));
        _monthlyRow.Children.Add(_dayOfMonth);
        panel.Children.Add(_monthlyRow);

        _cronRow.Children.Add(Label("Cron expression"));
        _cronRow.Children.Add(_cron);
        _cronRow.Children.Add(Hint("minute hour day-of-month month day-of-week, in local time. \"0 8 * * 1-5\" is 8:00 on weekdays."));
        panel.Children.Add(_cronRow);

        panel.Children.Add(_preview);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _save.Click += (_, _) => Save();
        buttons.Children.Add(_save);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 2, 16, 2) });
        panel.Children.Add(buttons);
        return panel;
    }

    private static IEnumerable<CheckBox> RotateToFirstDay(CheckBox[] days)
    {
        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        return days.Skip(first).Concat(days.Take(first));
    }

    private void Load(Routine? routine)
    {
        _clicky.SelectedIndex = 0;
        _repeat.SelectedIndex = 0;
        foreach (var day in _days)
        {
            day.IsChecked = (DayOfWeek)day.Tag is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
        }

        if (routine is null)
        {
            return;
        }

        _name.Text = routine.Name;
        _prompt.Text = routine.Prompt;
        var clicky = _clicky.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == routine.ClickyId);
        if (clicky is null && routine.ClickyId is not null)
        {
            clicky = new ComboBoxItem { Content = "(a Clicky that no longer exists)", Tag = routine.ClickyId };
            _clicky.Items.Add(clicky);
        }

        _clicky.SelectedItem = clicky ?? _clicky.Items[0];

        var schedule = routine.Schedule;
        var repeat = schedule.Kind switch
        {
            RoutineScheduleKind.Daily when (schedule.Days & RoutineDays.Every) == RoutineDays.Every => Repeat.Daily,
            RoutineScheduleKind.Daily when (schedule.Days & RoutineDays.Every) == RoutineDays.Weekdays => Repeat.Weekdays,
            RoutineScheduleKind.Daily => Repeat.Days,
            RoutineScheduleKind.Interval => Repeat.Interval,
            RoutineScheduleKind.Once => Repeat.Once,
            RoutineScheduleKind.Monthly => Repeat.Monthly,
            RoutineScheduleKind.AtStartup => Repeat.Startup,
            _ => Repeat.Cron,
        };
        _repeat.SelectedItem = _repeat.Items.Cast<ComboBoxItem>().First(i => (Repeat)i.Tag == repeat);
        _time.Text = Format(schedule.Kind == RoutineScheduleKind.Once && schedule.OnceAt is { } once ? TimeOnly.FromDateTime(once) : schedule.At);
        _date.SelectedDate = schedule.OnceAt?.Date ?? _date.SelectedDate;
        foreach (var day in _days)
        {
            day.IsChecked = schedule.Days.HasFlag((RoutineDays)(1 << (int)(DayOfWeek)day.Tag));
        }

        if (schedule.Kind == RoutineScheduleKind.Interval)
        {
            var hours = schedule.IntervalMinutes % 60 == 0;
            _every.Text = (hours ? schedule.IntervalMinutes / 60 : schedule.IntervalMinutes).ToString(CultureInfo.CurrentCulture);
            _unit.SelectedIndex = hours ? 1 : 0;
            _from.Text = schedule.From is { } f ? Format(f) : string.Empty;
            _until.Text = schedule.Until is { } u ? Format(u) : string.Empty;
        }

        _dayOfMonth.Text = schedule.DayOfMonth > 0 ? schedule.DayOfMonth.ToString(CultureInfo.CurrentCulture) : "1";
        _cron.Text = schedule.Cron ?? _cron.Text;
    }

    private Repeat Selected => _repeat.SelectedItem is ComboBoxItem { Tag: Repeat repeat } ? repeat : Repeat.Daily;

    private void Refresh()
    {
        var repeat = Selected;
        Show(_timeRow, repeat is Repeat.Daily or Repeat.Weekdays or Repeat.Days or Repeat.Once or Repeat.Monthly);
        Show(_dateRow, repeat == Repeat.Once);
        Show(_daysRow, repeat is Repeat.Days or Repeat.Interval);
        Show(_intervalRow, repeat == Repeat.Interval);
        Show(_monthlyRow, repeat == Repeat.Monthly);
        Show(_cronRow, repeat == Repeat.Cron);

        try
        {
            var schedule = BuildSchedule();
            if (schedule.Validate() is { } error)
            {
                ShowPreview(error, ok: false);
                return;
            }

            var now = DateTimeOffset.Now;
            var next = schedule.NextAfter(now, TimeZoneInfo.Local, now);
            ShowPreview(
                schedule.Kind == RoutineScheduleKind.AtStartup
                    ? $"{schedule.Describe()}. Runs about a minute after Clicky starts."
                    : next is { } at
                        ? $"{schedule.Describe()}. Next run: {RoutineFormat.When(at, now, TimeZoneInfo.Local)}."
                        : "That time has already passed.",
                ok: schedule.Kind == RoutineScheduleKind.AtStartup || next is not null);
        }
        catch (FormatException ex)
        {
            ShowPreview(ex.Message, ok: false);
        }
    }

    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void ShowPreview(string text, bool ok)
    {
        _preview.Text = text;
        _preview.Foreground = ok ? SystemColors.ControlTextBrush : System.Windows.Media.Brushes.Firebrick;
        _save.IsEnabled = ok;
    }

    private RoutineDays CheckedDays() =>
        _days.Where(d => d.IsChecked == true).Aggregate(RoutineDays.None, (all, d) => all | (RoutineDays)(1 << (int)(DayOfWeek)d.Tag));

    private RoutineSchedule BuildSchedule()
    {
        switch (Selected)
        {
            case Repeat.Daily:
                return RoutineSchedule.Daily(ParseTime(_time.Text, required: true)!.Value);
            case Repeat.Weekdays:
                return RoutineSchedule.Weekdays(ParseTime(_time.Text, required: true)!.Value);
            case Repeat.Days:
                return RoutineSchedule.Daily(ParseTime(_time.Text, required: true)!.Value, CheckedDays());
            case Repeat.Once:
                var date = _date.SelectedDate ?? throw new FormatException("Pick a date.");
                return RoutineSchedule.Once(date.Date + ParseTime(_time.Text, required: true)!.Value.ToTimeSpan());
            case Repeat.Interval:
                if (!int.TryParse(_every.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) || count <= 0)
                {
                    throw new FormatException("Enter how often, like 3.");
                }

                var unit = _unit.SelectedItem is ComboBoxItem { Tag: int minutes } ? minutes : 60;
                var days = CheckedDays();
                return RoutineSchedule.Every(
                    TimeSpan.FromMinutes((long)count * unit),
                    ParseTime(_from.Text, required: false),
                    ParseTime(_until.Text, required: false),
                    days == RoutineDays.None ? RoutineDays.Every : days);
            case Repeat.Monthly:
                if (!int.TryParse(_dayOfMonth.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var day))
                {
                    throw new FormatException("Enter a day of the month from 1 to 31.");
                }

                return RoutineSchedule.Monthly(day, ParseTime(_time.Text, required: true)!.Value);
            case Repeat.Startup:
                return RoutineSchedule.AtStartup();
            default:
                return RoutineSchedule.FromCron(_cron.Text);
        }
    }

    private void Save()
    {
        try
        {
            var draft = new RoutineDraft(_name.Text, _prompt.Text, BuildSchedule(), _clicky.SelectedItem is ComboBoxItem item ? item.Tag as string : null);
            if (draft.Validate() is { } error)
            {
                ShowPreview(error, ok: true);
                _preview.Foreground = System.Windows.Media.Brushes.Firebrick;
                return;
            }

            Result = draft;
            DialogResult = true;
        }
        catch (FormatException ex)
        {
            ShowPreview(ex.Message, ok: false);
        }
    }
}
