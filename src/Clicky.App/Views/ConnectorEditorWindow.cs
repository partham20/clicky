using System.Windows;
using System.Windows.Controls;
using Clicky.Core.Connectors;
using Clicky.Core.Settings;

namespace Clicky.App.Views;

/// <summary>Add or edit one connector: a local program (stdio) or a remote server (URL).</summary>
internal sealed class ConnectorEditorWindow : Window
{
    private const string ApiKeyHeader = "Authorization";

    private readonly ConnectorConfig? _original;
    private readonly ConnectorSecrets _secrets;

    private readonly TextBox _name = Field();
    private readonly RadioButton _stdio = new() { Content = "Local program (runs on this PC)", Margin = new Thickness(0, 2, 16, 2) };
    private readonly RadioButton _http = new() { Content = "Remote server (URL)", Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _command = Field();
    private readonly TextBox _arguments = Field();
    private readonly TextBox _workingDirectory = Field();
    private readonly TextBox _environment = Multiline();
    private readonly TextBox _secretEnvironment = Field();
    private readonly TextBox _url = Field();
    private readonly RadioButton _oauth = new() { Content = "Sign in through the browser if the server asks (OAuth)", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _apiKey = new() { Content = "API key or token (asked for next, kept in Credential Manager)", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _noAuth = new() { Content = "None", Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _headers = Multiline();
    private readonly TextBox _clientId = Field();
    private readonly PasswordBox _clientSecret = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox _scopes = Field();
    private readonly TextBox _redirectPort = Field();
    private readonly TextBox _timeout = Field();
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _stdioPanel = new();
    private readonly StackPanel _httpPanel = new();

    public ConnectorEditorWindow(ConnectorConfig? original, ConnectorSecrets secrets)
    {
        _original = original;
        _secrets = secrets;
        Title = original is null ? "Add connector" : $"Edit {original.Name}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _stdio.Checked += (_, _) => ShowTransport();
        _http.Checked += (_, _) => ShowTransport();
        Content = new ScrollViewer { Content = Build(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Load(original ?? new ConnectorConfig());
        Loaded += (_, _) => _name.Focus();
    }

    /// <summary>The edited connector, valid when ShowDialog returned true. Accounts and tool filters are the caller's.</summary>
    public ConnectorConfig? Result { get; private set; }

    /// <summary>Stores the OAuth client secret typed here, if any, under the connector's final id.</summary>
    public void SaveClientSecret(string connectorId)
    {
        if (_clientSecret.Password.Length > 0)
        {
            _secrets.Write(ConnectorSecrets.ClientSecretName(connectorId), _clientSecret.Password);
        }
        else if (string.IsNullOrWhiteSpace(_clientId.Text))
        {
            _secrets.Write(ConnectorSecrets.ClientSecretName(connectorId), null);
        }
    }

    private static TextBox Field() => new() { Margin = new Thickness(0, 0, 0, 4) };

    private static TextBox Multiline() => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        MinLines = 2,
        MaxLines = 6,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Margin = new Thickness(0, 0, 0, 4),
    };

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 8, 0, 2) };

    private static TextBlock Hint(string text) => new() { Text = text, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };

    private static string Lines(IReadOnlyDictionary<string, string> map) => string.Join(Environment.NewLine, map.Select(p => $"{p.Key}={p.Value}"));

    private static Dictionary<string, string> ParseLines(string text, char separator)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = line.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0)
            {
                map[line[..at].Trim()] = line[(at + 1)..].Trim();
            }
        }

        return map;
    }

    private static List<string> Names(string text) =>
        [.. text.Split([',', ' ', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    private StackPanel Build()
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Label("Name"));
        panel.Children.Add(_name);
        var kind = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        kind.Children.Add(_stdio);
        kind.Children.Add(_http);
        panel.Children.Add(kind);

        _stdioPanel.Children.Add(Label("Command"));
        _stdioPanel.Children.Add(Hint("npx, uvx, docker, or a full path. Node or Python installed after Clicky started is found too."));
        _stdioPanel.Children.Add(_command);
        _stdioPanel.Children.Add(Label("Arguments"));
        _stdioPanel.Children.Add(Hint("As on a command line; quote paths with spaces. Example: -y @modelcontextprotocol/server-filesystem \"C:\\Users\\me\\Documents\""));
        _stdioPanel.Children.Add(_arguments);
        _stdioPanel.Children.Add(Label("Working folder (optional)"));
        _stdioPanel.Children.Add(_workingDirectory);
        _stdioPanel.Children.Add(Label("Environment variables (optional, one NAME=value per line)"));
        _stdioPanel.Children.Add(_environment);
        _stdioPanel.Children.Add(Label("Secret variables (names only; values are asked for next, per account)"));
        _stdioPanel.Children.Add(Hint("For example GITHUB_PERSONAL_ACCESS_TOKEN. Separate several with commas."));
        _stdioPanel.Children.Add(_secretEnvironment);
        panel.Children.Add(_stdioPanel);

        _httpPanel.Children.Add(Label("Server URL"));
        _httpPanel.Children.Add(Hint("The MCP endpoint, for example https://mcp.linear.app/mcp. Streamable HTTP, with the older SSE transport as a fallback."));
        _httpPanel.Children.Add(_url);
        _httpPanel.Children.Add(Label("Sign-in"));
        _httpPanel.Children.Add(_oauth);
        _httpPanel.Children.Add(_apiKey);
        _httpPanel.Children.Add(_noAuth);
        _httpPanel.Children.Add(Label("Extra headers (optional, one Name: value per line; not secret)"));
        _httpPanel.Children.Add(_headers);

        var advanced = new StackPanel();
        advanced.Children.Add(Hint("Only for servers without automatic client registration: use a client you registered with the provider, with redirect URI http://127.0.0.1:<port>/callback."));
        advanced.Children.Add(Label("OAuth client ID"));
        advanced.Children.Add(_clientId);
        advanced.Children.Add(Label("OAuth client secret (kept in Credential Manager; leave empty to keep the saved one)"));
        advanced.Children.Add(_clientSecret);
        advanced.Children.Add(Label("Scopes (when the server does not say which to ask for)"));
        advanced.Children.Add(_scopes);
        advanced.Children.Add(Label("Redirect port (0 picks a free one)"));
        advanced.Children.Add(_redirectPort);
        _httpPanel.Children.Add(new Expander { Header = "Advanced sign-in", Content = advanced, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(_httpPanel);

        panel.Children.Add(Label("Tool call time limit (seconds)"));
        panel.Children.Add(_timeout);
        panel.Children.Add(_error);

        var save = new Button { Content = _original is null ? "Add" : "Save", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        return panel;
    }

    private void Load(ConnectorConfig config)
    {
        _name.Text = config.Name;
        (config.Transport == ConnectorTransport.Http ? _http : _stdio).IsChecked = true;
        _command.Text = config.Command;
        _arguments.Text = CommandLine.Join(config.Arguments);
        _workingDirectory.Text = config.WorkingDirectory;
        _environment.Text = Lines(config.Environment);
        _secretEnvironment.Text = string.Join(", ", config.SecretEnvironment);
        _url.Text = config.Url;
        var usesApiKey = config.SecretHeaders.Contains(ApiKeyHeader);
        (usesApiKey ? _apiKey : config.Auth == ConnectorAuth.OAuth || _original is null ? _oauth : _noAuth).IsChecked = true;
        _headers.Text = string.Join(Environment.NewLine, config.Headers.Select(p => $"{p.Key}: {p.Value}"));
        _clientId.Text = config.OAuthClientId;
        _scopes.Text = string.Join(' ', config.OAuthScopes);
        _redirectPort.Text = config.OAuthRedirectPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _timeout.Text = config.ToolTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowTransport();
    }

    private void ShowTransport()
    {
        _stdioPanel.Visibility = _stdio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _httpPanel.Visibility = _http.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save()
    {
        var http = _http.IsChecked == true;
        if (http && (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            _error.Text = "Enter the server's http(s) address.";
            return;
        }

        if (!http && string.IsNullOrWhiteSpace(_command.Text))
        {
            _error.Text = "Enter the program to run.";
            return;
        }

        if (!int.TryParse(_timeout.Text, out var timeout))
        {
            timeout = ConnectorConfig.DefaultToolTimeoutSeconds;
        }

        _ = int.TryParse(_redirectPort.Text, out var port);
        var original = _original ?? new ConnectorConfig();

        // Keep secret header names other than the API key one (e.g. imported X-API-Key).
        var secretHeaders = original.SecretHeaders.Where(h => h != ApiKeyHeader).ToList();
        if (_apiKey.IsChecked == true)
        {
            secretHeaders.Insert(0, ApiKeyHeader);
        }

        var name = string.IsNullOrWhiteSpace(_name.Text) ? (http ? new Uri(_url.Text.Trim()).Host : Path.GetFileNameWithoutExtension(_command.Text.Trim())) : _name.Text.Trim();
        Result = original with
        {
            Name = name,
            Transport = http ? ConnectorTransport.Http : ConnectorTransport.Stdio,
            Command = http ? string.Empty : _command.Text.Trim(),
            Arguments = http ? [] : CommandLine.Split(_arguments.Text),
            WorkingDirectory = http ? string.Empty : _workingDirectory.Text.Trim(),
            Environment = http ? new Dictionary<string, string>() : ParseLines(_environment.Text, '='),
            SecretEnvironment = http ? [] : Names(_secretEnvironment.Text),
            Url = http ? _url.Text.Trim() : string.Empty,
            Headers = http ? ParseLines(_headers.Text, ':') : new Dictionary<string, string>(),
            SecretHeaders = http ? secretHeaders : [],
            Auth = http && _oauth.IsChecked == true ? ConnectorAuth.OAuth : ConnectorAuth.None,
            OAuthClientId = http ? _clientId.Text.Trim() : string.Empty,
            OAuthScopes = http ? Names(_scopes.Text) : [],
            OAuthRedirectPort = http ? port : 0,
            ToolTimeoutSeconds = timeout,
        };
        DialogResult = true;
    }
}
