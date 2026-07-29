namespace Novalist.Extensions.Insight.Analysis;

/// <summary>One point on the curve: a scene, where it sits, and how it reads.</summary>
public sealed record PacingPoint(
    int Index,
    string SceneTitle,
    string ChapterTitle,
    string Act,
    int WordCount,
    int? Intensity,
    string Emotion,
    string Pov);

/// <summary>Something about the shape of the book worth pointing at.</summary>
public sealed record PacingObservation(Severity Severity, string Detail);

/// <summary>
/// The shape of the manuscript, scene by scene, and what that shape suggests.
///
/// The chart is the point - a writer looks at a tension curve and sees the sag in
/// the middle immediately, in a way no table conveys. The observations are a
/// second-order thing and are deliberately cautious: a flat stretch is sometimes
/// exactly right, and a tool that says "your act two is broken" when the writer
/// wrote a deliberately quiet interlude has made itself useless.
///
/// Nothing here invents an intensity. A scene the writer has not rated has no
/// point on the intensity curve, and the report says how many of those there are
/// rather than filling them in with a guess.
/// </summary>
public static class PacingCurve
{
    public static IReadOnlyList<PacingPoint> Build(IEnumerable<PacingPoint> scenes)
        => [.. scenes.Select((s, i) => s with { Index = i })];

    /// <summary>
    /// What the curve suggests, if anything.
    ///
    /// Every observation names the scenes it is about, so the writer can go and
    /// look rather than taking the tool's word for it.
    /// </summary>
    public static IReadOnlyList<PacingObservation> Observe(IReadOnlyList<PacingPoint> points)
    {
        var observations = new List<PacingObservation>();
        if (points.Count == 0) return observations;

        var rated = points.Where(p => p.Intensity.HasValue).ToList();
        var unrated = points.Count - rated.Count;

        if (rated.Count < 4)
        {
            observations.Add(new PacingObservation(
                Severity.Note,
                unrated == points.Count
                    ? "No scene has an intensity yet, so there is no curve to read. Set intensity in the scene notes."
                    : $"Only {rated.Count} scene(s) have an intensity, which is too few to say anything about the shape."));
            return observations;
        }

        if (unrated > 0)
            observations.Add(new PacingObservation(
                Severity.Note,
                $"{unrated} scene(s) have no intensity, so they are not on the curve."));

        FlatStretches(rated, observations);
        RepeatedEmotion(points, observations);
        AbsentPov(points, observations);
        LongScenes(points, observations);

        return observations;
    }

    /// <summary>
    /// A run of scenes at the same intensity. Five is the threshold because three
    /// or four flat scenes is a normal quiet passage, and reporting those makes
    /// the whole list noise.
    /// </summary>
    private static void FlatStretches(
        IReadOnlyList<PacingPoint> rated, List<PacingObservation> observations)
    {
        var runStart = 0;
        for (var i = 1; i <= rated.Count; i++)
        {
            var same = i < rated.Count && rated[i].Intensity == rated[runStart].Intensity;
            if (same) continue;

            var length = i - runStart;
            if (length >= 5)
                observations.Add(new PacingObservation(
                    Severity.Warning,
                    $"{length} scenes in a row sit at intensity {rated[runStart].Intensity}, "
                    + $"from \"{rated[runStart].SceneTitle}\" to \"{rated[i - 1].SceneTitle}\". "
                    + "That may be a deliberate plateau."));
            runStart = i;
        }
    }

    /// <summary>
    /// The same dominant emotion several scenes running. Worth noticing because it
    /// is invisible while writing them one at a time.
    /// </summary>
    private static void RepeatedEmotion(
        IReadOnlyList<PacingPoint> points, List<PacingObservation> observations)
    {
        var withEmotion = points.Where(p => !string.IsNullOrWhiteSpace(p.Emotion)).ToList();
        if (withEmotion.Count < 4) return;

        var runStart = 0;
        for (var i = 1; i <= withEmotion.Count; i++)
        {
            var same = i < withEmotion.Count
                && string.Equals(withEmotion[i].Emotion, withEmotion[runStart].Emotion,
                    StringComparison.OrdinalIgnoreCase);
            if (same) continue;

            var length = i - runStart;
            if (length >= 4)
                observations.Add(new PacingObservation(
                    Severity.Warning,
                    $"{length} scenes running carry \"{withEmotion[runStart].Emotion}\", "
                    + $"starting at \"{withEmotion[runStart].SceneTitle}\"."));
            runStart = i;
        }
    }

    /// <summary>
    /// A point-of-view character who disappears for a long stretch. This is the
    /// finding filtering cannot give you: hiding the other threads to look at one
    /// is exactly what makes a gap invisible.
    /// </summary>
    private static void AbsentPov(
        IReadOnlyList<PacingPoint> points, List<PacingObservation> observations)
    {
        var povs = points
            .Where(p => !string.IsNullOrWhiteSpace(p.Pov))
            .GroupBy(p => p.Pov, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 3)
            .ToList();

        foreach (var pov in povs)
        {
            var indices = pov.Select(p => p.Index).OrderBy(i => i).ToList();
            for (var i = 1; i < indices.Count; i++)
            {
                var gap = indices[i] - indices[i - 1] - 1;
                // A tenth of the book, and at least eight scenes: proportional so
                // it means the same thing in a novella and a doorstop.
                var threshold = Math.Max(8, points.Count / 10);
                if (gap < threshold) continue;

                observations.Add(new PacingObservation(
                    Severity.Warning,
                    $"\"{pov.Key}\" holds the point of view, then does not for {gap} scenes "
                    + $"after \"{points[indices[i - 1]].SceneTitle}\"."));
            }
        }
    }

    /// <summary>
    /// A scene far longer than the rest. Compared against the median rather than
    /// the mean, because one enormous scene drags a mean up and then hides itself.
    /// </summary>
    private static void LongScenes(
        IReadOnlyList<PacingPoint> points, List<PacingObservation> observations)
    {
        var lengths = points.Select(p => p.WordCount).Where(w => w > 0).OrderBy(w => w).ToList();
        if (lengths.Count < 5) return;

        var median = lengths[lengths.Count / 2];
        if (median == 0) return;

        foreach (var scene in points.Where(p => p.WordCount > median * 3))
        {
            observations.Add(new PacingObservation(
                Severity.Note,
                $"\"{scene.SceneTitle}\" is {scene.WordCount} words against a median of {median}."));
        }
    }
}
