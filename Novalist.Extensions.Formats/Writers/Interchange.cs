using System.Globalization;
using System.Text;
using System.Text.Json;
using Novalist.Extensions.Formats.Services;

namespace Novalist.Extensions.Formats.Writers;

/// <summary>One scene, flattened to the columns a spreadsheet wants.</summary>
public sealed record SceneRow(
    int ChapterNumber,
    string ChapterTitle,
    string Act,
    int SceneNumber,
    string SceneTitle,
    int Words,
    string Synopsis);

/// <summary>
/// The manuscript as data rather than as prose: CSV, JSON and OPML.
///
/// Every format Novalist writes is a document - something to read or to print.
/// Nothing machine-readable ever left a project, so an outline could not be
/// pulled into a spreadsheet, handed to a script, or opened in an outliner, and
/// the answer to "how many scenes per act" was to count them by hand.
///
/// These are deliberately flat and boring. An interchange format that needs
/// documenting has failed at the one thing it is for.
/// </summary>
public static class Interchange
{
    /// <summary>Every scene in reading order, with its chapter around it.</summary>
    public static IReadOnlyList<SceneRow> Rows(Manuscript book)
    {
        var rows = new List<SceneRow>();
        for (var c = 0; c < book.Chapters.Count; c++)
        {
            var chapter = book.Chapters[c];
            for (var s = 0; s < chapter.Scenes.Count; s++)
            {
                var scene = chapter.Scenes[s];
                rows.Add(new SceneRow(
                    c + 1, chapter.Title, chapter.Act, s + 1, scene.Title,
                    Manuscript.Words(scene.Text), string.Empty));
            }
        }
        return rows;
    }

    /// <summary>
    /// Comma-separated, with a header row, RFC 4180 quoting and CRLF endings.
    ///
    /// Written to the letter because a spreadsheet is unforgiving: a scene
    /// title with a comma in it, which is most of them eventually, breaks every
    /// row after it if the quoting is approximate.
    /// </summary>
    public static string Csv(Manuscript book)
    {
        var output = new StringBuilder();
        output.Append("chapterNumber,chapterTitle,act,sceneNumber,sceneTitle,words\r\n");
        foreach (var row in Rows(book))
        {
            output
                .Append(row.ChapterNumber.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Field(row.ChapterTitle)).Append(',')
                .Append(Field(row.Act)).Append(',')
                .Append(row.SceneNumber.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Field(row.SceneTitle)).Append(',')
                .Append(row.Words.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");
        }
        return output.ToString();
    }

    /// <summary>
    /// The book as JSON: chapters holding scenes, which is the shape it has.
    ///
    /// Nested rather than flat because a script reading this wants the
    /// structure, and flattening it only to have every consumer rebuild it is
    /// work done twice.
    /// </summary>
    public static string Json(Manuscript book)
    {
        var details = book.Details;
        var payload = new
        {
            title = book.Title,
            author = details.Author,
            language = details.Language,
            wordCount = book.WordCount,
            chapters = book.Chapters.Select((chapter, index) => new
            {
                number = index + 1,
                title = chapter.Title,
                act = chapter.Act,
                scenes = chapter.Scenes.Select((scene, sceneIndex) => new
                {
                    number = sceneIndex + 1,
                    title = scene.Title,
                    words = Manuscript.Words(scene.Text)
                })
            })
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            // Titles are prose and full of apostrophes, dashes and accents.
            // Escaping them to \u sequences makes the file unreadable to the
            // person most likely to open it, which is the writer.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// OPML, which every outliner reads: chapters as outlines, scenes nested
    /// inside them.
    /// </summary>
    public static string Opml(Manuscript book)
    {
        var output = new StringBuilder();
        output.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        output.AppendLine("<opml version=\"2.0\">");
        output.AppendLine("  <head>");
        output.AppendLine($"    <title>{Escape(book.Title)}</title>");
        output.AppendLine("  </head>");
        output.AppendLine("  <body>");

        foreach (var chapter in book.Chapters)
        {
            output.AppendLine($"    <outline text=\"{Escape(chapter.Title)}\">");
            foreach (var scene in chapter.Scenes)
                output.AppendLine($"      <outline text=\"{Escape(scene.Title)}\"/>");
            output.AppendLine("    </outline>");
        }

        output.AppendLine("  </body>");
        output.AppendLine("</opml>");
        return output.ToString();
    }

    /// <summary>
    /// One CSV field, quoted when it has to be. Quoted whenever it contains a
    /// comma, a quote or a line break - the three things that break a row.
    /// </summary>
    internal static string Field(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length == 0) return string.Empty;
        if (!text.Any(c => c is ',' or '"' or '\n' or '\r')) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
