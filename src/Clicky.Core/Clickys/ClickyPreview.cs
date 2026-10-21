using System.Net;
using System.Text.RegularExpressions;
using Markdig;

namespace Clicky.Core.Clickys;

/// <summary>
/// Turns a Clicky's reply into the page Home's preview shows: Markdown rendered with raw HTML
/// escaped, or, when the reply carries an ```html document, that document. Every page gets a
/// Content-Security-Policy that blocks scripts and anything loaded from the network (images may
/// be inline data: URIs), so a reply can't phone home or run code; the preview also runs with
/// scripts off.
/// </summary>
public static partial class ClickyPreview
{
    /// <summary>The policy every preview page carries.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'";

    /// <summary>WebView2's NavigateToString takes at most 2 MB; longer replies are cut with a note.</summary>
    public const int MaxPageChars = 1_500_000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private const string Style = """
        :root { color-scheme: light; }
        body { font: 14px/1.55 "Segoe UI", system-ui, sans-serif; color: #1f2328; background: #ffffff; margin: 0; padding: 16px 20px 32px; overflow-wrap: anywhere; }
        h1, h2, h3, h4 { line-height: 1.25; margin: 1.2em 0 0.5em; }
        h1 { font-size: 1.6em; } h2 { font-size: 1.3em; } h3 { font-size: 1.1em; }
        p, ul, ol, table, pre, blockquote { margin: 0 0 0.9em; }
        a { color: #0969da; }
        code { font: 12.5px Consolas, "Cascadia Mono", monospace; background: #f3f4f6; padding: 0.1em 0.35em; border-radius: 4px; }
        pre { background: #f6f8fa; padding: 10px 12px; border-radius: 6px; overflow: auto; }
        pre code { background: none; padding: 0; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid #d0d7de; padding: 4px 10px; text-align: left; vertical-align: top; }
        th { background: #f6f8fa; }
        blockquote { border-left: 3px solid #d0d7de; padding-left: 12px; color: #59636e; }
        img { max-width: 100%; }
        .note { color: #59636e; font-size: 12px; }
        """;

    /// <summary>A whole page for <paramref name="markdown"/>.</summary>
    public static string MarkdownPage(string markdown, string title)
    {
        var (text, cut) = Cap(markdown ?? string.Empty);
        var body = Markdown.ToHtml(text, Pipeline);
        if (cut)
        {
            body += "<p class=\"note\">The reply is too long to show here in full. Save it to files to read the rest.</p>";
        }

        return $"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{ContentSecurityPolicy}">
            <title>{WebUtility.HtmlEncode(title)}</title>
            <style>{Style}</style>
            </head><body>
            {body}
            </body></html>
            """;
    }

    /// <summary>The first ```html block of the reply, if there is one.</summary>
    public static string? HtmlDocument(string markdown)
    {
        var match = HtmlFence().Match(markdown ?? string.Empty);
        return match.Success && match.Groups["html"].Value.Trim() is { Length: > 0 } html ? html : null;
    }

    /// <summary><paramref name="html"/> with the preview's security policy put first in its head.</summary>
    public static string HtmlPage(string html)
    {
        var (text, _) = Cap(html ?? string.Empty);
        var meta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{ContentSecurityPolicy}\"><meta charset=\"utf-8\">";

        // The policy only counts from where it appears, so it goes in before anything else can load.
        var head = HeadOpen().Match(text);
        if (head.Success)
        {
            return text.Insert(head.Index + head.Length, meta);
        }

        var root = HtmlOpen().Match(text);
        return root.Success
            ? text.Insert(root.Index + root.Length, "<head>" + meta + "</head>")
            : "<!DOCTYPE html><html><head>" + meta + "</head><body>" + text + "</body></html>";
    }

    /// <summary>The page to show for a reply: its HTML document if it has one, else the rendered Markdown.</summary>
    public static string PageFor(string reply, string title) =>
        HtmlDocument(reply) is { } html ? HtmlPage(html) : MarkdownPage(reply, title);

    private static (string Text, bool Cut) Cap(string text) =>
        text.Length > MaxPageChars ? (text[..MaxPageChars], true) : (text, false);

    [GeneratedRegex(@"^[ \t]*```[ \t]*html[ \t]*\r?\n(?<html>.*?)^[ \t]*```[ \t]*\r?$", RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlFence();

    [GeneratedRegex(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpen();

    [GeneratedRegex(@"<html(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOpen();
}
