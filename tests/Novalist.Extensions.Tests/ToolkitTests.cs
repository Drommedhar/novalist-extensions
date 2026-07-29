using Novalist.Extensions.Toolkit.Services;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// The sprint timer and the reader-mode extractor.
///
/// The sprint is tested against an injected clock rather than a real one, so the
/// tests describe what happens after twenty-five minutes without waiting for
/// twenty-five minutes.
/// </summary>
public class ToolkitTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    // ── Sprints ──

    [Fact]
    public void ASprintStartsIdleAndSaysSo()
    {
        var sprint = new Sprint();

        var snapshot = sprint.Snapshot(Noon);

        Assert.Equal(SprintPhase.Idle, snapshot.Phase);
        Assert.Equal(0, snapshot.SecondsLeft);
        Assert.Equal("sprint", snapshot.Label);
    }

    [Fact]
    public void StartingCountsDownFromTheWritingLength()
    {
        var sprint = new Sprint { WritingMinutes = 25 };

        sprint.Start(1000, Noon);

        var snapshot = sprint.Snapshot(Noon.AddMinutes(5));
        Assert.Equal(SprintPhase.Writing, snapshot.Phase);
        Assert.Equal(20 * 60, snapshot.SecondsLeft);
    }

    [Fact]
    public void WordsWrittenDuringTheSprintAreTheOnesCounted()
    {
        // Not the size of the scene: a writer who deletes two paragraphs and
        // writes three has written three.
        var sprint = new Sprint();
        sprint.Start(1000, Noon);

        sprint.Update(1300);

        Assert.Equal(300, sprint.Snapshot(Noon.AddMinutes(10)).WordsSoFar);
    }

    [Fact]
    public void DeletingMoreThanYouWriteIsZeroRatherThanNegative()
    {
        var sprint = new Sprint();
        sprint.Start(1000, Noon);

        sprint.Update(800);

        Assert.Equal(0, sprint.Snapshot(Noon.AddMinutes(5)).WordsSoFar);
    }

    [Fact]
    public void TheRateIsWordsOverTheTimeActuallyElapsed()
    {
        var sprint = new Sprint();
        sprint.Start(0, Noon);
        sprint.Update(500);

        Assert.Equal(50, sprint.Snapshot(Noon.AddMinutes(10)).WordsPerMinute);
    }

    [Fact]
    public void StoppingEarlyRecordsTheTimeItActuallyRan()
    {
        // Rounding up to the full span would make the rate a lie, and the rate is
        // the only reason to keep records.
        var sprint = new Sprint { WritingMinutes = 25 };
        sprint.Start(1000, Noon);
        sprint.Update(1200);

        var record = sprint.Stop(Noon.AddMinutes(10));

        Assert.NotNull(record);
        Assert.Equal(10, record!.Minutes);
        Assert.Equal(200, record.Words);
        Assert.Equal(SprintPhase.Idle, sprint.Phase);
    }

    [Fact]
    public void ASprintAbandonedAfterSecondsStillCountsAsAMinute()
        // Otherwise the rate divides by zero and reports something implausible.
        => Assert.Equal(1, new Sprint()
            .Let(s => s.Start(0, Noon))
            .Stop(Noon.AddSeconds(10))!.Minutes);

    [Fact]
    public void StoppingWhenNothingIsRunningRecordsNothing()
    {
        var sprint = new Sprint();

        Assert.Null(sprint.Stop(Noon));
        Assert.Empty(sprint.History);
    }

    [Fact]
    public void AFinishedWritingPhaseRollsIntoARest()
    {
        var sprint = new Sprint { WritingMinutes = 25, RestingMinutes = 5 };
        sprint.Start(1000, Noon);
        sprint.Update(1500);

        var record = sprint.Tick(Noon.AddMinutes(25));

        Assert.NotNull(record);
        Assert.Equal(500, record!.Words);
        Assert.Equal(SprintPhase.Resting, sprint.Phase);
        Assert.Equal(5 * 60, sprint.Snapshot(Noon.AddMinutes(25)).SecondsLeft);
    }

    [Fact]
    public void AFinishedRestGoesIdleRatherThanStartingAnotherSprint()
    {
        // The next sprint should be a decision, not something that happens to the
        // writer while they are making tea.
        var sprint = new Sprint { WritingMinutes = 25, RestingMinutes = 5 };
        sprint.Start(0, Noon);
        sprint.Tick(Noon.AddMinutes(25));

        Assert.Null(sprint.Tick(Noon.AddMinutes(30)));
        Assert.Equal(SprintPhase.Idle, sprint.Phase);
    }

    [Fact]
    public void TickingBeforeThePhaseEndsChangesNothing()
    {
        var sprint = new Sprint { WritingMinutes = 25 };
        sprint.Start(0, Noon);

        Assert.Null(sprint.Tick(Noon.AddMinutes(24)));
        Assert.Equal(SprintPhase.Writing, sprint.Phase);
    }

    [Fact]
    public void TickingWhileIdleDoesNothing()
        => Assert.Null(new Sprint().Tick(Noon));

    [Fact]
    public void WordsAreOnlyCountedWhileWriting()
    {
        var sprint = new Sprint();
        sprint.Start(0, Noon);
        sprint.Tick(Noon.AddMinutes(25));

        // Now resting: typing during a rest is not part of a sprint.
        sprint.Update(9999);

        Assert.Equal(0, sprint.Snapshot(Noon.AddMinutes(26)).WordsSoFar);
    }

    [Fact]
    public void TotalsAddUpAcrossSprints()
    {
        var sprint = new Sprint { WritingMinutes = 10 };
        sprint.Start(0, Noon);
        sprint.Update(300);
        sprint.Tick(Noon.AddMinutes(10));
        sprint.Start(300, Noon.AddMinutes(20));
        sprint.Update(500);
        sprint.Tick(Noon.AddMinutes(30));

        var (words, minutes, rate) = sprint.Totals();

        Assert.Equal(500, words);
        Assert.Equal(20, minutes);
        Assert.Equal(25, rate);
    }

    [Fact]
    public void NoSprintsMeansARateOfZeroRatherThanADivisionByZero()
        => Assert.Equal(0, new Sprint().Totals().WordsPerMinute);

    [Fact]
    public void TheLabelIsShortEnoughForAStatusBar()
    {
        var sprint = new Sprint { WritingMinutes = 25 };
        sprint.Start(0, Noon);
        sprint.Update(120);

        Assert.Equal("20:00 · 120w", sprint.Snapshot(Noon.AddMinutes(5)).Label);
    }

    [Fact]
    public void SettingsAndHistorySurviveARoundTrip()
    {
        var sprint = new Sprint { WritingMinutes = 40, RestingMinutes = 8 };
        sprint.Start(0, Noon);
        sprint.Update(400);
        sprint.Stop(Noon.AddMinutes(40));

        var restored = Sprint.Load(sprint.Serialise());

        Assert.Equal(40, restored.WritingMinutes);
        Assert.Equal(8, restored.RestingMinutes);
        Assert.Equal(400, Assert.Single(restored.History).Words);
    }

    [Fact]
    public void AnUnreadableHistoryStartsFreshRatherThanThrowing()
    {
        // Losing a record of past sprints is a shame. A timer that refuses to
        // open is worse.
        Assert.Empty(Sprint.Load("{ not json").History);
        Assert.Empty(Sprint.Load(null).History);
        Assert.Equal(25, Sprint.Load("   ").WritingMinutes);
    }

    [Fact]
    public void NonsenseLengthsFallBackToSomethingUsable()
    {
        var restored = Sprint.Load("""{"writingMinutes":0,"restingMinutes":-5}""");

        Assert.Equal(25, restored.WritingMinutes);
        Assert.Equal(5, restored.RestingMinutes);
    }

    // ── Reader mode ──

    [Fact]
    public void TheArticleBodyIsWhatIsKept()
    {
        var page = ReaderMode.Extract(
            """
            <html><head><title>Tide tables - Example Society</title></head>
            <body>
              <nav><a href="/">Home</a><a href="/about">About</a></nav>
              <header>Example Maritime Society</header>
              <article>
                <h1>Reading a tide table</h1>
                <p>A tide table lists the predicted times and heights of high and low water.</p>
                <p>The figures are given for a standard port, and secondary ports are worked out from it.</p>
              </article>
              <footer>Copyright 1998</footer>
            </body></html>
            """,
            "https://example.test/tides");

        Assert.Equal("Reading a tide table", page.Title);
        Assert.Contains("predicted times", page.Text);
        Assert.Contains("standard port", page.Text);
        // The furniture is gone.
        Assert.DoesNotContain("Home", page.Text);
        Assert.DoesNotContain("Copyright", page.Text);
    }

    [Fact]
    public void TheHeadingIsPreferredOverTheTitleElement()
        // A title element usually carries the site name too, and a research note
        // called "Tide tables - Example Society - Home" is worse than one called
        // "Tide tables".
        => Assert.Equal("Just the headline", ReaderMode.Extract(
            "<html><head><title>Just the headline | Some Site</title></head>"
            + "<body><h1>Just the headline</h1><p>" + new string('x', 60) + "</p></body></html>",
            "https://example.test/x").Title);

    [Fact]
    public void TheTitleElementIsUsedWhenThereIsNoHeading()
        => Assert.Equal("Only a title", ReaderMode.Extract(
            "<html><head><title>Only a title</title></head><body><p>"
            + new string('x', 60) + "</p></body></html>",
            "https://example.test/x").Title);

    [Fact]
    public void APageWithNoTitleAtAllIsNamedFromItsAddress()
    {
        // The last path segment is nearly always the slug, which is nearly always
        // the headline.
        var page = ReaderMode.Extract(
            "<html><body><p>" + new string('x', 60) + "</p></body></html>",
            "https://example.test/reading-a-tide-table");

        Assert.Equal("reading a tide table", page.Title);
    }

    [Fact]
    public void AnAddressWithNoPathFallsBackToTheHost()
        => Assert.Equal("example.test",
            ReaderMode.Extract("<html><body></body></html>", "https://example.test/").Title);

    [Fact]
    public void ScriptsAndStylesNeverReachTheText()
    {
        var page = ReaderMode.Extract(
            "<html><body><script>var tracking = 1;</script>"
            + "<style>p { color: red }</style>"
            + "<p>" + new string('a', 60) + "</p></body></html>",
            "https://example.test/x");

        Assert.DoesNotContain("tracking", page.Text);
        Assert.DoesNotContain("color", page.Text);
    }

    [Fact]
    public void ShortBlocksAreDroppedAsFurniture()
    {
        // A byline, a share prompt, a cookie notice. Keeping them costs more than
        // losing the occasional real one-line paragraph.
        var page = ReaderMode.Extract(
            "<html><body><p>Share this</p><p>By A Writer</p>"
            + "<p>" + new string('b', 80) + "</p></body></html>",
            "https://example.test/x");

        Assert.DoesNotContain("Share this", page.Text);
        Assert.Contains("bbbb", page.Text);
    }

    [Fact]
    public void APageOfOnlyShortBlocksKeepsThemRatherThanReturningNothing()
    {
        var page = ReaderMode.Extract(
            "<html><body><p>Short one.</p><p>Short two.</p></body></html>",
            "https://example.test/x");

        Assert.Contains("Short one.", page.Text);
    }

    [Fact]
    public void EntitiesAreDecodedAndTagsInsideAParagraphAreDropped()
    {
        var page = ReaderMode.Extract(
            "<html><body><article><p>Salt &amp; ash, <em>plainly</em> put, "
            + new string('c', 40) + "</p></article></body></html>",
            "https://example.test/x");

        Assert.Contains("Salt & ash", page.Text);
        Assert.DoesNotContain("<em>", page.Text);
    }

    [Fact]
    public void ListItemsAndQuotesCountAsProse()
    {
        var page = ReaderMode.Extract(
            "<html><body><article>"
            + "<li>" + new string('d', 50) + "</li>"
            + "<blockquote>" + new string('e', 50) + "</blockquote>"
            + "</article></body></html>",
            "https://example.test/x");

        Assert.Contains("dddd", page.Text);
        Assert.Contains("eeee", page.Text);
    }

    [Fact]
    public void APageWithNoBlockElementsFallsBackToItsWholeText()
    {
        var page = ReaderMode.Extract(
            "<html><body><div>Everything is in one div, unhelpfully.</div></body></html>",
            "https://example.test/x");

        Assert.Contains("one div", page.Text);
    }

    [Fact]
    public void NothingInMeansAnEmptyCapture()
    {
        var page = ReaderMode.Extract(string.Empty, "https://example.test/x");

        Assert.Equal(string.Empty, page.Text);
        Assert.False(string.IsNullOrEmpty(page.Title));
    }

    [Fact]
    public void CapturedTextBecomesParagraphsAndKeepsItsSource()
    {
        // A captured page without its address is a quote nobody can check.
        var html = ReaderMode.ToHtml(
            new Captured("T", "First para.\n\nSecond para.", "https://example.test/x"));

        Assert.Contains("<p>First para.</p>", html);
        Assert.Contains("<p>Second para.</p>", html);
        Assert.Contains("https://example.test/x", html);
    }

    [Fact]
    public void MarkupInCapturedTextIsEscaped()
        => Assert.Contains("&lt;script&gt;", ReaderMode.ToHtml(
            new Captured("T", "<script>alert(1)</script>", "")));
}

/// <summary>Lets a test start a sprint and keep the reference in one expression.</summary>
internal static class SprintTestExtensions
{
    public static Sprint Let(this Sprint sprint, Action<Sprint> action)
    {
        action(sprint);
        return sprint;
    }
}
