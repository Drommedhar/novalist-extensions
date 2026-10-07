using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Formats.Importers;

/// <summary>What an import did, in terms the writer can check.</summary>
public sealed record ImportReport(int Chapters, int Scenes, IReadOnlyList<string> Skipped)
{
    public static ImportReport Nothing(string why) => new(0, 0, [why]);
}

/// <summary>
/// Reading other tools' projects into Novalist.
///
/// Every importer here adds chapters and scenes and writes prose into the scenes
/// it just created. None of them touch a scene that was already there: an import
/// that could overwrite the writer's own work would be a data-loss bug waiting
/// for its first user, and appending is both safer and what people actually want
/// when they are moving a book across.
/// </summary>
public static partial class ProjectImporters
{
    [GeneratedRegex(@"<Binder>(.*?)</Binder>", RegexOptions.Singleline)]
    private static partial Regex BinderRegex();

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex NewlineRegex();

    /// <summary>
    /// A Scrivener project folder or .scrivx file.
    ///
    /// Scrivener's binder is a tree of BinderItems; its prose lives in
    /// Files/Docs (Scrivener 2) or Files/Data/&lt;uuid&gt;/content.rtf
    /// (Scrivener 3), keyed by the item's ID or UUID. Folders become chapters
    /// and the text documents inside them become scenes, which is the mapping
    /// almost every Scrivener novel already uses.
    /// </summary>
    public static async Task<ImportReport> ScrivenerAsync(IHostServices host, string path)
    {
        var (scrivx, root) = ResolveScrivener(path);
        if (scrivx == null || root == null)
            return ImportReport.Nothing("No .scrivx file found at that path.");

        XDocument document;
        try
        {
            document = XDocument.Load(scrivx);
        }
        catch (System.Xml.XmlException)
        {
            return ImportReport.Nothing("The .scrivx file could not be read.");
        }

        var skipped = new List<string>();
        var chapters = 0;
        var scenes = 0;

        var manuscript = document.Descendants("BinderItem")
            .FirstOrDefault(i => (string?)i.Attribute("Type") == "DraftFolder");
        if (manuscript == null)
            return ImportReport.Nothing("The binder has no manuscript root (DraftFolder).");

        foreach (var item in manuscript.DescendantsAndSelf("BinderItem")
                     .Where(i => (string?)i.Attribute("Type") is "Folder" or "DraftFolder"))
        {
            var title = (string?)item.Element("Title") ?? "Untitled";
            var children = item.Element("Children")?.Elements("BinderItem")
                .Where(c => (string?)c.Attribute("Type") == "Text").ToList() ?? [];
            if (children.Count == 0) continue;

            var chapterGuid = await host.ProjectService.CreateChapterAsync(title);
            chapters++;

            foreach (var child in children)
            {
                var sceneTitle = (string?)child.Element("Title") ?? "Untitled";
                var sceneId = await host.ProjectService.CreateSceneAsync(chapterGuid, sceneTitle);
                scenes++;

                var rtf = FindScrivenerText(root, child);
                if (rtf == null)
                {
                    skipped.Add($"{title} / {sceneTitle}: no text file found.");
                    continue;
                }
                var content = await File.ReadAllTextAsync(rtf);
                var plainText = Path.GetExtension(rtf).Equals(".rtf", StringComparison.OrdinalIgnoreCase)
                    ? RtfReader.ToText(content) : content;
                await host.ProjectService.WriteSceneContentAsync(chapterGuid, sceneId, ToHtml(plainText));
            }
        }

        return new ImportReport(chapters, scenes, skipped);
    }

    /// <summary>
    /// A folder of Ulysses sheets, or any folder of Markdown or text files.
    ///
    /// Ulysses stores a sheet as Markdown inside a .ulysses package; a plain
    /// folder of .md files works the same way and is what most people can
    /// actually get their writing out as. Subfolders become chapters; files at
    /// the top level go into a chapter named after the folder.
    /// </summary>
    public static async Task<ImportReport> MarkdownFolderAsync(IHostServices host, string path)
    {
        if (!Directory.Exists(path))
            return ImportReport.Nothing("That folder does not exist.");

        var chapters = 0;
        var scenes = 0;
        var skipped = new List<string>();

        var groups = new List<(string Title, List<string> Files)>();
        var topLevel = TextFilesIn(path);
        if (topLevel.Count > 0)
            groups.Add((Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), topLevel));
        var traversal = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var directory in Directory.EnumerateDirectories(path, "*", traversal).OrderBy(d => d, StringComparer.Ordinal))
        {
            var files = TextFilesIn(directory);
            if (files.Count > 0) groups.Add((Path.GetRelativePath(path, directory).Replace('\\', '/'), files));
        }

        if (groups.Count == 0)
            return ImportReport.Nothing("No .md, .markdown or .txt files in that folder.");

        foreach (var (title, files) in groups)
        {
            var chapterGuid = await host.ProjectService.CreateChapterAsync(title);
            chapters++;
            foreach (var file in files)
            {
                var text = await File.ReadAllTextAsync(file);
                // A leading "# Heading" is the sheet's title in both Ulysses and
                // ordinary Markdown, so it names the scene instead of becoming
                // the first line of its prose.
                var (sceneTitle, body) = SplitHeading(text, Path.GetFileNameWithoutExtension(file));
                var sceneId = await host.ProjectService.CreateSceneAsync(chapterGuid, sceneTitle);
                scenes++;
                await host.ProjectService.WriteSceneContentAsync(chapterGuid, sceneId, ToHtml(body));
            }
        }

        return new ImportReport(chapters, scenes, skipped);
    }

    /// <summary>
    /// A delimited file, one row per scene.
    ///
    /// Columns are found by header name - chapter, scene/title, text/content -
    /// in any order, because a spreadsheet somebody assembled by hand will not
    /// have them in ours. A file with no header row cannot be read, and saying so
    /// is better than guessing which column is the prose.
    /// </summary>
    public static async Task<ImportReport> DelimitedAsync(IHostServices host, string path)
    {
        if (!File.Exists(path)) return ImportReport.Nothing("That file does not exist.");

        List<string> lines;
        try { lines = DelimitedRecords(await File.ReadAllTextAsync(path)); }
        catch (FormatException exception) { return ImportReport.Nothing(exception.Message); }
        if (lines.Count < 2) return ImportReport.Nothing("The file has no rows under its header.");

        var separator = lines[0].Contains('\t') ? '\t' : ',';
        List<List<string>> records;
        try { records = lines.Select(line => SplitRow(line, separator)).ToList(); }
        catch (FormatException exception) { return ImportReport.Nothing(exception.Message); }
        var header = records[0]
            .Select(h => h.Trim().ToLowerInvariant()).ToList();

        var chapterAt = IndexOfAny(header, "chapter", "act", "part");
        var titleAt = IndexOfAny(header, "scene", "title", "name");
        var textAt = IndexOfAny(header, "text", "content", "body", "prose");
        if (textAt < 0)
            return ImportReport.Nothing("No text, content, body or prose column in the header row.");

        var skipped = new List<string>();
        var chapters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scenes = 0;

        for (var row = 1; row < lines.Count; row++)
        {
            if (string.IsNullOrWhiteSpace(lines[row])) continue;
            var cells = records[row];
            if (cells.Count <= textAt)
            {
                skipped.Add($"Row {row + 1}: fewer columns than the header.");
                continue;
            }

            var chapterTitle = chapterAt >= 0 && cells.Count > chapterAt && cells[chapterAt].Length > 0
                ? cells[chapterAt]
                : "Imported";
            if (!chapters.TryGetValue(chapterTitle, out var chapterGuid))
            {
                chapterGuid = await host.ProjectService.CreateChapterAsync(chapterTitle);
                chapters[chapterTitle] = chapterGuid;
            }

            var sceneTitle = titleAt >= 0 && cells.Count > titleAt && cells[titleAt].Length > 0
                ? cells[titleAt]
                : $"Scene {scenes + 1}";
            var sceneId = await host.ProjectService.CreateSceneAsync(chapterGuid, sceneTitle);
            scenes++;
            await host.ProjectService.WriteSceneContentAsync(chapterGuid, sceneId, ToHtml(cells[textAt]));
        }

        return new ImportReport(chapters.Count, scenes, skipped);
    }

    // ── Shared reading ──

    private static List<string> DelimitedRecords(string text)
    {
        var rows = new List<string>();
        var row = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    row.Append("\"\"");
                    index++;
                    continue;
                }
                quoted = !quoted;
            }
            if (!quoted && character is ('\r' or '\n'))
            {
                rows.Add(row.ToString());
                row.Clear();
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else row.Append(character);
        }
        if (quoted) throw new FormatException("An imported text cell has an unclosed quote. No scenes were imported.");
        if (row.Length > 0) rows.Add(row.ToString());
        return rows;
    }

    private static (string? Scrivx, string? Root) ResolveScrivener(string path)
    {
        if (File.Exists(path) && path.EndsWith(".scrivx", StringComparison.OrdinalIgnoreCase))
            return (path, Path.GetDirectoryName(path));
        if (!Directory.Exists(path)) return (null, null);
        var found = Directory.GetFiles(path, "*.scrivx").FirstOrDefault();
        return (found, found == null ? null : path);
    }

    /// <summary>
    /// Scrivener has kept prose in two different places across its versions, so
    /// both are tried rather than one being assumed.
    /// </summary>
    private static string? FindScrivenerText(string root, XElement item)
    {
        var id = (string?)item.Attribute("UUID") ?? (string?)item.Attribute("ID");
        if (string.IsNullOrEmpty(id)) return null;

        var candidates = new[]
        {
            Path.Combine(root, "Files", "Data", id, "content.rtf"),
            Path.Combine(root, "Files", "Docs", id + ".rtf"),
            Path.Combine(root, "Files", "Data", id, "content.txt"),
            Path.Combine(root, "Files", "Docs", id + ".txt")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static List<string> TextFilesIn(string directory)
        => [.. Directory.GetFiles(directory)
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)];

    private static (string Title, string Body) SplitHeading(string text, string fallback)
    {
        var lines = NewlineRegex().Split(text);
        if (lines.Length > 0 && lines[0].TrimStart().StartsWith('#'))
            return (lines[0].TrimStart('#', ' ').Trim(), string.Join("\n", lines.Skip(1)));
        return (fallback, text);
    }

    private static int IndexOfAny(List<string> header, params string[] names)
        => header.FindIndex(h => names.Contains(h, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// One delimited row. Quoted cells may contain the separator and doubled
    /// quotes, which is what a spreadsheet writes when prose contains a comma -
    /// so a naive split would tear sentences apart at every comma.
    /// </summary>
    internal static List<string> SplitRow(string line, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var closedQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"') { cell.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; continue; }
                quoted = false;
                closedQuote = true;
                continue;
            }
            if (c == separator)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                closedQuote = false;
                continue;
            }
            if (closedQuote) throw new FormatException("Unexpected text after a closing quote. No scenes were imported.");
            if (c == '"')
            {
                if (cell.Length != 0) throw new FormatException("A quote must start a cell. No scenes were imported.");
                quoted = true;
                continue;
            }
            cell.Append(c);
        }
        if (quoted) throw new FormatException("An imported text cell has an unclosed quote. No scenes were imported.");
        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>
    /// Plain text to the paragraph markup a scene stores. Blank-line separated
    /// blocks become paragraphs; a single newline inside one is a soft wrap and
    /// is joined, because that is what it means in every editor prose comes from.
    /// </summary>
    internal static string ToHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalised = NewlineRegex().Replace(text, "\n");
        var blocks = normalised.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var output = new StringBuilder();
        foreach (var block in blocks)
        {
            var joined = string.Join(' ',
                block.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
            if (joined.Length == 0) continue;
            output.Append("<p>").Append(Escape(joined)).Append("</p>");
        }
        return output.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
