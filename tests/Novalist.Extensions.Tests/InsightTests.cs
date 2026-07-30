using Novalist.Extensions.Insight.Analysis;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// The analysis passes.
///
/// Each is a pure function over what a project contains, which is what makes them
/// testable and is also why they belong outside core. The tests care most about
/// the two ways a report like this fails: crying wolf, and missing the finding.
/// </summary>
public class InsightTests
{
    // ── Concordance ──

    [Fact]
    public void WordsAreCountedAcrossScenesAndWithinThem()
    {
        var counts = Concordance.Build([
            "The lantern swung. The lantern creaked.",
            "Another lantern entirely."
        ]);

        var lantern = counts.Single(w => w.Word == "lantern");
        Assert.Equal(3, lantern.Count);
        // Three uses across two scenes is a different thing from three in one.
        Assert.Equal(2, lantern.Scenes);
    }

    [Fact]
    public void StopWordsAreLeftOut()
    {
        var counts = Concordance.Build(["The bell and the rope and the tower."]);

        Assert.DoesNotContain(counts, w => w.Word == "the");
        Assert.DoesNotContain(counts, w => w.Word == "and");
        Assert.Contains(counts, w => w.Word == "bell");
    }

    [Fact]
    public void AnEmptyStopListCountsEverything()
    {
        // Somebody counting their own dialogue tags wants nothing filtered.
        var counts = Concordance.Build(["The bell tolled."], stopWords: []);

        Assert.Contains(counts, w => w.Word == "the");
    }

    [Fact]
    public void SaidIsNotAStopWordBecauseSomePeopleAreCountingIt()
        => Assert.DoesNotContain("said", Concordance.DefaultStopWords);

    [Fact]
    public void ShortWordsAreSkipped()
    {
        var counts = Concordance.Build(["Go up to it now"], stopWords: [], minimumLength: 3);

        Assert.DoesNotContain(counts, w => w.Word == "go");
        Assert.Contains(counts, w => w.Word == "now");
    }

    [Fact]
    public void CaseDoesNotSplitAWordInTwo()
    {
        var counts = Concordance.Build(["Lantern. lantern. LANTERN."]);

        var lantern = Assert.Single(counts, w => w.Word == "lantern");
        Assert.Equal(3, lantern.Count);
    }

    [Fact]
    public void AnApostropheStaysInsideItsWord()
    {
        var counts = Concordance.Build(["Nobody's fault. Nobody's problem."], stopWords: []);

        Assert.Contains(counts, w => w.Word == "nobody's");
    }

    [Fact]
    public void CountsComeBackHighestFirst()
    {
        var counts = Concordance.Build(["bell bell bell rope rope tower"], stopWords: []);

        Assert.Equal(["bell", "rope", "tower"], counts.Select(w => w.Word));
    }

    [Fact]
    public void AHabitIsAWordSpreadAcrossScenesRatherThanPiledIntoOne()
    {
        // Eight uses in one scene is that scene's subject. Across five scenes it
        // is a tic, and that is the one worth telling somebody about.
        var counts = Concordance.Build([
            "shrugged", "shrugged", "shrugged", "shrugged", "shrugged",
            "lantern lantern lantern lantern lantern lantern"
        ]);

        var habits = Concordance.Habits(counts);

        Assert.Contains(habits, w => w.Word == "shrugged");
        Assert.DoesNotContain(habits, w => w.Word == "lantern");
    }

    [Fact]
    public void NothingToCountIsAnEmptyList()
        => Assert.Empty(Concordance.Build([]));

    [Fact]
    public void ANullSceneDoesNotTakeTheCountDown()
        => Assert.Empty(Concordance.Build([null!]));

    // ── Name drift ──

    [Fact]
    public void ASpellingOneEditAwayFromACodexNameIsReported()
    {
        var findings = NameDrift.Find(
            ["Siobhan"],
            [("Chapter One", "Siobhan waited."), ("Chapter Four", "Siobahn waited again.")]);

        var finding = Assert.Single(findings);
        Assert.Equal("Siobhan", finding.Known);
        Assert.Equal("Siobahn", finding.Found);
        Assert.Equal(1, finding.Occurrences);
        Assert.Equal("Chapter Four", Assert.Single(finding.SceneTitles));
    }

    [Fact]
    public void ANameTheCodexKnowsIsNotDrift()
        => Assert.Empty(NameDrift.Find(
            ["Siobhan", "Ashport"], [("One", "Siobhan reached Ashport.")]));

    [Fact]
    public void AWordTwoEditsAwayIsNotReportedByDefault()
        // Two edits starts reporting genuinely different names as drift.
        => Assert.Empty(NameDrift.Find(["Siobhan"], [("One", "Siobbahn")]));

    [Fact]
    public void OccurrencesAndScenesAccumulate()
    {
        var findings = NameDrift.Find(
            ["Kaeleigh"],
            [("One", "Kaleigh. Kaleigh again."), ("Two", "Kaleigh once more.")]);

        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.Occurrences);
        Assert.Equal(2, finding.SceneTitles.Count);
    }

    [Fact]
    public void LowerCaseIsNotWhatThisIsAbout()
    {
        // A name typed in lower case is a different mistake, and reporting it
        // here would bury the spellings that matter.
        var findings = NameDrift.Find(["Ashport"], [("One", "ashport")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void NoNamesMeansNoFindingsRatherThanEverything()
        => Assert.Empty(NameDrift.Find([], [("One", "Siobhan and Ashport and Kaeleigh.")]));

    [Fact]
    public void BlankNamesAreIgnored()
        => Assert.Empty(NameDrift.Find(["", "   "], [("One", "Anything at all.")]));

    [Fact]
    public void TwoLettersInTheWrongOrderIsOneEditNotTwo()
    {
        // The commonest way a name gets misspelled. Plain Levenshtein scores it
        // as two, so a one-edit threshold would miss all of them.
        Assert.Equal(1, NameDrift.Distance("Siobahn", "Siobhan", 2));
        Assert.Equal(1, NameDrift.Distance("Ashprot", "Ashport", 2));
    }

    [Fact]
    public void TheDistanceCalculationGivesUpEarlyRatherThanFinishing()
    {
        // Bailing out matters: this runs over every capitalised word in a novel
        // against every name in the Codex.
        Assert.Equal(1, NameDrift.Distance("cat", "cot", 2));
        Assert.Equal(0, NameDrift.Distance("cat", "cat", 2));
        Assert.True(NameDrift.Distance("Siobhan", "Ashport", 1) > 1);
        Assert.Equal(3, NameDrift.Distance("", "cat", 5));
        Assert.Equal(3, NameDrift.Distance("cat", "", 5));
    }

    // ── Project health ──

    private static HealthInput Input(
        IReadOnlyList<HealthEntity>? entities = null,
        IReadOnlyList<HealthScene>? scenes = null,
        IReadOnlyList<string>? images = null,
        IReadOnlyList<HealthResearch>? research = null)
        => new(entities ?? [], scenes ?? [], images ?? [], research ?? []);

    private static HealthScene Scene(
        string title = "Arrival",
        string text = "Prose.",
        int words = 2,
        string[]? mentions = null,
        string[]? links = null,
        string[]? images = null)
        => new("s1", title, "One", text, words, mentions ?? [], links ?? [], images ?? []);

    [Fact]
    public void ALinkToNothingIsAProblem()
    {
        var findings = ProjectHealth.Run(Input(
            entities: [new HealthEntity("e1", "character", "Mira", [])],
            scenes: [Scene(links: ["Nobody"])]));

        // Mira is also unmentioned, so there are two findings; the dangling
        // link is the one this is about.
        var finding = Assert.Single(findings, f => f.Category == "Dangling link");
        Assert.Equal(Severity.Problem, finding.Severity);
        Assert.Contains("Nobody", finding.Detail);
    }

    [Fact]
    public void ALinkToSomethingThatExistsIsNotReported()
        => Assert.Empty(ProjectHealth.Run(Input(
            entities: [new HealthEntity("e1", "character", "Mira", [])],
            scenes: [Scene(links: ["Mira"], mentions: ["e1"])])));

    [Fact]
    public void TwoEntriesWithTheSameNameMakeALinkAmbiguous()
    {
        var findings = ProjectHealth.Run(Input(
            entities:
            [
                new HealthEntity("e1", "character", "Mira", []),
                new HealthEntity("e2", "location", "Mira", [])
            ],
            scenes: [Scene(text: "Mira", links: ["Mira"], mentions: ["e1", "e2"])]));

        Assert.Contains(findings, f => f.Category == "Ambiguous link");
    }

    [Fact]
    public void AnEntryNothingMentionsIsAWarningNotAProblem()
    {
        // Perfectly normal for a character who has not turned up yet.
        var findings = ProjectHealth.Run(Input(
            entities: [new HealthEntity("e1", "character", "Mira", [])],
            scenes: [Scene(text: "Nobody here.")]));

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("Mira", finding.Detail);
    }

    [Fact]
    public void AnEntryNamedInTheProseIsNotAnOrphanEvenWithoutAMention()
    {
        // A writer who never used an @-mention has not created an orphan.
        var findings = ProjectHealth.Run(Input(
            entities: [new HealthEntity("e1", "character", "Mira", [])],
            scenes: [Scene(text: "Mira crossed the yard.")]));

        Assert.DoesNotContain(findings, f => f.Category == "Unmentioned entry");
    }

    [Fact]
    public void AnImageNothingUsesIsANoteRatherThanAFault()
    {
        var findings = ProjectHealth.Run(Input(images: ["maps/harbour.png"]));

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Note, finding.Severity);
        Assert.Equal("Unused image", finding.Category);
    }

    [Fact]
    public void AnImageSomethingPointsAtIsNotUnused()
        => Assert.Empty(ProjectHealth.Run(Input(
            entities: [new HealthEntity("e1", "character", "Mira", ["portraits/mira.png"])],
            scenes: [Scene(text: "Mira")],
            images: ["portraits/mira.png"])));

    [Fact]
    public void PathsThatDifferOnlyBySeparatorAreTheSameImage()
    {
        // Comparing paths as written reports an image as both missing and unused
        // at the same time, which is the report contradicting itself.
        var findings = ProjectHealth.Run(Input(
            entities: [new HealthEntity("e1", "character", "Mira", [@"portraits\mira.png"])],
            scenes: [Scene(text: "Mira")],
            images: ["portraits/mira.png"]));

        Assert.Empty(findings);
    }

    [Fact]
    public void PointingAtAnImageThatIsNotThereIsAProblem()
    {
        var findings = ProjectHealth.Run(Input(
            entities: [new HealthEntity("e1", "character", "Mira", ["gone.png"])],
            scenes: [Scene(text: "Mira")],
            images: ["other.png"]));

        Assert.Contains(findings, f => f.Category == "Missing image" && f.Severity == Severity.Problem);
    }

    [Fact]
    public void MissingImagesAreNotReportedWhenThereIsNoImageListToCheckAgainst()
        // With no images at all, everything would look missing.
        => Assert.DoesNotContain(
            ProjectHealth.Run(Input(
                entities: [new HealthEntity("e1", "character", "Mira", ["x.png"])],
                scenes: [Scene(text: "Mira")])),
            f => f.Category == "Missing image");

    [Fact]
    public void ResearchAttachedToNothingIsANote()
    {
        var findings = ProjectHealth.Run(Input(
            research: [new HealthResearch("r1", "Tide tables", [], [])]));

        Assert.Contains(findings, f => f.Category == "Unfiled research");
    }

    [Fact]
    public void ResearchWithATagOrALinkIsFiled()
        => Assert.Empty(ProjectHealth.Run(Input(
            research:
            [
                new HealthResearch("r1", "Tagged", [], ["sea"]),
                new HealthResearch("r2", "Linked", ["e1"], [])
            ])));

    [Fact]
    public void AnEmptySceneIsAWarning()
    {
        var findings = ProjectHealth.Run(Input(scenes: [Scene(text: "", words: 0)]));

        Assert.Contains(findings, f => f.Category == "Empty scene" && f.Severity == Severity.Warning);
    }

    [Fact]
    public void FindingsComeBackWorstFirst()
    {
        var findings = ProjectHealth.Run(Input(
            entities: [new HealthEntity("e1", "character", "Mira", [])],
            scenes: [Scene(text: "Nobody.", links: ["Ghost"])],
            images: ["spare.png"]));

        Assert.Equal(
            [Severity.Problem, Severity.Warning, Severity.Note],
            findings.Select(f => f.Severity));
    }

    [Fact]
    public void AnEmptyProjectHasNothingToReport()
        => Assert.Empty(ProjectHealth.Run(Input()));

    // ── Pacing ──

    private static PacingPoint Point(
        int? intensity, string title = "S", int words = 1000,
        string emotion = "", string pov = "")
        => new(0, title, "One", "Act I", words, intensity, emotion, pov);

    [Fact]
    public void TheCurveNumbersItsScenesInReadingOrder()
    {
        var curve = PacingCurve.Build([Point(3, "A"), Point(5, "B"), Point(7, "C")]);

        Assert.Equal([0, 1, 2], curve.Select(p => p.Index));
        Assert.Equal("C", curve[2].SceneTitle);
    }

    [Fact]
    public void NoIntensitiesMeansNoCurveAndSaysSo()
    {
        var observation = Assert.Single(PacingCurve.Observe(
            PacingCurve.Build([Point(null), Point(null)])));

        Assert.Contains("No scene has an intensity", observation.Detail);
    }

    [Fact]
    public void TooFewRatedScenesSaysSoRatherThanGuessing()
    {
        var observation = Assert.Single(PacingCurve.Observe(
            PacingCurve.Build([Point(5), Point(6), Point(null)])));

        Assert.Contains("too few", observation.Detail);
    }

    [Fact]
    public void UnratedScenesAreCountedRatherThanFilledIn()
    {
        var observations = PacingCurve.Observe(PacingCurve.Build([
            Point(1), Point(3), Point(5), Point(7), Point(null), Point(null)
        ]));

        Assert.Contains(observations, o => o.Detail.Contains("2 scene(s) have no intensity"));
    }

    [Fact]
    public void AFlatStretchIsReportedAsPossiblyDeliberate()
    {
        var observations = PacingCurve.Observe(PacingCurve.Build([
            Point(4, "A"), Point(4, "B"), Point(4, "C"), Point(4, "D"), Point(4, "E"), Point(9, "F")
        ]));

        var flat = Assert.Single(observations, o => o.Detail.Contains("in a row"));
        Assert.Contains("5 scenes", flat.Detail);
        Assert.Contains("deliberate plateau", flat.Detail);
    }

    [Fact]
    public void FourFlatScenesIsNotWorthMentioning()
    {
        // Three or four flat scenes is a normal quiet passage. Reporting them
        // turns the whole list into noise.
        var observations = PacingCurve.Observe(PacingCurve.Build([
            Point(4), Point(4), Point(4), Point(4), Point(9)
        ]));

        Assert.DoesNotContain(observations, o => o.Detail.Contains("in a row"));
    }

    [Fact]
    public void TheSameEmotionSeveralScenesRunningIsReported()
    {
        var observations = PacingCurve.Observe(PacingCurve.Build([
            Point(1, emotion: "dread"), Point(3, emotion: "dread"),
            Point(5, emotion: "dread"), Point(7, emotion: "dread"), Point(2, emotion: "relief")
        ]));

        Assert.Contains(observations, o => o.Detail.Contains("dread"));
    }

    [Fact]
    public void APointOfViewThatDisappearsForALongStretchIsReported()
    {
        // This is the finding filtering cannot give you: hiding the other threads
        // to look at one is exactly what makes the gap invisible.
        var points = new List<PacingPoint> { Point(5, pov: "Mira"), Point(5, pov: "Mira") };
        for (var i = 0; i < 12; i++) points.Add(Point(5, pov: "Tobin"));
        points.Add(Point(5, pov: "Mira"));

        var observations = PacingCurve.Observe(PacingCurve.Build(points));

        Assert.Contains(observations, o => o.Detail.Contains("Mira") && o.Detail.Contains("12 scenes"));
    }

    [Fact]
    public void ASceneFarLongerThanTheRestIsANote()
    {
        var points = Enumerable.Range(0, 6).Select(_ => Point(5, words: 1000)).ToList();
        points.Add(Point(5, "The long one", words: 9000));

        var observations = PacingCurve.Observe(PacingCurve.Build(points));

        Assert.Contains(observations, o =>
            o.Severity == Severity.Note && o.Detail.Contains("The long one"));
    }

    [Fact]
    public void NoScenesMeansNoObservations()
        => Assert.Empty(PacingCurve.Observe([]));

    // ── Continuity worklist ──

    private static (string, string, string) Entity(string id, string name, string fingerprint)
        => (id, name, fingerprint);

    private static (string, string, string, string, IReadOnlyList<string>) SceneRow(
        string sceneId, string title, params string[] mentions)
        => ("c1", sceneId, "One", title, mentions);

    [Fact]
    public void AFingerprintChangesWithTheContentAndNotWithTheOrder()
    {
        var first = ContinuityWorklist.Fingerprint("Mira", "A mate",
            [("Childhood", "Docks"), ("Look", "Tall")]);
        var reordered = ContinuityWorklist.Fingerprint("Mira", "A mate",
            [("Look", "Tall"), ("Childhood", "Docks")]);
        var changed = ContinuityWorklist.Fingerprint("Mira", "A mate",
            [("Childhood", "Inland"), ("Look", "Tall")]);

        // A section moved is not a fact changed.
        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void AFirstRunHasNothingToCompareAgainst()
    {
        // Reporting the whole book as changed on the first run produces a list
        // nobody reads.
        var state = new WorklistState();

        var items = ContinuityWorklist.Build(
            [Entity("e1", "Mira", "aaa")], [SceneRow("s1", "Arrival", "e1")], state);

        Assert.Empty(items);
    }

    [Fact]
    public void ChangingAnEntryListsTheScenesThatMentionIt()
    {
        var state = new WorklistState();
        ContinuityWorklist.Rebase(state, [Entity("e1", "Mira", "aaa")]);

        var items = ContinuityWorklist.Build(
            [Entity("e1", "Mira", "bbb")],
            [SceneRow("s1", "Arrival", "e1"), SceneRow("s2", "Elsewhere", "e2")],
            state);

        var item = Assert.Single(items);
        Assert.Equal("Arrival", item.SceneTitle);
        Assert.Equal("Mira", Assert.Single(item.ChangedEntities));
        Assert.False(item.Reviewed);
    }

    [Fact]
    public void AnUnchangedEntryListsNothing()
    {
        var state = new WorklistState();
        ContinuityWorklist.Rebase(state, [Entity("e1", "Mira", "aaa")]);

        Assert.Empty(ContinuityWorklist.Build(
            [Entity("e1", "Mira", "aaa")], [SceneRow("s1", "Arrival", "e1")], state));
    }

    [Fact]
    public void TickingASceneOffMarksItReadWithoutHidingIt()
    {
        var state = new WorklistState();
        ContinuityWorklist.Rebase(state, [Entity("e1", "Mira", "aaa")]);

        ContinuityWorklist.MarkReviewed(state, "s1", ["e1"]);
        var items = ContinuityWorklist.Build(
            [Entity("e1", "Mira", "bbb")], [SceneRow("s1", "Arrival", "e1")], state);

        Assert.True(Assert.Single(items).Reviewed);
    }

    [Fact]
    public void TickingOffOneChangeDoesNotSilenceTheNextOne()
    {
        var state = new WorklistState();
        ContinuityWorklist.Rebase(state, [Entity("e1", "Mira", "aaa"), Entity("e2", "Tobin", "xxx")]);
        ContinuityWorklist.MarkReviewed(state, "s1", ["e1"]);

        // Now a different entry the same scene mentions changes.
        var items = ContinuityWorklist.Build(
            [Entity("e1", "Mira", "aaa"), Entity("e2", "Tobin", "yyy")],
            [SceneRow("s1", "Arrival", "e1", "e2")],
            state);

        Assert.False(Assert.Single(items).Reviewed);
    }

    [Fact]
    public void RebasingAcceptsEverythingAndForgetsTheTicks()
    {
        var state = new WorklistState();
        ContinuityWorklist.Rebase(state, [Entity("e1", "Mira", "aaa")]);
        ContinuityWorklist.MarkReviewed(state, "s1", ["e1"]);

        ContinuityWorklist.Rebase(state, [Entity("e1", "Mira", "bbb")]);

        // Those ticks were about changes that are now history.
        Assert.Empty(state.Reviewed);
        Assert.Empty(ContinuityWorklist.Build(
            [Entity("e1", "Mira", "bbb")], [SceneRow("s1", "Arrival", "e1")], state));
    }

    [Fact]
    public void UnreadScenesComeBeforeReadOnes()
    {
        var state = new WorklistState();
        ContinuityWorklist.Rebase(state, [Entity("e1", "Mira", "aaa")]);
        ContinuityWorklist.MarkReviewed(state, "s1", ["e1"]);

        var items = ContinuityWorklist.Build(
            [Entity("e1", "Mira", "bbb")],
            [SceneRow("s1", "Already read", "e1"), SceneRow("s2", "Still to read", "e1")],
            state);

        Assert.Equal("Still to read", items[0].SceneTitle);
    }

    [Fact]
    public void TheStateSurvivesARoundTripThroughItsFile()
    {
        var state = new WorklistState();
        ContinuityWorklist.Rebase(state, [Entity("e1", "Mira", "aaa")]);
        ContinuityWorklist.MarkReviewed(state, "s1", ["e1"]);

        var restored = ContinuityWorklist.Deserialise(ContinuityWorklist.Serialise(state));

        Assert.Equal("aaa", restored.EntityHashes["e1"]);
        Assert.Contains("s1|e1", restored.Reviewed);
    }

    [Fact]
    public void AnUnreadableStateFileStartsOverRatherThanThrowing()
    {
        // Losing which scenes were ticked off is a nuisance. A panel that will
        // not open is worse.
        Assert.Empty(ContinuityWorklist.Deserialise("{ not json").EntityHashes);
        Assert.Empty(ContinuityWorklist.Deserialise(null).EntityHashes);
        Assert.Empty(ContinuityWorklist.Deserialise("   ").EntityHashes);
    }

    // ── Who drops out of the book ──

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> Cast(
        params (string Name, int[] Chapters)[] rows)
        => rows.ToDictionary(
            r => r.Name,
            r => (IReadOnlyList<int>)r.Chapters.ToList(),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AGapIsCountedOnlyBetweenTheFirstAppearanceAndTheLast()
    {
        // Arriving in chapter eight is not an absence from one to seven; they
        // are not in the book yet, and counting it would bury the real gaps
        // under one row per late arrival.
        var rows = CastPresence.Build(10, Cast(("Late", [8, 9, 10])));

        var row = Assert.Single(rows);
        Assert.Equal(0, row.LongestGap);
        Assert.Equal(8, row.FirstChapter);
        Assert.Equal(10, row.LastChapter);
    }

    [Fact]
    public void TheLongestGapAndWhereItStartsAreReported()
    {
        var rows = CastPresence.Build(12, Cast(("Mira", [1, 2, 9, 12])));

        var row = Assert.Single(rows);
        // Six chapters between two and nine, starting at three.
        Assert.Equal(6, row.LongestGap);
        Assert.Equal(3, row.GapStartsAt);
        Assert.Equal(4, row.Appearances);
    }

    [Fact]
    public void ACharacterWhoLeavesEarlyIsCalledDropped()
    {
        // Last seen in chapter three of twelve: nine chapters without them, and
        // a reader has long since forgotten.
        var dropped = Assert.Single(CastPresence.Build(12, Cast(("Tobin", [1, 3]))));
        Assert.True(dropped.DisappearsEarly);

        // Last seen in chapter ten of twelve is not dropped - that is an ending.
        var ends = Assert.Single(CastPresence.Build(12, Cast(("Ada", [1, 10]))));
        Assert.False(ends.DisappearsEarly);
    }

    [Fact]
    public void AnEntryThatNeverReachesThePageIsReported()
    {
        // In the Codex, in no chapter: the one thing a cast list cannot show.
        var row = Assert.Single(CastPresence.Build(5, Cast(("Ghost", []))));

        Assert.Equal(0, row.Appearances);
        Assert.Equal(0, row.FirstChapter);
        Assert.False(row.DisappearsEarly);
    }

    [Fact]
    public void RepeatsAndOutOfRangeChaptersDoNotDistortTheCount()
    {
        // Three scenes of one chapter is one chapter, and a stale chapter
        // number from a deleted chapter is not an appearance.
        var row = Assert.Single(CastPresence.Build(3, Cast(("Mira", [2, 2, 2, 99, 0]))));

        Assert.Equal(1, row.Appearances);
        Assert.Equal(2, row.FirstChapter);
    }

    [Fact]
    public void TheWorstRowsComeFirst()
    {
        var rows = CastPresence.Build(12, Cast(
            ("Steady", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]),
            ("Gappy", [1, 7, 12]),
            ("Ghost", []),
            ("Dropped", [1, 2])));

        // A report you have to sort yourself has not answered anything.
        Assert.Equal(["Ghost", "Dropped", "Gappy", "Steady"], rows.Select(r => r.Name));
    }

}
