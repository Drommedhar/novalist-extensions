using System.Text.Json;

namespace Novalist.Extensions.Toolkit.Services;

/// <summary>What a sprint is doing right now.</summary>
public enum SprintPhase
{
    Idle,

    /// <summary>Writing. The part that counts.</summary>
    Writing,

    /// <summary>Resting between sprints.</summary>
    Resting
}

/// <summary>One finished sprint, kept so the numbers mean something over time.</summary>
public sealed record SprintRecord(string StartedAt, int Minutes, int Words);

/// <summary>The state a sprint panel needs to draw itself.</summary>
public sealed record SprintSnapshot(
    SprintPhase Phase,
    int SecondsLeft,
    int WordsSoFar,
    int WordsPerMinute,
    int Completed,
    string Label);

/// <summary>
/// A writing sprint: a fixed span of time, and how many words were written in it.
///
/// The timer is the easy half. The half that makes it worth having is counting
/// words written *during* the sprint, which means recording the count at the
/// start and subtracting - not counting the scene, because a writer who deletes
/// two paragraphs and writes three has still written three.
///
/// Deliberately unforgiving about one thing only: a sprint that is stopped early
/// is recorded with the time it actually ran. Rounding it up to the full span
/// would make the words-per-minute figure a lie, and that figure is the only
/// reason to keep records at all.
/// </summary>
public sealed class Sprint
{
    private int _wordsAtStart;
    private int _wordsNow;
    private DateTimeOffset _phaseEnds;
    private DateTimeOffset _startedAt;

    public SprintPhase Phase { get; private set; } = SprintPhase.Idle;
    public int WritingMinutes { get; set; } = 25;
    public int RestingMinutes { get; set; } = 5;

    /// <summary>Finished sprints, oldest first.</summary>
    public List<SprintRecord> History { get; private set; } = [];

    /// <summary>
    /// Starts writing. <paramref name="words"/> is the project's word count now,
    /// which is the baseline everything is measured against.
    /// </summary>
    public void Start(int words, DateTimeOffset now)
    {
        _wordsAtStart = words;
        _wordsNow = words;
        _startedAt = now;
        _phaseEnds = now.AddMinutes(Math.Max(1, WritingMinutes));
        Phase = SprintPhase.Writing;
    }

    /// <summary>
    /// Stops, recording what was written in the time it actually ran.
    /// </summary>
    public SprintRecord? Stop(DateTimeOffset now)
    {
        if (Phase != SprintPhase.Writing)
        {
            Phase = SprintPhase.Idle;
            return null;
        }

        // At least one minute, so a sprint abandoned after ten seconds does not
        // divide by zero and report an implausible rate.
        var minutes = Math.Max(1, (int)Math.Round((now - _startedAt).TotalMinutes));
        var record = new SprintRecord(
            _startedAt.ToString("o"), minutes, Math.Max(0, _wordsNow - _wordsAtStart));
        History.Add(record);
        Phase = SprintPhase.Idle;
        return record;
    }

    /// <summary>
    /// Tells the sprint the current word count. Called as the writer types.
    /// </summary>
    public void Update(int words)
    {
        if (Phase == SprintPhase.Writing) _wordsNow = words;
    }

    /// <summary>
    /// Moves the clock on, and returns the record if a writing phase just ended.
    ///
    /// A finished writing phase rolls straight into a rest, and a finished rest
    /// goes idle rather than starting another sprint - the next one should be a
    /// decision, not something that happens to the writer.
    /// </summary>
    public SprintRecord? Tick(DateTimeOffset now)
    {
        if (Phase == SprintPhase.Idle || now < _phaseEnds) return null;

        if (Phase == SprintPhase.Resting)
        {
            Phase = SprintPhase.Idle;
            return null;
        }

        var record = new SprintRecord(
            _startedAt.ToString("o"), Math.Max(1, WritingMinutes), Math.Max(0, _wordsNow - _wordsAtStart));
        History.Add(record);
        Phase = SprintPhase.Resting;
        _phaseEnds = now.AddMinutes(Math.Max(1, RestingMinutes));
        return record;
    }

    public SprintSnapshot Snapshot(DateTimeOffset now)
    {
        var secondsLeft = Phase == SprintPhase.Idle
            ? 0
            : Math.Max(0, (int)(_phaseEnds - now).TotalSeconds);
        var words = Phase == SprintPhase.Writing ? Math.Max(0, _wordsNow - _wordsAtStart) : 0;
        var elapsed = Phase == SprintPhase.Writing
            ? Math.Max(1.0, (now - _startedAt).TotalMinutes)
            : 1.0;

        return new SprintSnapshot(
            Phase, secondsLeft, words, (int)Math.Round(words / elapsed),
            History.Count, Label(secondsLeft, words));
    }

    /// <summary>
    /// What the status bar says. Short, because it sits next to the word count and
    /// a sentence there is noise.
    /// </summary>
    private string Label(int secondsLeft, int words) => Phase switch
    {
        SprintPhase.Writing => $"{secondsLeft / 60:00}:{secondsLeft % 60:00} · {words}w",
        SprintPhase.Resting => $"rest {secondsLeft / 60:00}:{secondsLeft % 60:00}",
        _ => "sprint"
    };

    /// <summary>Total words across every recorded sprint, and the average rate.</summary>
    public (int Words, int Minutes, int WordsPerMinute) Totals()
    {
        var words = History.Sum(h => h.Words);
        var minutes = History.Sum(h => h.Minutes);
        return (words, minutes, minutes == 0 ? 0 : (int)Math.Round((double)words / minutes));
    }

    public string Serialise() => JsonSerializer.Serialize(
        new Stored(WritingMinutes, RestingMinutes, History), JsonOptions);

    /// <summary>
    /// Reads back the settings and the history. A file that will not parse starts
    /// fresh: losing a record of past sprints is a shame, and a timer that
    /// refuses to open is worse.
    /// </summary>
    public static Sprint Load(string? json)
    {
        var sprint = new Sprint();
        if (string.IsNullOrWhiteSpace(json)) return sprint;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json, JsonOptions);
            if (stored == null) return sprint;
            sprint.WritingMinutes = stored.WritingMinutes > 0 ? stored.WritingMinutes : 25;
            sprint.RestingMinutes = stored.RestingMinutes > 0 ? stored.RestingMinutes : 5;
            sprint.History = stored.History ?? [];
        }
        catch (JsonException)
        {
            return new Sprint();
        }
        return sprint;
    }

    private sealed record Stored(int WritingMinutes, int RestingMinutes, List<SprintRecord>? History);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
