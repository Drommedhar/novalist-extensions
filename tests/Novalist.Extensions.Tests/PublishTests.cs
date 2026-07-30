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

    // ── Markdown in a section, and the language of the site's own words ──

    [Fact]
    public void SectionMarkdownIsRenderedRatherThanLeftAsAsterisks()
    {
        var page = Find(
            SiteGenerator.Generate(
                Content(entries:
                [
                    Entry("e1", "Mira", sections:
                    [
                        ("Life",
                         "She was **stubborn**, *once*, and ~~never~~ sorry.\n\n"
                         + "## Later\n\n- A ship\n- A debt\n\n> She kept both.")
                    ])
                ]),
                new SiteOptions()),
            "mira.html");

        Assert.Contains("<strong>stubborn</strong>", page);
        Assert.Contains("<em>once</em>", page);
        Assert.Contains("<del>never</del>", page);
        Assert.Contains("<li>A ship</li>", page);
        Assert.Contains("<blockquote><p>She kept both.</p></blockquote>", page);
        // A section heading is already an h2 on the page, so the "##" inside it
        // must not compete with it.
        Assert.Contains("<h4>Later</h4>", page);
        Assert.DoesNotContain("**stubborn**", page);
    }

    [Fact]
    public void AWikiLinkInsideMarkdownReachesThePageItNames()
    {
        var files = SiteGenerator.Generate(
            Content(entries:
            [
                Entry("e1", "Mira", sections: [("Life", "She sailed for [[Ashport]] and [[Nowhere]].")]),
                Entry("e2", "Ashport", "location")
            ]),
            new SiteOptions());

        var page = Find(files, "mira.html");
        Assert.Contains("<a href=\"ashport.html\">Ashport</a>", page);
        // Nothing was published under that name, and a link to a file that is not
        // in the folder is a broken link in somebody else's browser.
        Assert.Contains("Nowhere", page);
        Assert.DoesNotContain("nowhere.html", page);
    }

    [Fact]
    public void MarkupPastedIntoASectionCannotReachThePageAsMarkup()
    {
        var page = Find(
            SiteGenerator.Generate(
                Content(entries:
                [
                    Entry("e1", "Mira", sections:
                        [("Life", "<script>alert(1)</script> and [a](javascript:alert(1))")])
                ]),
                new SiteOptions()),
            "mira.html");

        Assert.DoesNotContain("<script>", page);
        Assert.DoesNotContain("javascript:", page);
    }

    [Fact]
    public void ALinkToTheOpenWebSurvives()
    {
        var page = Find(
            SiteGenerator.Generate(
                Content(entries:
                    [Entry("e1", "Mira", sections: [("Life", "See [the map](https://example.com/m).")])]),
                new SiteOptions()),
            "mira.html");

        Assert.Contains("<a href=\"https://example.com/m\">the map</a>", page);
    }

    [Fact]
    public void EverySiteDeclaresTheLanguageItIsWrittenIn()
    {
        var page = Find(
            SiteGenerator.Generate(Content(entries: [Entry("e1", "Mira")]),
                new SiteOptions { Language = "de" }),
            "index.html");

        Assert.Contains("<html lang=\"de\">", page);
        Assert.DoesNotContain("<html lang=\"en\">", page);
    }

    [Fact]
    public void TheSitesOwnWordsAreTheOnesItWasGiven()
    {
        var german = new SiteText
        {
            Contents = "Inhalt",
            Previous = "Zurück",
            Next = "Weiter",
            People = "Figuren",
            Character = "Figur",
            AlsoKnownAs = "Auch:",
            NothingWritten = "Hier steht noch nichts."
        };

        var files = SiteGenerator.Generate(
            Content(
                entries: [Entry("e1", "Mira", aliases: ["Vance"])],
                chapters: [Chapter("Eins", "A."), Chapter("Zwei", "B.")]),
            new SiteOptions { Scope = SiteScope.Everything, Text = german });

        var index = Find(files, "index.html");
        Assert.Contains("<h2>Inhalt</h2>", index);
        Assert.Contains("<h2>Figuren</h2>", index);

        var chapter = Find(files, "chapter-1.html");
        Assert.Contains(">Weiter</a>", chapter);
        Assert.Contains(">Inhalt</a>", chapter);

        var entry = Find(files, "mira.html");
        Assert.Contains("<p class=\"kind\">Figur</p>", entry);
        Assert.Contains("Auch: Vance", entry);
        Assert.Contains("Hier steht noch nichts.", entry);
    }

    [Fact]
    public void AnEmptySectionRendersToNothingAtAll()
    {
        Assert.Equal(string.Empty, Markup.ToHtml(null));
        Assert.Equal(string.Empty, Markup.ToHtml("   "));
        Assert.Equal(string.Empty, Markup.ToText(null));
    }

    [Fact]
    public void MarkdownReadsAsProseWithTheSyntaxTakenOff()
    {
        Assert.Equal(
            "A title\nShe was stubborn.\n- One\nQuoted.",
            Markup.ToText("# A title\nShe was **stubborn**.\n- One\n> Quoted."));
    }

    [Fact]
    public void AListThatChangesKindMidWayIsTwoLists()
    {
        var html = Markup.ToHtml("- One\n- Two\n1. First\n2. Second");

        Assert.Contains("<ul><li>One</li><li>Two</li></ul>", html);
        Assert.Contains("<ol><li>First</li><li>Second</li></ol>", html);
    }

    [Fact]
    public void ARuleIsARuleWhicheverWayItIsWritten()
    {
        foreach (var rule in new[] { "---", "***", "___" })
            Assert.Contains("<hr>", Markup.ToHtml($"A.\n\n{rule}\n\nB."));
    }

    [Fact]
    public void CodeSpansAreNotReadAsMarkup()
        => Assert.Contains("<code>**not bold**</code>", Markup.ToHtml("Try `**not bold**` here."));

    [Fact]
    public void AWikiLinkWithItsOwnLabelKeepsIt()
    {
        Assert.Contains(">the port<", Markup.ToHtml("[[Ashport|the port]]", _ => "ashport.html"));
        Assert.Equal("the port", Markup.ToText("[[Ashport|the port]]"));
    }

}
