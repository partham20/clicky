using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clicky.App.Buddy;
using Clicky.Core.Clickys;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views.Home;

/// <summary>
/// Clicky Home (Ctrl+Alt+A): the docked buddy, the user's Clickys with unread badges, the selected
/// Clicky's chat with its run controls (pause, resume, stop), and on the right a rich preview of
/// the selected reply, the run's activity and the Clicky's run history.
/// </summary>
internal sealed class HomeWindow : Window
{
    private const int ChatMessages = 200;
    private const int BubbleChars = 1500;

    private static readonly Brush UserBubble = Frozen(0xDD, 0xF4, 0xFF);
    private static readonly Brush RoutineBubble = Frozen(0xFF, 0xF8, 0xC5);
    private static readonly Brush AssistantBubble = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush SelectedBorder = Frozen(0x2F, 0x81, 0xF7);
    private static readonly Brush BubbleBorder = Frozen(0xD0, 0xD7, 0xDE);
    private static readonly Brush Muted = Frozen(0x59, 0x63, 0x6E);
    private static readonly Brush Sidebar = Frozen(0xF6, 0xF8, 0xFA);
    private static readonly Brush Badge = Frozen(0x2F, 0x81, 0xF7);
    private static readonly Brush Failure = Frozen(0xCF, 0x22, 0x2E);

    private readonly ClickyService _service;
    private readonly HomeViewState _view;
    private readonly bool _computerUse;
    private readonly ILogger<HomeWindow> _logger;
    private readonly DispatcherTimer _refresh;

    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _chat = new();
    private readonly StackPanel _empty = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460 };
    private readonly Ellipse _headerDot = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _headerName = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _headerInstructions = new() { Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(22, 2, 0, 0) };
    private readonly StackPanel _transcript = new() { Margin = new Thickness(16, 8, 16, 8) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Border _runBar = new() { Padding = new Thickness(16, 6, 16, 6), Background = Frozen(0xF6, 0xF8, 0xFA), Visibility = Visibility.Collapsed };
    private readonly TextBlock _runText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause = SmallButton("Pause");
    private readonly Button _resume = SmallButton("Resume");
    private readonly Button _stop = SmallButton("Stop");
    private readonly TextBox _composer = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 56,
        MaxHeight = 160,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(6, 4, 6, 4),
    };

    private readonly Button _send = new() { Content = "Send", MinWidth = 72, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _status = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 4) };
    private readonly TabControl _tabs = new() { Margin = new Thickness(0, 8, 8, 8) };
    private readonly PreviewPane _preview;
    private readonly ListBox _activity = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _history = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _retry = SmallButton("Ask again");
    private readonly TextBlock _activityTitle = new() { Foreground = Muted, Margin = new Thickness(4, 4, 4, 6), TextWrapping = TextWrapping.Wrap };

    private IReadOnlyList<ClickyAgent> _clickys = [];
    private IReadOnlyList<ClickyMessage> _messages = [];
    private IReadOnlyList<ClickyRun> _runs = [];
    private string? _selectedId;
    private string? _selectedRunId;
    private long? _selectedMessageId;
    private bool _dirtyList = true;
    private bool _dirtyChat = true;
    private bool _dirtyActivity = true;
    private bool _refreshing;
    private bool _suppressSelection;
    private bool _available = true;

    public HomeWindow(
        ClickyService service,
        IClickyActor actor,
        HomeViewState view,
        DockedBuddyView dockedBuddy,
        PreviewPane preview,
        ILogger<HomeWindow> logger)
    {
        _service = service;
        _view = view;
        _computerUse = actor is not ChatOnlyClickyActor;
        _preview = preview;
        _logger = logger;

        Title = "Clicky";
        Width = 1180;
        Height = 760;
        MinWidth = 860;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/clicky.ico"));
        Background = Brushes.White;

        _refresh = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            _ = RefreshAsync();
        };

        Content = Build(dockedBuddy);

        Loaded += (_, _) =>
        {
            _service.Changed += OnChanged;
            _ = RefreshAsync();
        };
        Closed += (_, _) =>
        {
            _service.Changed -= OnChanged;
            _refresh.Stop();
            _view.Watching = null;
            _preview.Dispose();
        };
        Activated += (_, _) =>
        {
            _view.Watching = _selectedId;
            _ = MarkReadAsync();
        };
        Deactivated += (_, _) => _view.Watching = null;
        PreviewKeyDown += OnWindowKeyDown;
    }

    /// <summary>Set by the shell: opens Settings.</summary>
    public Action? OpenSettings { get; set; }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush ColorOf(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Badge as SolidColorBrush ?? new SolidColorBrush(Colors.SteelBlue);
        }
    }

    private static Button SmallButton(string text) =>
        new() { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    private static Button LinkButton(string text, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, Foreground = Muted, FontSize = 11 },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            Cursor = Cursors.Hand,
        };
        button.Click += click;
        return button;
    }

    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }

    private static string Describe(ClickyRun run) => run.Status switch
    {
        ClickyRunStatus.Queued => "Waiting for its turn…",
        ClickyRunStatus.Running => run.Steps > 0 ? $"Working… (step {run.Steps + 1})" : "Working…",
        ClickyRunStatus.Paused => "Paused",
        ClickyRunStatus.Completed => "Done",
        ClickyRunStatus.Failed => "Failed",
        ClickyRunStatus.Cancelled => "Stopped",
        _ => "Interrupted",
    };

    private static string Duration(TimeSpan? duration) => duration switch
    {
        null => string.Empty,
        { TotalSeconds: < 60 } d => $"{d.TotalSeconds:0} s",
        { } d => $"{(int)d.TotalMinutes} min {d.Seconds} s",
    };

    private Grid Build(DockedBuddyView dockedBuddy)
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250), MinWidth = 200 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400), MinWidth = 260 });

        var sidebar = BuildSidebar(dockedBuddy);
        root.Children.Add(sidebar);

        var leftSplit = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = BubbleBorder, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(leftSplit, 1);
        root.Children.Add(leftSplit);

        var middle = new Grid();
        middle.Children.Add(BuildChat());
        middle.Children.Add(BuildEmptyState());
        Grid.SetColumn(middle, 2);
        root.Children.Add(middle);

        var rightSplit = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = BubbleBorder, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(rightSplit, 3);
        root.Children.Add(rightSplit);

        var tabs = BuildTabs();
        Grid.SetColumn(tabs, 4);
        root.Children.Add(tabs);
        return root;
    }

    private DockPanel BuildSidebar(DockedBuddyView dockedBuddy)
    {
        var sidebar = new DockPanel { Background = Sidebar, LastChildFill = true };

        var buddy = new Border { Padding = new Thickness(16, 16, 16, 8), Child = dockedBuddy };
        DockPanel.SetDock(buddy, Dock.Top);
        sidebar.Children.Add(buddy);

        var heading = new DockPanel { Margin = new Thickness(16, 8, 12, 6) };
        var add = SmallButton("New…");
        add.ToolTip = "New Clicky (Ctrl+N)";
        add.Click += (_, _) => _ = CreateAsync(null);
        DockPanel.SetDock(add, Dock.Right);
        heading.Children.Add(add);
        heading.Children.Add(new TextBlock { Text = "Clickys", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(heading, Dock.Top);
        sidebar.Children.Add(heading);

        var settings = SmallButton("Settings");
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        settings.Margin = new Thickness(16, 8, 16, 16);
        settings.Click += (_, _) => OpenSettings?.Invoke();
        DockPanel.SetDock(settings, Dock.Bottom);
        sidebar.Children.Add(settings);

        _list.SelectionChanged += (_, _) =>
        {
            if (!_suppressSelection && _list.SelectedItem is ListBoxItem { Tag: string id } && id != _selectedId)
            {
                Select(id);
            }
        };
        sidebar.Children.Add(_list);
        return sidebar;
    }

    private Grid BuildChat()
    {
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chat.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(16, 14, 16, 6) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var edit = SmallButton("Edit…");
        edit.Click += (_, _) => _ = EditAsync();
        var files = SmallButton("Files");
        files.ToolTip = "Open this Clicky's files folder. Text files in it are sent with every task; drop files on the chat to add them.";
        files.Click += (_, _) => OpenFiles();
        var delete = SmallButton("Delete");
        delete.Click += (_, _) => _ = DeleteAsync();
        actions.Children.Add(edit);
        actions.Children.Add(files);
        actions.Children.Add(delete);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var title = new StackPanel();
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(_headerDot);
        name.Children.Add(_headerName);
        title.Children.Add(name);
        title.Children.Add(_headerInstructions);
        header.Children.Add(title);
        _chat.Children.Add(header);

        _scroll.Content = _transcript;
        _scroll.AllowDrop = true;
        _scroll.DragOver += OnDragOver;
        _scroll.Drop += OnDrop;
        Grid.SetRow(_scroll, 1);
        _chat.Children.Add(_scroll);

        var bar = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        _pause.Click += (_, _) => Control(_service.Pause);
        _resume.Click += (_, _) => Control(_service.Resume);
        _stop.Click += (_, _) => Control(_service.Cancel);
        controls.Children.Add(_pause);
        controls.Children.Add(_resume);
        controls.Children.Add(_stop);
        DockPanel.SetDock(controls, Dock.Right);
        bar.Children.Add(controls);
        bar.Children.Add(_runText);
        _runBar.Child = bar;
        Grid.SetRow(_runBar, 2);
        _chat.Children.Add(_runBar);

        Grid.SetRow(_status, 3);
        _chat.Children.Add(_status);

        var composer = new DockPanel { Margin = new Thickness(16, 4, 16, 16) };
        _send.Click += (_, _) => _ = SendAsync();
        DockPanel.SetDock(_send, Dock.Right);
        composer.Children.Add(_send);
        _composer.PreviewKeyDown += OnComposerKeyDown;
        composer.Children.Add(_composer);
        Grid.SetRow(composer, 4);
        _chat.Children.Add(composer);
        return _chat;
    }

    private StackPanel BuildEmptyState()
    {
        _empty.Children.Add(new TextBlock { Text = "Make your first Clicky", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _empty.Children.Add(new TextBlock
        {
            Text = "A Clicky is a helper that keeps working for you: give it instructions once, then send it tasks in its chat, " +
                   "or let a routine run it on a schedule. Its replies wait here for you.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 16),
        });

        foreach (var template in ClickyTemplates.All)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = template.Title, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = template.Description, Foreground = Muted, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            button.Click += (_, _) => _ = CreateAsync(template);
            _empty.Children.Add(button);
        }

        return _empty;
    }

    private TabControl BuildTabs()
    {
        _tabs.Items.Add(new TabItem { Header = "Preview", Content = _preview });

        var activity = new DockPanel();
        DockPanel.SetDock(_activityTitle, Dock.Top);
        activity.Children.Add(_activityTitle);
        activity.Children.Add(_activity);
        _tabs.Items.Add(new TabItem { Header = "Activity", Content = activity });

        var history = new DockPanel();
        var historyBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        _retry.Margin = new Thickness(0);
        _retry.ToolTip = "Run the selected task again";
        _retry.Click += (_, _) => _ = RetryAsync();
        historyBar.Children.Add(_retry);
        DockPanel.SetDock(historyBar, Dock.Top);
        history.Children.Add(historyBar);
        _history.SelectionChanged += (_, _) =>
        {
            if (!_suppressSelection && _history.SelectedItem is ListBoxItem { Tag: string runId })
            {
                SelectRun(runId, showReply: true);
            }
        };
        history.Children.Add(_history);
        _tabs.Items.Add(new TabItem { Header = "History", Content = history });
        return _tabs;
    }

    private void OnChanged(object? sender, ClickyChangedEventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        switch (e.Change)
        {
            case ClickyChange.StepAdded:
                _dirtyActivity |= e.RunId == _selectedRunId;
                break;
            case ClickyChange.MessageAdded or ClickyChange.RunChanged:
                _dirtyList = true;
                _dirtyChat |= e.ClickyId == _selectedId;
                _dirtyActivity |= e.RunId == _selectedRunId;
                break;
            default:
                _dirtyList = true;
                _dirtyChat |= e.ClickyId == _selectedId;
                break;
        }

        if (!_refresh.IsEnabled)
        {
            _refresh.Start();
        }
    });

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refresh.Start();
            return;
        }

        _refreshing = true;
        try
        {
            if (_dirtyList)
            {
                _dirtyList = false;
                _clickys = await _service.ListAsync();
                RebuildList();
            }

            if (_dirtyChat)
            {
                _dirtyChat = false;
                await LoadChatAsync();
            }

            if (_dirtyActivity)
            {
                _dirtyActivity = false;
                await LoadActivityAsync();
            }
        }
        catch (ClickyDatabaseException ex)
        {
            _available = false;
            _status.Text = ex.Message;
            _status.Foreground = Failure;
            _logger.LogError(ex, "Home cannot open the Clickys database");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Home refresh failed");
            _status.Text = "Couldn't load Clickys: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }

        UpdateLayoutState();
    }

    private void RebuildList()
    {
        var active = _service.ActiveRuns;
        _suppressSelection = true;
        _list.Items.Clear();
        ListBoxItem? selected = null;
        foreach (var clicky in _clickys)
        {
            var run = active.LastOrDefault(r => r.ClickyId == clicky.Id);
            var line = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            if (clicky.Unread > 0)
            {
                var badge = new Border
                {
                    Background = Badge,
                    CornerRadius = new CornerRadius(9),
                    MinWidth = 18,
                    Padding = new Thickness(5, 0, 5, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = clicky.Unread > 99 ? "99+" : clicky.Unread.ToString(CultureInfo.CurrentCulture), Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                };
                DockPanel.SetDock(badge, Dock.Right);
                line.Children.Add(badge);
            }

            var dot = new Ellipse { Width = 10, Height = 10, Fill = ColorOf(clicky.Color), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(dot);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = clicky.Name, FontWeight = clicky.Unread > 0 ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = run is null ? When(clicky.LastActivityAt) : Describe(run), Foreground = Muted, FontSize = 11 });
            line.Children.Add(text);

            var item = new ListBoxItem { Content = line, Tag = clicky.Id };
            _list.Items.Add(item);
            if (clicky.Id == _selectedId)
            {
                selected = item;
            }
        }

        if (selected is null && _clickys.Count > 0)
        {
            selected = (ListBoxItem)_list.Items[0];
            _selectedId = (string)selected.Tag;
            _selectedRunId = null;
            _selectedMessageId = null;
            _dirtyChat = true;
        }
        else if (_clickys.Count == 0)
        {
            _selectedId = null;
        }

        _list.SelectedItem = selected;
        _suppressSelection = false;
    }

    private void Select(string clickyId)
    {
        _selectedId = clickyId;
        _selectedRunId = null;
        _selectedMessageId = null;
        _preview.Clear();
        if (IsActive)
        {
            _view.Watching = clickyId;
        }

        _dirtyChat = true;
        _dirtyActivity = true;
        _ = RefreshAsync();
    }

    private ClickyAgent? Selected => _clickys.FirstOrDefault(c => c.Id == _selectedId);

    private async Task LoadChatAsync()
    {
        if (Selected is not { } clicky)
        {
            _messages = [];
            _runs = [];
            _transcript.Children.Clear();
            return;
        }

        _messages = await _service.GetMessagesAsync(clicky.Id, ChatMessages);
        _runs = await _service.GetRunsAsync(clicky.Id);
        if (clicky.Id != _selectedId)
        {
            return;
        }

        _headerDot.Fill = ColorOf(clicky.Color);
        _headerName.Text = clicky.Name;
        _headerInstructions.Text = clicky.Instructions.Length > 0 ? clicky.Instructions.ReplaceLineEndings(" ") : "No instructions yet. Use Edit to add some.";

        // Keep following the bottom while a reply lands, unless the user scrolled up to read.
        var atBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 24;
        RebuildTranscript(clicky);
        if (atBottom)
        {
            _scroll.ScrollToEnd();
        }

        RebuildHistory();

        // Follow the newest run (the active one) unless the user picked one.
        if (_selectedRunId is null || _runs.All(r => r.Id != _selectedRunId))
        {
            _selectedRunId = _runs.Count > 0 ? _runs[0].Id : null;
            _dirtyActivity = true;
            if (_selectedMessageId is null && _messages.LastOrDefault(m => m.Role == ClickyMessageRole.Assistant) is { } latest)
            {
                ShowReply(latest, clicky);
            }
        }

        UpdateRunBar();
        await MarkReadAsync();
    }

    private void RebuildTranscript(ClickyAgent clicky)
    {
        _transcript.Children.Clear();
        if (_messages.Count == 0)
        {
            _transcript.Children.Add(new TextBlock
            {
                Text = $"Send {clicky.Name} its first task below. Replies are written for reading here, not spoken; anything long or formatted shows in Preview.",
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 24, 0, 0),
            });
            return;
        }

        foreach (var message in _messages)
        {
            _transcript.Children.Add(Bubble(message, clicky));
        }
    }

    private FrameworkElement Bubble(ClickyMessage message, ClickyAgent clicky)
    {
        var mine = message.Role is ClickyMessageRole.User or ClickyMessageRole.Routine;
        if (message.Role == ClickyMessageRole.System)
        {
            return new TextBlock
            {
                Text = $"{message.Text}  ·  {When(message.At)}",
                Foreground = message.Text.StartsWith("Couldn't", StringComparison.Ordinal) ? Failure : Muted,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 10),
            };
        }

        var text = message.Text.Length > BubbleChars ? message.Text[..BubbleChars] + "…" : message.Text;
        var body = new StackPanel();
        if (message.Role == ClickyMessageRole.Routine)
        {
            body.Children.Add(new TextBlock { Text = "Routine", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Muted });
        }

        body.Children.Add(new TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0),
        });

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        footer.Children.Add(new TextBlock { Text = When(message.At), Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        if (message.Role == ClickyMessageRole.Assistant)
        {
            footer.Children.Add(LinkButton("Preview", (_, _) => ShowReply(message, clicky)));
            footer.Children.Add(LinkButton("Copy", (_, _) => Copy(message.Text)));
            footer.Children.Add(LinkButton("Save to files", (_, _) => _ = SaveAsync(message, clicky)));
        }

        if (message.RunId is { } runId)
        {
            footer.Children.Add(LinkButton("Activity", (_, _) =>
            {
                SelectRun(runId, showReply: false);
                _tabs.SelectedIndex = 1;
            }));
        }

        body.Children.Add(footer);

        var bubble = new Border
        {
            Background = message.Role switch
            {
                ClickyMessageRole.User => UserBubble,
                ClickyMessageRole.Routine => RoutineBubble,
                _ => AssistantBubble,
            },
            BorderBrush = message.Id == _selectedMessageId ? SelectedBorder : BubbleBorder,
            BorderThickness = new Thickness(message.Id == _selectedMessageId ? 2 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = mine ? new Thickness(60, 0, 0, 10) : new Thickness(0, 0, 60, 10),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = body,
        };
        if (message.Role == ClickyMessageRole.Assistant)
        {
            bubble.PreviewMouseLeftButtonUp += (_, _) => ShowReply(message, clicky);
        }

        return bubble;
    }

    private void ShowReply(ClickyMessage message, ClickyAgent clicky)
    {
        var changed = _selectedMessageId != message.Id;
        _selectedMessageId = message.Id;
        _preview.Show(message.Text, clicky.Name);
        if (message.RunId is { } runId && runId != _selectedRunId)
        {
            _selectedRunId = runId;
            _dirtyActivity = true;
            _ = RefreshAsync();
        }

        if (changed)
        {
            RebuildTranscript(clicky);
        }
    }

    private void SelectRun(string runId, bool showReply)
    {
        _selectedRunId = runId;
        _dirtyActivity = true;
        if (showReply && Selected is { } clicky && _messages.LastOrDefault(m => m.RunId == runId && m.Role == ClickyMessageRole.Assistant) is { } reply)
        {
            ShowReply(reply, clicky);
        }

        _ = RefreshAsync();
    }

    private void RebuildHistory()
    {
        _suppressSelection = true;
        _history.Items.Clear();
        foreach (var run in _runs)
        {
            var line = new StackPanel { Margin = new Thickness(4, 4, 4, 4) };
            var trigger = run.Trigger == ClickyRunTrigger.Routine ? "Routine" + (run.RoutineName is { } name ? $" “{name}”" : string.Empty) : "Chat";
            line.Children.Add(new TextBlock
            {
                Text = $"{Describe(run)} · {trigger} · {When(run.QueuedAt)}",
                FontWeight = FontWeights.SemiBold,
                Foreground = run.Status == ClickyRunStatus.Failed ? Failure : Brushes.Black,
            });
            line.Children.Add(new TextBlock { Text = run.Prompt.ReplaceLineEndings(" "), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Muted });
            var details = new List<string>();
            if (Duration(run.Duration) is { Length: > 0 } duration)
            {
                details.Add(duration);
            }

            if (run.Steps > 1)
            {
                details.Add($"{run.Steps} steps");
            }

            if (run.InputTokens + run.OutputTokens > 0)
            {
                details.Add($"{run.InputTokens + run.OutputTokens:N0} tokens");
            }

            if (run.CostUsd is { } cost)
            {
                details.Add(cost.ToString("$0.000", CultureInfo.InvariantCulture));
            }

            if (run.Error is { Length: > 0 } error && run.Status != ClickyRunStatus.Completed)
            {
                details.Add(error);
            }

            if (details.Count > 0)
            {
                line.Children.Add(new TextBlock { Text = string.Join(" · ", details), Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            }

            var item = new ListBoxItem { Content = line, Tag = run.Id };
            _history.Items.Add(item);
            if (run.Id == _selectedRunId)
            {
                _history.SelectedItem = item;
            }
        }

        _suppressSelection = false;
    }

    private async Task LoadActivityAsync()
    {
        _activity.Items.Clear();
        if (_selectedRunId is not { } runId)
        {
            _activityTitle.Text = "Nothing has run yet.";
            _retry.IsEnabled = false;
            return;
        }

        var run = await _service.GetRunAsync(runId);
        var steps = await _service.GetStepsAsync(runId);
        if (runId != _selectedRunId)
        {
            return;
        }

        _retry.IsEnabled = run is { IsActive: false };
        _activityTitle.Text = run is null ? string.Empty : $"{Describe(run)}: {run.Prompt.ReplaceLineEndings(" ")}";
        foreach (var step in steps)
        {
            var line = new DockPanel { Margin = new Thickness(2) };
            var time = new TextBlock { Text = step.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture), Foreground = Muted, FontSize = 11, Width = 56, VerticalAlignment = VerticalAlignment.Top };
            line.Children.Add(time);
            line.Children.Add(new TextBlock
            {
                Text = step.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = step.Kind switch
                {
                    ClickyStepKind.Error or ClickyStepKind.Rejected => Failure,
                    ClickyStepKind.Control or ClickyStepKind.Request or ClickyStepKind.Screenshot => Muted,
                    _ => Brushes.Black,
                },
                FontWeight = step.Kind == ClickyStepKind.Action ? FontWeights.SemiBold : FontWeights.Normal,
            });
            _activity.Items.Add(new ListBoxItem { Content = line, Focusable = false });
        }

        if (_activity.Items.Count > 0)
        {
            _activity.ScrollIntoView(_activity.Items[^1]);
        }
    }

    private void UpdateRunBar()
    {
        var run = _service.ActiveRuns.LastOrDefault(r => r.ClickyId == _selectedId);
        if (run is null)
        {
            _runBar.Visibility = Visibility.Collapsed;
            return;
        }

        var pausing = _service.IsPauseRequested(run.Id);
        _runBar.Visibility = Visibility.Visible;
        _runBar.Tag = run.Id;
        _runText.Text = run.Status == ClickyRunStatus.Running && pausing ? "Pausing after this step…" : Describe(run);
        _pause.Visibility = pausing || run.Status == ClickyRunStatus.Paused ? Visibility.Collapsed : Visibility.Visible;
        _resume.Visibility = pausing || run.Status == ClickyRunStatus.Paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLayoutState()
    {
        var any = _clickys.Count > 0;
        _chat.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = any || !_available ? Visibility.Collapsed : Visibility.Visible;
        _send.IsEnabled = _available && any;
        _composer.IsEnabled = _available && any;
        if (any)
        {
            UpdateRunBar();
        }
    }

    private void Control(Func<string, bool> action)
    {
        if (_runBar.Tag is string runId)
        {
            action(runId);
            UpdateRunBar();
        }
    }

    private async Task MarkReadAsync()
    {
        if (IsActive && Selected is { Unread: > 0 } clicky)
        {
            try
            {
                await _service.MarkReadAsync(clicky.Id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Could not mark {Clicky} read", clicky.Id);
            }
        }
    }

    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = CreateAsync(null);
        }
    }

    private async Task SendAsync()
    {
        var text = _composer.Text.Trim();
        if (text.Length == 0 || _selectedId is not { } clickyId)
        {
            return;
        }

        _composer.Clear();
        try
        {
            var run = await _service.SendAsync(clickyId, text);
            _selectedRunId = run.Id;
            _selectedMessageId = null;
            _status.Text = string.Empty;
            _scroll.ScrollToEnd();
        }
        catch (Exception ex) when (ex is ClickyNotFoundException or ClickyDatabaseException or ObjectDisposedException)
        {
            _composer.Text = text;
            _status.Text = "Couldn't send: " + ex.Message;
        }
    }

    private async Task CreateAsync(ClickyTemplate? template)
    {
        ClickyDraft draft;
        if (template is not null && template != ClickyTemplates.All[0])
        {
            draft = template.Draft;
        }
        else
        {
            var editor = new ClickyEditorWindow(null, _computerUse) { Owner = this };
            if (editor.ShowDialog() != true || editor.Result is not { } result)
            {
                return;
            }

            draft = result;
        }

        try
        {
            var clicky = await _service.CreateAsync(draft);
            _selectedId = clicky.Id;
            _dirtyList = true;
            _dirtyChat = true;
            await RefreshAsync();
            _composer.Focus();
        }
        catch (Exception ex) when (ex is ArgumentException or ClickyDatabaseException)
        {
            _status.Text = "Couldn't create it: " + ex.Message;
        }
    }

    private async Task EditAsync()
    {
        if (Selected is not { } clicky)
        {
            return;
        }

        var editor = new ClickyEditorWindow(clicky, _computerUse) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } draft)
        {
            return;
        }

        try
        {
            await _service.UpdateAsync(clicky.Id, draft);
        }
        catch (Exception ex) when (ex is ClickyNotFoundException or ClickyDatabaseException)
        {
            _status.Text = "Couldn't save: " + ex.Message;
        }
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } clicky)
        {
            return;
        }

        var folder = _service.Workspace.DirectoryFor(clicky.Id);
        var answer = MessageBox.Show(
            this,
            $"Delete {clicky.Name}? Its chat and history go with it, and anything it's doing now stops." +
            (Directory.Exists(folder) ? $"\n\nIts files folder stays on disk:\n{folder}" : string.Empty),
            "Delete Clicky",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _service.DeleteAsync(clicky.Id);
            _selectedId = null;
            _preview.Clear();
        }
        catch (Exception ex) when (ex is TimeoutException or ClickyDatabaseException)
        {
            _status.Text = "Couldn't delete it: " + ex.Message;
        }
    }

    private async Task RetryAsync()
    {
        if (_selectedRunId is not { } runId)
        {
            return;
        }

        try
        {
            var run = await _service.RetryAsync(runId);
            _selectedRunId = run.Id;
        }
        catch (Exception ex) when (ex is ClickyNotFoundException or ClickyDatabaseException or ObjectDisposedException)
        {
            _status.Text = "Couldn't run it again: " + ex.Message;
        }
    }

    private void OpenFiles()
    {
        if (_selectedId is not { } clickyId)
        {
            return;
        }

        try
        {
            var folder = _service.Workspace.EnsureCreated(clickyId);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "Couldn't open the files folder: " + ex.Message;
        }
    }

    private async Task SaveAsync(ClickyMessage message, ClickyAgent clicky)
    {
        var html = ClickyPreview.HtmlDocument(message.Text);
        var name = $"{clicky.Name} {message.At.ToLocalTime():yyyy-MM-dd HHmm}{(html is null ? ".md" : ".html")}";
        try
        {
            var path = await _service.Workspace.SaveAsync(clicky.Id, name, html ?? message.Text);
            _status.Text = $"Saved to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Couldn't save: " + ex.Message;
        }
    }

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _status.Text = "Copied.";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Another app has the clipboard open.
            _status.Text = "Couldn't copy: " + ex.Message;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _selectedId is not null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_selectedId is not { } clickyId || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        try
        {
            var folder = _service.Workspace.EnsureCreated(clickyId);
            var copied = 0;
            foreach (var path in paths.Where(File.Exists))
            {
                var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(path));
                for (var n = 2; File.Exists(target); n++)
                {
                    target = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileNameWithoutExtension(path)} ({n}){System.IO.Path.GetExtension(path)}");
                }

                File.Copy(path, target);
                copied++;
            }

            _status.Text = copied == 1 ? "Added 1 file to its files folder." : $"Added {copied} files to its files folder.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Couldn't add the files: " + ex.Message;
        }
    }
}
