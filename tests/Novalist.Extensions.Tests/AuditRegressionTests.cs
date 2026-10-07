using System.Text.Json;
using NSubstitute;
using Novalist.Extensions.Formats.Importers;
using Novalist.Extensions.Formats.Services;
using Novalist.Extensions.Formats.Writers;
using Novalist.Extensions.Insight;
using Novalist.Extensions.Insight.Analysis;
using Novalist.Extensions.Publish;
using Novalist.Extensions.Publish.Site;
using Novalist.Extensions.Toolkit.Services;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novalist-audit-tests-" + Guid.NewGuid().ToString("N"));
    public AuditRegressionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task FormatsOmitInactiveAndWithheldScenes()
    {
        var host = new FakeHost();
        var chapter = new FakeChapter { Title = "Chapter" };
        chapter.Scenes.AddRange([new() { Title = "Public", Html = "<p>Public.</p>" },
            new() { Title = "Parked", Inactive = true }, new() { Title = "Private", ExcludeFromExport = true }]);
        host.Chapters.Add(chapter);
        var manuscript = await Manuscript.ReadAsync(host, "Book");
        Assert.Equal("Public", Assert.Single(Assert.Single(manuscript.Chapters).Scenes).Title);
    }

    [Fact]
    public void MarkdownUrlsCannotIntroduceAttributes()
    {
        var html = Markup.ToHtml("[reader](https://example.test/\"onmouseover=\"window.name=123)");
        Assert.DoesNotContain("\"onmouseover=\"", html);
        Assert.Contains("&quot;onmouseover=&quot;", html);
    }

    [Fact]
    public void HtmlCoverTitlesCannotIntroduceAttributes()
    {
        var cover = Path.Combine(_root, "cover.png");
        File.WriteAllBytes(cover, [1, 2, 3]);
        var title = "Book\" onload=\"window.name=1";
        var html = TextWriters.Html(new Manuscript(title, [], new(title, "", "en", cover, true)));
        Assert.Contains("alt=\"Book&quot; onload=&quot;window.name=1\"", html);
        Assert.DoesNotContain("\" onload=\"", html);
    }

    [Fact]
    public void AllPublishedPathsAreUniqueIncludingReservedNames()
    {
        var files = SiteGenerator.Generate(new SiteContent(
            [new("index", "lore", "Index", [], []), new("chapter", "lore", "Chapter 1", [], [])],
            [new("First", "", [])]), new SiteOptions { Scope = SiteScope.Everything });
        Assert.Equal(files.Count, files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(files, f => f.RelativePath == "index-2.html");
        Assert.Contains(files, f => f.RelativePath == "chapter-1-2.html");
    }

    [Fact]
    public async Task RepublishRemovesOnlyObsoleteOwnedPages()
    {
        await SitePublisher.WriteAsync(_root, [new("index.html", "Old"), new("chapter-1.html", "Secret")]);
        Write("notes.txt", "User file");
        await SitePublisher.WriteAsync(_root, [new("index.html", "World only")]);
        Assert.False(File.Exists(Path.Combine(_root, "chapter-1.html")));
        Assert.Equal("User file", File.ReadAllText(Path.Combine(_root, "notes.txt")));
        Assert.Equal("World only", File.ReadAllText(Path.Combine(_root, "index.html")));
    }

    [Fact]
    public async Task CancellationAndUnownedConflictsPreserveExistingOutput()
    {
        Write("index.html", "Unrelated");
        await Assert.ThrowsAsync<IOException>(() => SitePublisher.WriteAsync(_root, [new("index.html", "New")]));
        Assert.Equal("Unrelated", File.ReadAllText(Path.Combine(_root, "index.html")));
        var other = Path.Combine(_root, "site");
        await SitePublisher.WriteAsync(other, [new("index.html", "Original")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SitePublisher.WriteAsync(other,
            [new("index.html", "Changed")], cancellation.Token));
        Assert.Equal("Original", File.ReadAllText(Path.Combine(other, "index.html")));
    }

    [Fact]
    public async Task InvalidPublishManifestCannotDeleteOutsideTheSite()
    {
        Write("private.txt", "Keep");
        var site = Path.Combine(_root, "site");
        Write("site/.novalist-publish.json", "[\"../private.txt\"]");
        await Assert.ThrowsAsync<IOException>(() => SitePublisher.WriteAsync(site, [new("index.html", "New")]));
        Assert.Equal("Keep", File.ReadAllText(Path.Combine(_root, "private.txt")));
    }

    [Fact]
    public async Task CsvQuotedParagraphsRemainInOneSceneAndMalformedQuotesImportNothing()
    {
        var host = new FakeHost();
        var path = Write("novel.csv", "chapter,scene,text\r\nOne,Arrival,\"First, paragraph.\r\n\r\nSecond \"\"paragraph\"\".\"\r\n");
        var report = await ProjectImporters.DelimitedAsync(host, path);
        Assert.Equal(1, report.Scenes);
        Assert.Empty(report.Skipped);
        Assert.Equal("<p>First, paragraph.</p><p>Second \"paragraph\".</p>", host.Chapters[0].Scenes[0].Html);
        var malformed = new FakeHost();
        var refused = await ProjectImporters.DelimitedAsync(malformed, Write("bad.csv", "text\n\"unfinished\n"));
        Assert.Empty(malformed.Chapters);
        Assert.Contains("unclosed", Assert.Single(refused.Skipped));
    }

    [Fact]
    public async Task ScrivenerIncludesRootProseAndNeverImportsResearchDescendants()
    {
        var manifest = Write("Novel.scriv/book.scrivx", """
            <ScrivenerProject><Binder>
            <BinderItem Type="DraftFolder"><Title>Draft</Title><Children>
            <BinderItem Type="Text" ID="1"><Title>Actual prose</Title></BinderItem></Children></BinderItem>
            <BinderItem Type="ResearchFolder"><Title>Research</Title><Children><BinderItem Type="Folder">
            <Title>Private notes</Title><Children><BinderItem Type="Text" ID="2"><Title>Secret</Title></BinderItem>
            </Children></BinderItem></Children></BinderItem></Binder></ScrivenerProject>
            """);
        Write("Novel.scriv/Files/Docs/1.rtf", @"{\rtf1 Actual prose.}");
        var host = new FakeHost();
        var report = await ProjectImporters.ScrivenerAsync(host, manifest);
        Assert.Equal(1, report.Scenes);
        Assert.Equal("Actual prose", host.Chapters.Single().Scenes.Single().Title);
    }

    [Fact]
    public async Task MarkdownTraversesNestedChapterFolders()
    {
        Write("novel/Act One/Chapter One/scene.md", "# Arrival\n\nStory.");
        var host = new FakeHost();
        var result = await ProjectImporters.MarkdownFolderAsync(host, Path.Combine(_root, "novel"));
        Assert.Equal(1, result.Scenes);
        Assert.Equal("Act One/Chapter One", host.Chapters.Single().Title);
    }

    [Theory]
    [InlineData(@"{\rtf1\ansi\ansicpg1252 \'93hello\'94 \uc1\u233e}", "“hello” é")]
    [InlineData(@"{\rtf1\ansicpg1251 \'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    [InlineData(@"{\rtf1\uc1\u233\'65 {\uc0\u233}\u233e}", "é éé")]
    public void RtfRespectsDeclaredEncodingAndGroupScopedFallback(string rtf, string expected)
        => Assert.Equal(expected, RtfReader.ToText(rtf));

    [Fact]
    public void ContinuityTracksNewEntriesAndInvalidatesOldReviews()
    {
        var state = new WorklistState();
        var scenes = new[] { ("c", "s", "Chapter", "Scene", (IReadOnlyList<string>)new[] { "e", "new" }) };
        ContinuityWorklist.Rebase(state, [("e", "Entry", "v1")]);
        ContinuityWorklist.Build([("e", "Entry", "v2"), ("new", "New", "a")], scenes, state);
        ContinuityWorklist.MarkReviewed(state, "s", [("e", "v2")]);
        Assert.True(Assert.Single(ContinuityWorklist.Build([("e", "Entry", "v2")], scenes, state)).Reviewed);
        state = ContinuityWorklist.Deserialise(ContinuityWorklist.Serialise(state));
        Assert.False(Assert.Single(ContinuityWorklist.Build([("e", "Entry", "v3")], scenes, state)).Reviewed);
        Assert.Equal("New", Assert.Single(ContinuityWorklist.Build([("new", "New", "b")], scenes, state)).ChangedEntities.Single());
    }

    [Fact]
    public void EmptyImageLibraryStillReportsBrokenReferences()
    {
        var findings = ProjectHealth.Run(new HealthInput([new("e", "character", "Person", ["Images/lost.png"])], [], [], []));
        Assert.Contains(findings, f => f.Category == "Missing image");
    }

    [Fact]
    public void HealthResolvesLocalSceneUrisAndCountsMapsWithoutTreatingRemoteImagesAsMissing()
    {
        var input = new HealthInput([], [new("s", "Scene", "Chapter", "words", 1, [], [],
            ["novalist-project://nl/Images/scene%20picture.png", "https://example.test/remote.png", "data:image/png;base64,AA==", "Images/missing.png"])],
            ["Images/scene picture.png", "Images/map.png"], [])
        { Maps = [new("North", ["Images/map.png", "Images/missing-map.png"])] };
        var findings = ProjectHealth.Run(input);
        Assert.DoesNotContain(findings, f => f.Category == "Unused image");
        Assert.Equal(2, findings.Count(f => f.Category == "Missing image"));
        Assert.Contains(findings, f => f.Detail.Contains("missing-map.png"));
    }

    [Fact]
    public async Task PublishRollsBackWhenAReplacementCannotBeMovedIntoPlace()
    {
        await SitePublisher.WriteAsync(_root, [new("index.html", "Original"), new("old.html", "Keep until success")]);
        Directory.CreateDirectory(Path.Combine(_root, "blocked.html"));
        await Assert.ThrowsAnyAsync<IOException>(() => SitePublisher.WriteAsync(_root,
            [new("index.html", "Replacement"), new("blocked.html", "Cannot move")]));
        Assert.Equal("Original", File.ReadAllText(Path.Combine(_root, "index.html")));
        Assert.Equal("Keep until success", File.ReadAllText(Path.Combine(_root, "old.html")));
    }

    [Fact]
    public void SprintStopsBeforeUsingAnotherDraftsWordCount()
    {
        var now = DateTimeOffset.UtcNow;
        var sprint = new Sprint();
        sprint.Start(1000, now, "book/first-draft");
        sprint.Update(1010);
        Assert.True(sprint.StopIfScopeChanged("book/other-draft", now.AddMinutes(2)));
        sprint.Update(50000);
        Assert.Equal(10, Assert.Single(sprint.History).Words);
        Assert.Equal(SprintPhase.Idle, sprint.Phase);
    }

    private static IHostServices Host(IExtensionEntityService entities, FakeHost project)
    {
        var host = Substitute.For<IHostServices>();
        host.EntityService.Returns(entities);
        host.ProjectService.Returns(project);
        host.StoryService.Returns(project);
        var localization = Substitute.For<IExtensionLocalization>();
        localization.T(Arg.Any<string>()).Returns(call => call.Arg<string>());
        host.GetLocalization(Arg.Any<string>()).Returns(localization);
        host.ShowBusyProgress(Arg.Any<BusyProgressOptions>()).Returns(Substitute.For<IBusyProgress>());
        entities.LoadCharactersAsync().Returns(Array.Empty<CharacterInfo>());
        entities.LoadItemsAsync().Returns(Array.Empty<ItemInfo>());
        entities.LoadLoreAsync().Returns(Array.Empty<LoreInfo>());
        entities.GetCustomEntityTypes().Returns(Array.Empty<CustomEntityTypeInfo>());
        return host;
    }

    [Fact]
    public async Task PublishingUsesEntityBodiesAndRespectsReaderVisibility()
    {
        var entities = Substitute.For<IExtensionEntityService>();
        var project = new FakeHost();
        var chapter = new FakeChapter { Title = "Chapter" };
        chapter.Scenes.AddRange([new() { Title = "Public", Html = "<p>Public prose.</p>" },
            new() { Title = "Private", ExcludeFromExport = true, Html = "<p>Private prose.</p>" },
            new() { Title = "Parked", Inactive = true, Html = "<p>Parked prose.</p>" }]);
        project.Chapters.Add(chapter);
        var host = Host(entities, project);
        entities.LoadLocationsAsync().Returns(new[] { new LocationInfo { Id = "place", Name = "Port" }, new LocationInfo { Id = "secret" } });
        entities.GetEntityContentAsync("location", "place").Returns(new EntityContentInfo
        {
            Id = "place", Name = "Port", Description = "A **busy** harbour.",
            Sections = [new() { Title = "History", Content = "Old docks." }, new() { Content = "Hidden twist", ReaderHidden = true }]
        });
        entities.GetEntityContentAsync("location", "secret").Returns(new EntityContentInfo { Id = "secret", Name = "Secret", ReaderHidden = true });
        Func<string?, Task>? command = null;
        host.When(h => h.RegisterCommand(Arg.Any<HostCommandInfo>(), Arg.Any<Func<string?, Task>>()))
            .Do(call => command = call.Arg<Func<string?, Task>>());
        var extension = new PublishExtension();
        extension.Initialize(host);
        await (command ?? throw new InvalidOperationException("Publish command was not registered"))(
            JsonSerializer.Serialize(new { outputPath = _root, scope = "Everything" }));
        var page = File.ReadAllText(Path.Combine(_root, "port.html"));
        Assert.Contains("<strong>busy</strong>", page);
        Assert.Contains("Old docks", page);
        Assert.DoesNotContain("Hidden twist", page);
        Assert.False(File.Exists(Path.Combine(_root, "secret.html")));
        var prose = File.ReadAllText(Path.Combine(_root, "chapter-1.html"));
        Assert.Contains("Public prose", prose);
        Assert.DoesNotContain("Private prose", prose);
        Assert.DoesNotContain("Parked prose", prose);
    }

    [Fact]
    public async Task HealthControllerKeepsImageIdentityAndSerializesNamedSeverity()
    {
        var entities = Substitute.For<IExtensionEntityService>();
        var host = Host(entities, new FakeHost());
        host.ResearchService.Returns(Substitute.For<IExtensionResearchService>());
        entities.LoadLocationsAsync().Returns(new[] { new LocationInfo { Id = "place", Name = "Port" } });
        entities.GetEntityContentAsync("location", "place").Returns(new EntityContentInfo { Id = "place", Name = "Port", ImagePaths = ["Images/port.png"] });
        entities.GetProjectImages().Returns(["Images/port.png"]);
        var extension = new InsightExtension();
        extension.Initialize(host);
        var controller = extension.CreateController("com.novalist.insight.report.web") ?? throw new InvalidOperationException();
        var response = await controller.OnMessageAsync("{\"kind\":\"health\"}");
        Assert.DoesNotContain("Missing image", response);
        Assert.DoesNotContain("Unused image", response);
        Assert.Contains("\"severity\":\"Warning\"", response);
    }

    [Fact]
    public async Task AllEntityKindsPublishBodiesAndContinuityDetectsContentOnlyEdits()
    {
        var entities = Substitute.For<IExtensionEntityService>();
        var project = new FakeHost();
        var chapter = new FakeChapter { Title = "Chapter" };
        chapter.Scenes.Add(new() { Id = "scene", Title = "Scene", Html = "<p>Prose.</p>" });
        project.Chapters.Add(chapter);
        var host = Host(entities, project);
        var contents = EntityContents(entities);
        host.GetExtensionDataPath(Arg.Any<string>()).Returns(_root);
        host.GetConfirmedMentionIdsAsync(chapter.Guid, "scene").Returns(contents.Keys.ToArray());
        Func<string?, Task>? publish = null;
        host.When(h => h.RegisterCommand(Arg.Any<HostCommandInfo>(), Arg.Any<Func<string?, Task>>()))
            .Do(call => publish = call.Arg<Func<string?, Task>>());
        new PublishExtension().Initialize(host);
        await (publish ?? throw new InvalidOperationException("Missing publish command"))(
            JsonSerializer.Serialize(new { outputPath = Path.Combine(_root, "site"), scope = "World" }));
        var insight = new InsightExtension();
        insight.Initialize(host);
        var controller = insight.CreateController("com.novalist.insight.report.web")
            ?? throw new InvalidOperationException("Missing continuity controller");

        foreach (var type in contents.Keys.ToArray())
        {
            var page = await File.ReadAllTextAsync(Path.Combine(_root, "site", type + ".html"));
            Assert.Contains(type + " description", page);
            Assert.Contains(type + " section", page);
            await controller.OnMessageAsync("{\"kind\":\"continuityRebase\"}");
            contents[type] = new EntityContentInfo
            {
                Id = type, Name = type, Description = type + " description",
                Sections = [new() { Title = "History", Content = type + " edited section" }]
            };

            using var response = JsonDocument.Parse(await controller.OnMessageAsync("{\"kind\":\"continuity\"}")
                ?? throw new InvalidOperationException("Missing continuity response"));
            var item = Assert.Single(response.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray());
            Assert.Equal("scene", item.GetProperty("sceneId").GetString());
            Assert.Equal(type, Assert.Single(item.GetProperty("changedEntities").EnumerateArray()).GetString());
            Assert.False(item.GetProperty("reviewed").GetBoolean());
        }
    }

    private static Dictionary<string, EntityContentInfo> EntityContents(IExtensionEntityService entities)
    {
        entities.LoadCharactersAsync().Returns([new() { Id = "character", DisplayName = "character" }]);
        entities.LoadLocationsAsync().Returns([new() { Id = "location", Name = "location" }]);
        entities.LoadItemsAsync().Returns([new() { Id = "item", Name = "item" }]);
        entities.LoadLoreAsync().Returns([new() { Id = "lore", Name = "lore" }]);
        entities.GetCustomEntityTypes().Returns([new() { TypeKey = "faction" }]);
        entities.LoadCustomEntitiesAsync("faction").Returns([new() { Id = "faction", Name = "faction" }]);
        var contents = new[] { "character", "location", "item", "lore", "faction" }.ToDictionary(type => type,
            type => new EntityContentInfo { Id = type, Name = type, Description = type + " description",
                Sections = [new() { Title = "History", Content = type + " section" }] });
        entities.GetEntityContentAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => contents[call.ArgAt<string>(0)]);
        return contents;
    }
}
