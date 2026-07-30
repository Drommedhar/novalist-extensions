using System.Text.Json;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Toolkit.Services;

/// <summary>
/// The sprint panel and the to-do board.
///
/// The board is built from comments rather than from a store of its own. A writer
/// already marks jobs where the job is - "check the tide table here" in the
/// margin of the scene it applies to - and a task list that lived somewhere else
/// would be a second place to keep the same information in step. So this reads
/// the comments and groups them; the comment stays the truth.
/// </summary>
internal sealed class BoardController(
    IHostServices host, Sprint sprint, Action saveSprint, IExtensionLocalization loc)
    : IWebViewController
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public event Action<string>? MessagePosted;

    public async Task<string?> OnMessageAsync(string json)
    {
        JsonElement root;
        string kind;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
            kind = root.TryGetProperty("kind", out var value) ? value.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return Reply("error", new { error = loc.T("toolkit.unreadable") });
        }

        // Every sprint request answers as "sprint" and every task request as
        // "tasks": the host relays a reply with nothing tying it to the request,
        // so the kind is how the page knows which panel it is for.
        return kind switch
        {
            "strings" => Reply(kind, new { strings = Strings() }),
            "sprint" => Reply("sprint", SprintState()),
            "sprintStart" => Reply("sprint", StartSprint()),
            "sprintStop" => Reply("sprint", StopSprint()),
            "sprintSettings" => Reply("sprint", SetSprintSettings(root)),
            "tasks" => Reply("tasks", new { tasks = await TasksAsync() }),
            "taskDone" => Reply("tasks", new { tasks = await ResolveTaskAsync(root, true) }),
            "taskReopen" => Reply("tasks", new { tasks = await ResolveTaskAsync(root, false) }),
            _ => Reply(kind, new { error = $"Unknown request \"{kind}\"." })
        };
    }

    /// <summary>Every label the board shows, so the page holds no English.</summary>
    private Dictionary<string, string> Strings() => new()
    {
        ["tabSprint"] = loc.T("toolkit.sprint"),
        ["tabTasks"] = loc.T("toolkit.tasks"),

        ["start"] = loc.T("toolkit.start"),
        ["stop"] = loc.T("toolkit.stop"),
        ["write"] = loc.T("toolkit.write"),
        ["rest"] = loc.T("toolkit.rest"),
        ["minutes"] = loc.T("toolkit.minutes"),
        ["notRunning"] = loc.T("toolkit.notRunning"),
        ["resting"] = loc.T("toolkit.resting"),
        ["progress"] = loc.T("toolkit.progress"),

        ["statSprints"] = loc.T("toolkit.statSprints"),
        ["statWords"] = loc.T("toolkit.statWords"),
        ["statMinutes"] = loc.T("toolkit.statMinutes"),
        ["statRate"] = loc.T("toolkit.statRate"),

        ["finished"] = loc.T("toolkit.finished"),
        ["noSprints"] = loc.T("toolkit.noSprints"),
        ["colStarted"] = loc.T("toolkit.colStarted"),
        ["colMinutes"] = loc.T("toolkit.colMinutes"),
        ["colWords"] = loc.T("toolkit.colWords"),
        ["colRate"] = loc.T("toolkit.colRate"),

        ["showDone"] = loc.T("toolkit.showDone"),
        ["openOf"] = loc.T("toolkit.openOf"),
        ["allDone"] = loc.T("toolkit.allDone"),
        ["noComments"] = loc.T("toolkit.noComments"),
        ["onPhrase"] = loc.T("toolkit.onPhrase"),
        ["unreadable"] = loc.T("toolkit.unreadable"),
    };

    private static string Reply(string kind, object payload)
        => JsonSerializer.Serialize(new ReplyEnvelope(kind, payload), Json);

    private sealed record ReplyEnvelope(string Kind, object Payload);

    // ── Sprints ──

    private object SprintState()
    {
        var snapshot = sprint.Snapshot(DateTimeOffset.UtcNow);
        var (words, minutes, rate) = sprint.Totals();
        return new
        {
            phase = snapshot.Phase.ToString(),
            secondsLeft = snapshot.SecondsLeft,
            wordsSoFar = snapshot.WordsSoFar,
            wordsPerMinute = snapshot.WordsPerMinute,
            completed = snapshot.Completed,
            writingMinutes = sprint.WritingMinutes,
            restingMinutes = sprint.RestingMinutes,
            history = sprint.History.AsEnumerable().Reverse().Take(20),
            totalWords = words,
            totalMinutes = minutes,
            averageRate = rate
        };
    }

    private object StartSprint()
    {
        var words = host.ProjectService.GetChaptersOrdered()
            .SelectMany(c => host.ProjectService.GetScenesForChapter(c.Guid))
            .Sum(s => s.WordCount);
        sprint.Start(words, DateTimeOffset.UtcNow);
        return SprintState();
    }

    private object StopSprint()
    {
        sprint.Stop(DateTimeOffset.UtcNow);
        saveSprint();
        return SprintState();
    }

    private object SetSprintSettings(JsonElement request)
    {
        if (request.TryGetProperty("writingMinutes", out var writing)
            && writing.TryGetInt32(out var writingValue))
            sprint.WritingMinutes = Math.Clamp(writingValue, 1, 180);
        if (request.TryGetProperty("restingMinutes", out var resting)
            && resting.TryGetInt32(out var restingValue))
            sprint.RestingMinutes = Math.Clamp(restingValue, 1, 60);
        saveSprint();
        return SprintState();
    }

    // ── The board ──

    /// <summary>One job, and where it was written.</summary>
    private sealed record Task(
        string ChapterGuid, string SceneId, string CommentId,
        string ChapterTitle, string SceneTitle,
        string Anchor, string Text, string Author, bool Done);

    private async Task<List<Task>> TasksAsync()
    {
        var tasks = new List<Task>();
        if (!host.ProjectService.IsProjectLoaded) return tasks;

        foreach (var chapter in host.ProjectService.GetChaptersOrdered())
        {
            foreach (var scene in host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                foreach (var comment in await host.ReviewService.GetCommentsAsync(
                             chapter.Guid, scene.Id))
                {
                    tasks.Add(new Task(
                        chapter.Guid, scene.Id, comment.Id, chapter.Title, scene.Title,
                        comment.AnchorText, comment.Text, comment.Author, comment.Resolved));
                }
            }
        }

        // Open work first, then in reading order - which is the order somebody
        // working through a manuscript actually goes.
        return [.. tasks.OrderBy(t => t.Done)];
    }

    private async Task<List<Task>> ResolveTaskAsync(JsonElement request, bool done)
    {
        var chapterGuid = Text(request, "chapterGuid");
        var sceneId = Text(request, "sceneId");
        var commentId = Text(request, "commentId");

        if (chapterGuid != null && sceneId != null && commentId != null)
            await host.ReviewService.SetCommentResolvedAsync(chapterGuid, sceneId, commentId, done);

        return await TasksAsync();
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
