namespace Novalist.Extensions.Insight.Analysis;

/// <summary>How a page estimate is arrived at.</summary>
public enum PageBasis
{
    /// <summary>Words per page. What a writer means by "a paperback page".</summary>
    Words,

    /// <summary>Characters per page, spaces included. Closer for a language
    /// where word counts say less about set length - German compounds, and any
    /// language written without spaces.</summary>
    Characters
}

/// <summary>One line of the statistics table.</summary>
public sealed record StatisticsRow(
    string Scope, int Chapters, int Scenes, int Words, int Characters, int CharactersNoSpaces);

/// <summary>
/// What is in the manuscript, counted.
///
/// The status bar has a popover with per-chapter and per-scene counts, and the
/// only exact page number anywhere comes out of the Normseiten DOCX preset - so
/// a writer asking "how long is this book, in the shape it will be printed" had
/// to export to find out.
///
/// A page estimate is an estimate and the report says so. Both bases are given
/// rather than one, because words-per-page is what a writer means by a paperback
/// page and characters-per-page is closer for a language where a word count says
/// less about set length.
/// </summary>
public static class ProjectStatistics
{
    /// <summary>A trade-paperback page, in words. The figure Scrivener defaults to.</summary>
    public const int DefaultWordsPerPage = 350;

    /// <summary>A trade-paperback page, in characters with spaces.</summary>
    public const int DefaultCharactersPerPage = 1800;

    /// <summary>
    /// Pages for a count, rounded up: a page half full is still a page, and
    /// rounding down would report a shorter book than exists.
    /// </summary>
    public static int Pages(int count, int perPage)
    {
        if (count <= 0 || perPage <= 0) return 0;
        return (count + perPage - 1) / perPage;
    }

    /// <summary>
    /// The page estimate under the chosen basis. Zero for an empty manuscript
    /// rather than one: a book nobody has written yet has no pages.
    /// </summary>
    public static int Pages(StatisticsRow row, PageBasis basis, int wordsPerPage, int charactersPerPage)
        => basis == PageBasis.Words
            ? Pages(row.Words, wordsPerPage)
            : Pages(row.Characters, charactersPerPage);

    /// <summary>
    /// Characters in a piece of prose, with and without spaces.
    ///
    /// Counted off the plain text: a writer asking how many characters are in
    /// the book does not mean the markup.
    /// </summary>
    public static (int WithSpaces, int WithoutSpaces) CountCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);
        var withoutSpaces = 0;
        foreach (var ch in text)
            if (!char.IsWhiteSpace(ch))
                withoutSpaces++;
        return (text.Length, withoutSpaces);
    }
}
