using System.Windows;
using System.Windows.Controls;

namespace Clicky.App.Views;

/// <summary>A small modal that asks for one piece of text.</summary>
internal static class TextPrompt
{
    /// <summary>The text entered, or null when cancelled.</summary>
    public static string? Show(Window? owner, string title, string message, string initial, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = multiline,
            AcceptsTab = false,
            TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.Wrap,
            Height = multiline ? 260 : double.NaN,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            FontFamily = multiline ? new System.Windows.Media.FontFamily("Consolas") : SystemFonts.MessageFontFamily,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = multiline ? 620 : 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "OK", IsDefault = !multiline, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
        ok.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true ? box.Text : null;
    }
}
