using System.IO.Compression;
using NSubstitute;
using Novalist.Extensions.Formats;
using Novalist.Extensions.Formats.Importers;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.Tests;

public sealed class ExportImportAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novalist-acceptance-" + Guid.NewGuid().ToString("N"));

    public ExportImportAcceptanceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("html")]
    [InlineData("rtf")]
    [InlineData("txt")]
    [InlineData("fountain")]
    [InlineData("fb2")]
    [InlineData("csv")]
    [InlineData("json")]
    [InlineData("opml")]
    [InlineData("odt")]
    public async Task EveryContributedFormatOmitsInactiveAndExcludedContent(string key)
    {
        var project = new FakeHost();
        var chapter = new FakeChapter { Title = "Chapter" };
        chapter.Scenes.AddRange([
            new() { Title = "PUBLICMARKER", Html = "<p>Publicprose.</p>" },
            new() { Title = "INACTIVEMARKER", Html = "<p>Inactiveprose.</p>", Inactive = true },
            new() { Title = "WITHHELDMARKER", Html = "<p>Withheldprose.</p>", ExcludeFromExport = true }
        ]);
        project.Chapters.Add(chapter);
        var host = Substitute.For<IHostServices>();
        host.ProjectService.Returns(project);
        var localization = Substitute.For<IExtensionLocalization>();
        localization.T(Arg.Any<string>()).Returns(call => call.Arg<string>());
        host.GetLocalization(Arg.Any<string>()).Returns(localization);
        var extension = new FormatsExtension();
        extension.Initialize(host);
        var format = Assert.Single(extension.GetExportFormats(), f => f.FormatKey == key);
        var path = Path.Combine(_root, "export" + format.FileExtension);

        await (format.Export ?? throw new InvalidOperationException("Missing export handler"))(
            new ExportContext { OutputPath = path, BookName = "Book" });

        var content = key == "odt" ? ReadOdt(path) : await File.ReadAllTextAsync(path);
        Assert.Contains(key is "opml" or "json" or "csv" ? "PUBLICMARKER" : "Publicprose", content);
        Assert.DoesNotContain("INACTIVEMARKER", content);
        Assert.DoesNotContain("WITHHELDMARKER", content);
        Assert.DoesNotContain("Inactiveprose", content);
        Assert.DoesNotContain("Withheldprose", content);
    }

    private static string ReadOdt(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader((archive.GetEntry("content.xml")
            ?? throw new InvalidOperationException("Missing ODT content")).Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task ScrivenerUsesTypedDraftRootAndPreservesNestedOrderWithLocalizedNames()
    {
        var manifest = Write("Novel.scriv/book.scrivx", """
            <ScrivenerProject><Binder>
              <BinderItem Type="DraftFolder"><Title>Manuskript</Title><Children>
                <BinderItem Type="Text" ID="root"><Title>Prolog</Title></BinderItem>
                <BinderItem Type="Folder"><Title>Erster Akt</Title><Children>
                  <BinderItem Type="Folder"><Title>Kapitel</Title><Children>
                    <BinderItem Type="Text" ID="first"><Title>Ankunft</Title></BinderItem>
                    <BinderItem Type="Text" ID="second"><Title>Abreise</Title></BinderItem>
                  </Children></BinderItem>
                </Children></BinderItem>
              </Children></BinderItem>
              <BinderItem Type="ResearchFolder"><Title>Manuscript</Title><Children>
                <BinderItem Type="Text" ID="research"><Title>Secret notes</Title></BinderItem>
              </Children></BinderItem>
              <BinderItem Type="TrashFolder"><Title>Manuskript</Title><Children>
                <BinderItem Type="Text" ID="trash"><Title>Deleted prose</Title></BinderItem>
              </Children></BinderItem>
            </Binder></ScrivenerProject>
            """);
        foreach (var id in new[] { "root", "first", "second", "research", "trash" })
            Write($"Novel.scriv/Files/Docs/{id}.rtf", @"{\rtf1 " + id + "}");
        var host = new FakeHost();

        var result = await ProjectImporters.ScrivenerAsync(host, manifest);

        Assert.Equal(3, result.Scenes);
        Assert.Empty(result.Skipped);
        Assert.Equal(["Manuskript", "Kapitel"], host.Chapters.Select(c => c.Title));
        Assert.Equal(["Prolog", "Ankunft", "Abreise"], host.Chapters.SelectMany(c => c.Scenes).Select(s => s.Title));
        Assert.Equal(["<p>root</p>", "<p>first</p>", "<p>second</p>"],
            host.Chapters.SelectMany(c => c.Scenes).Select(s => s.Html));
    }

    [Fact]
    public async Task MarkdownImportsMixedDepthInStableOrderAndIgnoresNonProseFiles()
    {
        Write("novel/02.md", "# Second\n\nTwo.");
        Write("novel/01.txt", "One.");
        Write("novel/B/Chapter/02.markdown", "# Nested second\n\nFour.");
        Write("novel/B/Chapter/01.md", "# Nested first\n\nThree.");
        Write("novel/A/entry.md", "# Earlier chapter\n\nEarlier.");
        Write("novel/A/private.json", "Secret metadata");
        Write("novel/cover.png", "Not prose");
        var host = new FakeHost();

        var result = await ProjectImporters.MarkdownFolderAsync(host, Path.Combine(_root, "novel"));

        Assert.Equal(5, result.Scenes);
        Assert.Equal(["novel", "A", "B/Chapter"], host.Chapters.Select(c => c.Title));
        Assert.Equal(["01", "Second", "Earlier chapter", "Nested first", "Nested second"],
            host.Chapters.SelectMany(c => c.Scenes).Select(s => s.Title));
    }

    [Theory]
    [InlineData(@"{\rtf1\uc2\u233abZ}", "éZ")]
    [InlineData(@"{\rtf1\uc2\u233\'65\'66Z}", "éZ")]
    [InlineData(@"{\rtf1\uc1\u233e{\uc2\u233ab}\u233e}", "ééé")]
    [InlineData(@"{\rtf1\ansicpg932 \'82\'a0\'82\'a2}", "あい")]
    public void RtfHandlesTwoCharacterFallbackAndMultibyteCodePages(string input, string expected)
        => Assert.Equal(expected, RtfReader.ToText(input));

    [Theory]
    [InlineData("\"Valid\"trailing")]
    [InlineData("bad\"inside\"cell")]
    [InlineData("\"Unclosed")]
    public async Task MalformedCsvAfterAValidRowImportsNothing(string malformed)
    {
        var host = new FakeHost();
        var file = Write("bad.csv", "chapter,scene,text\nOne,Valid,Valid prose\nOne,Bad," + malformed);

        var report = await ProjectImporters.DelimitedAsync(host, file);

        Assert.Empty(host.Chapters);
        Assert.Equal(0, report.Scenes);
        Assert.Contains("No scenes were imported", Assert.Single(report.Skipped));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task CsvPreservesQuotedNewlinesDelimitersAndEscapedQuotes(string newline)
    {
        var host = new FakeHost();
        var file = Write("quoted.csv", "chapter,scene,text" + newline
            + "One,\"A, scene\",\"First paragraph." + newline + newline + "Second \"\"quoted\"\" paragraph.\"" + newline);

        var report = await ProjectImporters.DelimitedAsync(host, file);

        Assert.Empty(report.Skipped);
        var scene = Assert.Single(Assert.Single(host.Chapters).Scenes);
        Assert.Equal("A, scene", scene.Title);
        Assert.Equal("<p>First paragraph.</p><p>Second \"quoted\" paragraph.</p>", scene.Html);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
        File.WriteAllText(path, content);
        return path;
    }
}
