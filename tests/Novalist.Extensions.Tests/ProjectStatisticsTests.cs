using Novalist.Extensions.Insight.Analysis;
using Xunit;

namespace Novalist.Extensions.Tests;

/// <summary>
/// What is in the manuscript, counted, and how long it would print.
///
/// The status bar has a popover of counts and the only exact page number
/// anywhere comes out of the Normseiten export, so "how long is this book in
/// the shape it will be printed" meant exporting to find out.
/// </summary>
public class ProjectStatisticsTests
{
    private static StatisticsRow Row(int words, int characters = 0, int dense = 0)
        => new("draft", 1, 1, words, characters, dense);

    [Fact]
    public void AnEmptyManuscriptHasNoPages()
    {
        // Not one. A book nobody has written yet has no pages.
        Assert.Equal(0, ProjectStatistics.Pages(0, 350));
        Assert.Equal(0, ProjectStatistics.Pages(-10, 350));
    }

    [Fact]
    public void APageHalfFullIsStillAPage()
    {
        // Rounding down would report a shorter book than exists.
        Assert.Equal(1, ProjectStatistics.Pages(1, 350));
        Assert.Equal(1, ProjectStatistics.Pages(350, 350));
        Assert.Equal(2, ProjectStatistics.Pages(351, 350));
    }

    [Fact]
    public void APageSizeOfNothingProducesNothingRatherThanDividingByZero()
        => Assert.Equal(0, ProjectStatistics.Pages(1000, 0));

    [Fact]
    public void ANovelComesOutAboutTheRightLength()
    {
        // 90,000 words at a trade-paperback page is a book you can hold.
        Assert.Equal(258, ProjectStatistics.Pages(90_000, ProjectStatistics.DefaultWordsPerPage));
    }

    [Theory]
    [InlineData(PageBasis.Words, 3)]
    [InlineData(PageBasis.Characters, 2)]
    public void EachBasisCountsItsOwnThing(PageBasis basis, int expected)
    {
        // Both are offered rather than one: words per page is what a writer
        // means by a paperback page, and characters per page is closer for a
        // language where a word count says less about set length.
        var row = Row(words: 1_000, characters: 2_000);

        Assert.Equal(expected, ProjectStatistics.Pages(row, basis, 350, 1800));
    }

    [Fact]
    public void CharactersAreCountedWithAndWithoutSpaces()
    {
        var (all, dense) = ProjectStatistics.CountCharacters("She arrives.");

        Assert.Equal(12, all);
        Assert.Equal(11, dense);
    }

    [Fact]
    public void EveryKindOfWhitespaceIsSpace()
    {
        var (all, dense) = ProjectStatistics.CountCharacters("a\tb\nc d");

        Assert.Equal(7, all);
        Assert.Equal(4, dense);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingCountsAsNothing(string? text)
    {
        var (all, dense) = ProjectStatistics.CountCharacters(text);

        Assert.Equal(0, all);
        Assert.Equal(0, dense);
    }
}
