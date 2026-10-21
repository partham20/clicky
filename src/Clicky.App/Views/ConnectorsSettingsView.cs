using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Connectors;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Connectors section of Settings: MCP servers Claude can use, their accounts, sign-in and
/// keys, live connection state, and which of their tools Claude is offered. Configuration goes to
/// settings.json; keys and sign-ins go to Credential Manager.
/// </summary>
internal sealed class ConnectorsSettingsView : ContentControl
{
    private static readonly Brush Green = Frozen(0x2E, 0xA0, 0x43);
    private static readonly Brush Amber = Frozen(0xD2, 0x99, 0x22);
    private static readonly Brush Red = Frozen(0xCF, 0x22, 0x2E);
    private static readonly Brush Blue = Frozen(0x2F, 0x81, 0xF7);
    private static readonly Brush Gray = Frozen(0x8C, 0x95, 0x9F);

    private readonly ConnectorHub _hub;
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ConnectorsSettingsView> _logger;
    private readonly DispatcherTimer _refresh;
    private readonly StackPanel _cards = new();
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<(string, string)> _signingIn = [];

    public ConnectorsSettingsView(ConnectorHub hub, ISettingsStore settings, Dispatcher dispatcher, ILogger<ConnectorsSettingsView> logger)
    {
        _hub = hub;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        // Status events come in bursts (several accounts connecting at once); redraw at most 5x a second.
        _refresh = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Rebuild();
        };

        Content = Build();
        Loaded += (_, _) =>
        {
            _hub.StatusChanged += OnChanged;
            _settings.Changed += OnSettingsChanged;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            _hub.StatusChanged -= OnChanged;
            _settings.Changed -= OnSettingsChanged;
            _refresh.Stop();
        };
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Button SmallButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += click;
        return button;
    }

    private static (Brush Color, string Text) Describe(ConnectionStatus status) => status.State switch
    {
        ConnectionState.Connected => (Green, status.Tools.Count == 1 ? "Connected · 1 tool" : $"Connected · {status.Tools.Count} tools"),
        ConnectionState.Connecting => (Amber, status.Message ?? "Connecting…"),
        ConnectionState.SignInRequired => (Blue, status.Message ?? "Sign in to connect"),
        ConnectionState.NeedsSecrets => (Blue, "Needs a key: " + (status.Message ?? string.Empty)),
        ConnectionState.Failed when status.RetryAt is { } at => (Red, $"{status.Message} Retrying at {at.ToLocalTime():HH:mm:ss}."),
        ConnectionState.Failed => (Red, status.Message ?? "Could not connect."),
        _ => (Gray, "Off"),
    };

    private static string Summary(ConnectorConfig config) => config.Transport == ConnectorTransport.Http
        ? config.Url
        : $"{config.Command} {CommandLine.Join(config.Arguments)}".Trim();

    private StackPanel Build()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Connectors", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "Let Claude use other apps through MCP servers. Local servers run on this PC; remote ones sign in through your browser. " +
                   "Keys and sign-ins stay in Windows Credential Manager. Claude asks before using a tool that can change things.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(_cards);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Content = "Add connector…", Padding = new Thickness(10, 2, 10, 2) };
        add.Click += (_, _) => _ = AddAsync();
        buttons.Children.Add(add);
        buttons.Children.Add(SmallButton("Paste JSON…", (_, _) => _ = PasteJsonAsync()));
        buttons.Children.Add(SmallButton("Import from Claude Desktop", (_, _) => _ = ImportClaudeDesktopAsync()));
        buttons.Children.Add(_status);
        panel.Children.Add(buttons);
        return panel;
    }

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(ScheduleRefresh);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Connectors != e.Current.Connectors)
        {
            _dispatcher.InvokeAsync(ScheduleRefresh);
        }
    }

    private void ScheduleRefresh()
    {
        if (!_refresh.IsEnabled)
        {
            _refresh.Start();
        }
    }

    private void Rebuild()
    {
        _cards.Children.Clear();
        var connectors = _hub.Connectors;
        if (connectors.Count == 0)
        {
            _cards.Children.Add(new TextBlock
            {
                Text = "No connectors yet. Add one, or paste the \"mcpServers\" JSON from a server's README.",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var config in connectors)
        {
            _cards.Children.Add(Card(config));
        }
    }

    private Border Card(ConnectorConfig config)
    {
        var body = new StackPanel();

        var enabled = new CheckBox { IsChecked = config.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Connect this server" };
        enabled.Click += (_, _) => Update(config.Id, c => c with { Enabled = enabled.IsChecked == true });
        var header = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(SmallButton("Edit…", (_, _) => _ = EditAsync(config)));
        actions.Children.Add(SmallButton("Remove", (_, _) => _ = RemoveAsync(config)));
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(enabled);
        title.Children.Add(new TextBlock { Text = config.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock
        {
            Text = Summary(config),
            Opacity = 0.6,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
            ToolTip = Summary(config),
        });
        header.Children.Add(title);
        body.Children.Add(header);

        if (config.Enabled)
        {
            var showAccounts = config.HasCredentials || config.Accounts.Count > 1;
            foreach (var account in config.Accounts)
            {
                body.Children.Add(AccountRow(config, account, showAccounts));
            }

            if (config.HasCredentials)
            {
                var addAccount = new Button
                {
                    Content = "Add account…",
                    Padding = new Thickness(8, 1, 8, 1),
                    Margin = new Thickness(22, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                addAccount.Click += (_, _) => _ = AddAccountAsync(config);
                body.Children.Add(addAccount);
            }

            if (ToolsExpander(config) is { } tools)
            {
                body.Children.Add(tools);
            }
        }

        return new Border
        {
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body,
        };
    }

    private DockPanel AccountRow(ConnectorConfig config, ConnectorAccount account, bool showAccount)
    {
        var status = account.Enabled ? _hub.GetStatus(config.Id, account.Id) : ConnectionStatus.Off(config.Id, account.Id);
        var (color, text) = Describe(status);
        var row = new DockPanel { Margin = new Thickness(22, 6, 0, 0), LastChildFill = true };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (account.Enabled)
        {
            if (config.Auth == ConnectorAuth.OAuth)
            {
                if (_signingIn.Contains((config.Id, account.Id)))
                {
                    actions.Children.Add(new TextBlock { Text = "Waiting for the browser…", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                }
                else if (status.State == ConnectionState.SignInRequired)
                {
                    actions.Children.Add(SmallButton("Sign in…", (_, _) => _ = SignInAsync(config, account)));
                }
                else if (new OAuthTokenStore(_hub.Secrets, config.Id, account.Id).HasTokens)
                {
                    actions.Children.Add(SmallButton("Sign out", (_, _) => _ = SignOutAsync(config, account)));
                }
            }

            if (config.SecretEnvironment.Count + config.SecretHeaders.Count > 0)
            {
                actions.Children.Add(SmallButton("Keys…", (_, _) => _ = EditKeysAsync(config, account)));
            }

            if (status.State is ConnectionState.Failed or ConnectionState.Connected)
            {
                actions.Children.Add(SmallButton(status.State == ConnectionState.Failed ? "Retry" : "Reconnect", (_, _) => _ = _hub.ReconnectAsync(config.Id, account.Id)));
            }
        }

        if (showAccount && config.Accounts.Count > 1)
        {
            actions.Children.Add(SmallButton("Remove", (_, _) => _ = RemoveAccountAsync(config, account)));
        }

        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (showAccount)
        {
            var on = new CheckBox { IsChecked = account.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Use this account" };
            on.Click += (_, _) => Update(config.Id, c => c with { Accounts = [.. c.Accounts.Select(a => a.Id == account.Id ? a with { Enabled = on.IsChecked == true } : a)] });
            left.Children.Add(on);
            left.Children.Add(new TextBlock { Text = account.Label, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        }

        left.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        left.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            ToolTip = status.Server is null ? null : $"{status.Server}\n{status.Message}".Trim(),
        });
        row.Children.Add(left);
        return row;
    }

    private Expander? ToolsExpander(ConnectorConfig config)
    {
        // Union over connected accounts: the same server normally offers the same tools to each.
        var tools = config.Accounts
            .Select(a => _hub.GetStatus(config.Id, a.Id))
            .Where(s => s.State == ConnectionState.Connected)
            .SelectMany(s => s.Tools)
            .DistinctBy(t => t.Name)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tools.Count == 0)
        {
            return null;
        }

        var disabled = config.DisabledTools.ToHashSet(StringComparer.Ordinal);
        var list = new StackPanel { Margin = new Thickness(4, 4, 0, 0) };
        foreach (var tool in tools)
        {
            var box = new CheckBox
            {
                Content = tool.ReadOnlyHint == true ? tool.Name : $"{tool.Name}  (can change things; Claude asks first)",
                IsChecked = !disabled.Contains(tool.Name),
                ToolTip = tool.Description,
                Margin = new Thickness(0, 1, 0, 1),
            };
            box.Click += (_, _) => Update(config.Id, c => c with
            {
                DisabledTools = box.IsChecked == true ? [.. c.DisabledTools.Where(t => t != tool.Name)] : [.. c.DisabledTools, tool.Name],
            });
            list.Children.Add(box);
        }

        var offered = tools.Count(t => !disabled.Contains(t.Name));
        var expander = new Expander
        {
            Header = offered == tools.Count ? $"Tools ({tools.Count})" : $"Tools ({offered} of {tools.Count} offered to Claude)",
            Content = list,
            Margin = new Thickness(18, 6, 0, 0),
            IsExpanded = _expanded.Contains(config.Id),
        };
        expander.Expanded += (_, _) => _expanded.Add(config.Id);
        expander.Collapsed += (_, _) => _expanded.Remove(config.Id);
        return expander;
    }

    private void Update(string connectorId, Func<ConnectorConfig, ConnectorConfig> change) => _ = RunAsync(() => _hub.UpdateConnectorAsync(connectorId, change));

    private async Task AddAsync()
    {
        var editor = new ConnectorEditorWindow(null, _hub.Secrets) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Result is not { } config)
        {
            return;
        }

        var id = _hub.NewConnectorId(config.Name);
        editor.SaveClientSecret(id);
        await RunAsync(() => _hub.SaveConnectorAsync(config with { Id = id }));
        if (config.SecretEnvironment.Count + config.SecretHeaders.Count > 0 && _hub.Connectors.FirstOrDefault(c => c.Id == id) is { } saved)
        {
            await EditKeysAsync(saved, saved.Accounts[0]);
        }
        else if (config.Auth == ConnectorAuth.OAuth)
        {
            _status.Text = "Saved. If the server needs a sign-in, a Sign in button appears in a moment.";
        }
    }

    private async Task EditAsync(ConnectorConfig config)
    {
        var editor = new ConnectorEditorWindow(config, _hub.Secrets) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Result is not { } changed)
        {
            return;
        }

        editor.SaveClientSecret(config.Id);
        await RunAsync(() => _hub.SaveConnectorAsync(changed with { Id = config.Id, Accounts = config.Accounts, DisabledTools = config.DisabledTools, Enabled = config.Enabled }));
        if (_hub.Connectors.FirstOrDefault(c => c.Id == config.Id) is { } saved
            && saved.EnabledAccounts.FirstOrDefault(a => _hub.Secrets.MissingFor(saved, a.Id).Count > 0) is { } needsKeys)
        {
            await EditKeysAsync(saved, needsKeys);
        }
    }

    private async Task RemoveAsync(ConnectorConfig config)
    {
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Remove {config.Name}? Its keys and sign-ins are deleted from Credential Manager.",
            "Remove connector",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            await RunAsync(() => _hub.RemoveConnectorAsync(config.Id));
        }
    }

    private async Task AddAccountAsync(ConnectorConfig config)
    {
        var label = TextPrompt.Show(Window.GetWindow(this), "Add account", $"A name for the new {config.Name} account, like Work or Personal:", string.Empty);
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        string? id = null;
        await RunAsync(async () => id = await _hub.AddAccountAsync(config.Id, label));
        if (id is not null && config.SecretEnvironment.Count + config.SecretHeaders.Count > 0 && _hub.Connectors.FirstOrDefault(c => c.Id == config.Id) is { } saved && saved.FindAccount(id) is { } account)
        {
            await EditKeysAsync(saved, account);
        }
    }

    private async Task RemoveAccountAsync(ConnectorConfig config, ConnectorAccount account)
    {
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Remove the {account.Label} account from {config.Name}? Its keys and sign-in are deleted.",
            "Remove account",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            await RunAsync(() => _hub.RemoveAccountAsync(config.Id, account.Id));
        }
    }

    private async Task EditKeysAsync(ConnectorConfig config, ConnectorAccount account)
    {
        var dialog = new ConnectorKeysWindow(config, account, _hub.Secrets) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await RunAsync(() => _hub.ReconnectAsync(config.Id, account.Id));
        }
    }

    private async Task SignInAsync(ConnectorConfig config, ConnectorAccount account)
    {
        _signingIn.Add((config.Id, account.Id));
        Rebuild();
        try
        {
            var status = await _hub.SignInAsync(config.Id, account.Id);
            _status.Text = status.State == ConnectionState.Connected || status.State == ConnectionState.Connecting
                ? $"Signed in to {config.Name}."
                : status.Message ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Sign-in to {Connector} failed", config.Id);
            _status.Text = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            _signingIn.Remove((config.Id, account.Id));
            Rebuild();
        }
    }

    private Task SignOutAsync(ConnectorConfig config, ConnectorAccount account) => RunAsync(() => _hub.SignOutAsync(config.Id, account.Id));

    private async Task PasteJsonAsync()
    {
        var json = TextPrompt.Show(
            Window.GetWindow(this),
            "Paste MCP server JSON",
            "Paste an \"mcpServers\" block, for example from a server's README or claude_desktop_config.json. Values under \"env\" are stored in Credential Manager.",
            string.Empty,
            multiline: true);
        if (!string.IsNullOrWhiteSpace(json))
        {
            await ImportAsync(json);
        }
    }

    private async Task ImportClaudeDesktopAsync()
    {
        var path = McpServersImport.ClaudeDesktopConfigPath;
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"No Claude Desktop config at {path}.";
            return;
        }

        await ImportAsync(json);
    }

    private async Task ImportAsync(string json)
    {
        var servers = McpServersImport.Parse(json, out var problems);
        var existing = _hub.Connectors.Select(Summary).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = servers.Where(s => !existing.Contains(Summary(s.Config))).ToList();
        var skipped = servers.Count - fresh.Count;
        if (fresh.Count > 0)
        {
            await RunAsync(() => _hub.ImportAsync(fresh));
        }

        var notes = new List<string>();
        if (fresh.Count > 0)
        {
            notes.Add($"Added {string.Join(", ", fresh.Select(s => s.Config.Name))}.");
        }

        if (skipped > 0)
        {
            notes.Add($"{skipped} already here.");
        }

        notes.AddRange(problems);
        _status.Text = string.Join(' ', notes);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Connector settings change failed");
            _status.Text = ex.Message;
        }
    }
}
