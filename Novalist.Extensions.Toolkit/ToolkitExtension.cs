using System.Text.Json;
using Novalist.Extensions.Toolkit.Services;
using Novalist.Sdk;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Toolkit;

/// <summary>
/// The small tools: a sprint timer, a board over your to-dos, word lookup, and
/// capturing a web page as research.
///
/// None of these are big enough to be their own extension and all of them are
/// things a writer reaches for during a session rather than while planning. The
/// lookup and the capture are networked, which is the clearest reason for them to
/// live out here: a writing application should not have to make a request to the
/// internet to be a writing application.
/// </summary>
public sealed class ToolkitExtension :
    IExtension, IStatusBarContributor, IWebViewContributor, IInlineActionContributor
{
    internal const string BoardView = "com.novalist.toolkit.board.web";

    public string Id => "com.novalist.toolkit";
    public string DisplayName => "Toolkit";
    public string Description =>
        "Writing sprints, a board over your to-do comments, dictionary and thesaurus lookup, "
        + "and web page capture that keeps the readable text.";
    public string Version { get; } = ManifestVersion.Read<ToolkitExtension>();
    public string Author => "Novalist Team";

    private IHostServices _host = null!;
    private Sprint _sprint = new();
    private Lookup _lookup = null!;

    private string SprintPath => Path.Combine(
        _host.GetExtensionSettingsPath(Id), "sprints.json");

    public void Initialize(IHostServices host)
    {
        _host = host;
        _lookup = new Lookup();
        _sprint = Sprint.Load(File.Exists(SprintPath) ? File.ReadAllText(SprintPath) : null);

        // The sprint has to know the word count as it changes, and a save is the
        // only moment the host reports one. Polling for it would be worse.
        _host.SceneSaved += OnSceneSaved;

        RegisterCommands();
    }

    public void Shutdown()
    {
        _host.SceneSaved -= OnSceneSaved;
        SaveSprint();
        _lookup.Dispose();
        foreach (var id in CommandIds) _host.UnregisterCommand(id);
    }

    private void OnSceneSaved(SceneInfo scene) => _sprint.Update(ProjectWords());

    /// <summary>
    /// The whole book's word count. The sprint measures against this rather than
    /// the open scene, because a writer who moves to the next scene mid-sprint has
    /// not stopped writing.
    /// </summary>
    private int ProjectWords()
        => _host.ProjectService.GetChaptersOrdered()
            .SelectMany(c => _host.ProjectService.GetScenesForChapter(c.Guid))
            .Sum(s => s.WordCount);

    private void SaveSprint()
    {
        try
        {
            File.WriteAllText(SprintPath, _sprint.Serialise());
        }
        catch (IOException)
        {
            // Losing a sprint history is not worth taking the app down for.
        }
    }

    // ── Status bar ──

    public IReadOnlyList<StatusBarItem> GetStatusBarItems() =>
    [
        new StatusBarItem
        {
            Id = "com.novalist.toolkit.sprint",
            Alignment = "Right",
            Order = 40,
            GetText = () => _sprint.Snapshot(DateTimeOffset.UtcNow).Label,
            GetTooltip = () => _sprint.Phase == SprintPhase.Idle
                ? "Start a writing sprint"
                : "Open the sprint panel",
            OnClick = () => _host.ActivateContentView(BoardView),
            // The host refreshes the bar on a timer, which is also the tick the
            // sprint needs; a second timer inside the extension would be one more
            // thing to get out of step.
            OnRefresh = () =>
            {
                if (_sprint.Tick(DateTimeOffset.UtcNow) != null) SaveSprint();
            }
        }
    ];

    // ── Inline actions: lookup on a selected word ──

    public IReadOnlyList<InlineActionDescriptor> GetInlineActions() =>
    [
        new InlineActionDescriptor
        {
            Id = "toolkit.define",
            Label = "Define",
            Group = "Look up",
            Priority = 10
        },
        new InlineActionDescriptor
        {
            Id = "toolkit.synonyms",
            Label = "Synonyms",
            Group = "Look up",
            Priority = 20
        }
    ];

    /// <summary>
    /// Looks a word up and offers what it found as alternatives rather than
    /// replacing anything.
    ///
    /// A thesaurus that silently swaps a word for a synonym is worse than no
    /// thesaurus: the two words rarely mean the same thing, and the writer chose
    /// the first one. So a lookup returns candidates and the host shows them.
    /// </summary>
    public async Task<InlineActionResult> ExecuteAsync(
        string actionId, InlineActionRequest request, CancellationToken cancellationToken)
    {
        var word = (request.SelectedText ?? string.Empty).Trim();
        if (word.Length == 0)
            return new InlineActionResult { Error = "Select a word first." };
        if (word.Contains(' '))
            return new InlineActionResult { Error = "Select a single word." };

        try
        {
            return actionId switch
            {
                "toolkit.define" => Definition(await _lookup.DefineAsync(word, cancellationToken)),
                "toolkit.synonyms" => Synonyms(
                    word, await _lookup.SynonymsAsync(word, cancellationToken)),
                _ => new InlineActionResult { Error = $"Unknown action \"{actionId}\"." }
            };
        }
        catch (OperationCanceledException)
        {
            return new InlineActionResult { Error = "Cancelled." };
        }
        catch (HttpRequestException ex)
        {
            return new InlineActionResult { Error = $"Could not reach the dictionary: {ex.Message}" };
        }
    }

    /// <summary>
    /// A definition goes in as a comment on the passage, not into the prose. The
    /// writer asked what a word means; they did not ask for the answer to be
    /// written into their book.
    /// </summary>
    private InlineActionResult Definition(IReadOnlyList<string> senses)
        => senses.Count == 0
            ? new InlineActionResult { Error = "No definition found." }
            : new InlineActionResult
            {
                Text = string.Join("  ", senses.Select((s, i) => $"{i + 1}. {s}")),
                Disposition = InlineActionDisposition.InsertAfterSelection,
                Alternatives = [.. senses]
            };

    private static InlineActionResult Synonyms(string word, IReadOnlyList<string> synonyms)
        => synonyms.Count == 0
            ? new InlineActionResult { Error = $"No synonyms found for \"{word}\"." }
            : new InlineActionResult
            {
                Text = synonyms[0],
                Disposition = InlineActionDisposition.ReplaceSelection,
                // Every one of them, so the writer picks rather than being given
                // whichever the dictionary happened to list first.
                Alternatives = [.. synonyms],
                AsSuggestion = true
            };

    // ── Commands ──

    private static readonly string[] CommandIds =
    [
        "com.novalist.toolkit.sprint.start",
        "com.novalist.toolkit.sprint.stop",
        "com.novalist.toolkit.board",
        "com.novalist.toolkit.capture"
    ];

    private void RegisterCommands()
    {
        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = CommandIds[0],
                Title = "Start a writing sprint",
                Description = "Times a stretch of writing and counts the words written in it."
            },
            _ =>
            {
                _sprint.Start(ProjectWords(), DateTimeOffset.UtcNow);
                _host.ShowNotification($"Sprint started: {_sprint.WritingMinutes} minutes.");
                return Task.CompletedTask;
            });

        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = CommandIds[1],
                Title = "Stop the writing sprint",
                Description = "Ends the sprint and records what was written in it."
            },
            _ =>
            {
                var record = _sprint.Stop(DateTimeOffset.UtcNow);
                SaveSprint();
                _host.ShowNotification(record == null
                    ? "No sprint was running."
                    : $"{record.Words} words in {record.Minutes} minute(s).");
                return Task.CompletedTask;
            });

        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = CommandIds[2],
                Title = "Open the Toolkit board",
                Description = "Sprints and the to-do board."
            },
            _ =>
            {
                _host.ActivateContentView(BoardView);
                return Task.CompletedTask;
            });

        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = CommandIds[3],
                Title = "Capture a web page as research",
                Description = "Fetches a page, keeps its readable text, and files it as a note.",
                Mutates = true,
                ArgumentsSchema =
                    """
                    {"type":"object","required":["url"],
                     "properties":{"url":{"type":"string","description":"The page to capture."},
                     "tags":{"type":"array","items":{"type":"string"}}}}
                    """
            },
            CaptureAsync);
    }

    /// <summary>
    /// Fetches a page and files what it says.
    ///
    /// The text is stored, not just the address. A research note holding only a
    /// URL stops working when the page does, which is most pages within a few
    /// years.
    /// </summary>
    private async Task CaptureAsync(string? argumentsJson)
    {
        string? url = null;
        var tags = new List<string> { "inbox" };
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? "{}");
            if (document.RootElement.TryGetProperty("url", out var value))
                url = value.GetString();
            if (document.RootElement.TryGetProperty("tags", out var given)
                && given.ValueKind == JsonValueKind.Array)
                tags.AddRange(given.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t.Length > 0));
        }
        catch (JsonException)
        {
            _host.ShowNotification("That capture request was not readable.");
            return;
        }

        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            _host.ShowNotification("Capture needs an http or https address.");
            return;
        }

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = "Capturing",
            InitialStatus = address.Host,
            IsIndeterminate = true,
            AllowCancel = true
        });

        Captured page;
        try
        {
            page = await _lookup.CaptureAsync(address, progress.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException ex)
        {
            _host.ShowNotification($"Could not fetch that page: {ex.Message}");
            return;
        }

        await _host.ResearchService.SaveAsync(new ResearchItemInfo
        {
            Title = page.Title,
            Type = "Note",
            Content = ReaderMode.ToHtml(page),
            Tags = tags
        });

        progress.Dispose();
        _host.ShowNotification($"Captured \"{page.Title}\".");
    }

    // ── The board view ──

    public IWebViewController? CreateController(string viewKey)
        => viewKey == BoardView ? new BoardController(_host, _sprint, SaveSprint) : null;
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
