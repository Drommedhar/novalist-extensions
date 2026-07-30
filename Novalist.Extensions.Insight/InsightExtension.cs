using System.Text.Json;
using Novalist.Extensions.Insight.Analysis;
using Novalist.Sdk;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Insight;

/// <summary>
/// Read-only reports over the whole manuscript.
///
/// Everything here is analysis and nothing here writes. That is not a limitation
/// of the SDK - it could propose edits - it is the point: these reports are for
/// noticing, and the moment a hygiene tool starts fixing things it has to be
/// right, which none of these can promise. A name that is spelled two ways might
/// be two characters. An unmentioned entry might be next chapter's arrival. A
/// flat stretch of intensity might be a deliberate interlude.
///
/// So each finding is graded, says what it found rather than what to do, and
/// names the scene so the writer can go and look.
/// </summary>
public sealed class InsightExtension : IExtension, IWebViewContributor
{
    internal const string ReportView = "com.novalist.insight.report.web";

    public string Id => "com.novalist.insight";
    public string DisplayName => "Insight";
    public string Description =>
        "Reports over the whole manuscript: name drift, project health, a continuity worklist, "
        + "a word-frequency concordance and a pacing curve.";
    public string Version { get; } = ManifestVersion.Read<InsightExtension>();
    public string Author => "Novalist Team";

    private IHostServices _host = null!;
    private IExtensionLocalization _loc = null!;

    public void Initialize(IHostServices host)
    {
        _host = host;
        _loc = host.GetLocalization(Id);
        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = "com.novalist.insight.open",
                Title = _loc.T("insight.command"),
                Description = _loc.T("insight.commandDesc"),
                Mutates = false
            },
            _ =>
            {
                _host.ActivateContentView(ReportView);
                return Task.CompletedTask;
            });
    }

    public void Shutdown() => _host.UnregisterCommand("com.novalist.insight.open");

    public IWebViewController? CreateController(string viewKey)
        => viewKey == ReportView ? new ReportController(_host, Id) : null;
}

/// <summary>
/// Answers the report view's requests. One report per message, run on demand
/// rather than on a schedule - these read the whole book, and doing that in the
/// background while somebody is writing is how an app earns a reputation for
/// stuttering.
/// </summary>
internal sealed class ReportController(IHostServices host, string extensionId) : IWebViewController
{
    private readonly IExtensionLocalization _loc = host.GetLocalization(extensionId);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public event Action<string>? MessagePosted;

    public async Task<string?> OnMessageAsync(string json)
    {
        string kind;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
            kind = root.TryGetProperty("kind", out var value) ? value.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return Reply("error", new { error = _loc.T("insight.unreadable") });
        }

        // The strings request works with no project open: the panel has to be
        // able to label its own "open a project" message.
        if (kind != "strings" && !host.ProjectService.IsProjectLoaded)
            return Reply(kind, new { error = _loc.T("insight.noProject") });

        return kind switch
        {
            // Sent once when the page opens. Every label the panel shows comes
            // from here, so it follows the project language rather than holding
            // English of its own.
            "strings" => Reply(kind, new { strings = Strings() }),
            "health" => Reply(kind, new { findings = await HealthAsync() }),
            "drift" => Reply(kind, new { findings = await DriftAsync() }),
            "concordance" => Reply(kind, await ConcordanceAsync(root)),
            "pacing" => Reply(kind, await PacingAsync()),
            "continuity" => Reply("continuity", await ContinuityAsync()),
            "continuityReviewed" => Reply("continuity", await MarkReviewedAsync(root)),
            "continuityRebase" => Reply("continuity", await RebaseAsync()),
            _ => Reply(kind, new { error = $"Unknown request \"{kind}\"." })
        };
    }

    /// <summary>
    /// Wraps a payload with the kind it answers.
    ///
    /// The host posts a reply back into the frame with nothing tying it to the
    /// request, so the kind is how the page knows which panel to draw. The two
    /// continuity mutations answer as "continuity" because that is the panel
    /// waiting for them.
    /// </summary>
    /// <summary>
    /// Every string the panel needs, by the key the page asks for. Kept in one
    /// place so a label that has no translation shows up here rather than in the
    /// middle of the markup.
    /// </summary>
    private Dictionary<string, string> Strings() => new()
    {
        ["tabHealth"] = _loc.T("insight.health"),
        ["tabDrift"] = _loc.T("insight.drift"),
        ["tabContinuity"] = _loc.T("insight.continuity"),
        ["tabConcordance"] = _loc.T("insight.concordance"),
        ["tabPacing"] = _loc.T("insight.pacing"),

        ["reading"] = _loc.T("insight.reading"),
        ["noProject"] = _loc.T("insight.noProject"),
        ["unreadable"] = _loc.T("insight.unreadable"),

        ["healthClean"] = _loc.T("insight.healthClean"),
        ["driftNone"] = _loc.T("insight.driftNone"),
        ["driftHint"] = _loc.T("insight.driftHint"),
        ["driftFound"] = _loc.T("insight.driftFound"),
        ["driftKnown"] = _loc.T("insight.driftKnown"),
        ["driftTimes"] = _loc.T("insight.driftTimes"),
        ["driftScenes"] = _loc.T("insight.driftScenes"),

        ["continuityRebase"] = _loc.T("insight.continuityRebase"),
        ["continuityTracked"] = _loc.T("insight.continuityTracked"),
        ["continuityBaseline"] = _loc.T("insight.continuityBaseline"),
        ["continuityClear"] = _loc.T("insight.continuityClear"),
        ["continuityHint"] = _loc.T("insight.continuityHint"),
        ["continuityRead"] = _loc.T("insight.continuityRead"),

        ["concordanceStop"] = _loc.T("insight.concordanceStop"),
        ["concordanceCount"] = _loc.T("insight.concordanceCount"),
        ["concordanceDistinct"] = _loc.T("insight.concordanceDistinct"),
        ["concordanceHabits"] = _loc.T("insight.concordanceHabits"),
        ["concordanceNoHabits"] = _loc.T("insight.concordanceNoHabits"),
        ["concordanceAll"] = _loc.T("insight.concordanceAll"),
        ["colWord"] = _loc.T("insight.colWord"),
        ["colTimes"] = _loc.T("insight.colTimes"),
        ["colScenes"] = _loc.T("insight.colScenes"),

        ["pacingNone"] = _loc.T("insight.pacingNone"),
        ["pacingSuggests"] = _loc.T("insight.pacingSuggests"),
        ["pacingNothing"] = _loc.T("insight.pacingNothing"),
        ["pacingUnrated"] = _loc.T("insight.pacingUnrated"),
        ["pacingIntensity"] = _loc.T("insight.pacingIntensity"),
    };

    private static string Reply(string kind, object payload)
        => JsonSerializer.Serialize(new ReplyEnvelope(kind, payload), Json);

    private sealed record ReplyEnvelope(string Kind, object Payload);

    // ── The reports ──

    private async Task<IReadOnlyList<HealthFinding>> HealthAsync()
    {
        var entities = await AllEntitiesAsync();
        var scenes = new List<HealthScene>();

        foreach (var chapter in host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                var html = await host.ProjectService.ReadSceneContentAsync(chapter.Guid, scene.Id);
                var mentions = await host.GetConfirmedMentionIdsAsync(chapter.Guid, scene.Id);
                scenes.Add(new HealthScene(
                    scene.Id, scene.Title, chapter.Title, Prose.ToText(html), scene.WordCount,
                    mentions, Prose.WikiLinks(html), Prose.ImagePaths(html)));
            }
        }

        var research = host.ResearchService.GetAll()
            .Select(r => new HealthResearch(r.Id, r.Title, r.EntityRefs, r.Tags))
            .ToList();

        return ProjectHealth.Run(new HealthInput(
            entities, scenes, host.EntityService.GetProjectImages(), research));
    }

    private async Task<IReadOnlyList<DriftFinding>> DriftAsync()
    {
        var names = new List<string>();
        foreach (var character in await host.EntityService.LoadCharactersAsync())
        {
            // Both halves separately: a manuscript refers to a person by either.
            names.AddRange(character.DisplayName.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            names.AddRange(character.Aliases);
        }
        names.AddRange((await host.EntityService.LoadLocationsAsync()).Select(l => l.Name));
        names.AddRange((await host.EntityService.LoadItemsAsync()).Select(i => i.Name));
        names.AddRange((await host.EntityService.LoadLoreAsync()).Select(l => l.Name));

        var scenes = new List<(string, string)>();
        foreach (var chapter in host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                var html = await host.ProjectService.ReadSceneContentAsync(chapter.Guid, scene.Id);
                scenes.Add(($"{chapter.Title} / {scene.Title}", Prose.ToText(html)));
            }
        }

        return NameDrift.Find(names, scenes);
    }

    private async Task<object> ConcordanceAsync(JsonElement request)
    {
        var stopWords = request.TryGetProperty("stopWords", out var value)
            && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
            : null;

        var texts = new List<string>();
        foreach (var chapter in host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                texts.Add(Prose.ToText(
                    await host.ProjectService.ReadSceneContentAsync(chapter.Guid, scene.Id)));
            }
        }

        var counts = Concordance.Build(texts, stopWords);
        return new
        {
            // Capped for the view's sake, not the analysis's: a novel has tens of
            // thousands of distinct words and no writer scrolls all of them.
            words = counts.Take(500),
            habits = Concordance.Habits(counts).Take(100),
            distinct = counts.Count,
            defaultStopWords = Concordance.DefaultStopWords
        };
    }

    private async Task<object> PacingAsync()
    {
        var points = new List<PacingPoint>();
        foreach (var chapter in host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                var detail = host.StoryService.GetSceneDetail(chapter.Guid, scene.Id);
                points.Add(new PacingPoint(
                    0, scene.Title, chapter.Title, detail?.Act ?? string.Empty,
                    scene.WordCount, detail?.Intensity, detail?.Emotion ?? string.Empty,
                    detail?.Pov ?? string.Empty));
            }
        }

        var curve = PacingCurve.Build(points);
        return new { points = curve, observations = PacingCurve.Observe(curve) };
    }

    // ── Continuity, which is the one with memory ──

    private string StatePath => Path.Combine(
        host.GetExtensionDataPath(extensionId), "continuity.json");

    private async Task<WorklistState> LoadStateAsync()
        => ContinuityWorklist.Deserialise(
            File.Exists(StatePath) ? await File.ReadAllTextAsync(StatePath) : null);

    private async Task SaveStateAsync(WorklistState state)
        => await File.WriteAllTextAsync(StatePath, ContinuityWorklist.Serialise(state));

    private async Task<object> ContinuityAsync()
    {
        var entities = await FingerprintsAsync();
        var state = await LoadStateAsync();

        // A first run has nothing to compare against, so it records the baseline
        // and says so rather than reporting the whole book as changed.
        if (state.EntityHashes.Count == 0)
        {
            ContinuityWorklist.Rebase(state, entities);
            await SaveStateAsync(state);
            return new
            {
                items = Array.Empty<WorklistItem>(),
                baseline = true,
                tracked = entities.Count
            };
        }

        var scenes = new List<(string, string, string, string, IReadOnlyList<string>)>();
        foreach (var chapter in host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                scenes.Add((chapter.Guid, scene.Id, chapter.Title, scene.Title,
                    await host.GetConfirmedMentionIdsAsync(chapter.Guid, scene.Id)));
            }
        }

        return new
        {
            items = ContinuityWorklist.Build(entities, scenes, state),
            baseline = false,
            tracked = entities.Count
        };
    }

    private async Task<object> MarkReviewedAsync(JsonElement request)
    {
        var sceneId = request.TryGetProperty("sceneId", out var value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(sceneId)) return new { error = "No scene given." };

        var state = await LoadStateAsync();
        var entities = await FingerprintsAsync();
        ContinuityWorklist.MarkReviewed(state, sceneId, entities.Select(e => e.Id));
        await SaveStateAsync(state);
        return await ContinuityAsync();
    }

    private async Task<object> RebaseAsync()
    {
        var state = await LoadStateAsync();
        ContinuityWorklist.Rebase(state, await FingerprintsAsync());
        await SaveStateAsync(state);
        return await ContinuityAsync();
    }

    // ── Reading the Codex ──

    private async Task<List<(string Id, string Name, string Fingerprint)>> FingerprintsAsync()
    {
        var fingerprints = new List<(string, string, string)>();
        foreach (var entry in await ReadEntriesAsync())
        {
            // A character's real content is in their resolved profile, which is
            // where the facts a scene could be relying on actually live.
            var detailed = entry.TypeKey == "character"
                ? await host.EntityService.GetCharacterDetailedAsync(entry.Id, null, null)
                : null;
            IEnumerable<(string Title, string Content)> sections =
                detailed != null
                    ? detailed.Sections.Select(s => (s.Title, s.Content))
                    : entry.Sections;
            fingerprints.Add((entry.Id, entry.Name,
                ContinuityWorklist.Fingerprint(entry.Name, entry.Description, sections)));
        }
        return fingerprints;
    }

    /// <summary>An entry of any kind, flattened to what the reports need.</summary>
    private sealed record Entry(
        string Id, string TypeKey, string Name, string Description,
        IReadOnlyList<(string Title, string Content)> Sections,
        IReadOnlyList<string> ImagePaths);

    private async Task<List<HealthEntity>> AllEntitiesAsync()
        => [.. (await ReadEntriesAsync()).Select(
            e => new HealthEntity(e.Id, e.TypeKey, e.Name, e.ImagePaths))];

    private async Task<List<Entry>> ReadEntriesAsync()
    {
        var entries = new List<Entry>();

        foreach (var character in await host.EntityService.LoadCharactersAsync())
        {
            var image = await host.EntityService.GetCharacterImagePathAsync(character.Id, null, null);
            entries.Add(new Entry(
                character.Id, "character", character.DisplayName, string.Empty, [],
                image == null ? [] : [image]));
        }
        foreach (var location in await host.EntityService.LoadLocationsAsync())
            entries.Add(new Entry(location.Id, "location", location.Name, string.Empty, [], []));
        foreach (var item in await host.EntityService.LoadItemsAsync())
            entries.Add(new Entry(item.Id, "item", item.Name, string.Empty, [], []));
        foreach (var lore in await host.EntityService.LoadLoreAsync())
            entries.Add(new Entry(lore.Id, "lore", lore.Name, string.Empty, [], []));

        foreach (var type in host.EntityService.GetCustomEntityTypes())
        {
            foreach (var custom in await host.EntityService.LoadCustomEntitiesAsync(type.TypeKey))
            {
                entries.Add(new Entry(
                    custom.Id, type.TypeKey, custom.Name, string.Empty,
                    [.. (custom.Sections ?? []).Select(s => (s.Title, s.Content))], []));
            }
        }

        return entries;
    }
}

/// <summary>Shared reading of scene markup.</summary>
internal static class Prose
{
    public static string ToText(string html)
        => string.IsNullOrEmpty(html)
            ? string.Empty
            : System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(
                    System.Text.RegularExpressions.Regex.Replace(
                        html, @"</p\s*>|<br\s*/?>", "\n",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    "<[^>]+>", string.Empty));

    /// <summary>The [[targets]] a scene links to.</summary>
    public static IReadOnlyList<string> WikiLinks(string html)
        => [.. System.Text.RegularExpressions.Regex
            .Matches(html ?? string.Empty, @"\[\[([^\]|]+)(?:\|[^\]]*)?\]\]")
            .Select(m => m.Groups[1].Value.Trim())];

    /// <summary>The image paths a scene's markup points at.</summary>
    public static IReadOnlyList<string> ImagePaths(string html)
        => [.. System.Text.RegularExpressions.Regex
            .Matches(html ?? string.Empty, @"<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)];
}

internal static class ManifestVersion
{
    public static string Read<T>()
    {
        try
        {
            var directory = Path.GetDirectoryName(typeof(T).Assembly.Location);
            if (directory == null) return "0.0.0";
            var manifest = Path.Combine(directory, "extension.json");
            if (!File.Exists(manifest)) return "0.0.0";
            using var stream = File.OpenRead(manifest);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("version", out var value)
                ? value.GetString() ?? "0.0.0"
                : "0.0.0";
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return "0.0.0";
        }
    }
}
