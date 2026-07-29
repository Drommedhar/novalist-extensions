using Novalist.Extensions.Publish.Site;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// The site generator. A pure function from what a project contains to a list of
/// files, which is why it can be checked without writing anything to disk.
/// </summary>
public class PublishTests
{
    private static SiteEntry Entry(
        string id, string name, string typeKey = "character",
        (string, string)[]? sections = null, string[]? aliases = null)
        => new(id, typeKey, name, sections ?? [], aliases ?? []);

    private static SiteChapter Chapter(string title, params string[] paragraphs)
        => new(title, string.Empty, [new SiteScene("S", paragraphs)]);

    private static SiteContent Content(
        IReadOnlyList<SiteEntry>? entries = null, IReadOnlyList<SiteChapter>? chapters = null)
        => new(entries ?? [], chapters ?? []);

    private static string Find(IReadOnlyList<SiteFile> files, string path)
        => files.Single(f => f.RelativePath == path).Content;

    // ── What gets written ──

    [Fact]
    public void AWorldSiteHasAnIndexAndOnePagePerEntry()
    {
        var files = SiteGenerator.Generate(
            Content(entries: [Entry("e1", "Mira Vance"), Entry("e2", "Ashport", "location")]),
            new SiteOptions { Title = "The Salt Road" });

        Assert.Contains(files, f => f.RelativePath == "index.html");
        Assert.Contains(files, f => f.RelativePath == "mira-vance.html");
        Assert.Contains(files, f => f.RelativePath == "ashport.html");
    }

    [Fact]
    public void AManuscriptSiteHasAPagePerChapterAndNoEntryPages()
    {
        var files = SiteGenerator.Generate(
            Content(
                entries: [Entry("e1", "Mira")],
                chapters: [Chapter("One", "Prose."), Chapter("Two", "More.")]),
            new SiteOptions { Scope = SiteScope.Manuscript });

        Assert.Contains(files, f => f.RelativePath == "chapter-1.html");
        Assert.Contains(files, f => f.RelativePath == "chapter-2.html");
        Assert.DoesNotContain(files, f => f.RelativePath == "mira.html");
    }

    [Fact]
    public void EverythingMeansBoth()
    {
        var files = SiteGenerator.Generate(
            Content(entries: [Entry("e1", "Mira")], chapters: [Chapter("One", "Prose.")]),
            new SiteOptions { Scope = SiteScope.Everything });

        Assert.Contains(files, f => f.RelativePath == "mira.html");
        Assert.Contains(files, f => f.RelativePath == "chapter-1.html");
    }

    [Fact]
    public void AWorldSiteLeavesTheChaptersOut()
        => Assert.DoesNotContain(
            SiteGenerator.Generate(
                Content(chapters: [Chapter("One", "Prose.")]),
                new SiteOptions { Scope = SiteScope.World }),
            f => f.RelativePath.StartsWith("chapter-"));

    // ── Not publishing to the world by accident ──

    [Fact]
    public void CrawlersAreDiscouragedByDefault()
    {
        // Somebody generating a site for three beta readers has not decided to
        // publish their unfinished novel to the open internet.
        var files = SiteGenerator.Generate(Content(entries: [Entry("e1", "Mira")]), new SiteOptions());

        Assert.Contains("Disallow: /", Find(files, "robots.txt"));
        Assert.Contains("noindex", Find(files, "index.html"));
        Assert.Contains("noindex", Find(files, "mira.html"));
    }

    [Fact]
    public void ThatCanBeTurnedOffForSomethingActuallyMeantToBeFound()
    {
        var files = SiteGenerator.Generate(
            Content(entries: [Entry("e1", "Mira")]),
            new SiteOptions { DiscourageCrawlers = false });

        Assert.DoesNotContain(files, f => f.RelativePath == "robots.txt");
        Assert.DoesNotContain("noindex", Find(files, "index.html"));
    }

    // ── Pages that stay working ──

    [Fact]
    public void EveryPageCarriesItsOwnStylesAndNeedsNothingElse()
    {
        // No stylesheet to lose, no script, no font request. A page mailed on its
        // own still looks like itself.
        var page = Find(
            SiteGenerator.Generate(Content(entries: [Entry("e1", "Mira")]), new SiteOptions()),
            "mira.html");

        Assert.Contains("<style>", page);
        Assert.DoesNotContain("<script", page);
        Assert.DoesNotContain("<link", page);
        Assert.DoesNotContain("http://", page);
    }

    [Fact]
    public void PagesDeclareTheirEncodingAndAViewport()
    {
        var page = Find(SiteGenerator.Generate(Content(), new SiteOptions()), "index.html");

        Assert.Contains("charset=\"utf-8\"", page);
        Assert.Contains("viewport", page);
    }

    [Fact]
    public void PagesStyleForLightAndDark()
        => Assert.Contains("prefers-color-scheme:dark",
            Find(SiteGenerator.Generate(Content(), new SiteOptions()), "index.html"));

    // ── Linking ──

    [Fact]
    public void AWikiLinkToAPublishedEntryBecomesALink()
    {
        var files = SiteGenerator.Generate(
            Content(entries:
            [
                Entry("e1", "Mira", sections: [("Life", "She sailed with [[Tobin]] for years.")]),
                Entry("e2", "Tobin")
            ]),
            new SiteOptions());

        Assert.Contains("<a href=\"tobin.html\">Tobin</a>", Find(files, "mira.html"));
    }

    [Fact]
    public void AWikiLinkToSomethingNotPublishedBecomesPlainText()
    {
        // A link to a page that is not in the site is a broken link in somebody
        // else's browser, which is worse than no link at all.
        var files = SiteGenerator.Generate(
            Content(entries: [Entry("e1", "Mira", sections: [("Life", "She knew [[Nobody]].")])]),
            new SiteOptions());

        var page = Find(files, "mira.html");
        Assert.Contains("She knew Nobody.", page);
        Assert.DoesNotContain("nobody.html", page);
    }

    [Fact]
    public void APipedLinkKeepsItsLabel()
    {
        var files = SiteGenerator.Generate(
            Content(entries:
            [
                Entry("e1", "Mira", sections: [("Life", "She sailed with [[Tobin|the mate]].")]),
                Entry("e2", "Tobin")
            ]),
            new SiteOptions());

        Assert.Contains("<a href=\"tobin.html\">the mate</a>", Find(files, "mira.html"));
    }

    // ── Names and files ──

    [Fact]
    public void TwoEntriesWithTheSameNameGetSeparatePages()
    {
        // One silently replacing the other's page would lose an entry.
        var files = SiteGenerator.Generate(
            Content(entries: [Entry("e1", "Mira"), Entry("e2", "Mira", "location")]),
            new SiteOptions());

        Assert.Contains(files, f => f.RelativePath == "mira.html");
        Assert.Contains(files, f => f.RelativePath == "mira-2.html");
    }

    [Fact]
    public void ANameWithPunctuationStillMakesAUsableFileName()
    {
        var slugs = SiteGenerator.Slugs([Entry("e1", "Mira O'Brien-Vance, Esq.")]);

        Assert.Equal("mira-o-brien-vance-esq", slugs["e1"]);
    }

    [Fact]
    public void AnEntryWithNoUsableNameStillGetsAPage()
    {
        var slugs = SiteGenerator.Slugs([Entry("e1", "...", "character"), Entry("e2", "", "")]);

        Assert.Equal("character", slugs["e1"]);
        Assert.Equal("entry", slugs["e2"]);
    }

    // ── Reading ──

    [Fact]
    public void TheIndexGroupsEntriesByKindWithPeopleFirst()
    {
        var page = Find(
            SiteGenerator.Generate(
                Content(entries:
                [
                    Entry("e1", "Ashport", "location"),
                    Entry("e2", "Mira", "character")
                ]),
                new SiteOptions()),
            "index.html");

        Assert.True(page.IndexOf("People", StringComparison.Ordinal)
            < page.IndexOf("Places", StringComparison.Ordinal));
    }

    [Fact]
    public void TheIndexListsTheChaptersInOrder()
    {
        var page = Find(
            SiteGenerator.Generate(
                Content(chapters: [Chapter("First"), Chapter("Second")]),
                new SiteOptions { Scope = SiteScope.Manuscript }),
            "index.html");

        Assert.True(page.IndexOf("First", StringComparison.Ordinal)
            < page.IndexOf("Second", StringComparison.Ordinal));
    }

    [Fact]
    public void AChapterPageLinksForwardAndBack()
    {
        // Reading a book means going to the next chapter, so this is the one link
        // every page here has to get right.
        var files = SiteGenerator.Generate(
            Content(chapters: [Chapter("One"), Chapter("Two"), Chapter("Three")]),
            new SiteOptions { Scope = SiteScope.Manuscript });

        var middle = Find(files, "chapter-2.html");
        Assert.Contains("chapter-1.html", middle);
        Assert.Contains("chapter-3.html", middle);

        var first = Find(files, "chapter-1.html");
        Assert.DoesNotContain("chapter-0.html", first);
        var last = Find(files, "chapter-3.html");
        Assert.DoesNotContain("chapter-4.html", last);
    }

    [Fact]
    public void ASceneBreakIsMarkedBetweenScenes()
    {
        var files = SiteGenerator.Generate(
            Content(chapters:
            [
                new SiteChapter("One", string.Empty,
                [
                    new SiteScene("A", ["First."]),
                    new SiteScene("B", ["Second."])
                ])
            ]),
            new SiteOptions { Scope = SiteScope.Manuscript });

        Assert.Contains("<hr>", Find(files, "chapter-1.html"));
    }

    [Fact]
    public void TheFirstParagraphAfterAHeadingIsNotIndented()
    {
        var page = Find(
            SiteGenerator.Generate(
                Content(chapters: [Chapter("One", "First.", "Second.")]),
                new SiteOptions { Scope = SiteScope.Manuscript }),
            "chapter-1.html");

        Assert.Contains("<p class=\"first\">First.</p>", page);
        Assert.Contains("<p>Second.</p>", page);
    }

    [Fact]
    public void AliasesAreShownSoAReaderCanFindSomebody()
    {
        var files = SiteGenerator.Generate(
            Content(entries: [Entry("e1", "Mira Vance", aliases: ["the mate", "Vance"])]),
            new SiteOptions());

        Assert.Contains("the mate", Find(files, "index.html"));
        Assert.Contains("the mate", Find(files, "mira-vance.html"));
    }

    [Fact]
    public void SectionMarkupIsStrippedRatherThanPassedThrough()
    {
        var page = Find(
            SiteGenerator.Generate(
                Content(entries:
                [
                    Entry("e1", "Mira",
                        sections: [("Life", "<p><em>She</em> sailed.</p><p>Then she did not.</p>")])
                ]),
                new SiteOptions()),
            "mira.html");

        Assert.Contains("<p>She sailed.</p>", page);
        Assert.Contains("<p>Then she did not.</p>", page);
        Assert.DoesNotContain("<em>", page);
    }

    // ── Being wrong safely ──

    [Fact]
    public void ProseThatLooksLikeMarkupIsEscaped()
    {
        var page = Find(
            SiteGenerator.Generate(
                Content(entries:
                [
                    Entry("e1", "Mira", sections: [("Life", "She wrote &lt;script&gt; on the board.")])
                ]),
                new SiteOptions()),
            "mira.html");

        Assert.DoesNotContain("<script>", page);
    }

    [Fact]
    public void ATitleWithAnAmpersandOrAQuoteDoesNotBreakThePage()
    {
        var page = Find(
            SiteGenerator.Generate(Content(), new SiteOptions { Title = "Salt & \"Ash\"" }),
            "index.html");

        Assert.Contains("&amp;", page);
        Assert.Contains("&quot;", page);
    }

    [Fact]
    public void AnEmptyProjectStillProducesAReadableIndex()
    {
        var files = SiteGenerator.Generate(Content(), new SiteOptions { Title = "Nothing yet" });

        var page = Find(files, "index.html");
        Assert.Contains("Nothing yet", page);
        Assert.Contains("Nothing was selected to publish", page);
    }

    [Fact]
    public void AnEntryWithNothingWrittenSaysSoRatherThanBeingBlank()
        => Assert.Contains("Nothing written here yet", Find(
            SiteGenerator.Generate(
                Content(entries: [Entry("e1", "Mira", sections: [("Life", "   ")])]),
                new SiteOptions()),
            "mira.html"));

    [Fact]
    public void AChapterWithNoProseSaysSo()
        => Assert.Contains("no prose yet", Find(
            SiteGenerator.Generate(
                Content(chapters: [new SiteChapter("One", "", [new SiteScene("S", [])])]),
                new SiteOptions { Scope = SiteScope.Manuscript }),
            "chapter-1.html"));

    [Fact]
    public void AnActLabelIsShownWhereThereIsOne()
    {
        var files = SiteGenerator.Generate(
            Content(chapters:
            [
                new SiteChapter("One", "Act I", [new SiteScene("S", ["Prose."])])
            ]),
            new SiteOptions { Scope = SiteScope.Manuscript });

        Assert.Contains("Act I", Find(files, "chapter-1.html"));
        Assert.Contains("Act I", Find(files, "index.html"));
    }

    [Fact]
    public void EveryPageButTheIndexLinksHome()
    {
        var files = SiteGenerator.Generate(
            Content(entries: [Entry("e1", "Mira")]),
            new SiteOptions { Title = "The Salt Road" });

        Assert.Contains("index.html", Find(files, "mira.html"));
        // The index does not link to itself.
        Assert.DoesNotContain("class=\"home\"", Find(files, "index.html"));
    }
}
