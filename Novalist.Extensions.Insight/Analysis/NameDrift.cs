using System.Text.RegularExpressions;

namespace Novalist.Extensions.Insight.Analysis;

/// <summary>A spelling found in the prose that is close to a name the Codex holds.</summary>
public sealed record DriftFinding(
    string Known,
    string Found,
    int Occurrences,
    IReadOnlyList<string> SceneTitles);

/// <summary>
/// Names in the prose that are nearly, but not quite, names in the Codex.
///
/// Every long manuscript has these: a character named Siobhan who is Siobhán in
/// chapter four, a Kaeleigh who becomes Kaleigh, a place spelled two ways since
/// the day it was invented. They are invisible to a spell checker, which has been
/// told both spellings, and invisible to a search, which needs to be asked for
/// the wrong one.
///
/// It reports rather than fixes. Which spelling is right is a decision about the
/// book, and half of these findings are two characters with similar names who are
/// both spelled correctly.
/// </summary>
public static partial class NameDrift
{
    [GeneratedRegex(@"\b\p{Lu}[\p{L}\p{M}'’-]{2,}\b")]
    private static partial Regex CapitalisedRegex();

    /// <summary>
    /// Finds near-misses.
    /// </summary>
    /// <param name="knownNames">
    /// Every name and alias the Codex holds, including surnames as separate
    /// entries - a manuscript refers to people by one or the other.
    /// </param>
    /// <param name="scenes">Scene title and plain text, in reading order.</param>
    /// <param name="maxDistance">
    /// How many single-character changes still counts as the same name. One
    /// catches the accents and the doubled letters; two starts reporting
    /// genuinely different names as drift.
    /// </param>
    public static IReadOnlyList<DriftFinding> Find(
        IEnumerable<string> knownNames,
        IEnumerable<(string Title, string Text)> scenes,
        int maxDistance = 1)
    {
        var known = new HashSet<string>(
            knownNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.Ordinal);
        if (known.Count == 0) return [];

        // Case-insensitive too: a name typed in lower case is a different
        // mistake and not one this is about.
        var knownFolded = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);

        var candidates = new Dictionary<string, (string Known, int Count, HashSet<string> Scenes)>(
            StringComparer.Ordinal);

        foreach (var (title, text) in scenes)
        {
            foreach (Match match in CapitalisedRegex().Matches(text ?? string.Empty))
            {
                var word = match.Value;
                // A word the Codex knows is not drift, whatever its case.
                if (knownFolded.Contains(word)) continue;

                var nearest = Nearest(word, known, maxDistance);
                if (nearest == null) continue;

                if (!candidates.TryGetValue(word, out var entry))
                    entry = (nearest, 0, new HashSet<string>(StringComparer.Ordinal));
                entry = (entry.Known, entry.Count + 1, entry.Scenes);
                entry.Scenes.Add(title);
                candidates[word] = entry;
            }
        }

        return [.. candidates
            .Select(pair => new DriftFinding(
                pair.Value.Known, pair.Key, pair.Value.Count,
                [.. pair.Value.Scenes.OrderBy(s => s, StringComparer.Ordinal)]))
            .OrderByDescending(f => f.Occurrences)
            .ThenBy(f => f.Found, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The known name closest to a word, or null when none is close enough.
    /// Length is checked first because the distance calculation is the expensive
    /// part and a name four letters longer can never be within one edit.
    /// </summary>
    private static string? Nearest(string word, IEnumerable<string> known, int maxDistance)
    {
        string? best = null;
        var bestDistance = maxDistance + 1;

        foreach (var name in known)
        {
            if (Math.Abs(name.Length - word.Length) > maxDistance) continue;

            var distance = Distance(word, name, bestDistance);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = name;
            if (distance == 1) break;
        }

        return bestDistance <= maxDistance ? best : null;
    }

    /// <summary>
    /// Edit distance counting an adjacent swap as one change, giving up once it
    /// passes <paramref name="ceiling"/>.
    ///
    /// The swap matters more than it looks. "Siobahn" for "Siobhan" is two
    /// letters in the wrong order, which plain Levenshtein scores as two edits -
    /// so a one-edit threshold misses it, and a two-edit threshold to catch it
    /// starts reporting genuinely different names. Transposition is the commonest
    /// way a name gets misspelled, so it is worth one edit rather than two.
    ///
    /// Bailing out early matters too: this runs over every capitalised word in a
    /// novel against every name in the Codex.
    /// </summary>
    internal static int Distance(string a, string b, int ceiling)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Three rows, because a transposition looks two back on both axes.
        var twoBack = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var best = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    best = Math.Min(best, twoBack[j - 2] + 1);

                current[j] = best;
                if (best < rowBest) rowBest = best;
            }

            // Every remaining row can only add, so once the best cell in a row is
            // past the ceiling the answer is too.
            if (rowBest > ceiling) return ceiling + 1;
            (twoBack, previous, current) = (previous, current, twoBack);
        }

        return previous[b.Length];
    }
}
