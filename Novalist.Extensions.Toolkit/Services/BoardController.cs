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
    IHostServices host, Sprint sprint, Action saveSprint) : IWebViewController
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
            return Reply(new { error = "That message was not readable." });
        }

        return kind switch
        {
            "sprint" => Reply(SprintState()),
            "sprintStart" => Reply(StartSprint()),
            "sprintStop" => Reply(StopSprint()),
            "sprintSettings" => Reply(SetSprintSettings(root)),
            "tasks" => Reply(new { tasks = await TasksAsync() }),
            "taskDone" => Reply(new { tasks = await ResolveTaskAsync(root, true) }),
            "taskReopen" => Reply(new { tasks = await ResolveTaskAsync(root, false) }),
            "openScene" => Reply(OpenScene(root)),
            _ => Reply(new { error = $"Unknown request \"{kind}\"." })
        };
    }

    private static string Reply(object payload) => JsonSerializer.Serialize(payload, Json);

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

    private object OpenScene(JsonElement request)
    {
        // Nothing to do here beyond acknowledging: the panel asks the host to
        // open the scene through its own command, and this only exists so the
        // board can report a bad request rather than failing silently.
        var sceneId = Text(request, "sceneId");
        return sceneId == null
            ? new { error = "No scene given." }
            : new { ok = true };
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
