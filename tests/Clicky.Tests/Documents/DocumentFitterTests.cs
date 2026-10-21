using Clicky.Core.Assistant;
using Clicky.Core.Documents;

namespace Clicky.Tests.Documents;

public sealed class DocumentFitterTests
{
    [Fact]
    public void A_document_that_fits_goes_whole_with_page_markers()
    {
        var document = Paged(("Intro text.", 1), ("Body text.", 2));

        var fitted = DocumentFitter.Fit(document, 10_000, "anything");

        Assert.False(fitted.IsPartial);
        Assert.Equal("--- page 1 ---\nIntro text.\n\n--- page 2 ---\nBody text.", fitted.Attachment.Text);
        Assert.Null(fitted.Attachment.Note);
        Assert.Equal(AttachmentKind.Document, fitted.Attachment.Kind);
    }

    [Fact]
    public void A_long_document_keeps_the_start_the_end_and_the_pages_the_question_is_about()
    {
        var pages = Enumerable.Range(1, 40)
            .Select(n => (Text: n == 23 ? Filler(n) + " The refund policy allows returns within 30 days." : Filler(n), Number: n))
            .ToArray();
        var document = Paged(pages);
        Assert.True(document.EstimatedTokens > 20_000);

        var fitted = DocumentFitter.Fit(document, 6_000, "What is the refund policy?");
        var text = fitted.Attachment.Text!;

        Assert.True(fitted.IsPartial);
        Assert.InRange(TokenEstimate.Of(text) + TokenEstimate.Of(fitted.Attachment.Note), 1, 6_000);
        Assert.StartsWith("--- page 1 ---", text, StringComparison.Ordinal);
        Assert.Contains("--- page 23 ---", text, StringComparison.Ordinal);
        Assert.Contains("refund policy allows returns", text, StringComparison.Ordinal);
        Assert.Contains("--- page 40 ---", text, StringComparison.Ordinal);
        Assert.Contains("[... left out: pages 2-", text, StringComparison.Ordinal);
        Assert.Contains("of 40", fitted.Attachment.Note, StringComparison.Ordinal);
        Assert.Contains("Included: pages 1", fitted.Attachment.Note, StringComparison.Ordinal);

        // Pages stay in document order.
        var markers = text.Split('\n').Where(l => l.StartsWith("--- page ", StringComparison.Ordinal)).Select(l => int.Parse(l.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(markers.Order(), markers);
    }

    [Fact]
    public void Without_useful_words_the_pick_is_spread_evenly()
    {
        var document = Paged([.. Enumerable.Range(1, 30).Select(n => (Filler(n), n))]);

        var fitted = DocumentFitter.Fit(document, 5_000, "summarize this");
        var pages = fitted.Attachment.Text!.Split('\n').Where(l => l.StartsWith("--- page ", StringComparison.Ordinal)).Select(l => int.Parse(l.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture)).ToList();

        Assert.Contains(1, pages);
        Assert.Contains(30, pages);
        Assert.Contains(pages, p => p is > 10 and < 20);
    }

    [Fact]
    public void Plain_text_gets_word_count_gaps()
    {
        var document = new DocumentContent("log.txt", RequestAttachment.FileOrigin, [new DocumentSection(string.Join('\n', Enumerable.Range(0, 3_000).Select(i => $"line {i} of the build log with some words")))]);

        var fitted = DocumentFitter.Fit(document, 3_000, null);

        Assert.True(fitted.IsPartial);
        Assert.StartsWith("line 0 of", fitted.Attachment.Text, StringComparison.Ordinal);
        Assert.Contains("line 2999 of", fitted.Attachment.Text, StringComparison.Ordinal);
        Assert.Matches(@"\[\.\.\. about [\d,]+ words left out \.\.\.\]", fitted.Attachment.Text!);
        Assert.Contains("the start, the end", fitted.Attachment.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_enormous_line_is_still_cut_to_the_budget()
    {
        var document = new DocumentContent("min.js", RequestAttachment.FileOrigin, [new DocumentSection(string.Join(' ', Enumerable.Repeat("token", 40_000)))]);

        var fitted = DocumentFitter.Fit(document, 2_000, null);

        Assert.InRange(TokenEstimate.Of(fitted.Attachment.Text), 1, 2_000);
    }

    [Fact]
    public void Tiny_budgets_cut_the_first_chunk()
    {
        var document = Paged((Filler(1), 1), (Filler(2), 2));

        var fitted = DocumentFitter.Fit(document, 300, null);

        Assert.InRange(TokenEstimate.Of(fitted.Attachment.Text), 1, 300);
        Assert.Contains("the rest is left out", fitted.Attachment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_documents_carry_their_warning()
    {
        var document = new DocumentContent("scan.pdf", RequestAttachment.FileOrigin, [new DocumentSection(string.Empty, 1)], "page", 1, "looks scanned");

        var fitted = DocumentFitter.Fit(document, 1_000, null);

        Assert.Equal(string.Empty, fitted.Attachment.Text);
        Assert.Equal("looks scanned", fitted.Attachment.Note);
    }

    [Fact]
    public void Small_documents_go_whole_and_big_ones_share_the_rest()
    {
        Assert.Equal([100, 450, 450], DocumentFitter.Shares([100, 5_000, 9_000], 1_000));
        Assert.Equal([100, 200], DocumentFitter.Shares([100, 200], 1_000));
        Assert.Equal([0, 0], DocumentFitter.Shares([10, 20], 0));
    }

    [Fact]
    public void FitAll_keeps_every_document_within_its_share()
    {
        var small = new DocumentContent("a.txt", RequestAttachment.FileOrigin, [new DocumentSection("short note")]);
        var big = Paged([.. Enumerable.Range(1, 40).Select(n => (Filler(n), n))]);

        var fitted = DocumentFitter.FitAll([small, big], 8_000, null);

        Assert.False(fitted[0].IsPartial);
        Assert.True(fitted[1].IsPartial);
        Assert.InRange(fitted.Sum(f => f.Attachment.EstimatedTokens), 1, 8_000);
    }

    [Fact]
    public void Bisection_spreads_any_prefix()
    {
        var ranks = DocumentFitter.BisectionRanks(7);

        Assert.Equal(7, ranks.Distinct().Count());
        Assert.Equal(0, ranks[3]);
        Assert.Equal([1, 2], new[] { ranks[1], ranks[5] }.Order());
    }

    [Fact]
    public void Ranges_are_compact()
    {
        Assert.Equal("pages 1-3, 7, 9-10", DocumentFitter.Ranges("page", [9, 1, 2, 3, 7, 10, 2]));
        Assert.Equal("slide 4", DocumentFitter.Ranges("slide", [4]));
    }

    [Fact]
    public void Terms_skip_stop_words_and_fold_plurals()
    {
        Assert.Equal(["refund", "invoice", "policy"], DocumentFitter.Terms("What does the document say about refunds, invoices and policy?"));
    }

    private static DocumentContent Paged(params (string Text, int Number)[] pages) =>
        new("report.pdf", RequestAttachment.FileOrigin, [.. pages.Select(p => new DocumentSection(p.Text, p.Number))], "page", pages.Length);

    // About 700 tokens of text unique to the page, with no word from the questions above.
    private static string Filler(int page) =>
        string.Join(' ', Enumerable.Range(0, 450).Select(i => $"w{page}x{i % 37}"));
}
