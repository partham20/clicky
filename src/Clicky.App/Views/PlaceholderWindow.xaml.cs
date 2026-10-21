using System.Windows;

namespace Clicky.App.Views;

/// <summary>Stand-in for Home (M14) so the tray menu is exercisable before it lands.</summary>
public partial class PlaceholderWindow : Window
{
    private readonly Action? _action;

    public PlaceholderWindow(string title, string body, string? actionText = null, Action? action = null, UIElement? extra = null)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Body.Text = body;
        _action = action;
        Extra.Content = extra;
        if (extra is not null)
        {
            Height += 120;
        }

        if (actionText is not null && action is not null)
        {
            ActionButton.Content = actionText;
            ActionButton.Visibility = Visibility.Visible;
        }
    }

    private void OnActionClick(object sender, RoutedEventArgs e) => _action?.Invoke();
}
