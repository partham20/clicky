using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Clicky.Core.Clickys;

namespace Clicky.App.Views.Home;

/// <summary>Create or edit one Clicky: name, colour, instructions, notes and what it may do.</summary>
internal sealed class ClickyEditorWindow : Window
{
    private static readonly string[] Swatches = ["#2F81F7", "#8250DF", "#BF3989", "#CF222E", "#BC4C00", "#9A6700", "#1A7F37", "#57606A"];

    private readonly bool _creating;
    private readonly TextBox _name = new() { Margin = new Thickness(0, 0, 0, 8), MaxLength = ClickyDraft.MaxNameLength };
    private readonly TextBox _instructions = Multiline(6, ClickyDraft.MaxInstructionsLength);
    private readonly TextBox _notes = Multiline(4, ClickyDraft.MaxNotesLength);
    private readonly CheckBox _usesComputer = new() { Content = "Can see the screen and use the computer", Margin = new Thickness(0, 4, 0, 2) };
    private readonly TextBox _maxSteps = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _swatches = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    private string _color = ClickyDraft.DefaultColor;

    /// <param name="original">The Clicky to edit, or null to create one.</param>
    /// <param name="computerUseAvailable">False until computer use is wired in; the option is then explained as not yet working.</param>
    public ClickyEditorWindow(ClickyAgent? original, bool computerUseAvailable)
    {
        _creating = original is null;
        Title = original is null ? "New Clicky" : $"Edit {original.Name}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        Content = new ScrollViewer { Content = Build(computerUseAvailable), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 820 };
        Load(original is null ? ClickyTemplates.All[0].Draft : ClickyDraft.From(original));
        Loaded += (_, _) =>
        {
            _name.Focus();
            _name.SelectAll();
        };
    }

    /// <summary>The edited Clicky, valid when ShowDialog returned true.</summary>
    public ClickyDraft? Result { get; private set; }

    private static TextBox Multiline(int lines, int max) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinLines = lines,
        MaxLines = lines * 3,
        MaxLength = max,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };

    private static TextBlock Hint(string text) => new() { Text = text, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

    private StackPanel Build(bool computerUseAvailable)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };

        if (_creating)
        {
            panel.Children.Add(Label("Start from"));
            var templates = new ComboBox { Margin = new Thickness(0, 0, 0, 12), ItemsSource = ClickyTemplates.All, SelectedIndex = 0 };
            templates.ItemTemplate = TemplateItem();
            templates.SelectionChanged += (_, _) =>
            {
                if (templates.SelectedItem is ClickyTemplate template)
                {
                    Load(template.Draft);
                }
            };
            panel.Children.Add(templates);
        }

        panel.Children.Add(Label("Name"));
        panel.Children.Add(_name);

        panel.Children.Add(Label("Colour"));
        foreach (var swatch in Swatches)
        {
            var dot = new RadioButton
            {
                GroupName = "colour",
                Tag = swatch,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = swatch,
                Content = new Ellipse { Width = 18, Height = 18, Fill = Brush(swatch) },
            };
            dot.Checked += (_, _) => _color = swatch;
            _swatches.Children.Add(dot);
        }

        panel.Children.Add(_swatches);

        panel.Children.Add(Label("Instructions"));
        panel.Children.Add(Hint("Who this Clicky is and what it does. Sent with every task."));
        panel.Children.Add(_instructions);

        panel.Children.Add(Label("Notes"));
        panel.Children.Add(Hint("Things it should keep in mind: names, preferences, where things are. Files it should know go in its files folder."));
        panel.Children.Add(_notes);

        panel.Children.Add(_usesComputer);
        panel.Children.Add(Hint(computerUseAvailable
            ? "It can then click, type and scroll for you. Clicky still asks before anything that sends, deletes or buys, and refuses those while you're away."
            : "Computer use isn't available in this build yet; until it is, this Clicky works in its chat and with your connectors only."));

        var steps = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        steps.Children.Add(new TextBlock { Text = "Most steps per task: ", VerticalAlignment = VerticalAlignment.Center });
        steps.Children.Add(_maxSteps);
        panel.Children.Add(steps);

        panel.Children.Add(_error);

        var save = new Button { Content = _creating ? "Create" : "Save", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        return panel;
    }

    private static DataTemplate TemplateItem()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ClickyTemplate.Title)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var description = new FrameworkElementFactory(typeof(TextBlock));
        description.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ClickyTemplate.Description)));
        description.SetValue(UIElement.OpacityProperty, 0.7);
        panel.AppendChild(title);
        panel.AppendChild(description);
        return new DataTemplate { VisualTree = panel };
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void Load(ClickyDraft draft)
    {
        _name.Text = draft.Name;
        _instructions.Text = draft.Instructions;
        _notes.Text = draft.Notes;
        _usesComputer.IsChecked = draft.UsesComputer;
        _maxSteps.Text = draft.MaxSteps.ToString(CultureInfo.CurrentCulture);
        _color = draft.Color;
        foreach (RadioButton dot in _swatches.Children)
        {
            dot.IsChecked = string.Equals((string)dot.Tag, draft.Color, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _error.Text = "Give your Clicky a name.";
            _name.Focus();
            return;
        }

        if (!int.TryParse(_maxSteps.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var maxSteps) ||
            maxSteps is < ClickyDraft.MinMaxSteps or > ClickyDraft.MaxMaxSteps)
        {
            _error.Text = $"Most steps should be a number from {ClickyDraft.MinMaxSteps} to {ClickyDraft.MaxMaxSteps}.";
            _maxSteps.Focus();
            return;
        }

        Result = new ClickyDraft(_name.Text, _instructions.Text, _notes.Text, _color, _usesComputer.IsChecked == true, maxSteps).Normalize();
        DialogResult = true;
    }
}
