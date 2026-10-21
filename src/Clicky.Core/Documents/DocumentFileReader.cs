using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Clicky.Core.Assistant;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Clicky.Core.Documents;

public enum DocumentFileKind
{
    Pdf,

    /// <summary>.docx (Word 2007 and later).</summary>
    Word,

    /// <summary>.pptx (PowerPoint 2007 and later).</summary>
    PowerPoint,

    /// <summary>Anything that decodes as text: .txt, .md, .csv, code, logs, and unknown extensions that look like text.</summary>
    Text,

    /// <summary>A picture, sent as an image rather than read (see <see cref="IImageLoader"/>).</summary>
    Image,

    Unsupported,
}

/// <summary>
/// Reads the text of files the user drops on Clicky: PDFs (PdfPig, page by page in reading
/// order), Word and PowerPoint files (straight from their XML), and anything else that is text.
/// Pure managed code, so it is tested on any OS.
/// </summary>
/// <remarks>
/// Reading stops after <see cref="MaxChars"/> (about half a million tokens, far more than one
/// request takes, so <see cref="DocumentFitter"/> still has the whole of most documents to pick
/// from) and says so in <see cref="DocumentContent.Warning"/>. CPU-bound: call it off the UI thread.
/// </remarks>
public static class DocumentFileReader
{
    public const int MaxChars = 2_000_000;

    public const long MaxPdfBytes = 200L * 1024 * 1024;

    public const long MaxOfficeBytes = 100L * 1024 * 1024;

    /// <summary>A PDF with less text than this per page is probably scanned images.</summary>
    public const int ScannedCharsPerPage = 25;

    private const int SniffBytes = 8_192;

    private static readonly HashSet<string> s_textExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".rst", ".csv", ".tsv", ".log", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg",
        ".conf", ".html", ".htm", ".css", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".cs", ".csproj", ".props", ".targets", ".sln", ".xaml", ".vb", ".fs",
        ".py", ".rb", ".go", ".rs", ".java", ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".m", ".mm", ".sh", ".bash", ".ps1",
        ".psm1", ".bat", ".cmd", ".sql", ".r", ".lua", ".pl", ".php", ".tex", ".srt", ".vtt", ".diff", ".patch", ".s", ".asm", ".ld", ".mk",
        ".cmake", ".dts", ".dtsi", ".hex", ".map", ".lst", ".gitignore", ".editorconfig", ".env",
    };

    private static readonly Dictionary<string, string> s_imageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
    };

    private static readonly HashSet<string> s_knownBinary = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".zip", ".7z", ".rar", ".msi", ".iso", ".mp3", ".mp4", ".wav", ".mov", ".avi", ".mkv", ".doc", ".ppt", ".xls", ".xlsx",
        ".xlsm", ".odt", ".odp", ".ods", ".pages", ".key", ".numbers", ".heic", ".psd", ".bin", ".elf", ".o", ".obj", ".lib", ".pdb",
    };

    public static DocumentFileKind Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFileKind.Pdf;
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFileKind.Word;
        }

        if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pptm", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFileKind.PowerPoint;
        }

        if (s_imageTypes.ContainsKey(extension))
        {
            return DocumentFileKind.Image;
        }

        if (s_textExtensions.Contains(extension) || Path.GetFileName(path).StartsWith('.'))
        {
            return DocumentFileKind.Text;
        }

        if (s_knownBinary.Contains(extension))
        {
            return DocumentFileKind.Unsupported;
        }

        return LooksLikeText(path) ? DocumentFileKind.Text : DocumentFileKind.Unsupported;
    }

    /// <summary>The media type of an image file by extension (see <see cref="DocumentFileKind.Image"/>).</summary>
    public static string? ImageMediaType(string path) => s_imageTypes.GetValueOrDefault(Path.GetExtension(path));

    /// <summary>Reads a document file's text.</summary>
    /// <exception cref="DocumentException">Missing, too big, encrypted, damaged, or not a kind Clicky reads.</exception>
    public static DocumentContent Read(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = Path.GetFileName(path);
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new DocumentException($"Clicky can't find {name}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DocumentException($"Clicky can't open {name}: {ex.Message}", ex);
        }

        try
        {
            return Classify(path) switch
            {
                DocumentFileKind.Pdf => ReadPdf(file, cancellationToken),
                DocumentFileKind.Word => ReadWord(file, cancellationToken),
                DocumentFileKind.PowerPoint => ReadPowerPoint(file, cancellationToken),
                DocumentFileKind.Text => ReadText(file),
                DocumentFileKind.Image => throw new DocumentException($"{name} is a picture; it is sent as an image, not read as a document."),
                _ => throw new DocumentException(UnsupportedMessage(name)),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DocumentException($"Clicky can't read {name}: {ex.Message}", ex);
        }
    }

    internal static DocumentContent ReadPdf(FileInfo file, CancellationToken cancellationToken)
    {
        var name = file.Name;
        if (file.Length > MaxPdfBytes)
        {
            throw new DocumentException($"{name} is {file.Length / (1024 * 1024)} MB; Clicky reads PDFs up to {MaxPdfBytes / (1024 * 1024)} MB.");
        }

        try
        {
            using var pdf = PdfDocument.Open(file.FullName, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var total = pdf.NumberOfPages;
            var sections = new List<DocumentSection>(total);
            var chars = 0;
            var read = 0;
            for (var number = 1; number <= total && chars < MaxChars; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = Tidy(ContentOrderTextExtractor.GetText(pdf.GetPage(number)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // One damaged page shouldn't cost the whole document.
                    text = "[Clicky could not read this page.]";
                }

                sections.Add(new DocumentSection(text, number));
                chars += text.Length;
                read = number;
            }

            string? warning = null;
            var visible = sections.Sum(s => s.Text.Count(c => !char.IsWhiteSpace(c)));
            if (total > 0 && visible < ScannedCharsPerPage * read)
            {
                warning = "This PDF has little or no text layer (it looks like scanned pages), so Clicky can't read most of it.";
            }
            else if (read < total)
            {
                warning = string.Create(CultureInfo.InvariantCulture, $"Only the first {read} of {total} pages were read; the document is very long.");
            }

            return new DocumentContent(name, RequestAttachment.FileOrigin, sections, "page", total, warning);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentException($"{name} is password protected. Open it, save a copy without a password, and drop that instead.", ex);
        }
        catch (Exception ex) when (ex is UglyToad.PdfPig.Core.PdfDocumentFormatException or InvalidOperationException or ArgumentException or InvalidCastException or IndexOutOfRangeException or KeyNotFoundException)
        {
            throw new DocumentException($"{name} looks damaged; Clicky couldn't read it as a PDF.", ex);
        }
    }

    internal static DocumentContent ReadWord(FileInfo file, CancellationToken cancellationToken)
    {
        using var zip = OpenOffice(file);
        var entry = zip.GetEntry("word/document.xml") ?? throw new DocumentException($"{file.Name} doesn't look like a Word document.");
        var text = OfficeText(file.Name, entry, "p", "t", cancellationToken);
        var warning = text.Length >= MaxChars ? "Only the start of this document was read; it is very long." : null;
        return new DocumentContent(file.Name, RequestAttachment.FileOrigin, [new DocumentSection(text)], Warning: warning);
    }

    internal static DocumentContent ReadPowerPoint(FileInfo file, CancellationToken cancellationToken)
    {
        using var zip = OpenOffice(file);
        var slides = zip.Entries
            .Select(e => (Entry: e, Number: SlideNumber(e.FullName)))
            .Where(s => s.Number > 0)
            .OrderBy(s => s.Number)
            .ToList();
        if (slides.Count == 0)
        {
            throw new DocumentException($"{file.Name} doesn't look like a PowerPoint file.");
        }

        var sections = new List<DocumentSection>(slides.Count);
        var chars = 0;
        foreach (var (entry, number) in slides)
        {
            if (chars >= MaxChars)
            {
                break;
            }

            var text = OfficeText(file.Name, entry, "p", "t", cancellationToken);
            sections.Add(new DocumentSection(text, number));
            chars += text.Length;
        }

        var warning = sections.Count < slides.Count ? $"Only the first {sections.Count} of {slides.Count} slides were read." : null;
        return new DocumentContent(file.Name, RequestAttachment.FileOrigin, sections, "slide", slides.Count, warning);
    }

    internal static DocumentContent ReadText(FileInfo file)
    {
        string text;
        bool cut;
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            (text, cut) = DecodeText(stream, MaxChars);
        }

        var warning = cut ? "Only the start of this file was read; it is very long." : null;
        return new DocumentContent(file.Name, RequestAttachment.FileOrigin, [new DocumentSection(Tidy(text))], Warning: warning);
    }

    /// <summary>Text from a stream: BOM if any, else strict UTF-8, else Latin-1 (old ANSI files).</summary>
    internal static (string Text, bool Cut) DecodeText(Stream stream, int maxChars)
    {
        // UTF-8 needs at most 3 bytes per UTF-16 char (4 per surrogate pair), so this always covers maxChars.
        var limit = (long)maxChars * 3;
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int count;
        while (buffer.Length < limit && (count = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, count);
        }

        var more = stream.ReadByte() >= 0;
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        string text;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            text = Encoding.UTF8.GetString(bytes[3..]);
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            text = Encoding.Unicode.GetString(bytes[2..]);
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            text = Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // A cut can land inside a UTF-8 sequence; only the tail is suspect then.
                text = more && TryUtf8WithoutTail(bytes, out var trimmed) ? trimmed : Encoding.Latin1.GetString(bytes);
            }
        }

        var cut = more || text.Length > maxChars;
        if (text.Length > maxChars)
        {
            text = text[..maxChars];
        }

        return (text, cut);
    }

    private static bool TryUtf8WithoutTail(ReadOnlySpan<byte> bytes, out string text)
    {
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        for (var drop = 1; drop <= 3 && drop < bytes.Length; drop++)
        {
            try
            {
                text = strict.GetString(bytes[..^drop]);
                return true;
            }
            catch (DecoderFallbackException)
            {
            }
        }

        text = string.Empty;
        return false;
    }

    /// <summary>Normalizes line endings, drops control characters, and squeezes runs of blank lines.</summary>
    internal static string Tidy(string text)
    {
        var builder = new StringBuilder(text.Length);
        var newlines = 0;
        foreach (var c in text.ReplaceLineEndings("\n"))
        {
            if (c == '\n')
            {
                if (++newlines <= 2)
                {
                    builder.Append(c);
                }

                continue;
            }

            if (char.IsControl(c) && c != '\t')
            {
                continue;
            }

            newlines = 0;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static bool LooksLikeText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[SniffBytes];
            var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var span = buffer.AsSpan(0, count);
            if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            {
                return true;
            }

            return count > 0 && !span.Contains((byte)0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string UnsupportedMessage(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".doc" => $"{name} is an old Word file. Save it as .docx or PDF and drop that instead.",
        ".ppt" => $"{name} is an old PowerPoint file. Save it as .pptx or PDF and drop that instead.",
        ".xls" or ".xlsx" or ".xlsm" or ".ods" => $"Clicky can't read spreadsheets yet. Save {name} as CSV, or ask with the sheet open on screen.",
        _ => $"Clicky can't read {name}. It reads PDFs, Word and PowerPoint files, pictures, and text files.",
    };

    private static ZipArchive OpenOffice(FileInfo file)
    {
        if (file.Length > MaxOfficeBytes)
        {
            throw new DocumentException($"{file.Name} is {file.Length / (1024 * 1024)} MB; Clicky reads Office files up to {MaxOfficeBytes / (1024 * 1024)} MB.");
        }

        try
        {
            return ZipFile.OpenRead(file.FullName);
        }
        catch (InvalidDataException ex)
        {
            // Encrypted Office files are OLE compound files, not zips.
            throw new DocumentException($"{file.Name} is password protected or damaged, so Clicky can't read it.", ex);
        }
    }

    private static int SlideNumber(string entryName)
    {
        const string Prefix = "ppt/slides/slide";
        if (!entryName.StartsWith(Prefix, StringComparison.Ordinal) || !entryName.EndsWith(".xml", StringComparison.Ordinal))
        {
            return 0;
        }

        return int.TryParse(entryName.AsSpan(Prefix.Length, entryName.Length - Prefix.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    /// <summary>
    /// Text of an Office XML part: the text of every <paramref name="textElement"/>, a line break at
    /// the end of every <paramref name="paragraphElement"/>, tabs and breaks kept. Namespaces are
    /// ignored, so it serves w: (Word) and a: (DrawingML, slides) alike.
    /// </summary>
    private static string OfficeText(string name, ZipArchiveEntry entry, string paragraphElement, string textElement, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
            var inText = false;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.LocalName == textElement:
                        inText = !reader.IsEmptyElement;
                        break;
                    case XmlNodeType.Element when reader.LocalName == "tab":
                        text.Append('\t');
                        break;
                    case XmlNodeType.Element when reader.LocalName is "br" or "cr":
                        text.Append('\n');
                        break;
                    case XmlNodeType.EndElement when reader.LocalName == textElement:
                        inText = false;
                        break;
                    case XmlNodeType.EndElement when reader.LocalName == paragraphElement:
                        text.Append('\n');
                        if (text.Length >= MaxChars)
                        {
                            return Tidy(text.ToString(0, MaxChars));
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        break;
                    case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when inText:
                        text.Append(reader.Value);
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new DocumentException($"{name} looks damaged; Clicky couldn't read it.", ex);
        }

        return Tidy(text.ToString());
    }
}
