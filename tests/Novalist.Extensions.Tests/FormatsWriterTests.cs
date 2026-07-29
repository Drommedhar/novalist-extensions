using System.IO.Compression;
using Novalist.Extensions.Formats.Services;
using Novalist.Extensions.Formats.Writers;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// The export writers. Each is a pure function from a manuscript to a string, so
/// each is testable without a project, a host or a file.
/// </summary>
public class FormatsWriterTests
{
    private static Manuscript Book(params (string Chapter, string[] Scenes)[] chapters)
        => new("The Salt Road", [.. chapters.Select(c => new MsChapter(
            c.Chapter, string.Empty,
            [.. c.Scenes.Select((html, i) => new MsScene(
                $"Scene {i + 1}", html, Manuscript.ToText(html)))]))]);

    private static Manuscript OneScene(string html) => Book(("One", [html]));

    // ── Reading prose ──

    [Fact]
    public void ParagraphBoundariesSurviveTheTripToText()
    {
        var text = Manuscript.ToText("<p>First line.</p><p>Second line.</p>");

        Assert.Equal("First line.\n\nSecond line.", text);
    }

    [Fact]
    public void MarkupInsideAParagraphIsDroppedAndEntitiesAreDecoded()
        => Assert.Equal("Mira & Co said \"no\".",
            Manuscript.ToText("<p><em>Mira</em> &amp; Co said &quot;no&quot;.</p>"));

    [Fact]
    public void ARunOfBlankLinesBecomesOne()
        // A deliberate blank line between passages is punctuation; five of them
        // are an artefact of stripping tags.
        => Assert.Equal("A\n\nB", Manuscript.ToText("<p>A</p><p></p><p></p><p>B</p>"));

    [Fact]
    public void ParagraphsComeBackOneAtATime()
    {
        var paragraphs = Manuscript.Paragraphs("<p>One.</p><p>Two.</p><p>Three.</p>");

        Assert.Equal(["One.", "Two.", "Three."], paragraphs);
    }

    [Fact]
    public void EmptyProseIsNoParagraphsRatherThanOneEmptyOne()
    {
        Assert.Empty(Manuscript.Paragraphs(string.Empty));
        Assert.Equal(string.Empty, Manuscript.ToText(string.Empty));
    }

    [Fact]
    public void TheWordCountIsTheWholeBook()
        => Assert.Equal(6, Book(
            ("One", ["<p>Three words here.</p>"]),
            ("Two", ["<p>And three more.</p>"])).WordCount);

    // ── Plain text ──

    [Fact]
    public void PlainTextCarriesTheTitleChaptersAndProse()
    {
        var output = TextWriters.PlainText(OneScene("<p>The bell rang.</p>"));

        Assert.Contains("The Salt Road", output);
        Assert.Contains("One", output);
        Assert.Contains("The bell rang.", output);
    }

    [Fact]
    public void ASceneBreakIsARealMarkAndNotJustSpacing()
    {
        // A printed book puts something between two scenes in a chapter. A file
        // that relies on a blank line loses the break the moment it is reflowed.
        var output = TextWriters.PlainText(Book(("One", ["<p>First.</p>", "<p>Second.</p>"])));

        Assert.Contains("* * *", output);
    }

    [Fact]
    public void OneSceneNeedsNoSceneBreak()
        => Assert.DoesNotContain("* * *", TextWriters.PlainText(OneScene("<p>Alone.</p>")));

    // ── HTML ──

    [Fact]
    public void HtmlIsSelfContainedAndDeclaresItsEncoding()
    {
        var output = TextWriters.Html(OneScene("<p>The bell rang.</p>"));

        Assert.StartsWith("<!doctype html>", output);
        Assert.Contains("charset=\"utf-8\"", output);
        // No external stylesheet or script: a file sent to somebody has to work
        // on its own in twenty years.
        Assert.Contains("<style>", output);
        Assert.DoesNotContain("<script", output);
        Assert.DoesNotContain("<link", output);
    }

    [Fact]
    public void HtmlDoesNotIndentTheFirstParagraphAfterAHeading()
    {
        var output = TextWriters.Html(OneScene("<p>First.</p><p>Second.</p>"));

        Assert.Contains("<p class=\"first\">First.</p>", output);
        Assert.Contains("<p>Second.</p>", output);
    }

    [Fact]
    public void HtmlEscapesTheProseRatherThanPassingItThrough()
    {
        // The editor's own markup must not reach a file the writer is sending
        // someone, and a stray angle bracket must not become a tag.
        var output = TextWriters.Html(OneScene("<p>She wrote &lt;i&gt; on the board.</p>"));

        Assert.Contains("&lt;i&gt;", output);
        Assert.DoesNotContain("<i>", output);
    }

    [Fact]
    public void HtmlEscapesTheTitleToo()
        => Assert.Contains("&amp;", TextWriters.Html(
            new Manuscript("Salt & Ash", [])));

    [Fact]
    public void HtmlStylesForBothLightAndDark()
        => Assert.Contains("prefers-color-scheme:dark", TextWriters.Html(OneScene("<p>x</p>")));

    // ── RTF ──

    [Fact]
    public void RtfOpensAndClosesAsOneGroup()
    {
        var output = TextWriters.Rtf(OneScene("<p>The bell rang.</p>"));

        Assert.StartsWith(@"{\rtf1", output);
        Assert.EndsWith("}", output);
        Assert.Equal(output.Count(c => c == '{'), output.Count(c => c == '}'));
    }

    [Fact]
    public void RtfSendsCurlyQuotesAndDashesAsEscapesRatherThanBytes()
    {
        // Prose is full of these, and an RTF file with raw high bytes in it
        // arrives as mojibake.
        var output = TextWriters.Rtf(OneScene("<p>\u201CNo,\u201D she said\u2014once.</p>"));

        // 8220 is the left double quote, 8212 the em dash.
        Assert.Contains("\\u8220?", output);
        Assert.Contains("\\u8212?", output);
        Assert.DoesNotContain('\u201C', output);
    }

    [Fact]
    public void RtfEscapesItsOwnSyntaxCharacters()
    {
        var output = TextWriters.Rtf(OneScene(@"<p>A backslash \ and a brace {.</p>"));

        Assert.Contains(@"\\", output);
        Assert.Contains(@"\{", output);
    }

    [Fact]
    public void RtfIndentsEveryParagraphButTheFirst()
    {
        var output = TextWriters.Rtf(OneScene("<p>First.</p><p>Second.</p>"));

        Assert.Contains(@"\fi0 First.", output);
        Assert.Contains(@"\fi360 Second.", output);
    }

    // ── Fountain ──

    [Fact]
    public void FountainCarriesATitleAndForcesItsSceneHeadings()
    {
        // A chapter title is not an INT./EXT. line, and inventing one would be
        // inventing staging the writer never wrote.
        var output = TextWriters.Fountain(OneScene("<p>She walks in.</p>"));

        Assert.Contains("Title: The Salt Road", output);
        Assert.Contains(".ONE", output);
        Assert.Contains("She walks in.", output);
    }

    // ── FictionBook ──

    [Fact]
    public void Fb2IsWellFormedAndCarriesItsMetadata()
    {
        var output = TextWriters.Fb2(OneScene("<p>The bell rang.</p>"));

        Assert.Contains("<?xml version=\"1.0\" encoding=\"utf-8\"?>", output);
        Assert.Contains("<book-title>The Salt Road</book-title>", output);
        Assert.Contains("<p>The bell rang.</p>", output);
        // Parses as XML, which is the only claim that matters for a reader.
        System.Xml.Linq.XDocument.Parse(output);
    }

    [Fact]
    public void Fb2MarksASceneBreakWithAnEmptyLine()
    {
        var output = TextWriters.Fb2(Book(("One", ["<p>First.</p>", "<p>Second.</p>"])));

        Assert.Contains("<empty-line/>", output);
    }

    [Fact]
    public void Fb2EscapesProseThatWouldBreakTheDocument()
    {
        var output = TextWriters.Fb2(OneScene("<p>Salt &amp; ash &lt;here&gt;</p>"));

        System.Xml.Linq.XDocument.Parse(output);
        Assert.Contains("&amp;", output);
    }

    // ── ODT ──

    [Fact]
    public async Task OdtIsAZipWhoseMimetypeIsFirstAndStored()
    {
        var path = Path.Combine(Path.GetTempPath(), "nl-odt-" + Guid.NewGuid().ToString("N") + ".odt");
        try
        {
            await OdtWriter.WriteAsync(OneScene("<p>The bell rang.</p>"), path);

            using var archive = ZipFile.OpenRead(path);
            var first = archive.Entries[0];
            Assert.Equal("mimetype", first.FullName);
            // Readers that sniff rather than parse need it at a fixed offset.
            Assert.Equal(first.Length, first.CompressedLength);

            Assert.NotNull(archive.GetEntry("META-INF/manifest.xml"));
            Assert.NotNull(archive.GetEntry("content.xml"));
            Assert.NotNull(archive.GetEntry("styles.xml"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OdtContentIsWellFormedAndCarriesTheProse()
    {
        var path = Path.Combine(Path.GetTempPath(), "nl-odt-" + Guid.NewGuid().ToString("N") + ".odt");
        try
        {
            await OdtWriter.WriteAsync(
                Book(("One", ["<p>First.</p><p>Second.</p>", "<p>After the break.</p>"])), path);

            using var archive = ZipFile.OpenRead(path);
            await using var stream = archive.GetEntry("content.xml")!.Open();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            System.Xml.Linq.XDocument.Parse(content);
            Assert.Contains("The Salt Road", content);
            Assert.Contains("First.", content);
            Assert.Contains("* * *", content);
            // First paragraph unindented, the rest indented.
            Assert.Contains("NlFirst", content);
            Assert.Contains("NlBody", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OdtOverwritesAFileThatIsAlreadyThere()
    {
        var path = Path.Combine(Path.GetTempPath(), "nl-odt-" + Guid.NewGuid().ToString("N") + ".odt");
        try
        {
            await File.WriteAllTextAsync(path, "not a zip");

            await OdtWriter.WriteAsync(OneScene("<p>x</p>"), path);

            using var archive = ZipFile.OpenRead(path);
            Assert.Equal("mimetype", archive.Entries[0].FullName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
