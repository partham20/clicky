using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Clicky.Core.Dictation;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Dictation section of Settings: how dictation behaves, where its history is, and the
/// personal dictionary (words to get right, and what speech-to-text writes instead). Options go to
/// settings.json; the dictionary to dictionary.json. M18 moves the dictionary to its own page.
/// </summary>
internal sealed class DictationSettingsView : ContentControl
{
    private static readonly int[] HistoryDays = [0, 7, 30, 90, 365];

    private readonly ISettingsStore _settings;
    private readonly PersonalVocabulary _vocabulary;
    private readonly DictationHistory _history;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<DictationSettingsView> _logger;

    private readonly CheckBox _typeWhileSpeaking = new() { Content = "Hands-free: type each sentence as soon as it is recognised" };
    private readonly CheckBox _spokenCommands = new() { Content = "Say \"new line\" or \"new paragraph\" for line breaks" };
    private readonly CheckBox _learn = new() { Content = "Learn words I correct by hand within 20 seconds" };
    private readonly ComboBox _keepHistory = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _cleanup = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ObservableCollection<WordRow> _rows = [];
    private readonly DataGrid _grid;
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private bool _syncing;
    private bool _dirty;

    public DictationSettingsView(ISettingsStore settings, PersonalVocabulary vocabulary, DictationHistory history, Dispatcher dispatcher, ILogger<DictationSettingsView> logger)
    {
        _settings = settings;
        _vocabulary = vocabulary;
        _history = history;
        _dispatcher = dispatcher;
        _logger = logger;

        foreach (var days in HistoryDays)
        {
            _keepHistory.Items.Add(new ComboBoxItem { Content = days == 0 ? "Don't keep" : $"{days} days", Tag = days });
        }

        _cleanup.Items.Add(new ComboBoxItem { Content = "With an API key only (fast, under a second)", Tag = DictationCleanup.Auto });
        _cleanup.Items.Add(new ComboBoxItem { Content = "Always (seconds per sentence on a subscription)", Tag = DictationCleanup.Always });
        _cleanup.Items.Add(new ComboBoxItem { Content = "Off: as transcribed", Tag = DictationCleanup.Off });
        _cleanup.SelectionChanged += (_, _) =>
        {
            if (_cleanup.SelectedItem is ComboBoxItem { Tag: DictationCleanup cleanup })
            {
                Save(s => s with { Cleanup = cleanup });
            }
        };

        _grid = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            CanUserAddRows = true,
            CanUserDeleteRows = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MinHeight = 120,
            MaxHeight = 260,
            Margin = new Thickness(0, 4, 0, 0),
            Columns =
            {
                new DataGridTextColumn { Header = "Word or phrase", Binding = new Binding(nameof(WordRow.Phrase)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) },
                new DataGridTextColumn { Header = "Sounds like (comma separated)", Binding = new Binding(nameof(WordRow.SoundsLike)), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) },
                new DataGridCheckBoxColumn { Header = "Learned", Binding = new Binding(nameof(WordRow.Learned)), IsReadOnly = true, Width = DataGridLength.SizeToHeader },
            },
        };
        _grid.CellEditEnding += (_, _) => _dirty = true;
        _rows.CollectionChanged += (_, _) => _dirty = !_syncing || _dirty;

        _typeWhileSpeaking.Click += (_, _) => Save(s => s with { TypeWhileSpeaking = _typeWhileSpeaking.IsChecked == true });
        _spokenCommands.Click += (_, _) => Save(s => s with { SpokenCommands = _spokenCommands.IsChecked == true });
        _learn.Click += (_, _) => Save(s => s with { LearnCorrections = _learn.IsChecked == true });
        _keepHistory.SelectionChanged += (_, _) =>
        {
            if (_keepHistory.SelectedItem is ComboBoxItem { Tag: int days })
            {
                Save(s => s with { KeepHistoryDays = days });
            }
        };

        Content = Build();
        Loaded += (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            _vocabulary.Changed += OnVocabularyChanged;
            Sync();
            LoadWords();
        };
        Unloaded += (_, _) =>
        {
            _settings.Changed -= OnSettingsChanged;
            _vocabulary.Changed -= OnVocabularyChanged;
        };
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private StackPanel Build()
    {
        var openHistory = new Button { Content = "Open history folder", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        openHistory.Click += (_, _) => OpenFolder(_history.Directory);
        var save = new Button { Content = "Save words", Padding = new Thickness(10, 2, 10, 2) };
        save.Click += (_, _) => SaveWords();
        var remove = new Button { Content = "Remove selected", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        remove.Click += (_, _) =>
        {
            foreach (var row in _grid.SelectedItems.OfType<WordRow>().ToArray())
            {
                _rows.Remove(row);
            }

            SaveWords();
        };

        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Dictation", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "Hold Ctrl+Win and speak; let go and the text goes where your cursor is. Double-tap Ctrl+Win to dictate hands-free, and double-tap again to stop. " +
                "Start with \"type a reply to this\" and Claude reads the screen and writes the reply in your voice. " +
                "In terminals, line breaks become spaces so nothing runs by accident. Language, engine and microphone are the Voice settings above.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        panel.Children.Add(Label("Options"));
        panel.Children.Add(_typeWhileSpeaking);
        panel.Children.Add(_spokenCommands);
        panel.Children.Add(_learn);

        panel.Children.Add(Label("Tidy punctuation and grammar with Claude's fast model (never changes your words)"));
        panel.Children.Add(_cleanup);

        panel.Children.Add(Label("Keep a text copy of every dictation"));
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _keepHistory, openHistory } });

        panel.Children.Add(Label("Personal dictionary"));
        panel.Children.Add(new TextBlock
        {
            Text = "Names and jargon Clicky should spell your way. \"Sounds like\" is what it wrote instead; it is replaced with your spelling.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        panel.Children.Add(_grid);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { save, remove, _status } });
        return panel;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _dispatcher.InvokeAsync(Sync);

    private void OnVocabularyChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() =>
    {
        // Don't throw away edits in progress for a word Clicky just learned.
        if (!_dirty)
        {
            LoadWords();
        }
    });

    private void Sync()
    {
        _syncing = true;
        try
        {
            var d = _settings.Current.Dictation;
            _typeWhileSpeaking.IsChecked = d.TypeWhileSpeaking;
            _spokenCommands.IsChecked = d.SpokenCommands;
            _learn.IsChecked = d.LearnCorrections;
            _cleanup.SelectedItem = _cleanup.Items.Cast<ComboBoxItem>().First(i => (DictationCleanup)i.Tag == d.Cleanup);
            _keepHistory.SelectedItem = _keepHistory.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == d.KeepHistoryDays)
                ?? AddCustomDays(d.KeepHistoryDays);
        }
        finally
        {
            _syncing = false;
        }
    }

    private ComboBoxItem AddCustomDays(int days)
    {
        var item = new ComboBoxItem { Content = $"{days} days", Tag = days };
        _keepHistory.Items.Add(item);
        return item;
    }

    private void LoadWords()
    {
        _syncing = true;
        try
        {
            _rows.Clear();
            foreach (var entry in _vocabulary.Entries)
            {
                _rows.Add(new WordRow { Phrase = entry.Phrase, SoundsLike = string.Join(", ", entry.SoundsLike), Learned = entry.Learned });
            }

            _dirty = false;
            _status.Text = $"{_rows.Count} words in {_vocabulary.FilePath}";
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SaveWords()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        var entries = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Phrase))
            .Select(r => new DictionaryEntry(
                r.Phrase!.Trim(),
                [.. (r.SoundsLike ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                r.Learned));
        _dirty = false;
        _status.Text = _vocabulary.Save(entries) ? "Saved." : "Could not save; see the log.";
        LoadWords();
    }

    private void Save(Func<DictationSettings, DictationSettings> change)
    {
        if (_syncing)
        {
            return;
        }

        _ = SaveAsync(change);
    }

    private async Task SaveAsync(Func<DictationSettings, DictationSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Dictation = change(s.Dictation) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save dictation settings");
        }

        await _dispatcher.InvokeAsync(Sync);
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not open {Folder}", folder);
        }
    }

    /// <summary>One editable row; the grid adds blank ones itself.</summary>
    public sealed class WordRow
    {
        public string? Phrase { get; set; }

        public string? SoundsLike { get; set; }

        public bool Learned { get; set; }
    }
}
