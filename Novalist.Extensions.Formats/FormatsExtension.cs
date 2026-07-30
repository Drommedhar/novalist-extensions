using System.Text;
using System.Text.Json;
using Novalist.Extensions.Formats.Importers;
using Novalist.Extensions.Formats.Services;
using Novalist.Extensions.Formats.Writers;
using Novalist.Sdk;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Formats;

/// <summary>
/// Export formats and project importers.
///
/// Novalist ships the formats a novelist sends to an agent or a shop: DOCX,
/// EPUB, PDF, Markdown. Everything else - HTML, RTF, ODT, plain text, Fountain,
/// FictionBook - is a format somebody needs and nobody needs all of, which is
/// exactly what an extension is for. The same goes in the other direction: a
/// reader for another tool's project format is specialist, occasionally
/// breaking work that should not have to ship inside the app to be available
/// in it.
/// </summary>
public sealed class FormatsExtension : IExtension, IExportFormatContributor
{
    public string Id => "com.novalist.formats";
    public string DisplayName => "Formats";
    public string Description =>
        "Export to HTML, RTF, ODT, plain text, Fountain and FictionBook. Import from Scrivener, "
        + "Ulysses, Markdown folders and delimited files. Checks a finished EPUB before you send it.";
    public string Version { get; } = ManifestVersion.Read<FormatsExtension>();
    public string Author => "Novalist Team";

    private IHostServices _host = null!;
    private IExtensionLocalization _loc = null!;
    private readonly EpubPreflight _preflight = new();

    public void Initialize(IHostServices host)
    {
        _host = host;
        _loc = host.GetLocalization(Id);
        // The preflight is a hook rather than part of the exporter: whoever knows
        // the EPUB specification should own the check, and that is not the code
        // that wrote the file.
        _host.RegisterExportPostProcessor(_preflight);
        RegisterImportCommands();
    }

    public void Shutdown()
    {
        _host.UnregisterExportPostProcessor(_preflight);
        foreach (var command in ImportCommands.Keys) _host.UnregisterCommand(command);
    }

    // ── Export formats ──

    public IReadOnlyList<ExportFormatDescriptor> GetExportFormats() =>
    [
        // HTML embeds the cover as a data URI and FictionBook has a coverpage
        // element for it. The rest have nowhere to put a picture, so they say so
        // and the Export view hides the toggle rather than offering a lie.
        Text("html", "formats.html", ".html",
            "M4 4h16v16H4z M8 9h8 M8 13h5",
            TextWriters.Html, supportsCover: true),
        Text("rtf", "formats.rtf", ".rtf",
            "M6 3h9l5 5v13H6z M14 3v6h6",
            TextWriters.Rtf),
        Text("txt", "formats.txt", ".txt",
            "M5 4h14 M5 9h14 M5 14h9 M5 19h6",
            TextWriters.PlainText),
        Text("fountain", "formats.fountain", ".fountain",
            "M4 3h16v18H4z M8 7h8 M8 11h4 M8 15h8",
            TextWriters.Fountain),
        Text("fb2", "formats.fb2", ".fb2",
            "M4 4h12l4 4v12H4z M9 12h6 M9 16h4",
            TextWriters.Fb2, supportsCover: true),
        new ExportFormatDescriptor
        {
            FormatKey = "odt",
            DisplayName = _loc.T("formats.odt"),
            FileExtension = ".odt",
            Icon = string.Empty,
            IconPath = "M6 3h9l5 5v13H6z M9 12h6 M9 16h5",
            Export = async context =>
                await OdtWriter.WriteAsync(await ReadBookAsync(context), context.OutputPath)
        }
    ];

    /// <summary>
    /// A format that is one text file. Written UTF-8 without a byte-order mark,
    /// because a mark at the front of an HTML or Fountain file shows up as a
    /// stray character in half the tools that read them.
    /// </summary>
    private ExportFormatDescriptor Text(
        string key, string nameKey, string extension, string iconPath,
        Func<Manuscript, string> write, bool supportsCover = false)
        => new()
        {
            FormatKey = key,
            DisplayName = _loc.T(nameKey),
            FileExtension = extension,
            Icon = string.Empty,
            IconPath = iconPath,
            SupportsCover = supportsCover,
            Export = async context =>
            {
                var book = await ReadBookAsync(context);
                var directory = Path.GetDirectoryName(context.OutputPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(
                    context.OutputPath, write(book), new UTF8Encoding(false));
            }
        };

    /// <summary>
    /// The book, plus everything the host knows about it that a format needs: the
    /// author, the language the writer works in, and where their cover is. Without
    /// these every file here claimed to be English and had no picture on the front.
    /// </summary>
    private Task<Manuscript> ReadBookAsync(ExportContext context)
    {
        var title = string.IsNullOrWhiteSpace(context.BookName) ? "Untitled" : context.BookName;
        return Manuscript.ReadAsync(
            _host,
            title,
            new MsBook(
                title,
                context.Author,
                string.IsNullOrWhiteSpace(context.Language) ? "en" : context.Language,
                context.CoverImagePath,
                context.IncludeTitlePage),
            // The writer's chapter selection. Contributed formats used to be
            // given none, so every run produced the whole book and there was no
            // way to send somebody three chapters in anything but a built-in.
            context.SelectedChapterGuids);
    }

    // ── Importers, exposed as commands ──

    /// <summary>
    /// The importers, by command id. Commands rather than a view: an import is
    /// one decision and a file path, it is scriptable, and a whole panel to hold
    /// two controls would be worse than the command palette entry.
    /// </summary>
    private Dictionary<string, (string Title, string Description,
        Func<IHostServices, string, Task<ImportReport>> Run)> ImportCommands => new()
    {
        ["com.novalist.formats.import.scrivener"] = (
            _loc.T("formats.importScrivener"),
            _loc.T("formats.importScrivenerDesc"),
            ProjectImporters.ScrivenerAsync),
        ["com.novalist.formats.import.markdown"] = (
            _loc.T("formats.importMarkdown"),
            _loc.T("formats.importMarkdownDesc"),
            ProjectImporters.MarkdownFolderAsync),
        ["com.novalist.formats.import.delimited"] = (
            _loc.T("formats.importDelimited"),
            _loc.T("formats.importDelimitedDesc"),
            ProjectImporters.DelimitedAsync)
    };

    private void RegisterImportCommands()
    {
        foreach (var (id, (title, description, run)) in ImportCommands)
        {
            _host.RegisterCommand(
                new HostCommandInfo
                {
                    Id = id,
                    Title = title,
                    Description = description,
                    Mutates = true,
                    ArgumentsSchema =
                        """
                        {"type":"object","required":["path"],
                         "properties":{"path":{"type":"string",
                          "description":"Absolute path of the project, folder or file to read."}}}
                        """
                },
                argumentsJson => RunImportAsync(run, argumentsJson));
        }
    }

    /// <summary>
    /// Runs an importer and tells the writer what it did.
    ///
    /// Nothing here is silent. An import adds chapters to the open book, and a
    /// writer who does not know how many arrived, or that four scenes had no
    /// text file behind them, has no way to notice that something went wrong
    /// until much later.
    /// </summary>
    private async Task RunImportAsync(
        Func<IHostServices, string, Task<ImportReport>> run, string? argumentsJson)
    {
        var path = ReadPath(argumentsJson);
        if (path == null)
        {
            _host.ShowNotification(_loc.T("formats.importNeedsPath"));
            return;
        }

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = _loc.T("formats.importing"),
            InitialStatus = Path.GetFileName(path),
            IsIndeterminate = true
        });

        ImportReport report;
        try
        {
            report = await run(_host, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowNotification(_loc.T("formats.importFailed").Replace("{0}", ex.Message));
            return;
        }

        progress.Dispose();

        var summary = new StringBuilder(_loc.T("formats.imported")
            .Replace("{0}", report.Chapters.ToString())
            .Replace("{1}", report.Scenes.ToString()));
        if (report.Skipped.Count > 0)
            summary.Append(' ').Append(_loc.T("formats.importSkipped")
                .Replace("{0}", report.Skipped.Count.ToString())
                .Replace("{1}", report.Skipped[0]));
        _host.ShowNotification(summary.ToString());
    }

    private static string? ReadPath(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("path", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads the version out of the manifest next to the assembly, so the number in
/// extension.json is the only place it is written down.
/// </summary>
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
