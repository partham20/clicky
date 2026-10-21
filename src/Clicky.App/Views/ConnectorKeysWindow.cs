using System.Windows;
using System.Windows.Controls;
using Clicky.Core.Connectors;
using Clicky.Core.Settings;

namespace Clicky.App.Views;

/// <summary>Enter one account's secret environment values and secret headers. Empty boxes keep what is saved.</summary>
internal sealed class ConnectorKeysWindow : Window
{
    private readonly ConnectorConfig _config;
    private readonly ConnectorAccount _account;
    private readonly ConnectorSecrets _secrets;
    private readonly List<(string Name, PasswordBox Box, CheckBox Clear)> _fields = [];

    public ConnectorKeysWindow(ConnectorConfig config, ConnectorAccount account, ConnectorSecrets secrets)
    {
        _config = config;
        _account = account;
        _secrets = secrets;
        Title = config.Accounts.Count > 1 ? $"{config.Name} keys: {account.Label}" : $"{config.Name} keys";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = Build();
        Loaded += (_, _) => _fields.FirstOrDefault().Box?.Focus();
    }

    private StackPanel Build()
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Saved in Windows Credential Manager, never in settings.json. Leave a box empty to keep the saved value.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        foreach (var variable in _config.SecretEnvironment)
        {
            AddField(variable, ConnectorSecrets.EnvironmentName(_config.Id, _account.Id, variable), panel);
        }

        foreach (var header in _config.SecretHeaders)
        {
            AddField(header == "Authorization" ? "API key or token (Authorization header)" : $"{header} header", ConnectorSecrets.HeaderName(_config.Id, _account.Id, header), panel);
        }

        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        return panel;
    }

    private void AddField(string label, string secretName, StackPanel panel)
    {
        var saved = _secrets.Has(secretName);
        panel.Children.Add(new TextBlock { Text = label + (saved ? "  (saved)" : string.Empty), Margin = new Thickness(0, 10, 0, 2) });
        var box = new PasswordBox();
        var clear = new CheckBox { Content = "Remove the saved value", Visibility = saved ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        panel.Children.Add(box);
        panel.Children.Add(clear);
        _fields.Add((secretName, box, clear));
    }

    private void Save()
    {
        foreach (var (name, box, clear) in _fields)
        {
            if (box.Password.Length > 0)
            {
                var value = box.Password.Trim();

                // An API key pasted on its own goes out as a bearer token.
                if (name.EndsWith("/header/Authorization", StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal))
                {
                    value = "Bearer " + value;
                }

                _secrets.Write(name, value);
            }
            else if (clear.IsChecked == true)
            {
                _secrets.Write(name, null);
            }
        }

        DialogResult = true;
    }
}
