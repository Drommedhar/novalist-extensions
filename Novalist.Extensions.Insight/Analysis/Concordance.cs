using System.Text.RegularExpressions;

namespace Novalist.Extensions.Insight.Analysis;

/// <summary>One word and how often it appears.</summary>
public sealed record WordCount(string Word, int Count, int Scenes);

/// <summary>
/// How often each word appears across the manuscript, minus the ones that carry
/// no information.
///
/// The useful output is not the top of the list - that is always "the" - but the
/// long tail a writer recognises: a verb they have leaned on forty times, a
/// gesture every character makes, a favourite adverb. So the stop list matters
/// as much as the count, and it has to be editable, because "said" is noise in
/// one writer's analysis and the finding in another's.
/// </summary>
public static partial class Concordance
{
    /// <summary>
    /// Words too common to be interesting in any English manuscript. Kept short
    /// on purpose: a long list starts hiding the words a writer wants to see.
    /// Deliberately excludes "said" - dialogue tags are exactly the thing some
    /// writers are counting.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultStopWords =
    [
        "a", "about", "after", "all", "also", "an", "and", "any", "are", "as", "at",
        "back", "be", "because", "been", "before", "being", "but", "by",
        "can", "could", "did", "do", "does", "down",
        "even", "for", "from", "get", "got", "had", "has", "have", "he", "her", "here",
        "him", "his", "how", "i", "if", "in", "into", "is", "it", "its",
        "just", "like", "me", "more", "most", "much", "my",
        "no", "not", "now", "of", "off", "on", "once", "one", "only", "or", "other",
        "our", "out", "over", "own",
        "she", "should", "so", "some", "still", "such",
        "than", "that", "the", "their", "them", "then", "there", "these", "they",
        "this", "those", "though", "through", "to", "too",
        "up", "us", "very", "was", "we", "well", "were", "what", "when", "where",
        "which", "while", "who", "why", "will", "with", "would",
        "you", "your"
    ];

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’][\p{L}]+)?")]
    private static partial Regex WordRegex();

    /// <summary>
    /// Counts words across scenes.
    /// </summary>
    /// <param name="scenes">One entry per scene, of plain text.</param>
    /// <param name="stopWords">
    /// Words to leave out. Null takes <see cref="DefaultStopWords"/>; an empty
    /// list counts everything, which is what somebody looking at their own
    /// dialogue tags wants.
    /// </param>
    /// <param name="minimumLength">
    /// Words shorter than this are skipped. Two is a reasonable floor: single
    /// letters are almost always initials or artefacts.
    /// </param>
    public static IReadOnlyList<WordCount> Build(
        IEnumerable<string> scenes,
        IReadOnlyList<string>? stopWords = null,
        int minimumLength = 3)
    {
        var stop = new HashSet<string>(
            stopWords ?? DefaultStopWords, StringComparer.OrdinalIgnoreCase);

        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sceneCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var scene in scenes)
        {
            // Counted per scene as well as overall: a word used nine times in one
            // scene is a different problem from one used nine times across nine.
            var seenHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in WordRegex().Matches(scene ?? string.Empty))
            {
                var word = match.Value;
                if (word.Length < minimumLength) continue;
                if (stop.Contains(word)) continue;

                totals[word] = totals.GetValueOrDefault(word) + 1;
                if (seenHere.Add(word))
                    sceneCounts[word] = sceneCounts.GetValueOrDefault(word) + 1;
            }
        }

        return [.. totals
            .Select(pair => new WordCount(
                pair.Key.ToLowerInvariant(), pair.Value, sceneCounts.GetValueOrDefault(pair.Key)))
            .OrderByDescending(w => w.Count)
            .ThenBy(w => w.Word, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The words worth a second look: used often, and used across the book
    /// rather than concentrated in one scene.
    ///
    /// A word that appears eight times in one scene is usually the subject of
    /// that scene. The same word in eight different scenes is a habit, and that
    /// is the one worth telling somebody about.
    /// </summary>
    public static IReadOnlyList<WordCount> Habits(
        IReadOnlyList<WordCount> counts, int minimumScenes = 3, int minimumTotal = 5)
        => [.. counts.Where(w => w.Scenes >= minimumScenes && w.Count >= minimumTotal)];
}
