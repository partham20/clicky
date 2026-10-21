using System.IO.Compression;
using System.Text;
using Clicky.Core.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Clicky.Tests.Documents;

public sealed class DocumentFileReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));

    public DocumentFileReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("a.pdf", DocumentFileKind.Pdf)]
    [InlineData("a.DOCX", DocumentFileKind.Word)]
    [InlineData("a.pptx", DocumentFileKind.PowerPoint)]
    [InlineData("a.md", DocumentFileKind.Text)]
    [InlineData("main.c", DocumentFileKind.Text)]
    [InlineData("photo.JPG", DocumentFileKind.Image)]
    [InlineData("setup.exe", DocumentFileKind.Unsupported)]
    [InlineData("old.doc", DocumentFileKind.Unsupported)]
    public void Classifies_by_extension(string name, DocumentFileKind kind) => Assert.Equal(kind, DocumentFileReader.Classify(name));

    [Fact]
    public void Unknown_extensions_are_sniffed()
    {
        var text = Write("notes.weird", "plain words\nmore words"u8.ToArray());
        var binary = Write("blob.weird", [1, 2, 0, 3]);

        Assert.Equal(DocumentFileKind.Text, DocumentFileReader.Classify(text));
        Assert.Equal(DocumentFileKind.Unsupported, DocumentFileReader.Classify(binary));
    }

    [Fact]
    public void Text_files_decode_bom_utf8_and_fall_back_to_latin1()
    {
        var utf16 = Write("u16.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("héllo wörld")]);
        var utf8 = Write("u8.txt", Encoding.UTF8.GetBytes("naïve café\r\n\r\n\r\n\r\nend"));
        var ansi = Write("ansi.txt", [0x63, 0x61, 0x66, 0xE9]);

        Assert.Equal("héllo wörld", Text(DocumentFileReader.Read(utf16)));
        Assert.Equal("naïve café\n\nend", Text(DocumentFileReader.Read(utf8)));
        Assert.Equal("café", Text(DocumentFileReader.Read(ansi)));
    }

    [Fact]
    public void Long_text_is_cut_and_says_so()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 100) + "é" + new string('b', 100)));

        var (text, cut) = DocumentFileReader.DecodeText(stream, 50);

        Assert.True(cut);
        Assert.Equal(new string('a', 50), text);
    }

    [Fact]
    public void Word_documents_keep_paragraphs_tabs_and_breaks()
    {
        var path = Zip("plan.docx", ("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:r><w:t>Quarterly</w:t></w:r><w:r><w:t xml:space="preserve"> plan</w:t></w:r></w:p>
            <w:p><w:r><w:t>Owner</w:t><w:tab/><w:t>Partha</w:t><w:br/><w:t>Due Friday</w:t></w:r></w:p>
            </w:body></w:document>
            """));

        var document = DocumentFileReader.Read(path);

        Assert.Equal("plan.docx", document.Name);
        Assert.Null(document.Unit);
        Assert.Equal("Quarterly plan\nOwner\tPartha\nDue Friday", Text(document));
    }

    [Fact]
    public void PowerPoint_slides_are_numbered_in_order()
    {
        static string Slide(string text) => $"""<p:sld xmlns:p="p" xmlns:a="a"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>""";
        var path = Zip("deck.pptx", ("ppt/slides/slide10.xml", Slide("Ten")), ("ppt/slides/slide2.xml", Slide("Two")), ("ppt/slides/slide1.xml", Slide("One")));

        var document = DocumentFileReader.Read(path);

        Assert.Equal("slide", document.Unit);
        Assert.Equal(3, document.TotalUnits);
        Assert.Equal([(1, "One"), (2, "Two"), (10, "Ten")], document.Sections.Select(s => (s.Number!.Value, s.Text)));
    }

    [Fact]
    public void Pdf_pages_are_read_with_numbers()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in new[] { "Invoice number 42 for consulting work done in October", "Payment is due within thirty days of receipt" })
        {
            builder.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(40, 700), font);
        }

        var path = Write("invoice.pdf", builder.Build());

        var document = DocumentFileReader.Read(path);

        Assert.Equal("page", document.Unit);
        Assert.Equal(2, document.TotalUnits);
        Assert.Null(document.Warning);
        Assert.Contains("Invoice number 42", document.Sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("thirty days", document.Sections[1].Text, StringComparison.Ordinal);
        Assert.Equal([1, 2], document.Sections.Select(s => s.Number!.Value));
    }

    [Fact]
    public void Pdf_without_text_is_flagged_as_scanned()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        var path = Write("scan.pdf", builder.Build());

        var document = DocumentFileReader.Read(path);

        Assert.True(document.IsEmpty);
        Assert.Contains("scanned", document.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Problems_are_explained_to_the_user()
    {
        Assert.Contains("can't find", Assert.Throws<DocumentException>(() => DocumentFileReader.Read(Path.Combine(_directory, "gone.pdf"))).Message, StringComparison.Ordinal);
        Assert.Contains("damaged", Assert.Throws<DocumentException>(() => DocumentFileReader.Read(Write("bad.pdf", "not a pdf at all"u8.ToArray()))).Message, StringComparison.Ordinal);
        Assert.Contains("password protected or damaged", Assert.Throws<DocumentException>(() => DocumentFileReader.Read(Write("locked.docx", [0xD0, 0xCF, 0x11, 0xE0]))).Message, StringComparison.Ordinal);
        Assert.Contains("spreadsheets", Assert.Throws<DocumentException>(() => DocumentFileReader.Read(Write("sheet.xlsx", [1]))).Message, StringComparison.Ordinal);
    }

    private static string Text(DocumentContent document) => Assert.Single(document.Sections).Text;

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Zip(string name, params (string Entry, string Xml)[] parts)
    {
        var path = Path.Combine(_directory, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        return path;
    }
}
