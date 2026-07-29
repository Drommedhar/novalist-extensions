using System.IO.Compression;
using Novalist.Extensions.Formats.Importers;
using Novalist.Extensions.Formats.Services;
using Novalist.Sdk.Hooks;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// Reading other tools' files.
///
/// This is the code most likely to be handed something malformed, so most of
/// these are about being wrong safely: a file that cannot be read has to say so
/// rather than importing half a book.
/// </summary>
public class ImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "nl-import-" + Guid.NewGuid().ToString("N"));

    public ImporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ── RTF reading ──

    [Fact]
    public void PlainRtfBecomesItsParagraphs()
    {
        var text = RtfReader.ToText(@"{\rtf1\ansi First line.\par Second line.\par}");

        Assert.Equal("First line.\n\nSecond line.", text);
    }

    [Fact]
    public void TheFontAndColourTablesAreNotProse()
    {
        // These sit at the top of every RTF file. A reader that does not skip
        // them puts "Times New Roman" in the middle of the writer's first
        // sentence.
        var text = RtfReader.ToText(
            @"{\rtf1\ansi{\fonttbl{\f0\froman Times New Roman;}}{\colortbl;\red0\green0\blue0;}The bell rang.\par}");

        Assert.Equal("The bell rang.", text);
        Assert.DoesNotContain("Times", text);
    }

    [Fact]
    public void AnIgnorableDestinationIsSkippedWholesale()
        => Assert.Equal("Kept.", RtfReader.ToText(
            @"{\rtf1{\*\generator Riched20 10.0;}Kept.\par}"));

    [Fact]
    public void UnicodeEscapesComeBackAsCharactersAndTheirFallbackIsDropped()
    {
        var text = RtfReader.ToText(
            "{\\rtf1 \\u8220?No,\\u8221? she said\\u8212?once.\\par}");

        Assert.Equal("\u201CNo,\u201D she said\u2014once.", text);
        // The '?' after each escape is a fallback for readers that cannot do
        // unicode, and leaving it in doubles every quote mark.
        Assert.DoesNotContain('?', text);
    }

    [Fact]
    public void TheNamedPunctuationControlWordsAreUnderstood()
    {
        var text = RtfReader.ToText(
            @"{\rtf1 \ldblquote No,\rdblquote\ she said\emdash once\endash twice.\par}");

        Assert.Contains('\u201C', text);
        Assert.Contains('\u2014', text);
        Assert.Contains('\u2013', text);
    }

    [Fact]
    public void HexEscapedBytesAreReadAsLatin1()
        // Scrivener files from western European systems are full of these.
        => Assert.Equal("caf\u00e9", RtfReader.ToText(@"{\rtf1 caf\'e9\par}"));

    [Fact]
    public void EscapedBracesAndBackslashesSurvive()
        => Assert.Equal(@"A { and a \ and a }", RtfReader.ToText(
            @"{\rtf1 A \{ and a \\ and a \}\par}"));

    [Fact]
    public void RunsOfWhitespaceInsideALineCollapse()
        => Assert.Equal("One two three", RtfReader.ToText(
            "{\\rtf1 One    two\t\tthree\\par}"));

    [Fact]
    public void AnEmptyOrGarbledFileReadsAsNothingRatherThanThrowing()
    {
        Assert.Equal(string.Empty, RtfReader.ToText(string.Empty));
        Assert.Equal(string.Empty, RtfReader.ToText(@"{\rtf1}"));
        // Unbalanced braces and a truncated control word: a real file that got
        // cut off mid-write should not take an import down.
        Assert.NotNull(RtfReader.ToText(@"{\rtf1\ansi{\fonttbl Half a file\"));
    }

    [Fact]
    public void AMalformedHexEscapeKeepsWhateverTextFollowsIt()
    {
        // A garbled file should give up as little prose as possible, so the
        // broken escape marker is dropped and the characters after it are
        // kept rather than the whole sequence being thrown away.
        Assert.Equal("azzb", RtfReader.ToText(@"{\rtf1 a\'zzb\par}"));
    }

    // ── Delimited files ──

    [Fact]
    public void AQuotedCellMayContainTheSeparator()
    {
        // Prose is full of commas, and a naive split tears sentences apart at
        // every one of them.
        var cells = ProjectImporters.SplitRow("a,\"one, two\",c", ',');

        Assert.Equal(["a", "one, two", "c"], cells);
    }

    [Fact]
    public void ADoubledQuoteInsideAQuotedCellIsOneQuote()
        => Assert.Equal(["she said \"no\""],
            ProjectImporters.SplitRow("\"she said \"\"no\"\"\"", ','));

    [Fact]
    public void AnEmptyCellStaysAnEmptyCell()
        => Assert.Equal(["a", "", "c"], ProjectImporters.SplitRow("a,,c", ','));

    [Fact]
    public void TabsWorkTheSameWayAsCommas()
        => Assert.Equal(["a", "b c"], ProjectImporters.SplitRow("a\t\"b c\"", '\t'));

    // ── Text to markup ──

    [Fact]
    public void BlankLineSeparatedBlocksBecomeParagraphs()
        => Assert.Equal("<p>One.</p><p>Two.</p>",
            ProjectImporters.ToHtml("One.\n\nTwo."));

    [Fact]
    public void ASingleNewlineInsideAParagraphIsASoftWrap()
        // Every editor prose comes from wraps lines. Treating each as a
        // paragraph would double the paragraph count of an imported book.
        => Assert.Equal("<p>One sentence over two lines.</p>",
            ProjectImporters.ToHtml("One sentence\nover two lines."));

    [Fact]
    public void MarkupCharactersInImportedProseAreEscaped()
        => Assert.Equal("<p>She wrote &lt;i&gt; &amp; left.</p>",
            ProjectImporters.ToHtml("She wrote <i> & left."));

    [Fact]
    public void NothingInMeansNothingOut()
    {
        Assert.Equal(string.Empty, ProjectImporters.ToHtml(string.Empty));
        Assert.Equal(string.Empty, ProjectImporters.ToHtml("   \n\n  "));
    }

    // ── Whole imports, against a fake host ──

    [Fact]
    public async Task ADelimitedFileBecomesChaptersAndScenes()
    {
        var host = new FakeHost();
        var path = Write("book.csv",
            "chapter,scene,text\nOne,Arrival,\"The bell rang, once.\"\nOne,After,Nobody spoke.\nTwo,Later,The tide turned.");

        var report = await ProjectImporters.DelimitedAsync(host, path);

        Assert.Equal(2, report.Chapters);
        Assert.Equal(3, report.Scenes);
        Assert.Empty(report.Skipped);
        Assert.Equal(["One", "Two"], host.Chapters.Select(c => c.Title));
        Assert.Contains("The bell rang, once.", host.Chapters[0].Scenes[0].Html);
    }

    [Fact]
    public async Task ColumnsAreFoundByNameInAnyOrder()
    {
        var host = new FakeHost();
        var path = Write("odd.csv", "body,title,part\nProse here.,A scene,A part");

        var report = await ProjectImporters.DelimitedAsync(host, path);

        Assert.Equal(1, report.Scenes);
        Assert.Equal("A part", host.Chapters[0].Title);
        Assert.Equal("A scene", host.Chapters[0].Scenes[0].Title);
    }

    [Fact]
    public async Task ADelimitedFileWithNoTextColumnIsRefusedRatherThanGuessed()
    {
        var host = new FakeHost();
        var path = Write("nope.csv", "one,two,three\na,b,c");

        var report = await ProjectImporters.DelimitedAsync(host, path);

        Assert.Equal(0, report.Scenes);
        Assert.Single(report.Skipped);
        Assert.Empty(host.Chapters);
    }

    [Fact]
    public async Task AShortRowIsSkippedAndReportedRatherThanPartlyImported()
    {
        var host = new FakeHost();
        var path = Write("ragged.csv", "chapter,scene,text\nOne,Good,Prose.\nOne");

        var report = await ProjectImporters.DelimitedAsync(host, path);

        Assert.Equal(1, report.Scenes);
        Assert.Single(report.Skipped);
        Assert.Contains("Row 3", report.Skipped[0]);
    }

    [Fact]
    public async Task AFileThatIsNotThereOrHasNoRowsSaysSo()
    {
        var host = new FakeHost();

        Assert.Single((await ProjectImporters.DelimitedAsync(host, Path.Combine(_dir, "missing.csv"))).Skipped);
        Assert.Single((await ProjectImporters.DelimitedAsync(host, Write("bare.csv", "chapter,text"))).Skipped);
    }

    [Fact]
    public async Task AFolderOfMarkdownBecomesChaptersFromItsSubfolders()
    {
        var host = new FakeHost();
        Write("book/Part One/01.md", "# Arrival\n\nThe bell rang.");
        Write("book/Part One/02.md", "# After\n\nNobody spoke.");
        Write("book/Part Two/01.md", "The tide turned.");

        var report = await ProjectImporters.MarkdownFolderAsync(host, Path.Combine(_dir, "book"));

        Assert.Equal(2, report.Chapters);
        Assert.Equal(3, report.Scenes);
        // A leading heading names the scene rather than becoming its first line.
        Assert.Equal("Arrival", host.Chapters[0].Scenes[0].Title);
        Assert.DoesNotContain("# Arrival", host.Chapters[0].Scenes[0].Html);
        // A file with no heading falls back to its own name.
        Assert.Equal("01", host.Chapters[1].Scenes[0].Title);
    }

    [Fact]
    public async Task FilesAtTheTopOfTheFolderGetAChapterNamedAfterIt()
    {
        var host = new FakeHost();
        Write("loose/one.md", "First.");
        Write("loose/two.txt", "Second.");

        var report = await ProjectImporters.MarkdownFolderAsync(host, Path.Combine(_dir, "loose"));

        Assert.Equal(1, report.Chapters);
        Assert.Equal("loose", host.Chapters[0].Title);
        Assert.Equal(2, report.Scenes);
    }

    [Fact]
    public async Task AFolderWithNothingReadableInItSaysSo()
    {
        var host = new FakeHost();
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));

        Assert.Single((await ProjectImporters.MarkdownFolderAsync(host, Path.Combine(_dir, "empty"))).Skipped);
        Assert.Single((await ProjectImporters.MarkdownFolderAsync(host, Path.Combine(_dir, "nope"))).Skipped);
    }

    [Fact]
    public async Task AScrivenerProjectBecomesChaptersFromItsFolders()
    {
        var host = new FakeHost();
        Write("Novel.scriv/Novel.scrivx",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <ScrivenerProject>
              <Binder>
                <BinderItem UUID="F1" Type="Folder">
                  <Title>Chapter One</Title>
                  <Children>
                    <BinderItem UUID="S1" Type="Text"><Title>Arrival</Title></BinderItem>
                    <BinderItem UUID="S2" Type="Text"><Title>After</Title></BinderItem>
                  </Children>
                </BinderItem>
                <BinderItem UUID="F2" Type="Folder">
                  <Title>Research</Title>
                  <Children>
                    <BinderItem UUID="S3" Type="Text"><Title>Tide tables</Title></BinderItem>
                  </Children>
                </BinderItem>
              </Binder>
            </ScrivenerProject>
            """);
        Write("Novel.scriv/Files/Data/S1/content.rtf", @"{\rtf1\ansi The bell rang.\par}");
        Write("Novel.scriv/Files/Docs/S2.rtf", @"{\rtf1\ansi Nobody spoke.\par}");

        var report = await ProjectImporters.ScrivenerAsync(host, Path.Combine(_dir, "Novel.scriv"));

        // Research is not manuscript, and importing it as a chapter is the
        // commonest way one of these tools makes a mess of somebody's binder.
        Assert.Equal(1, report.Chapters);
        Assert.Equal(2, report.Scenes);
        Assert.Equal("Chapter One", host.Chapters[0].Title);
        Assert.Contains("The bell rang.", host.Chapters[0].Scenes[0].Html);
        // Both of Scrivener's storage layouts are tried.
        Assert.Contains("Nobody spoke.", host.Chapters[0].Scenes[1].Html);
    }

    [Fact]
    public async Task AScrivenerSceneWithNoTextFileIsReportedRatherThanSilentlyEmpty()
    {
        var host = new FakeHost();
        Write("Novel.scriv/Novel.scrivx",
            """
            <ScrivenerProject><Binder>
              <BinderItem UUID="F1" Type="Folder"><Title>One</Title><Children>
                <BinderItem UUID="MISSING" Type="Text"><Title>Ghost</Title></BinderItem>
              </Children></BinderItem>
            </Binder></ScrivenerProject>
            """);

        var report = await ProjectImporters.ScrivenerAsync(host, Path.Combine(_dir, "Novel.scriv"));

        Assert.Equal(1, report.Scenes);
        Assert.Contains("no text file", Assert.Single(report.Skipped));
    }

    [Fact]
    public async Task AnEmptyScrivenerFolderIsSkippedRatherThanBecomingAnEmptyChapter()
    {
        var host = new FakeHost();
        Write("Novel.scriv/Novel.scrivx",
            """
            <ScrivenerProject><Binder>
              <BinderItem UUID="F1" Type="Folder"><Title>Nothing here</Title></BinderItem>
            </Binder></ScrivenerProject>
            """);

        Assert.Equal(0, (await ProjectImporters.ScrivenerAsync(
            host, Path.Combine(_dir, "Novel.scriv"))).Chapters);
    }

    [Fact]
    public async Task AMissingOrBrokenScrivenerProjectSaysSo()
    {
        var host = new FakeHost();

        Assert.Single((await ProjectImporters.ScrivenerAsync(host, Path.Combine(_dir, "nope"))).Skipped);
        var broken = Write("Broken.scriv/Broken.scrivx", "<ScrivenerProject><unclosed>");
        Assert.Single((await ProjectImporters.ScrivenerAsync(host, broken)).Skipped);
    }

    [Fact]
    public async Task AScrivxFilePathWorksAsWellAsItsFolder()
    {
        var host = new FakeHost();
        var scrivx = Write("Novel.scriv/Novel.scrivx",
            """
            <ScrivenerProject><Binder>
              <BinderItem UUID="F1" Type="Folder"><Title>One</Title><Children>
                <BinderItem UUID="S1" Type="Text"><Title>A</Title></BinderItem>
              </Children></BinderItem>
            </Binder></ScrivenerProject>
            """);
        Write("Novel.scriv/Files/Data/S1/content.txt", "Plain text prose.");

        var report = await ProjectImporters.ScrivenerAsync(host, scrivx);

        Assert.Equal(1, report.Chapters);
        Assert.Contains("Plain text prose.", host.Chapters[0].Scenes[0].Html);
    }

    // ── EPUB preflight ──

    [Fact]
    public async Task AWellFormedEpubPasses()
    {
        var path = Path.Combine(_dir, "good.epub");
        BuildEpub(path, opf:
            """
            <package><metadata>
              <dc:title>The Salt Road</dc:title><dc:language>en</dc:language>
              <dc:identifier>urn:uuid:1</dc:identifier>
            </metadata></package>
            """,
            extra: [("OEBPS/nav.xhtml", "<html><body><nav/></body></html>")]);

        var result = await new EpubPreflight().CheckAsync(path, "Epub");

        Assert.True(result.Ok);
        Assert.Empty(result.Problems);
        // It says what it is not, rather than implying it is a validator.
        Assert.Contains(result.Notes, n => n.Contains("not EPUBCheck"));
    }

    [Fact]
    public async Task MissingMetadataIsReportedOnePieceAtATime()
    {
        var path = Path.Combine(_dir, "bare.epub");
        BuildEpub(path, opf: "<package><metadata></metadata></package>",
            extra: [("OEBPS/nav.xhtml", "<html/>")]);

        var result = await new EpubPreflight().CheckAsync(path, "Epub");

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("dc:title"));
        Assert.Contains(result.Problems, p => p.Contains("dc:language"));
        Assert.Contains(result.Problems, p => p.Contains("dc:identifier"));
    }

    [Fact]
    public async Task ACompressedMimetypeEntryIsReported()
    {
        var path = Path.Combine(_dir, "compressed.epub");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            // Deliberately compressed and long enough that it actually shrinks.
            var entry = archive.CreateEntry("mimetype", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(new string('a', 500));
        }

        var result = await new EpubPreflight().CheckAsync(path, "Epub");

        Assert.Contains(result.Problems, p => p.Contains("compressed"));
    }

    [Fact]
    public async Task AMissingTableOfContentsAndContainerAreReported()
    {
        var path = Path.Combine(_dir, "noToc.epub");
        BuildEpub(path,
            opf: "<package><metadata><dc:title>T</dc:title><dc:language>en</dc:language><dc:identifier>i</dc:identifier></metadata></package>",
            includeContainer: false);

        var result = await new EpubPreflight().CheckAsync(path, "Epub");

        Assert.Contains(result.Problems, p => p.Contains("container.xml"));
        Assert.Contains(result.Problems, p => p.Contains("table of contents"));
    }

    [Fact]
    public async Task ImagesWithoutAltTextAreCounted()
    {
        var path = Path.Combine(_dir, "noAlt.epub");
        BuildEpub(path,
            opf: "<package><metadata><dc:title>T</dc:title><dc:language>en</dc:language><dc:identifier>i</dc:identifier></metadata></package>",
            extra:
            [
                ("OEBPS/nav.xhtml", "<html/>"),
                ("OEBPS/ch1.xhtml", "<html><body><img src=\"a.png\"><img src=\"b.png\" alt=\"A map\"></body></html>")
            ]);

        var result = await new EpubPreflight().CheckAsync(path, "Epub");

        Assert.Contains(result.Problems, p => p.Contains("1 image"));
    }

    [Fact]
    public async Task AFileThatIsNotAnEpubIsReportedRatherThanCrashing()
    {
        var notAZip = Write("notazip.epub", "just some text");

        var result = await new EpubPreflight().CheckAsync(notAZip, "Epub");

        Assert.False(result.Ok);
        Assert.Contains("not a readable EPUB", Assert.Single(result.Problems));
    }

    [Fact]
    public async Task AFileThatWasNotWrittenIsReported()
    {
        var result = await new EpubPreflight().CheckAsync(Path.Combine(_dir, "gone.epub"), "Epub");

        Assert.False(result.Ok);
        Assert.Contains("not written", Assert.Single(result.Problems));
    }

    [Fact]
    public async Task AnEpubWithNoPackageDocumentIsReported()
    {
        var path = Path.Combine(_dir, "noOpf.epub");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddStored(archive, "mimetype", "application/epub+zip");
            Add(archive, "META-INF/container.xml", "<container/>");
        }

        var result = await new EpubPreflight().CheckAsync(path, "Epub");

        Assert.Contains(result.Problems, p => p.Contains("package document"));
    }

    [Fact]
    public async Task AnUnmarkedCoverIsANoteRatherThanAProblem()
    {
        // Most shops want one, but a book without a cover is still a valid book,
        // so this must not fail the check.
        var path = Path.Combine(_dir, "noCover.epub");
        BuildEpub(path,
            opf:
            """
            <package><metadata><dc:title>T</dc:title><dc:language>en</dc:language>
            <dc:identifier>i</dc:identifier></metadata>
            <manifest><item id="i1" href="a.png" media-type="image/png"/></manifest></package>
            """,
            extra: [("OEBPS/nav.xhtml", "<html/>")]);

        var result = await new EpubPreflight().CheckAsync(path, "Epub");

        Assert.True(result.Ok);
        Assert.Contains(result.Notes, n => n.Contains("cover"));
    }

    [Fact]
    public void ThePreflightSaysWhichFormatsItKnowsAbout()
    {
        var preflight = new EpubPreflight();

        Assert.Contains("Epub", preflight.Formats);
        Assert.False(string.IsNullOrEmpty(preflight.DisplayName));
    }

    private static void BuildEpub(
        string path, string opf, bool includeContainer = true,
        (string Name, string Content)[]? extra = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddStored(archive, "mimetype", "application/epub+zip");
        if (includeContainer) Add(archive, "META-INF/container.xml", "<container/>");
        Add(archive, "OEBPS/content.opf", opf);
        foreach (var (name, content) in extra ?? []) Add(archive, name, content);
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static void AddStored(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
