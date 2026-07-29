using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Novalist.Extensions.Insight.Analysis;

/// <summary>A scene that needs re-reading because something it relies on changed.</summary>
public sealed record WorklistItem(
    string ChapterGuid,
    string SceneId,
    string ChapterTitle,
    string SceneTitle,
    IReadOnlyList<string> ChangedEntities,
    bool Reviewed);

/// <summary>
/// What the worklist remembers between runs: the state each entry was in when
/// its scenes were last reviewed.
/// </summary>
public sealed class WorklistState
{
    /// <summary>Entity id to a hash of the entry as it was when last reviewed.</summary>
    public Dictionary<string, string> EntityHashes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Scene ids the writer has ticked off, by "sceneId|entityId".</summary>
    public HashSet<string> Reviewed { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// When a Codex entry changes, the scenes that mention it may no longer be true.
///
/// A writer who decides in chapter thirty that the harbourmaster has always had a
/// limp has just made an unknown number of earlier scenes wrong, and there is no
/// way to find them except remembering. This keeps a record of what each entry
/// looked like when its scenes were last read, and lists the scenes that mention
/// anything which has changed since.
///
/// It cannot tell whether a change matters. Correcting a typo in a description
/// marks the same scenes as rewriting the character's history, so the list is
/// something to work through and tick off rather than an accusation. Ticking a
/// scene off is the whole point: without that it would show the same scenes for
/// ever and get ignored.
/// </summary>
public static class ContinuityWorklist
{
    /// <summary>
    /// A fingerprint of everything about an entry that a scene could be relying
    /// on. Name and sections, not the id or the ordering - a section moved is not
    /// a fact changed.
    /// </summary>
    public static string Fingerprint(
        string name, string? description, IEnumerable<(string Title, string Content)> sections)
    {
        var parts = new List<string> { name ?? string.Empty, description ?? string.Empty };
        parts.AddRange(sections
            .Select(s => $"{s.Title}{s.Content}")
            .OrderBy(s => s, StringComparer.Ordinal));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('', parts)));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// The scenes worth re-reading.
    /// </summary>
    /// <param name="entities">Every entry, with its current fingerprint.</param>
    /// <param name="scenes">Scenes, with which entries each mentions.</param>
    /// <param name="state">
    /// What was recorded last time. An entry the state has never seen is not
    /// reported: on a first run everything would be "changed", which is a list
    /// nobody reads.
    /// </param>
    public static IReadOnlyList<WorklistItem> Build(
        IReadOnlyList<(string Id, string Name, string Fingerprint)> entities,
        IReadOnlyList<(string ChapterGuid, string SceneId, string ChapterTitle, string SceneTitle,
            IReadOnlyList<string> MentionedIds)> scenes,
        WorklistState state)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, name, fingerprint) in entities)
        {
            if (!state.EntityHashes.TryGetValue(id, out var previous)) continue;
            if (previous == fingerprint) continue;
            changed[id] = name;
        }
        if (changed.Count == 0) return [];

        var items = new List<WorklistItem>();
        foreach (var scene in scenes)
        {
            var touched = scene.MentionedIds
                .Where(changed.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (touched.Count == 0) continue;

            // Reviewed per scene and per entry: ticking off a scene for one
            // change must not silence it for the next one.
            var reviewed = touched.All(id => state.Reviewed.Contains(Key(scene.SceneId, id)));
            items.Add(new WorklistItem(
                scene.ChapterGuid, scene.SceneId, scene.ChapterTitle, scene.SceneTitle,
                [.. touched.Select(id => changed[id]).OrderBy(n => n, StringComparer.Ordinal)],
                reviewed));
        }

        return [.. items.OrderBy(i => i.Reviewed).ThenBy(i => i.ChapterTitle, StringComparer.Ordinal)];
    }

    /// <summary>Marks a scene read against every entry currently flagged on it.</summary>
    public static void MarkReviewed(
        WorklistState state, string sceneId, IEnumerable<string> entityIds)
    {
        foreach (var id in entityIds) state.Reviewed.Add(Key(sceneId, id));
    }

    /// <summary>
    /// Accepts the current state of every entry as the new baseline, and forgets
    /// what was ticked off - those ticks were about changes that are now history.
    /// </summary>
    public static void Rebase(
        WorklistState state, IReadOnlyList<(string Id, string Name, string Fingerprint)> entities)
    {
        state.EntityHashes = entities.ToDictionary(e => e.Id, e => e.Fingerprint, StringComparer.Ordinal);
        state.Reviewed.Clear();
    }

    private static string Key(string sceneId, string entityId) => $"{sceneId}|{entityId}";

    public static string Serialise(WorklistState state)
        => JsonSerializer.Serialize(state, JsonOptions);

    /// <summary>
    /// Reads the stored state. A file that cannot be read starts over rather than
    /// throwing: losing which scenes were ticked off is a nuisance, and a panel
    /// that will not open is worse.
    /// </summary>
    public static WorklistState Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new WorklistState();
        try
        {
            return JsonSerializer.Deserialize<WorklistState>(json, JsonOptions) ?? new WorklistState();
        }
        catch (JsonException)
        {
            return new WorklistState();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
