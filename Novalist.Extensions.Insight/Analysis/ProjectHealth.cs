namespace Novalist.Extensions.Insight.Analysis;

/// <summary>How serious a finding is, and therefore what order to read them in.</summary>
public enum Severity
{
    /// <summary>Something is broken and will read as broken.</summary>
    Problem,

    /// <summary>Probably unintended, but a writer might have meant it.</summary>
    Warning,

    /// <summary>Worth knowing. Not wrong.</summary>
    Note
}

/// <summary>One thing found in the project.</summary>
public sealed record HealthFinding(Severity Severity, string Category, string Detail);

/// <summary>What the health pass was given to look at.</summary>
public sealed record HealthInput(
    IReadOnlyList<HealthEntity> Entities,
    IReadOnlyList<HealthScene> Scenes,
    IReadOnlyList<string> ProjectImages,
    IReadOnlyList<HealthResearch> Research);

public sealed record HealthEntity(
    string Id, string TypeKey, string Name, IReadOnlyList<string> ImagePaths);

public sealed record HealthScene(
    string Id,
    string Title,
    string ChapterTitle,
    string Text,
    int WordCount,
    IReadOnlyList<string> MentionedEntityIds,
    IReadOnlyList<string> LinkTargets,
    IReadOnlyList<string> ImagePaths);

public sealed record HealthResearch(
    string Id, string Title, IReadOnlyList<string> EntityRefs, IReadOnlyList<string> Tags);

/// <summary>
/// The hygiene pass: entries nothing mentions, links that point nowhere, images
/// nothing uses, research attached to nothing.
///
/// None of these are bugs in the writer's book. A character who has not appeared
/// yet is not an error, and an unused image might be next chapter's map. So
/// everything here is graded, and the wording says what was found rather than
/// what to do about it - the alternative is a report that cries wolf and gets
/// ignored, which is worse than no report.
/// </summary>
public static class ProjectHealth
{
    public static IReadOnlyList<HealthFinding> Run(HealthInput input)
    {
        var findings = new List<HealthFinding>();

        DanglingLinks(input, findings);
        OrphanEntries(input, findings);
        UnusedImages(input, findings);
        MissingImages(input, findings);
        UnattachedResearch(input, findings);
        EmptyScenes(input, findings);

        return [.. findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.Category, StringComparer.Ordinal)
            .ThenBy(f => f.Detail, StringComparer.Ordinal)];
    }

    /// <summary>
    /// A [[link]] in the prose whose target does not exist. This one is a real
    /// problem: the writer wrote it expecting it to resolve, and it does not.
    /// </summary>
    private static void DanglingLinks(HealthInput input, List<HealthFinding> findings)
    {
        var names = new HashSet<string>(
            input.Entities.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var scene in input.Scenes)
        {
            foreach (var target in scene.LinkTargets.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(target) || names.Contains(target)) continue;
                findings.Add(new HealthFinding(
                    Severity.Problem, "Dangling link",
                    $"\"{scene.ChapterTitle} / {scene.Title}\" links to \"{target}\", which is not in the Codex."));
            }

            // Two entries with the same name make every link to it ambiguous,
            // and the writer cannot tell which one they got.
            foreach (var duplicate in input.Entities
                         .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1 && scene.LinkTargets.Contains(
                             g.Key, StringComparer.OrdinalIgnoreCase))
                         .Select(g => g.Key)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new HealthFinding(
                    Severity.Problem, "Ambiguous link",
                    $"\"{duplicate}\" is the name of more than one Codex entry, so links to it are ambiguous."));
            }
        }
    }

    /// <summary>
    /// An entry no scene mentions. A warning rather than a problem: this is
    /// perfectly normal for a character who has not turned up yet, and the point
    /// is to notice the ones the writer has forgotten about.
    /// </summary>
    private static void OrphanEntries(HealthInput input, List<HealthFinding> findings)
    {
        var mentioned = new HashSet<string>(
            input.Scenes.SelectMany(s => s.MentionedEntityIds), StringComparer.Ordinal);
        var namesInProse = input.Scenes.Select(s => s.Text ?? string.Empty).ToList();

        foreach (var entity in input.Entities)
        {
            if (mentioned.Contains(entity.Id)) continue;
            // Before calling it an orphan, check the prose for its name -
            // a writer who never used an @-mention has not created an orphan.
            if (!string.IsNullOrWhiteSpace(entity.Name)
                && namesInProse.Any(text => text.Contains(entity.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            findings.Add(new HealthFinding(
                Severity.Warning, "Unmentioned entry",
                $"{Kind(entity.TypeKey)} \"{entity.Name}\" is not mentioned in any scene."));
        }
    }

    /// <summary>
    /// An image in the project folder that nothing points at. A note: the writer
    /// put it there, and a reference picture that is not attached to anything is
    /// still doing its job.
    /// </summary>
    private static void UnusedImages(HealthInput input, List<HealthFinding> findings)
    {
        var used = new HashSet<string>(
            input.Entities.SelectMany(e => e.ImagePaths)
                .Concat(input.Scenes.SelectMany(s => s.ImagePaths))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Normalise),
            StringComparer.OrdinalIgnoreCase);

        foreach (var image in input.ProjectImages)
        {
            if (string.IsNullOrWhiteSpace(image)) continue;
            if (used.Contains(Normalise(image))) continue;
            findings.Add(new HealthFinding(
                Severity.Note, "Unused image", $"\"{image}\" is not used by any entry, scene or map."));
        }
    }

    /// <summary>
    /// The other direction: something points at an image that is not there. This
    /// is a problem - it is a broken picture in the app and a missing one in an
    /// export.
    /// </summary>
    private static void MissingImages(HealthInput input, List<HealthFinding> findings)
    {
        var present = new HashSet<string>(
            input.ProjectImages.Select(Normalise), StringComparer.OrdinalIgnoreCase);
        if (present.Count == 0) return;

        foreach (var entity in input.Entities)
        {
            foreach (var path in entity.ImagePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                if (present.Contains(Normalise(path))) continue;
                findings.Add(new HealthFinding(
                    Severity.Problem, "Missing image",
                    $"{Kind(entity.TypeKey)} \"{entity.Name}\" points at \"{path}\", which is not in the project."));
            }
        }
    }

    private static void UnattachedResearch(HealthInput input, List<HealthFinding> findings)
    {
        foreach (var item in input.Research)
        {
            if (item.EntityRefs.Count > 0 || item.Tags.Count > 0) continue;
            findings.Add(new HealthFinding(
                Severity.Note, "Unfiled research",
                $"\"{item.Title}\" has no linked entries and no tags, so nothing will surface it."));
        }
    }

    /// <summary>
    /// A scene with no words in it. A warning, because a deliberate placeholder is
    /// a normal thing to have and the writer knows which of theirs are which.
    /// </summary>
    private static void EmptyScenes(HealthInput input, List<HealthFinding> findings)
    {
        foreach (var scene in input.Scenes.Where(s => s.WordCount == 0))
        {
            findings.Add(new HealthFinding(
                Severity.Warning, "Empty scene",
                $"\"{scene.ChapterTitle} / {scene.Title}\" has no prose in it."));
        }
    }

    /// <summary>
    /// Paths come from several places - some project-relative, some with the
    /// other separator - and comparing them as written reports images as both
    /// missing and unused at the same time.
    /// </summary>
    private static string Normalise(string path)
        => path.Replace('\\', '/').TrimStart('/').Trim();

    private static string Kind(string typeKey) => typeKey.ToLowerInvariant() switch
    {
        "character" => "Character",
        "location" => "Location",
        "item" => "Item",
        "lore" => "Lore entry",
        _ => "Entry"
    };
}
