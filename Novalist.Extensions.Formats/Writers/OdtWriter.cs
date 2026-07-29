using System.IO.Compression;
using System.Text;
using Novalist.Extensions.Formats.Services;

namespace Novalist.Extensions.Formats.Writers;

/// <summary>
/// OpenDocument Text, which is a zip of XML parts.
///
/// Written by hand for the same reason the RTF writer is: the subset of ODF a
/// manuscript needs is small, and it does not justify a dependency that has to
/// be kept current and audited for a file format that has not changed in years.
/// </summary>
public static class OdtWriter
{
    public static async Task WriteAsync(Manuscript book, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        // mimetype has to be first and stored uncompressed, or a reader that
        // sniffs the file rather than parsing it will not recognise it.
        var mimetype = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
        await using (var stream = mimetype.Open())
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            await writer.WriteAsync("application/vnd.oasis.opendocument.text");

        await AddAsync(archive, "META-INF/manifest.xml", Manifest());
        await AddAsync(archive, "styles.xml", Styles());
        await AddAsync(archive, "content.xml", Content(book));
    }

    private static async Task AddAsync(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content);
    }

    private static string Manifest() =>
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">
          <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/>
          <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
          <manifest:file-entry manifest:full-path="styles.xml" manifest:media-type="text/xml"/>
        </manifest:manifest>
        """;

    private static string Styles() =>
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document-styles
            xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
            xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
            xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0"
            office:version="1.2">
          <office:styles>
            <style:style style:name="Standard" style:family="paragraph">
              <style:text-properties style:font-name="Times New Roman" fo:font-size="12pt"/>
            </style:style>
          </office:styles>
        </office:document-styles>
        """;

    private static string Content(Manuscript book)
    {
        var output = new StringBuilder();
        output.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        output.AppendLine("<office:document-content");
        output.AppendLine("    xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"");
        output.AppendLine("    xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"");
        output.AppendLine("    xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\"");
        output.AppendLine("    xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"");
        output.AppendLine("    office:version=\"1.2\">");

        output.AppendLine("  <office:automatic-styles>");
        // Body, first-line-indented body, chapter heading and title. Four styles
        // is what a manuscript needs; anything more is a word processor's job.
        output.AppendLine(ParagraphStyle("NlTitle", "24pt", "bold", "center", null));
        output.AppendLine(ParagraphStyle("NlChapter", "16pt", "bold", "center", null));
        output.AppendLine(ParagraphStyle("NlFirst", "12pt", null, null, "0cm"));
        output.AppendLine(ParagraphStyle("NlBody", "12pt", null, null, "0.5cm"));
        output.AppendLine(ParagraphStyle("NlBreak", "12pt", null, "center", null));
        output.AppendLine("  </office:automatic-styles>");

        output.AppendLine("  <office:body>");
        output.AppendLine("    <office:text>");
        output.AppendLine($"      <text:p text:style-name=\"NlTitle\">{Esc(book.Title)}</text:p>");

        foreach (var chapter in book.Chapters)
        {
            output.AppendLine(
                $"      <text:p text:style-name=\"NlChapter\">{Esc(chapter.Title)}</text:p>");
            for (var i = 0; i < chapter.Scenes.Count; i++)
            {
                if (i > 0)
                    output.AppendLine("      <text:p text:style-name=\"NlBreak\">* * *</text:p>");
                var paragraphs = Manuscript.Paragraphs(chapter.Scenes[i].Html);
                for (var p = 0; p < paragraphs.Count; p++)
                {
                    var style = p == 0 ? "NlFirst" : "NlBody";
                    output.AppendLine(
                        $"      <text:p text:style-name=\"{style}\">{Esc(paragraphs[p])}</text:p>");
                }
            }
        }

        output.AppendLine("    </office:text>");
        output.AppendLine("  </office:body>");
        output.AppendLine("</office:document-content>");
        return output.ToString();
    }

    private static string ParagraphStyle(
        string name, string size, string? weight, string? align, string? indent)
    {
        var output = new StringBuilder();
        output.AppendLine(
            $"    <style:style style:name=\"{name}\" style:family=\"paragraph\" style:parent-style-name=\"Standard\">");
        var properties = new StringBuilder("      <style:paragraph-properties");
        if (align != null) properties.Append($" fo:text-align=\"{align}\"");
        if (indent != null) properties.Append($" fo:text-indent=\"{indent}\"");
        properties.Append("/>");
        output.AppendLine(properties.ToString());
        output.Append("      <style:text-properties");
        output.Append($" fo:font-size=\"{size}\"");
        if (weight != null) output.Append($" fo:font-weight=\"{weight}\"");
        output.AppendLine("/>");
        output.Append("    </style:style>");
        return output.ToString();
    }

    private static string Esc(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
