using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Clicky.Core;
using Clicky.Core.Clickys;
using Clicky.Core.Connectors;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Clicky.App.Views.Home;

/// <summary>
/// Home's rich preview of a Clicky's reply in WebView2: rendered Markdown, or the HTML document the
/// reply carries. Locked down: scripts, dev tools, host objects and web messages are off, every
/// network request is refused, and links open in the user's browser instead. Falls back to plain
/// text when the WebView2 Runtime is missing.
/// </summary>
internal sealed class PreviewPane : ContentControl, IDisposable
{
    private static Task<CoreWebView2Environment>? s_environment;

    private readonly ClickyPaths _paths;
    private readonly IBrowserLauncher _browser;
    private readonly ILogger<PreviewPane> _logger;
    private readonly WebView2 _web = new();
    private readonly TextBox _fallback = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(12),
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
    };

    private Task? _ready;
    private string _page = ClickyPreview.MarkdownPage("*Select a reply to preview it here.*", "Preview");
    private string _text = string.Empty;

    public PreviewPane(ClickyPaths paths, IBrowserLauncher browser, ILogger<PreviewPane> logger)
    {
        _paths = paths;
        _browser = browser;
        _logger = logger;
        Content = _web;
        Loaded += (_, _) => _ready ??= InitializeAsync();
    }

    /// <summary>Shows a reply. UI thread.</summary>
    public void Show(string reply, string title)
    {
        _text = reply;
        _page = ClickyPreview.PageFor(reply, title);
        Render();
    }

    public void Dispose() => _web.Dispose();

    public void Clear()
    {
        _text = string.Empty;
        _page = ClickyPreview.MarkdownPage("*Select a reply to preview it here.*", "Preview");
        Render();
    }

    // One environment per process: WebView2 requires every control sharing a user data folder to use the same options.
    private Task<CoreWebView2Environment> Environment() =>
        s_environment ??= CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: Path.Combine(_paths.LocalRoot, "webview2"));

    private async Task InitializeAsync()
    {
        try
        {
            // The default data folder is next to the exe, which is read-only once installed.
            await _web.EnsureCoreWebView2Async(await Environment());
            var core = _web.CoreWebView2;
            var settings = core.Settings;
            settings.IsScriptEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.AreDefaultContextMenusEnabled = true;

            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            Render();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "WebView2 is unavailable; Home previews show plain text");
            _fallback.Text = "Rich preview needs the Microsoft Edge WebView2 Runtime (it comes with Windows 11). Showing plain text.\n\n" + _text;
            Content = _fallback;
        }
    }

    private void Render()
    {
        if (ReferenceEquals(Content, _fallback))
        {
            _fallback.Text = _text;
            return;
        }

        if (_web.CoreWebView2 is not null)
        {
            _web.NavigateToString(_page);
        }
    }

    private static bool IsOwnPage(string uri) =>
        uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsOwnPage(e.Uri))
        {
            return;
        }

        // Links leave the preview: the page itself never navigates anywhere.
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!IsOwnPage(e.Request.Uri))
        {
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
        }
    }

    private void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var target) && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeMailto))
        {
            _logger.LogInformation("Opening a link from a Clicky preview in the browser: {Host}", target.Host);
            _browser.Open(target);
        }
    }
}
