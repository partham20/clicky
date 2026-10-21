using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Clicky.App.Hosting;
using Clicky.Core.Diagnostics;
using Clicky.Core.Settings;
using Clicky.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// Report a bug: the person describes what happened, Clicky bundles that with system info, logs and
/// settings into a zip they can look through, then opens a GitHub issue with the text filled in.
/// Nothing is sent anywhere by Clicky itself.
/// </summary>
internal sealed class BugReportWindow : Window
{
    private readonly BugReportWriter _writer;
    private readonly UpdateService _updates;
    private readonly ISettingsStore _settings;
    private readonly ILogger<BugReportWindow> _logger;

    private readonly TextBox _description = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 150,
    };

    private readonly CheckBox _includeLogs = new() { Content = "Include Clicky's logs from the last 7 days", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _includeSettings = new() { Content = "Include settings.json (preferences only; API keys are never in it)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _create = new() { Content = "Create report", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _openIssue = new() { Content = "Open GitHub issue", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Button _showZip = new() { Content = "Show the zip", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Button _close = new() { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 3, 14, 3) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    private BugReport? _report;

    public BugReportWindow(BugReportWriter writer, UpdateService updates, ISettingsStore settings, ILogger<BugReportWindow> logger)
    {
        _writer = writer;
        _updates = updates;
        _settings = settings;
        _logger = logger;

        Title = "Report a bug";
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/clicky.ico", UriKind.Absolute));
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build();

        _create.Click += async (_, _) => await CreateAsync();
        _openIssue.Click += (_, _) => OpenIssue();
        _showZip.Click += (_, _) => ShowZip();
        Loaded += (_, _) => _description.Focus();
    }

    private StackPanel Build()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "What happened?", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "What were you doing, what did Clicky do, and what did you expect? About when did it happen?",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(_description);
        panel.Children.Add(_includeLogs);
        panel.Children.Add(_includeSettings);
        panel.Children.Add(new TextBlock
        {
            Text = "Clicky saves a zip in your Clicky folder and sends nothing itself. Look through it, then attach it to the " +
                   "GitHub issue Clicky opens for you. Keys and your Windows user name are removed; logs can still mention " +
                   "app names and window titles. Memory, screenshots and recordings are never included.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 10, 0, 0),
        });
        panel.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_create);
        buttons.Children.Add(_openIssue);
        buttons.Children.Add(_showZip);
        buttons.Children.Add(_close);
        panel.Children.Add(buttons);
        return panel;
    }

    private async Task CreateAsync()
    {
        _create.IsEnabled = false;
        _status.Text = "Creating the report…";
        try
        {
            var request = new BugReportRequest(_description.Text, _includeLogs.IsChecked == true, _includeSettings.IsChecked == true)
            {
                SystemInfo = SystemInfo.Collect(_updates.Status, _settings.Current),
                ExtraFiles = VelopackUpdateBackend.InstallerLogPath is { } installerLog ? [installerLog] : [],
            };
            _report = await Task.Run(() => _writer.WriteAsync(request));
            _status.Text = $"Saved {Path.GetFileName(_report.ZipPath)}{(_report.LogFiles > 0 ? $" with {_report.LogFiles} log file(s)" : string.Empty)}. " +
                           "Open the GitHub issue, then drag the zip into it.";
            _create.Content = "Create again";
            _openIssue.Visibility = _showZip.Visibility = Visibility.Visible;
            _openIssue.IsDefault = true;
            _close.Content = "Close";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not create a bug report");
            _status.Text = $"Couldn't create the report: {ex.Message}";
        }
        finally
        {
            _create.IsEnabled = true;
        }
    }

    private void OpenIssue()
    {
        if (_report is null)
        {
            return;
        }

        ShowZip();
        Launch(GitHubIssueLink.Create(VelopackUpdateBackend.DefaultRepository, _report, _description.Text).AbsoluteUri, null);
    }

    private void ShowZip()
    {
        if (_report is not null)
        {
            Launch("explorer.exe", $"/select,\"{_report.ZipPath}\"");
        }
    }

    private void Launch(string target, string? arguments)
    {
        try
        {
            var start = arguments is null ? new ProcessStartInfo(target) : new ProcessStartInfo(target, arguments);
            start.UseShellExecute = true;
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not open {Target}", target);
            _status.Text = $"Couldn't open it: {ex.Message}";
        }
    }
}
