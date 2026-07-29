using System.Text;
using System.Text.Json;
using Novalist.Extensions.Publish.Site;
using Novalist.Sdk;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Publish;

/// <summary>
/// Generates a static website from a project.
///
/// A series bible people can read, or a draft that can be sent to beta readers
/// without asking them to install anything. Static files, no build step, no
/// server: the writer can open the folder locally, put it on any hosting, or mail
/// it as a zip and it still works in ten years.
///
/// Nothing here uploads anything. It writes a folder and tells the writer where
/// it is. Where that folder goes next is their decision, and an extension that
/// pushed a draft somewhere on a writer's behalf would be making the one decision
/// that is genuinely not its to make.
/// </summary>
public sealed class PublishExtension : IExtension, IWizardContributor
{
    private const string CommandId = "com.novalist.publish.site";
    private const string WizardId = "com.novalist.publish.wizard";

    public string Id => "com.novalist.publish";
    public string DisplayName => "Publish";
    public string Description =>
        "Generates a self-contained static website from your world, your manuscript or both.";
    public string Version { get; } = ManifestVersion.Read<PublishExtension>();
    public string Author => "Novalist Team";

    private IHostServices _host = null!;

    public void Initialize(IHostServices host)
    {
        _host = host;
        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = CommandId,
                Title = "Publish a website",
                Description = "Writes a folder of HTML you can share or host.",
                ArgumentsSchema =
                    """
                    {"type":"object","required":["outputPath"],
                     "properties":{
                       "outputPath":{"type":"string","description":"Folder to write into."},
                       "scope":{"type":"string","enum":["World","Manuscript","Everything"]},
                       "title":{"type":"string"},
                       "subtitle":{"type":"string"},
                       "discourageCrawlers":{"type":"boolean"}}}
                    """
            },
            argumentsJson => string.IsNullOrWhiteSpace(argumentsJson)
                ? RunWizardAsync()
                : RunAsync(argumentsJson));
    }

    public void Shutdown() => _host.UnregisterCommand(CommandId);

    // ── The wizard, which is how a person reaches this ──

    public IReadOnlyList<Sdk.Models.Wizards.WizardDefinition> GetWizards() => [Definition()];

    private static Sdk.Models.Wizards.WizardDefinition Definition() => new()
    {
        Id = WizardId,
        DisplayName = "Publish a website",
        Description =
            "Writes a folder of HTML from your world, your manuscript or both. "
            + "Nothing is uploaded anywhere - you get a folder.",
        Scope = Sdk.Models.Wizards.WizardScope.Project,
        Steps =
        [
            new Sdk.Models.Wizards.ChoiceStep
            {
                Id = "scope",
                Title = "What to publish",
                Help = "A world bible for readers, the manuscript to be read, or both.",
                Skippable = false,
                Choices =
                [
                    new Sdk.Models.Wizards.WizardChoice
                    {
                        Value = "World",
                        Label = "The world",
                        Description = "Codex entries only - a series bible."
                    },
                    new Sdk.Models.Wizards.WizardChoice
                    {
                        Value = "Manuscript",
                        Label = "The manuscript",
                        Description = "Chapters and scenes, laid out to be read."
                    },
                    new Sdk.Models.Wizards.WizardChoice
                    {
                        Value = "Everything",
                        Label = "Both",
                        Description = "The manuscript with the world cross-linked into it."
                    }
                ]
            },
            new Sdk.Models.Wizards.TextStep
            {
                Id = "title",
                Title = "Site title",
                Help = "Shown at the top of every page.",
                Placeholder = "The name of the book or the series"
            },
            new Sdk.Models.Wizards.TextStep
            {
                Id = "subtitle",
                Title = "Subtitle",
                Help = "Optional. One line under the title on the front page."
            },
            new Sdk.Models.Wizards.TextStep
            {
                Id = "outputPath",
                Title = "Where to write it",
                Help = "An empty folder. Files with the same names are replaced.",
                Skippable = false,
                Validator = result => Task.FromResult(
                    string.IsNullOrWhiteSpace(result.GetText("outputPath"))
                        ? "Pick a folder to write into."
                        : null)
            }
        ]
    };

    /// <summary>
    /// Runs the wizard and generates from its answers. Reached from the command,
    /// because a wizard that has been filled in and then does nothing is worse
    /// than no wizard.
    /// </summary>
    private async Task RunWizardAsync()
    {
        var result = await _host.RunWizardAsync(Definition());
        if (result is not { Completed: true }) return;

        await RunAsync(JsonSerializer.Serialize(new
        {
            outputPath = result.GetText("outputPath"),
            scope = result.GetText("scope"),
            title = result.GetText("title"),
            subtitle = result.GetText("subtitle")
        }));
    }

    // ── Generating ──

    private async Task RunAsync(string? argumentsJson)
    {
        SiteOptions options;
        string outputPath;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? "{}");
            var root = document.RootElement;
            outputPath = Text(root, "outputPath") ?? string.Empty;
            options = new SiteOptions
            {
                Title = Text(root, "title") is { Length: > 0 } title
                    ? title
                    : _host.ProjectService.ActiveBookRoot is { } book
                        ? Path.GetFileName(book)
                        : "Untitled",
                Subtitle = Text(root, "subtitle") ?? string.Empty,
                Scope = Enum.TryParse<SiteScope>(Text(root, "scope"), true, out var scope)
                    ? scope
                    : SiteScope.World,
                DiscourageCrawlers =
                    !root.TryGetProperty("discourageCrawlers", out var crawlers)
                    || crawlers.ValueKind != JsonValueKind.False
            };
        }
        catch (JsonException)
        {
            _host.ShowNotification("That publish request was not readable.");
            return;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            _host.ShowNotification("Publishing needs a folder to write into.");
            return;
        }
        if (!_host.ProjectService.IsProjectLoaded)
        {
            _host.ShowNotification("Open a project first.");
            return;
        }

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = "Publishing",
            InitialStatus = "Reading the project",
            IsIndeterminate = true,
            AllowCancel = true
        });

        try
        {
            var content = await ReadAsync(options.Scope, progress.CancellationToken);
            progress.SetStatus("Writing pages");

            var files = SiteGenerator.Generate(content, options);
            Directory.CreateDirectory(outputPath);
            foreach (var file in files)
            {
                progress.CancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(
                    Path.Combine(outputPath, file.RelativePath),
                    file.Content,
                    new UTF8Encoding(false),
                    progress.CancellationToken);
            }

            progress.Dispose();
            _host.ShowNotification($"Wrote {files.Count} page(s) to {outputPath}.");
        }
        catch (OperationCanceledException)
        {
            // A cancelled publish leaves the pages it already wrote. Deleting a
            // folder the writer chose is not something to do on their behalf.
            _host.ShowNotification("Publishing was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowNotification($"Could not write the site: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads what the chosen scope needs, and no more. A manuscript-only site does
    /// not need the Codex read, and a world-only site does not need every scene
    /// loaded off disk.
    /// </summary>
    private async Task<SiteContent> ReadAsync(SiteScope scope, CancellationToken cancellationToken)
    {
        var entries = new List<SiteEntry>();
        var chapters = new List<SiteChapter>();

        if (scope != SiteScope.Manuscript)
        {
            foreach (var character in await _host.EntityService.LoadCharactersAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detailed = await _host.EntityService.GetCharacterDetailedAsync(
                    character.Id, null, null);
                entries.Add(new SiteEntry(
                    character.Id, "character", character.DisplayName,
                    [.. (detailed?.Sections ?? []).Select(s => (s.Title, s.Content))],
                    character.Aliases));
            }
            foreach (var location in await _host.EntityService.LoadLocationsAsync())
                entries.Add(new SiteEntry(location.Id, "location", location.Name, [], []));
            foreach (var item in await _host.EntityService.LoadItemsAsync())
                entries.Add(new SiteEntry(item.Id, "item", item.Name, [], []));
            foreach (var lore in await _host.EntityService.LoadLoreAsync())
                entries.Add(new SiteEntry(lore.Id, "lore", lore.Name, [], []));

            foreach (var type in _host.EntityService.GetCustomEntityTypes())
            {
                foreach (var custom in await _host.EntityService.LoadCustomEntitiesAsync(type.TypeKey))
                {
                    entries.Add(new SiteEntry(
                        custom.Id, type.TypeKey, custom.Name,
                        [.. (custom.Sections ?? []).Select(s => (s.Title, s.Content))], []));
                }
            }
        }

        if (scope != SiteScope.World)
        {
            foreach (var chapter in _host.ProjectService.GetChaptersOrdered())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scenes = new List<SiteScene>();
                foreach (var scene in _host.ProjectService.GetScenesForChapter(chapter.Guid))
                {
                    var html = await _host.ProjectService.ReadSceneContentAsync(
                        chapter.Guid, scene.Id);
                    scenes.Add(new SiteScene(scene.Title, Paragraphs(html)));
                }

                var first = _host.ProjectService.GetScenesForChapter(chapter.Guid).FirstOrDefault();
                var act = first == null
                    ? string.Empty
                    : _host.StoryService.GetSceneDetail(chapter.Guid, first.Id)?.Act ?? string.Empty;
                chapters.Add(new SiteChapter(chapter.Title, act, scenes));
            }
        }

        return new SiteContent(entries, chapters);
    }

    private static IReadOnlyList<string> Paragraphs(string html)
    {
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(
            html ?? string.Empty, @"</p\s*>|<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            withBreaks, "<[^>]+>", string.Empty);
        return [.. System.Net.WebUtility.HtmlDecode(stripped)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)];
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;
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
