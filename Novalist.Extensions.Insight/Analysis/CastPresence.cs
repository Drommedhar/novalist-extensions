namespace Novalist.Extensions.Insight.Analysis;

/// <summary>One character measured against the whole book.</summary>
public sealed record PresenceRow(
    string Name,
    int Appearances,
    /// <summary>Chapter number of the first appearance, or 0 for none.</summary>
    int FirstChapter,
    int LastChapter,
    /// <summary>The longest run of consecutive chapters they are absent from,
    /// between their first appearance and their last.</summary>
    int LongestGap,
    /// <summary>Chapter the longest gap starts at, or 0 when there is none.</summary>
    int GapStartsAt,
    /// <summary>True when they never appear after their first stretch - a
    /// character who was set up and then dropped.</summary>
    bool DisappearsEarly);

/// <summary>
/// Who drops out of the book, and where.
///
/// Novalist reports "last seen N chapters ago" inside the Inspector, for the
/// scene that happens to be open, at a fixed threshold of three chapters. That
/// answers "is this character overdue right now" and cannot answer the question
/// a revision actually asks - who vanished from act two, and for how long.
///
/// Gaps are measured only between a character's first appearance and their last.
/// A character who arrives in chapter twenty is not absent from chapters one to
/// nineteen; they are not in the book yet, and reporting that as a gap would
/// bury the real ones under one row per late arrival.
/// </summary>
public static class CastPresence
{
    /// <summary>
    /// A character is called dropped when the tail of the book without them is
    /// this share of it. Two thirds: enough that a reader would have forgotten
    /// them, and not so much that a late-book death counts.
    /// </summary>
    internal const double DroppedTailShare = 2.0 / 3.0;

    /// <param name="chapterCount">Chapters in the book, in reading order.</param>
    /// <param name="appearances">
    /// Chapter numbers each character appears in, from one. Duplicates and
    /// unsorted input are fine - a character in three scenes of one chapter
    /// appears in one chapter.
    /// </param>
    public static IReadOnlyList<PresenceRow> Build(
        int chapterCount, IReadOnlyDictionary<string, IReadOnlyList<int>> appearances)
    {
        var rows = new List<PresenceRow>();

        foreach (var (name, raw) in appearances)
        {
            var chapters = raw
                .Where(c => c >= 1 && c <= chapterCount)
                .Distinct()
                .OrderBy(c => c)
                .ToArray();

            if (chapters.Length == 0)
            {
                // In the Codex, in no chapter. Worth reporting: an entry that
                // never reaches the page is the one thing a cast list cannot
                // show you.
                rows.Add(new PresenceRow(name, 0, 0, 0, 0, 0, false));
                continue;
            }

            var first = chapters[0];
            var last = chapters[^1];

            var longestGap = 0;
            var gapStartsAt = 0;
            for (var i = 1; i < chapters.Length; i++)
            {
                var gap = chapters[i] - chapters[i - 1] - 1;
                if (gap > longestGap)
                {
                    longestGap = gap;
                    gapStartsAt = chapters[i - 1] + 1;
                }
            }

            // Dropped: the run after their last appearance is most of the book.
            var tail = chapterCount - last;
            var dropped = chapterCount > 0 && tail >= chapterCount * DroppedTailShare;

            rows.Add(new PresenceRow(
                name, chapters.Length, first, last, longestGap, gapStartsAt, dropped));
        }

        // Worst first: the absent, then the dropped, then the longest gaps. A
        // report you have to sort yourself has not answered anything.
        return [.. rows
            .OrderBy(r => r.Appearances == 0 ? 0 : 1)
            .ThenByDescending(r => r.DisappearsEarly)
            .ThenByDescending(r => r.LongestGap)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
