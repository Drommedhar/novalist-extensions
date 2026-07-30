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
    private IExtensionLocalization _loc = null!;

    public void Initialize(IHostServices host)
    {
        _host = host;
        _loc = host.GetLocalization(Id);
        _host.RegisterCommand(
            new HostCommandInfo
            {
                Id = CommandId,
                Title = _loc.T("publish.command"),
                Description = _loc.T("publish.commandDesc"),
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

    private Sdk.Models.Wizards.WizardDefinition Definition() => new()
    {
        Id = WizardId,
        // Without this the wizard is a form that goes nowhere: the host collects
        // the answers and hands them to whoever asked for the run, which for a
        // wizard picked out of the command palette is not this extension.
        OnCompleted = GenerateFromAnswersAsync,
        DisplayName = _loc.T("publish.command"),
        Description = _loc.T("publish.wizardDesc"),
        Scope = Sdk.Models.Wizards.WizardScope.Project,
        Steps =
        [
            new Sdk.Models.Wizards.ChoiceStep
            {
                Id = "scope",
                Title = _loc.T("publish.scopeTitle"),
                Help = _loc.T("publish.scopeHelp"),
                Skippable = false,
                Choices =
                [
                    new Sdk.Models.Wizards.WizardChoice
                    {
                        Value = "World",
                        Label = _loc.T("publish.scopeWorld"),
                        Description = _loc.T("publish.scopeWorldDesc")
                    },
                    new Sdk.Models.Wizards.WizardChoice
                    {
                        Value = "Manuscript",
                        Label = _loc.T("publish.scopeManuscript"),
                        Description = _loc.T("publish.scopeManuscriptDesc")
                    },
                    new Sdk.Models.Wizards.WizardChoice
                    {
                        Value = "Everything",
                        Label = _loc.T("publish.scopeBoth"),
                        Description = _loc.T("publish.scopeBothDesc")
                    }
                ]
            },
            new Sdk.Models.Wizards.TextStep
            {
                Id = "title",
                Title = _loc.T("publish.titleTitle"),
                Help = _loc.T("publish.titleHelp"),
                Placeholder = _loc.T("publish.titlePlaceholder")
            },
            new Sdk.Models.Wizards.TextStep
            {
                Id = "subtitle",
                Title = _loc.T("publish.subtitleTitle"),
                Help = _loc.T("publish.subtitleHelp")
            },
            // A choice step whose one option is filled in by opening a native
            // folder dialog. A path typed by hand into a text box is wrong often
            // enough that the writer only finds out once the work has run.
            new Sdk.Models.Wizards.ChoiceStep
            {
                Id = "outputPath",
                Title = _loc.T("publish.folderTitle"),
                Help = _loc.T("publish.folderHelp"),
                Skippable = false,
                DynamicChoicesProvider = async _ =>
                {
                    var picked = await _host.PickFolderAsync(_loc.T("publish.pickTitle"));
                    return picked == null
                        ? []
                        : [new Sdk.Models.Wizards.WizardChoice
                        {
                            Value = picked,
                            Label = Path.GetFileName(picked.TrimEnd(
                                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                            Description = picked
                        }];
                }
            }
        ]
    };

    /// <summary>
    /// Runs the wizard. The generating is in OnCompleted, so it happens whether
    /// the wizard was started here or picked out of the command palette.
    /// </summary>
    private async Task RunWizardAsync() => await _host.RunWizardAsync(Definition());

    /// <summary>
    /// Turns the answers into a generate request.
    ///
    /// The folder step is a choice rather than free text, so the answer is the
    /// picked path - read as text either way, because a single-choice answer and
    /// a typed one arrive the same.
    /// </summary>
    private async Task GenerateFromAnswersAsync(Sdk.Models.Wizards.WizardResult result)
    {
        var outputPath = result.GetText("outputPath");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var multi = result.GetMulti("outputPath");
            if (multi.Count > 0) outputPath = multi[0];
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            // The writer got as far as the last step and cancelled the folder
            // dialog. Saying so beats writing nothing and looking successful.
            _host.ShowNotification(_loc.T("publish.noFolderChosen"));
            return;
        }

        await RunAsync(JsonSerializer.Serialize(new
        {
            outputPath,
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
                // The pages declare the language the book is written in, which is
                // not necessarily the one the menus are in - somebody can read
                // Novalist in English and write in German. The site's own words
                // follow the interface, because they are interface.
                Language = string.IsNullOrWhiteSpace(_host.WritingLanguage)
                    ? "en"
                    : _host.WritingLanguage,
                Text = Wording(),
                DiscourageCrawlers =
                    !root.TryGetProperty("discourageCrawlers", out var crawlers)
                    || crawlers.ValueKind != JsonValueKind.False
            };
        }
        catch (JsonException)
        {
            _host.ShowNotification(_loc.T("publish.unreadable"));
            return;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            _host.ShowNotification(_loc.T("publish.needsFolder"));
            return;
        }
        if (!_host.ProjectService.IsProjectLoaded)
        {
            _host.ShowNotification(_loc.T("publish.noProject"));
            return;
        }

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("publish.publishing"),
            InitialStatus = _loc.T("publish.readingProject"),
            IsIndeterminate = true,
            AllowCancel = true
        });

        try
        {
            var content = await ReadAsync(options.Scope, progress.CancellationToken);
            progress.SetStatus(_loc.T("publish.writingPages"));

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
            // The path, not just a count: a writer who has just generated a site
            // needs to be able to find it.
            _host.ShowNotification(_loc.T("publish.wrote")
                .Replace("{0}", files.Count.ToString())
                .Replace("{1}", outputPath));
        }
        catch (OperationCanceledException)
        {
            // A cancelled publish leaves the pages it already wrote. Deleting a
            // folder the writer chose is not something to do on their behalf.
            _host.ShowNotification(_loc.T("publish.cancelled"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowNotification(
                _loc.T("publish.writeFailed").Replace("{0}", ex.Message));
        }
    }

    /// <summary>
    /// The words the site puts on a page that the writer did not write, in their
    /// language. The generator has no host to ask, so they are handed to it.
    /// </summary>
    private SiteText Wording() => new()
    {
        Contents = _loc.T("publish.site.contents"),
        Previous = _loc.T("publish.site.previous"),
        Next = _loc.T("publish.site.next"),
        AlsoKnownAs = _loc.T("publish.site.alsoKnownAs"),
        NothingSelected = _loc.T("publish.site.nothingSelected"),
        NothingWritten = _loc.T("publish.site.nothingWritten"),
        NoProse = _loc.T("publish.site.noProse"),
        People = _loc.T("publish.site.people"),
        Places = _loc.T("publish.site.places"),
        Things = _loc.T("publish.site.things"),
        Lore = _loc.T("publish.site.lore"),
        Other = _loc.T("publish.site.other"),
        Character = _loc.T("publish.site.character"),
        Location = _loc.T("publish.site.location"),
        Item = _loc.T("publish.site.item"),
        Entry = _loc.T("publish.site.entry")
    };

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
