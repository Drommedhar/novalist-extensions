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

    // ── The book's details: who wrote it, what language, and its cover ──

    /// <summary>
    /// A cover on disk, so the writers that embed one have something to read.
    /// A one-pixel PNG is enough: the point is that the bytes arrive, not what
    /// they draw.
    /// </summary>
    private static string WriteCover(string name = "cover.png")
    {
        var directory = Path.Combine(Path.GetTempPath(), "nl-cover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPng));
        return path;
    }

    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static Manuscript WithDetails(MsBook details)
        => OneScene("<p>A line.</p>") with { Book = details };

    [Fact]
    public void HtmlDeclaresTheBooksLanguageRatherThanEnglish()
    {
        var html = TextWriters.Html(WithDetails(
            new MsBook("The Salt Road", string.Empty, "de", string.Empty, true)));

        Assert.Contains("<html lang=\"de\">", html);
        Assert.DoesNotContain("<html lang=\"en\">", html);
    }

    [Fact]
    public void AManuscriptWithNoDetailsStillSaysSomethingSensible()
    {
        // Every real export path passes the host's answer. A test - or a caller
        // written before this existed - gets English rather than an empty tag.
        var html = TextWriters.Html(OneScene("<p>A line.</p>"));

        Assert.Contains("<html lang=\"en\">", html);
    }

    [Fact]
    public void TheCoverIsEmbeddedInTheHtmlRatherThanLinked()
    {
        var cover = WriteCover();
        var html = TextWriters.Html(WithDetails(
            new MsBook("The Salt Road", "Ada Cole", "en", cover, true)));

        // Embedded, because a single file that loses its picture when it moves
        // is not the self-contained file this format promises to be.
        Assert.Contains("<img class=\"cover\" src=\"data:image/png;base64,", html);
        Assert.DoesNotContain(cover, html);
        Assert.Contains("<p class=\"author\">Ada Cole</p>", html);
    }

    [Fact]
    public void AMissingOrUnreadableCoverIsNotAnError()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nl-not-here", "cover.png");
        Assert.Null(TextWriters.DataUri(missing));
        Assert.Null(TextWriters.DataUri(string.Empty));
        // A format nothing can decode is no cover rather than a broken one.
        Assert.Null(TextWriters.DataUri(WriteCover("cover.tiff")));

        var html = TextWriters.Html(WithDetails(
            new MsBook("The Salt Road", string.Empty, "en", missing, true)));
        Assert.DoesNotContain("<img class=\"cover\"", html);
        Assert.Contains("The Salt Road", html);
    }

    [Fact]
    public void AnEmptyCoverFileIsNoCover()
    {
        var path = WriteCover();
        File.WriteAllBytes(path, []);

        Assert.Null(TextWriters.DataUri(path));
    }

    [Fact]
    public void FictionBookGetsTheLanguageTheAuthorAndTheCover()
    {
        var cover = WriteCover();
        var fb2 = TextWriters.Fb2(WithDetails(
            new MsBook("The Salt Road", "Ada Marie Cole", "de-DE", cover, true)));

        // FictionBook wants the bare subtag, so "de-DE" is "de".
        Assert.Contains("<lang>de</lang>", fb2);
        Assert.Contains("<first-name>Ada Marie</first-name>", fb2);
        Assert.Contains("<last-name>Cole</last-name>", fb2);
        Assert.Contains("<coverpage><image l:href=\"#cover.png\"/></coverpage>", fb2);
        Assert.Contains("<binary id=\"cover.png\" content-type=\"image/png\">", fb2);
    }

    [Fact]
    public void FictionBookLeavesOutWhatWasNotGiven()
    {
        var fb2 = TextWriters.Fb2(OneScene("<p>A line.</p>"));

        Assert.Contains("<lang>en</lang>", fb2);
        Assert.DoesNotContain("<author>", fb2);
        Assert.DoesNotContain("<coverpage>", fb2);
        Assert.DoesNotContain("<binary", fb2);
    }

    [Fact]
    public void AOneWordAuthorIsASurname()
    {
        Assert.Equal((string.Empty, "Cole"), TextWriters.SplitName("Cole"));
        Assert.Equal(("Ada", "Cole"), TextWriters.SplitName("  Ada Cole  "));
    }

    [Fact]
    public void AnEmptyLanguageIsEnglishRatherThanNothing()
    {
        Assert.Equal("en", TextWriters.ShortLanguage(" "));
        Assert.Equal("pt", TextWriters.ShortLanguage("pt-BR"));
        Assert.Equal("fr", TextWriters.ShortLanguage("fr"));
    }

    [Fact]
    public void PlainTextAndRtfNameTheAuthorUnderTheTitle()
    {
        var book = WithDetails(new MsBook("The Salt Road", "Ada Cole", "en", string.Empty, true));

        Assert.Contains("The Salt Road\nAda Cole", TextWriters.PlainText(book).Replace("\r\n", "\n"));
        Assert.Contains(@"Ada Cole\fs24\par", TextWriters.Rtf(book));
    }

    [Fact]
    public async Task OpenDocumentCarriesTheLanguageAndTheAuthor()
    {
        var book = WithDetails(new MsBook("The Salt Road", "Ada Cole", "de-DE", string.Empty, true));
        var path = Path.Combine(
            Path.GetTempPath(), "nl-odt-" + Guid.NewGuid().ToString("N"), "book.odt");

        await OdtWriter.WriteAsync(book, path);

        using var archive = ZipFile.OpenRead(path);
        var styles = new StreamReader(archive.GetEntry("styles.xml")!.Open()).ReadToEnd();
        var content = new StreamReader(archive.GetEntry("content.xml")!.Open()).ReadToEnd();

        // A word processor spell-checks against whatever the document claims to
        // be. Claiming English for a German novel underlines every second word.
        Assert.Contains("fo:language=\"de\"", styles);
        Assert.Contains("fo:country=\"DE\"", styles);
        Assert.Contains("Ada Cole", content);
    }

    [Fact]
    public void ALanguageWithNoCountryLeavesTheCountryOut()
    {
        Assert.Equal(("en", string.Empty), OdtWriter.LanguageParts(string.Empty));
        Assert.Equal(("fr", string.Empty), OdtWriter.LanguageParts("FR"));
        Assert.Equal(("pt", "BR"), OdtWriter.LanguageParts("pt-BR"));
    }


    // ── The manuscript as data rather than as a document ──

    private static Manuscript TwoChapters() => new(
        "The Salt Road",
        [
            new MsChapter("Chapter, One", "Act One",
                [new MsScene("Low water", "<p>One two three.</p>", "One two three.")]),
            new MsChapter("Chapter \"Two\"", "Act Two",
                [
                    new MsScene("The crossing", "<p>Four five.</p>", "Four five."),
                    new MsScene("After", "<p>Six.</p>", "Six.")
                ])
        ]);

    [Fact]
    public void CsvQuotesTheFieldsThatWouldBreakARow()
    {
        var csv = Interchange.Csv(TwoChapters());
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            "chapterNumber,chapterTitle,act,sceneNumber,sceneTitle,words", lines[0]);
        // A comma in a title breaks every row after it unless it is quoted,
        // and titles acquire commas eventually.
        Assert.Equal("1,\"Chapter, One\",Act One,1,Low water,3", lines[1]);
        // A quote inside a field is doubled, per RFC 4180.
        Assert.Contains("\"Chapter \"\"Two\"\"\"", lines[2]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void CsvNumbersChaptersAndScenesFromOne()
    {
        var rows = Interchange.Rows(TwoChapters());

        Assert.Equal([1, 2, 2], rows.Select(r => r.ChapterNumber));
        // Scene numbers restart inside each chapter, which is how anybody
        // reading a spreadsheet of them expects to find scene two of three.
        Assert.Equal([1, 1, 2], rows.Select(r => r.SceneNumber));
    }

    [Fact]
    public void CsvFieldQuotingIsOnlyAppliedWhereItIsNeeded()
    {
        Assert.Equal(string.Empty, Interchange.Field(null));
        Assert.Equal("plain", Interchange.Field("plain"));
        Assert.Equal("\"with, comma\"", Interchange.Field("with, comma"));
        Assert.Equal("\"line\nbreak\"", Interchange.Field("line\nbreak"));
    }

    [Fact]
    public void JsonKeepsTheShapeTheBookHas()
    {
        var json = Interchange.Json(TwoChapters() with
        {
            Book = new MsBook("The Salt Road", "Ada Cole", "de", string.Empty, true)
        });

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("Ada Cole", root.GetProperty("author").GetString());
        Assert.Equal("de", root.GetProperty("language").GetString());
        Assert.Equal(6, root.GetProperty("wordCount").GetInt32());

        var chapters = root.GetProperty("chapters");
        Assert.Equal(2, chapters.GetArrayLength());
        Assert.Equal(2, chapters[1].GetProperty("scenes").GetArrayLength());
        // Titles are prose; escaping them to \u sequences makes the file
        // unreadable to the person most likely to open it.
        Assert.Contains("Chapter, One", json);
    }

    [Fact]
    public void OpmlNestsScenesInsideTheirChapter()
    {
        var opml = Interchange.Opml(TwoChapters());

        Assert.Contains("<opml version=\"2.0\">", opml);
        Assert.Contains("<outline text=\"Chapter, One\">", opml);
        Assert.Contains("<outline text=\"Low water\"/>", opml);
        // A quote in a title would end the attribute early.
        Assert.Contains("Chapter &quot;Two&quot;", opml);
    }

    [Fact]
    public void AnEmptyBookProducesValidFilesRatherThanNothing()
    {
        var empty = new Manuscript("Untitled", []);

        Assert.StartsWith("chapterNumber,", Interchange.Csv(empty));
        Assert.Contains("</opml>", Interchange.Opml(empty));
        using var document = System.Text.Json.JsonDocument.Parse(Interchange.Json(empty));
        Assert.Equal(0, document.RootElement.GetProperty("chapters").GetArrayLength());
    }

}
