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
    private readonly EpubPreflight _preflight = new();

    public void Initialize(IHostServices host)
    {
        _host = host;
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
        Text("html", "Web page (HTML)", ".html",
            "M4 4h16v16H4z M8 9h8 M8 13h5",
            TextWriters.Html),
        Text("rtf", "Rich Text Format", ".rtf",
            "M6 3h9l5 5v13H6z M14 3v6h6",
            TextWriters.Rtf),
        Text("txt", "Plain text", ".txt",
            "M5 4h14 M5 9h14 M5 14h9 M5 19h6",
            TextWriters.PlainText),
        Text("fountain", "Screenplay (Fountain)", ".fountain",
            "M4 3h16v18H4z M8 7h8 M8 11h4 M8 15h8",
            TextWriters.Fountain),
        Text("fb2", "FictionBook 2", ".fb2",
            "M4 4h12l4 4v12H4z M9 12h6 M9 16h4",
            TextWriters.Fb2),
        new ExportFormatDescriptor
        {
            FormatKey = "odt",
            DisplayName = "OpenDocument Text",
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
        string key, string name, string extension, string iconPath, Func<Manuscript, string> write)
        => new()
        {
            FormatKey = key,
            DisplayName = name,
            FileExtension = extension,
            Icon = string.Empty,
            IconPath = iconPath,
            Export = async context =>
            {
                var book = await ReadBookAsync(context);
                var directory = Path.GetDirectoryName(context.OutputPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(
                    context.OutputPath, write(book), new UTF8Encoding(false));
            }
        };

    private Task<Manuscript> ReadBookAsync(ExportContext context)
        => Manuscript.ReadAsync(_host, string.IsNullOrWhiteSpace(context.BookName)
            ? "Untitled"
            : context.BookName);

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
            "Import a Scrivener project",
            "Folders in the binder become chapters; the text documents inside them become scenes.",
            ProjectImporters.ScrivenerAsync),
        ["com.novalist.formats.import.markdown"] = (
            "Import a folder of Markdown or Ulysses sheets",
            "Subfolders become chapters; each file becomes a scene.",
            ProjectImporters.MarkdownFolderAsync),
        ["com.novalist.formats.import.delimited"] = (
            "Import a CSV or TSV file",
            "One row per scene. Columns are found by header name.",
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
            _host.ShowNotification("Import needs a path.");
            return;
        }

        using var progress = _host.ShowBusyProgress(new BusyProgressOptions
        {
            Title = "Importing",
            InitialStatus = Path.GetFileName(path),
            IsIndeterminate = true
        });

        ImportReport report;
        try
        {
            report = await run(_host, path);
        }
        catch (IOException ex)
        {
            _host.ShowNotification($"Import failed: {ex.Message}");
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _host.ShowNotification($"Import failed: {ex.Message}");
            return;
        }

        progress.Dispose();

        var summary = new StringBuilder(
            $"Imported {report.Chapters} chapter(s) and {report.Scenes} scene(s).");
        if (report.Skipped.Count > 0)
            summary.Append($" {report.Skipped.Count} thing(s) were skipped: {report.Skipped[0]}");
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
