using Clicky.Core.Clickys;

namespace Clicky.Tests.Clickys;

public sealed class ClickyPreviewTests
{
    private const string CspMeta = "<meta http-equiv=\"Content-Security-Policy\" content=\"" + ClickyPreview.ContentSecurityPolicy + "\">";

    [Fact]
    public void Markdown_is_rendered_with_raw_html_escaped()
    {
        var page = ClickyPreview.MarkdownPage("# Report\n\n<script>alert('hi')</script>\n\nSome **bold** <img src=x onerror=alert(1)>.", "Mine & yours");

        Assert.StartsWith("<!DOCTYPE html>", page, StringComparison.Ordinal);
        Assert.Contains(CspMeta, page, StringComparison.Ordinal);
        Assert.Contains("<h1", page, StringComparison.Ordinal);
        Assert.Contains("<strong>bold</strong>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", page, StringComparison.Ordinal);
        Assert.Contains("<title>Mine &amp; yours</title>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_blocks_scripts_and_the_network()
    {
        Assert.Contains("default-src 'none'", ClickyPreview.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", ClickyPreview.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("img-src data:", ClickyPreview.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_markdown_is_cut_with_a_note()
    {
        var page = ClickyPreview.MarkdownPage(new string('a', ClickyPreview.MaxPageChars + 10), "t");

        Assert.Contains("too long to show here in full", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_block_is_extracted()
    {
        const string reply = "Here's your page:\n\n```html\n<!DOCTYPE html>\n<html><head><title>T</title></head><body>Hi</body></html>\n```\n\nEnjoy.\n\n```html\n<p>second</p>\n```";

        Assert.Equal("<!DOCTYPE html>\n<html><head><title>T</title></head><body>Hi</body></html>", ClickyPreview.HtmlDocument(reply));
        Assert.Equal("<p>x</p>", ClickyPreview.HtmlDocument("  ```HTML  \n<p>x</p>\n  ```"));
    }

    [Theory]
    [InlineData("No code here.")]
    [InlineData("```js\nalert(1)\n```")]
    [InlineData("```html\n\n```")]
    [InlineData("```html\n<p>never closed</p>")]
    [InlineData("")]
    public void Without_an_html_block_there_is_no_document(string reply)
    {
        Assert.Null(ClickyPreview.HtmlDocument(reply));
    }

    [Fact]
    public void Html_page_puts_the_policy_first_in_head()
    {
        const string html = "<!DOCTYPE html><html lang=\"en\"><HEAD data-x=\"1\"><script src=\"https://evil.example/x.js\"></script></HEAD><body>Hi</body></html>";

        var page = ClickyPreview.HtmlPage(html);

        Assert.StartsWith("<!DOCTYPE html><html lang=\"en\"><HEAD data-x=\"1\">" + CspMeta, page, StringComparison.Ordinal);
        Assert.True(page.IndexOf(CspMeta, StringComparison.Ordinal) < page.IndexOf("<script", StringComparison.Ordinal));
        Assert.EndsWith("<body>Hi</body></html>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_without_head_gets_one_after_the_html_tag()
    {
        var page = ClickyPreview.HtmlPage("<html><header>Top</header><body>Hi</body></html>");

        Assert.StartsWith("<html><head>" + CspMeta, page, StringComparison.Ordinal);
        Assert.Contains("</head><header>Top</header>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Fragment_is_wrapped_in_a_page()
    {
        var page = ClickyPreview.HtmlPage("<p>Just a fragment</p>");

        Assert.StartsWith("<!DOCTYPE html><html><head>" + CspMeta, page, StringComparison.Ordinal);
        Assert.EndsWith("<body><p>Just a fragment</p></body></html>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_for_prefers_the_html_document()
    {
        var withHtml = ClickyPreview.PageFor("Intro\n```html\n<p>doc</p>\n```", "t");
        var withoutHtml = ClickyPreview.PageFor("Just *text*", "t");

        Assert.Contains("<body><p>doc</p></body>", withHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Intro", withHtml, StringComparison.Ordinal);
        Assert.Contains("<em>text</em>", withoutHtml, StringComparison.Ordinal);
        Assert.Contains(CspMeta, withHtml, StringComparison.Ordinal);
        Assert.Contains(CspMeta, withoutHtml, StringComparison.Ordinal);
    }

    // See the report: the closing fence's "[ \t]*$" never matches before "\r\n" in multiline mode.
    [Fact]
    public void Html_block_with_windows_line_endings_is_extracted()
    {
        Assert.Equal("<p>x</p>", ClickyPreview.HtmlDocument("Intro\r\n```html\r\n<p>x</p>\r\n```\r\nAfter"));
    }
}
