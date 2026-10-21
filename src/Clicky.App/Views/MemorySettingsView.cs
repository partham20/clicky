using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Memory;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Memory section of Settings: PROFILE.md and VOLATILE.md as editable text, with save, revert,
/// forget, and undo of Clicky's last automatic change. Switches go to settings.memory.
/// </summary>
internal sealed class MemorySettingsView : ContentControl
{
    private readonly MemoryStore _store;
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<MemorySettingsView> _logger;

    private readonly CheckBox _enabled = new() { Content = "Remember things about me and use them in answers" };
    private readonly CheckBox _autoUpdate = new() { Content = "Update memory after conversations (otherwise only my own edits change it)" };
    private readonly FileEditor _profile;
    private readonly FileEditor _volatile;
    private readonly Button _undo = new() { Content = "Undo Clicky's last change", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private bool _syncing;

    public MemorySettingsView(MemoryStore store, ISettingsStore settings, Dispatcher dispatcher, ILogger<MemorySettingsView> logger)
    {
        _store = store;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
        _profile = new FileEditor(this, MemoryFile.Profile, "Profile: who you are, habits, and how you like answers");
        _volatile = new FileEditor(this, MemoryFile.Volatile, "Current work: what you're in the middle of");

        _enabled.Click += (_, _) => SaveSettings(m => m with { Enabled = _enabled.IsChecked == true });
        _autoUpdate.Click += (_, _) => SaveSettings(m => m with { AutoUpdate = _autoUpdate.IsChecked == true });
        _undo.Click += async (_, _) => await RunAsync(async () =>
        {
            var undone = await _store.UndoLastUpdateAsync();
            _status.Text = undone ? "Put back the version from before Clicky's last change." : "Nothing to undo: the file has changed since.";
        });

        Content = Build();
        Loaded += async (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            _store.Changed += OnStoreChanged;
            SyncSettings();
            await ReloadAsync(force: true);
        };
        Unloaded += (_, _) =>
        {
            _settings.Changed -= OnSettingsChanged;
            _store.Changed -= OnStoreChanged;
        };
    }

    private StackPanel Build()
    {
        var openFolder = new Button { Content = "Open memory folder", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
        openFolder.Click += (_, _) => OpenFolder();
        var forgetAll = new Button { Content = "Forget everything", Padding = new Thickness(10, 2, 10, 2) };
        forgetAll.Click += async (_, _) => await ForgetAsync(null);

        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Memory", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "Clicky reads these notes before every answer and keeps them up to date after conversations, " +
                   "so you don't have to repeat yourself. Tell it \"you talk too much\" and it remembers. " +
                   "Never stores passwords or keys. One fact per \"- \" line under a \"## \" heading.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(_enabled);
        panel.Children.Add(_autoUpdate);
        panel.Children.Add(_profile.Root);
        panel.Children.Add(_volatile.Root);
        panel.Children.Add(new Border { Height = 10 });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_undo);
        row.Children.Add(openFolder);
        row.Children.Add(forgetAll);
        panel.Children.Add(row);
        panel.Children.Add(_status);
        return panel;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Memory != e.Current.Memory)
        {
            _dispatcher.InvokeAsync(SyncSettings);
        }
    }

    private void OnStoreChanged(object? sender, MemoryChangedEventArgs e) =>
        _dispatcher.InvokeAsync(async () =>
        {
            await ReloadAsync(force: false);
            if (e.Source == MemoryChangeSource.Updater)
            {
                _status.Text = $"Clicky updated memory at {DateTime.Now.ToString("t", CultureInfo.CurrentCulture)}.";
            }
        });

    private void SyncSettings()
    {
        var memory = _settings.Current.Memory;
        _syncing = true;
        try
        {
            _enabled.IsChecked = memory.Enabled;
            _autoUpdate.IsChecked = memory.AutoUpdate;
            _autoUpdate.IsEnabled = memory.Enabled;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <param name="force">Also overwrite unsaved typing (first load, revert).</param>
    private async Task ReloadAsync(bool force)
    {
        MemorySnapshot snapshot;
        try
        {
            snapshot = await _store.ReadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read memory");
            _status.Text = $"Couldn't read memory: {ex.Message}";
            return;
        }

        _profile.Show(snapshot.ProfileText, force);
        _volatile.Show(snapshot.VolatileText, force);
        _undo.IsEnabled = _store.CanUndo;
    }

    private async Task SaveAsync(MemoryFile file, string text) => await RunAsync(async () =>
    {
        await _store.SaveTextAsync(file, text);
        _status.Text = $"Saved {MemoryTemplates.FileName(file)}.";
    });

    private async Task ForgetAsync(MemoryFile? file)
    {
        var what = file is { } f ? MemoryTemplates.FileName(f) : "everything Clicky remembers about you";
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Forget {what}? This can't be undone.",
            "Clicky memory",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _store.ForgetAsync(file);
            await ReloadAsync(force: true);
            _status.Text = "Forgotten.";
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Memory change failed");
            _status.Text = $"Couldn't change memory: {ex.Message}";
        }

        _undo.IsEnabled = _store.CanUndo;
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.Directory}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not open {Folder}", _store.Directory);
        }
    }

    private void SaveSettings(Func<MemorySettings, MemorySettings> change)
    {
        if (_syncing)
        {
            return;
        }

        _ = SaveSettingsAsync(change);
    }

    private async Task SaveSettingsAsync(Func<MemorySettings, MemorySettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Memory = change(s.Memory) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save memory settings");
        }

        await _dispatcher.InvokeAsync(SyncSettings);
    }

    /// <summary>One file: heading with a size count, the text, and Save / Revert / Forget.</summary>
    private sealed class FileEditor
    {
        private readonly MemorySettingsView _owner;
        private readonly MemoryFile _file;
        private readonly TextBox _text = new()
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Height = 170,
        };

        private readonly TextBlock _size = new() { Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _save = new() { Content = "Save", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        private readonly Button _revert = new() { Content = "Revert", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        private string _loaded = string.Empty;

        public FileEditor(MemorySettingsView owner, MemoryFile file, string title)
        {
            _owner = owner;
            _file = file;
            var forget = new Button { Content = "Forget", Padding = new Thickness(10, 2, 10, 2) };

            _text.TextChanged += (_, _) => UpdateState();
            _save.Click += async (_, _) => await _owner.SaveAsync(_file, _text.Text);
            _revert.Click += async (_, _) => await _owner.ReloadAsync(force: true);
            forget.Click += async (_, _) => await _owner.ForgetAsync(_file);

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 4) };
            header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            header.Children.Add(_size);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            buttons.Children.Add(_save);
            buttons.Children.Add(_revert);
            buttons.Children.Add(forget);

            Root = new StackPanel { Children = { header, _text, buttons } };
        }

        public StackPanel Root { get; }

        private bool Dirty => _text.Text.ReplaceLineEndings("\n") != _loaded;

        /// <summary>Shows the file's text unless the person is mid-edit (then only <paramref name="force"/> replaces it).</summary>
        public void Show(string text, bool force)
        {
            var wasDirty = Dirty;
            _loaded = text;
            if (force || !wasDirty)
            {
                _text.Text = text.ReplaceLineEndings(Environment.NewLine);
            }

            UpdateState();
        }

        private void UpdateState()
        {
            var cap = _owner._store.Limits.CapFor(_file);
            var used = MemoryMerger.BodyLength(MemoryDocument.Parse(_text.Text));
            _size.Text = string.Create(CultureInfo.CurrentCulture, $"{used:N0} / {cap:N0} characters{(used > cap ? "; over the limit, Clicky may leave some sections out of its answers" : string.Empty)}");
            _size.Foreground = used > cap ? Brushes.DarkOrange : SystemColors.ControlTextBrush;
            _save.IsEnabled = _revert.IsEnabled = Dirty;
        }
    }
}
