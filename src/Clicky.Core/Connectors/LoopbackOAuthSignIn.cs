using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Connectors;

/// <summary>Opens a URL in the user's default browser.</summary>
public interface IBrowserLauncher
{
    void Open(Uri uri);
}

/// <summary>
/// The native-app OAuth redirect (RFC 8252 §7.3): a one-shot HTTP listener on
/// http://127.0.0.1:&lt;port&gt;/callback that the browser lands on after sign-in. A plain socket
/// rather than HttpListener, because http.sys wants a URL reservation for anything but
/// "localhost" prefixes and some firewalls prompt for it.
/// </summary>
public sealed class LoopbackOAuthSignIn : IOAuthSignIn, IDisposable
{
    public const string CallbackPath = "/callback";
    private const int MaxRequestBytes = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly IBrowserLauncher _browser;
    private readonly ILogger _logger;

    private LoopbackOAuthSignIn(TcpListener listener, IBrowserLauncher browser, ILogger logger)
    {
        _listener = listener;
        _browser = browser;
        _logger = logger;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{port}{CallbackPath}");
    }

    public Uri RedirectUri { get; }

    public int Port => RedirectUri.Port;

    /// <summary>
    /// Starts listening on <paramref name="preferredPort"/> (so a registered redirect URI stays
    /// valid), or on any free port when it is 0 or taken and <paramref name="requirePort"/> is false.
    /// </summary>
    /// <exception cref="SocketException">The port is taken and <paramref name="requirePort"/> is true.</exception>
    public static LoopbackOAuthSignIn Start(int preferredPort, bool requirePort, IBrowserLauncher browser, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(logger);

        if (preferredPort is > 0 and <= IPEndPoint.MaxPort)
        {
            try
            {
                return new LoopbackOAuthSignIn(Listen(preferredPort), browser, logger);
            }
            catch (SocketException ex) when (!requirePort)
            {
                logger.LogInformation("OAuth redirect port {Port} is busy ({Error}); using another", preferredPort, ex.SocketErrorCode);
            }
        }

        return new LoopbackOAuthSignIn(Listen(0), browser, logger);
    }

    public async Task<OAuthRedirect> AuthorizeAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);

        // The URL comes from the server's metadata. Only ever hand the shell a web URL, never a
        // file:, ms-settings: or other protocol a hostile server could name.
        if (!IsWebUrl(authorizationUri))
        {
            throw new InvalidOperationException($"The server asked to sign in at {authorizationUri.Scheme}: addresses, which Clicky will not open.");
        }

        _browser.Open(authorizationUri);
        _logger.LogInformation("Opened the browser for sign-in at {Host}; waiting for the redirect", authorizationUri.Host);

        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var redirect = await HandleAsync(client, cancellationToken).ConfigureAwait(false);
            if (redirect is not null)
            {
                return redirect;
            }
        }
    }

    public void Dispose() => _listener.Stop();

    internal static bool IsWebUrl(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    internal static OAuthRedirect ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            values.TryAdd(key, value);
        }

        return new OAuthRedirect(
            values.GetValueOrDefault("code"),
            values.GetValueOrDefault("state"),
            values.GetValueOrDefault("iss"),
            values.GetValueOrDefault("error"),
            values.GetValueOrDefault("error_description"));
    }

    private static TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(backlog: 4);
        return listener;
    }

    /// <summary>Answers one request. Returns the redirect for GET /callback, null for anything else (favicon, probes).</summary>
    private static async Task<OAuthRedirect?> HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        string requestLine;
        try
        {
            requestLine = await ReadRequestLineAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        // "GET /callback?code=…&state=… HTTP/1.1"
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET")
        {
            await RespondAsync(stream, 405, "Method not allowed", string.Empty, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var target = parts[1];
        var queryStart = target.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? target : target[..queryStart];
        if (path != CallbackPath)
        {
            await RespondAsync(stream, 404, "Not found", string.Empty, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var redirect = ParseQuery(queryStart < 0 ? string.Empty : target[queryStart..]);
        var ok = redirect.Error is null && !string.IsNullOrEmpty(redirect.Code);
        var body = Page(
            ok ? "Signed in" : "Sign-in failed",
            ok ? "Clicky is connected. You can close this tab." : WebUtility.HtmlEncode(redirect.ErrorDescription ?? redirect.Error ?? "The server did not send an authorization code."));
        await RespondAsync(stream, 200, "OK", body, cancellationToken).ConfigureAwait(false);
        return redirect;
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Read until the end of the headers so the browser sees a complete exchange.
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string reason, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser went away; the redirect itself was already read.
        }
    }

    private static string Page(string title, string message) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Clicky</title>" +
        "<style>body{font-family:'Segoe UI',system-ui,sans-serif;margin:15vh auto;max-width:28rem;text-align:center;color:#1f2328}" +
        "h1{font-size:1.4rem;font-weight:600}@media(prefers-color-scheme:dark){body{background:#1b1b1f;color:#e6e6e6}}</style></head>" +
        $"<body><h1>{title}</h1><p>{message}</p></body></html>";
}
