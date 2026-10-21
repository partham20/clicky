using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicky.Core;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// Settings: a sidebar of pages and the open page's sections. Pages are built the first time they
/// are opened and kept while the window is open, so a half-typed field survives switching pages.
/// Every section writes straight to settings.json and follows edits made to the file meanwhile.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly IServiceProvider _services;
    private readonly ClickyPaths _paths;
    private readonly ILogger<SettingsWindow> _logger;
    private readonly IReadOnlyList<(SettingsPage Page, IReadOnlyList<SettingsSection> Sections)> _pages;
    private readonly Dictionary<string, FrameworkElement> _built = [];
    private readonly ListBox _sidebar = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0, 8, 0, 8) };
    private readonly ScrollViewer _content = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public SettingsWindow(IServiceProvider services, IEnumerable<SettingsSection> sections, ClickyPaths paths, ILogger<SettingsWindow> logger)
    {
        _services = services;
        _paths = paths;
        _logger = logger;
        _pages = sections
            .GroupBy(s => s.Page)
            .OrderBy(g => g.Key.Order)
            .Select(g => (g.Key, (IReadOnlyList<SettingsSection>)g.OrderBy(s => s.Order).ToList()))
            .ToList();

        Title = "Clicky Settings";
        Width = 900;
        Height = 720;
        MinWidth = 640;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/clicky.ico", UriKind.Absolute));
        Content = Build();

        _sidebar.SelectionChanged += (_, _) => ShowSelected();
        if (_sidebar.Items.Count > 0)
        {
            _sidebar.SelectedIndex = 0;
        }
    }

    /// <summary>Opens a page by <see cref="SettingsPage.Id"/>; unknown ids leave the current page.</summary>
    public void ShowPage(string pageId)
    {
        foreach (ListBoxItem item in _sidebar.Items)
        {
            if (item.Tag is SettingsPage page && string.Equals(page.Id, pageId, StringComparison.OrdinalIgnoreCase))
            {
                _sidebar.SelectedItem = item;
                return;
            }
        }

        _logger.LogWarning("Settings has no page {Page}", pageId);
    }

    private Grid Build()
    {
        foreach (var (page, _) in _pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = page.Glyph, FontFamily = IconFont, FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = page.Title, VerticalAlignment = VerticalAlignment.Center });
            _sidebar.Items.Add(new ListBoxItem { Content = row, Tag = page, Padding = new Thickness(16, 8, 16, 8) });
        }

        var openFile = new Button { Content = "Open settings.json", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(16, 8, 16, 16), HorizontalAlignment = HorizontalAlignment.Left };
        openFile.ToolTip = "Every setting is in this file; edits there apply live.";
        openFile.Click += (_, _) => RevealSettingsFile();

        var sidebar = new DockPanel { Background = SystemColors.ControlBrush };
        DockPanel.SetDock(openFile, Dock.Bottom);
        sidebar.Children.Add(openFile);
        sidebar.Children.Add(_sidebar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(sidebar);
        Grid.SetColumn(_content, 1);
        grid.Children.Add(_content);
        return grid;
    }

    private void ShowSelected()
    {
        if (_sidebar.SelectedItem is not ListBoxItem { Tag: SettingsPage page })
        {
            return;
        }

        if (!_built.TryGetValue(page.Id, out var view))
        {
            view = BuildPage(page);
            _built[page.Id] = view;
        }

        _content.Content = view;
        _content.ScrollToTop();
    }

    private StackPanel BuildPage(SettingsPage page)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        foreach (var section in _pages.First(p => p.Page == page).Sections)
        {
            try
            {
                panel.Children.Add(section.Create(_services));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // One broken section must not take the rest of Settings with it.
                _logger.LogError(ex, "Settings: could not build a section of {Page}", page.Id);
                panel.Children.Add(new TextBlock { Text = $"Part of this page couldn't load: {ex.Message}", Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });
            }
        }

        return panel;
    }

    private void RevealSettingsFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_paths.SettingsFile}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not show {File}", _paths.SettingsFile);
        }
    }
}
