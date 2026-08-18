using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Tests;

/// <summary>A chapter an importer built, with the scenes it put in it.</summary>
public sealed class FakeChapter
{
    public string Guid { get; init; } = System.Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Act { get; set; } = string.Empty;
    public List<FakeScene> Scenes { get; } = [];
}

public sealed class FakeScene
{
    public string Id { get; init; } = System.Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
}

/// <summary>
/// A host that records what an extension did to a project rather than having one.
///
/// Only the calls these extensions actually make are implemented; everything
/// else throws, so a test that starts depending on a new part of the host fails
/// loudly rather than passing against a silent no-op.
/// </summary>
public sealed class FakeHost : IHostServices, IExtensionProjectService, IExtensionStoryService
{
    public List<FakeChapter> Chapters { get; } = [];
    public List<string> Notifications { get; } = [];

    // ── The bits importers use ──

    Task<string> IExtensionProjectService.CreateChapterAsync(string title)
    {
        var chapter = new FakeChapter { Title = title };
        Chapters.Add(chapter);
        return Task.FromResult(chapter.Guid);
    }

    Task<string> IExtensionProjectService.CreateSceneAsync(string chapterGuid, string title)
    {
        var chapter = Chapters.FirstOrDefault(c => c.Guid == chapterGuid);
        if (chapter == null) return Task.FromResult(string.Empty);
        var scene = new FakeScene { Title = title };
        chapter.Scenes.Add(scene);
        return Task.FromResult(scene.Id);
    }

    Task IExtensionProjectService.WriteSceneContentAsync(
        string chapterGuid, string sceneId, string html)
    {
        var scene = Chapters.FirstOrDefault(c => c.Guid == chapterGuid)?
            .Scenes.FirstOrDefault(s => s.Id == sceneId);
        if (scene != null) scene.Html = html;
        return Task.CompletedTask;
    }

    Task<string> IExtensionProjectService.ReadSceneContentAsync(string chapterGuid, string sceneId)
        => Task.FromResult(
            Chapters.FirstOrDefault(c => c.Guid == chapterGuid)?
                .Scenes.FirstOrDefault(s => s.Id == sceneId)?.Html ?? string.Empty);

    IReadOnlyList<ChapterInfo> IExtensionProjectService.GetChaptersOrdered()
        => [.. Chapters.Select((c, i) => new ChapterInfo
        {
            Guid = c.Guid,
            Title = c.Title,
            Order = i + 1
        })];

    IReadOnlyList<SceneInfo> IExtensionProjectService.GetScenesForChapter(string chapterGuid)
    {
        var chapter = Chapters.FirstOrDefault(c => c.Guid == chapterGuid);
        return chapter == null
            ? []
            : [.. chapter.Scenes.Select(s => new SceneInfo
            {
                Id = s.Id,
                Title = s.Title,
                ChapterGuid = chapter.Guid,
                ChapterTitle = chapter.Title
            })];
    }

    /// <summary>The book, as the host now reports it. Whatever a test sets here
    /// is what an extension reading the book's own declarations sees - the
    /// narrator's brief is built from exactly these.</summary>
    public BookDetailInfo Book { get; set; } = new();

    BookDetailInfo? IExtensionStoryService.GetBookDetail() => Book;

    SceneDetailInfo? IExtensionStoryService.GetSceneDetail(string chapterGuid, string sceneId)
    {
        var chapter = Chapters.FirstOrDefault(c => c.Guid == chapterGuid);
        var scene = chapter?.Scenes.FirstOrDefault(s => s.Id == sceneId);
        return scene == null
            ? null
            : new SceneDetailInfo
            {
                Id = scene.Id,
                Title = scene.Title,
                ChapterGuid = chapterGuid,
                Act = chapter!.Act
            };
    }

    public void ShowNotification(string message) => Notifications.Add(message);

    /// <summary>What a picker returns. Set by a test that needs one to succeed.</summary>
    public string? PickResult { get; set; }
    public List<string> PickTitles { get; } = [];

    public Task<string?> PickFolderAsync(string title)
    {
        PickTitles.Add(title);
        return Task.FromResult(PickResult);
    }

    public Task<string?> PickFileAsync(string title, bool images = false)
    {
        PickTitles.Add(title);
        return Task.FromResult(PickResult);
    }

    public IExtensionProjectService ProjectService => this;
    public IExtensionStoryService StoryService => this;

    // ── Everything else: loud rather than silent ──

    private static T No<T>() => throw new NotSupportedException(
        "The fake host does not implement this. Add it when a test needs it.");

    public IExtensionFileService FileService => No<IExtensionFileService>();
    public IExtensionEntityService EntityService => No<IExtensionEntityService>();
    public IExtensionResearchService ResearchService => No<IExtensionResearchService>();
    public IExtensionReviewService ReviewService => No<IExtensionReviewService>();
    public IExtensionArchiveService ArchiveService => No<IExtensionArchiveService>();
    public string HostVersion => "3.0.0";
    public string CurrentLanguage => "en";
    public string WritingLanguage { get; set; } = "en";
    public string CurrentLanguageDisplayName => "English";

    string? IExtensionProjectService.ProjectRoot => "/project";
    string? IExtensionProjectService.ActiveBookRoot => "/project/Books/One";
    string? IExtensionProjectService.WorldBibleRoot => "/project/WorldBible";
    bool IExtensionProjectService.IsProjectLoaded => true;
    SceneInfo? IExtensionProjectService.CurrentScene => null;
    string? IExtensionProjectService.ActiveBookId => "book-one";
    string? IExtensionProjectService.ActiveDraftId => "draft-one";

    Task<string> IExtensionProjectService.GetSceneSynopsisAsync(string c, string s) => No<Task<string>>();
    Task IExtensionProjectService.SetSceneSynopsisAsync(string c, string s, string y) => No<Task>();
    Task<bool> IExtensionProjectService.IsSceneBusyAsync(string c, string s)
        => Task.FromResult(false);
    Task<bool> IExtensionProjectService.RenameChapterAsync(string c, string t) => No<Task<bool>>();
    Task<bool> IExtensionProjectService.RenameSceneAsync(string c, string s, string t) => No<Task<bool>>();
    Task<bool> IExtensionProjectService.MoveSceneAsync(string s, string c, int i) => No<Task<bool>>();
    Task<bool> IExtensionProjectService.MoveChapterAsync(string c, int o) => No<Task<bool>>();
    Task<bool> IExtensionProjectService.SetChapterActAsync(string c, string a) => No<Task<bool>>();
    Task<bool> IExtensionProjectService.TrashChapterAsync(string c) => No<Task<bool>>();
    Task<bool> IExtensionProjectService.ArchiveSceneAsync(string c, string s) => No<Task<bool>>();
    Task<string?> IExtensionProjectService.CreateProjectAsync(string p, string n, string b)
        => No<Task<string?>>();
    IReadOnlyList<BookInfo> IExtensionProjectService.GetBooks() => [];
    Task<string> IExtensionProjectService.CreateBookAsync(string n) => No<Task<string>>();
    Task<bool> IExtensionProjectService.RenameBookAsync(string b, string n) => No<Task<bool>>();
    Task<bool> IExtensionProjectService.SwitchBookAsync(string b) => No<Task<bool>>();
    IReadOnlyList<DraftInfo> IExtensionProjectService.GetDrafts() => [];
    Task<string> IExtensionProjectService.CreateDraftAsync(string n, string? c)
        => No<Task<string>>();
    Task<bool> IExtensionProjectService.RenameDraftAsync(string d, string n) => No<Task<bool>>();
    Task<bool> IExtensionProjectService.SwitchDraftAsync(string d) => No<Task<bool>>();

    ChapterDetailInfo? IExtensionStoryService.GetChapterDetail(string c) => null;
    Task<bool> IExtensionStoryService.SetChapterStatusAsync(string c, string s) => No<Task<bool>>();
    Task<bool> IExtensionStoryService.SetSceneMetadataAsync(
        string c, string s, SceneMetadataPatch p) => No<Task<bool>>();
    IReadOnlyList<ActInfo> IExtensionStoryService.GetActs() => [];
    IReadOnlyList<PlotlineInfo> IExtensionStoryService.GetPlotlines() => [];
    Task<string> IExtensionStoryService.CreatePlotlineAsync(string n, string c, string d) => No<Task<string>>();
    Task<bool> IExtensionStoryService.SetScenePlotlinesAsync(string c, string s, IReadOnlyList<string> p) => No<Task<bool>>();
    string IExtensionStoryService.GetCellNote(string c, string s, string p) => string.Empty;
    Task<bool> IExtensionStoryService.SetCellNoteAsync(string c, string s, string p, string n)
        => No<Task<bool>>();
    IReadOnlyList<SmartListInfo> IExtensionStoryService.GetSmartLists() => [];
    Task<IReadOnlyList<MapInfo>> IExtensionStoryService.GetMapsAsync()
        => Task.FromResult<IReadOnlyList<MapInfo>>([]);
    IReadOnlyList<TimelineEventInfo> IExtensionStoryService.GetTimelineEvents() => [];
    Task<string> IExtensionStoryService.SaveTimelineEventAsync(TimelineEventInfo e) => No<Task<string>>();
    Task<bool> IExtensionStoryService.DeleteTimelineEventAsync(string e) => No<Task<bool>>();

    public string GetExtensionDataPath(string extensionId) => "/data";
    public string GetExtensionSettingsPath(string extensionId) => "/settings";
    public void PostToUI(Action action) => action();
    public IExtensionLocalization GetLocalization(string extensionId) => No<IExtensionLocalization>();
    public IBusyProgress ShowBusyProgress(BusyProgressOptions options) => new NoProgress();
    public void ActivateContentView(string viewKey) { }
    public void ToggleRightSidebar(string panelId) { }
    public void RegisterEditorExtension(IEditorExtension extension) { }
    public void UnregisterEditorExtension(IEditorExtension extension) { }
    public void RegisterInlineActionContributor(IInlineActionContributor contributor) { }
    public void UnregisterInlineActionContributor(IInlineActionContributor contributor) { }
    public IReadOnlyList<IInlineActionContributor> GetInlineActionContributors() => [];
    public Task<Sdk.Models.Wizards.WizardResult?> RunWizardAsync(
        Sdk.Models.Wizards.WizardDefinition definition,
        Sdk.Models.Wizards.WizardResult? seed = null) => No<Task<Sdk.Models.Wizards.WizardResult?>>();
    public void RegisterHotkey(HotkeyDescriptor descriptor) { }
    public void UnregisterHotkey(string actionId) { }
    public Task<SceneAnalysisRecord?> GetSceneAnalysisAsync(string sceneId) => No<Task<SceneAnalysisRecord?>>();
    public Task SaveSceneAnalysisAsync(SceneAnalysisRecord record, string sceneText) => No<Task>();
    public Task<bool> IsSceneAnalysisStaleAsync(string sceneId, string sceneText) => No<Task<bool>>();
    public Task<IReadOnlyList<string>> GetStaleSceneIdsAsync(IReadOnlyList<SceneTextPair> scenes)
        => No<Task<IReadOnlyList<string>>>();
    public Task<IReadOnlyList<string>> GetConfirmedMentionIdsAsync(string chapterGuid, string sceneId)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public IReadOnlyList<IAiHook> GetAiHooks() => [];
    public string? ReadHostData(string key) => null;
    public Task WriteHostDataAsync(string key, string json) => Task.CompletedTask;

    public List<HostCommandInfo> Commands { get; } = [];
    private readonly Dictionary<string, Func<string?, Task>> _handlers = new(StringComparer.Ordinal);
    public IReadOnlyList<HostCommandInfo> GetCommands() => Commands;
    public Task<bool> InvokeCommandAsync(string commandId, string? argumentsJson = null)
        => _handlers.TryGetValue(commandId, out var handler)
            ? handler(argumentsJson).ContinueWith(_ => true)
            : Task.FromResult(false);
    public void RegisterCommand(HostCommandInfo command, Func<string?, Task> handler)
    {
        Commands.Add(command);
        _handlers[command.Id] = handler;
    }
    public void UnregisterCommand(string commandId)
    {
        Commands.RemoveAll(c => c.Id == commandId);
        _handlers.Remove(commandId);
    }

    public List<IExportPostProcessor> ExportProcessors { get; } = [];
    public void RegisterExportPostProcessor(IExportPostProcessor processor)
        => ExportProcessors.Add(processor);
    public void UnregisterExportPostProcessor(IExportPostProcessor processor)
        => ExportProcessors.Remove(processor);

    public event Action<ProjectInfo>? ProjectLoaded;
    public event Action<SceneInfo>? SceneOpened;
    public event Action<SceneInfo>? SceneSaved;
    public event Action<BookInfo>? BookChanged;
    public event Action<string>? LanguageChanged;

    /// <summary>Fires the events, so a test can check an extension reacts to them.</summary>
    public void RaiseProjectLoaded() => ProjectLoaded?.Invoke(new ProjectInfo { Name = "P" });
    public void RaiseSceneOpened(SceneInfo scene) => SceneOpened?.Invoke(scene);
    public void RaiseSceneSaved(SceneInfo scene) => SceneSaved?.Invoke(scene);
    public void RaiseBookChanged() => BookChanged?.Invoke(new BookInfo { Name = "One" });
    public void RaiseLanguageChanged(string language) => LanguageChanged?.Invoke(language);

    private sealed class NoProgress : IBusyProgress
    {
        public void SetStatus(string status) { }
        public void SetProgress(double value) { }
        public void SetTitle(string title) { }
        public void SetIndeterminate(bool isIndeterminate) { }
        public void SetDetails(IReadOnlyList<string>? lines) { }
        public CancellationToken CancellationToken => CancellationToken.None;
        public bool IsClosed { get; private set; }
        public event Action? Cancelled;
        public void Dispose()
        {
            IsClosed = true;
            Cancelled?.Invoke();
        }
    }
}
