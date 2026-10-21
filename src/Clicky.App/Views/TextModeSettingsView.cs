using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Typed questions and documents page (M10): what goes with a typed question, whether its
/// answer is also spoken, and how much document text one question may carry.
/// </summary>
internal sealed class TextModeSettingsView : ContentControl
{
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<TextModeSettingsView> _logger;

    private readonly CheckBox _speak = new() { Content = "Also read typed answers aloud" };
    private readonly CheckBox _screenshot = new() { Content = "Send a screenshot with typed questions", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _windowDocument = new() { Content = "Offer the document open in the app in front (Word, PDF readers, editors…)", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _dropTarget = new() { Content = "Show \"Drop here to ask Clicky\" by the pointer while I drag files", Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _budget = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _syncing;

    public TextModeSettingsView(ISettingsStore settings, Dispatcher dispatcher, ILogger<TextModeSettingsView> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        _budget.Items.Add(new ComboBoxItem { Content = "Short: about 15 pages per question", Tag = 15_000 });
        _budget.Items.Add(new ComboBoxItem { Content = "Standard: about 100 pages", Tag = TextModeSettings.DefaultDocumentTokenBudget });
        _budget.Items.Add(new ComboBoxItem { Content = "Long: about 300 pages (slower, costs more on the API)", Tag = 180_000 });

        _speak.Click += (_, _) => Save(t => t with { SpeakReplies = _speak.IsChecked == true });
        _screenshot.Click += (_, _) => Save(t => t with { IncludeScreenshot = _screenshot.IsChecked == true });
        _windowDocument.Click += (_, _) => Save(t => t with { OfferWindowDocument = _windowDocument.IsChecked == true });
        _dropTarget.Click += (_, _) => Save(t => t with { OfferDropTarget = _dropTarget.IsChecked == true });
        _budget.SelectionChanged += (_, _) =>
        {
            if (_budget.SelectedItem is ComboBoxItem { Tag: int budget })
            {
                Save(t => t with { DocumentTokenBudget = budget });
            }
        };

        Content = Build();
        Loaded += (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            Sync();
        };
        Unloaded += (_, _) => _settings.Changed -= OnSettingsChanged;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private StackPanel Build()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Typed questions and documents", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "Double-tap Ctrl to type a question instead of saying it. Drop or paste files into the box, or onto the buddy, to ask about them.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        panel.Children.Add(Label("With each typed question"));
        panel.Children.Add(_speak);
        panel.Children.Add(_screenshot);
        panel.Children.Add(_windowDocument);
        panel.Children.Add(_dropTarget);

        panel.Children.Add(Label("Document length per question"));
        panel.Children.Add(_budget);
        panel.Children.Add(new TextBlock
        {
            Text = "Longer documents are cut to the parts that match the question.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 0),
        });
        return panel;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Text != e.Current.Text)
        {
            _dispatcher.InvokeAsync(Sync);
        }
    }

    private void Sync()
    {
        var text = _settings.Current.Text;
        _syncing = true;
        try
        {
            _speak.IsChecked = text.SpeakReplies;
            _screenshot.IsChecked = text.IncludeScreenshot;
            _windowDocument.IsChecked = text.OfferWindowDocument;
            _dropTarget.IsChecked = text.OfferDropTarget;

            // A hand-edited budget that matches no preset shows as its own entry.
            var match = _budget.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == text.DocumentTokenBudget);
            if (match is null)
            {
                match = new ComboBoxItem { Content = $"Custom: {text.DocumentTokenBudget:N0} tokens (from settings.json)", Tag = text.DocumentTokenBudget };
                _budget.Items.Add(match);
            }

            _budget.SelectedItem = match;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Save(Func<TextModeSettings, TextModeSettings> change)
    {
        if (!_syncing)
        {
            _ = SaveAsync(change);
        }
    }

    private async Task SaveAsync(Func<TextModeSettings, TextModeSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Text = change(s.Text) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save text mode settings");
        }

        await _dispatcher.InvokeAsync(Sync);
    }
}
