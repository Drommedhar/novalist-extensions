using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.Formats.Services;

/// <summary>One scene, as every writer here needs it.</summary>
public sealed record MsScene(string Title, string Html, string Text);

/// <summary>One chapter and its scenes.</summary>
public sealed record MsChapter(string Title, string Act, IReadOnlyList<MsScene> Scenes);

/// <summary>
/// The book, read once and handed to whichever writer was asked for.
///
/// Every format here needs the same thing - chapters in order, scenes in order,
/// prose as markup and as text - so it is assembled once rather than six times,
/// and a writer becomes a pure function from this to a string.
/// </summary>
public sealed record Manuscript(string Title, IReadOnlyList<MsChapter> Chapters)
{
    public int WordCount => Chapters
        .SelectMany(c => c.Scenes)
        .Sum(s => Words(s.Text));

    public static int Words(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Reads the open book. Scenes with nothing in them are kept: a writer who
    /// left a placeholder scene meant to, and dropping it would renumber
    /// everything after it in the output.
    /// </summary>
    public static async Task<Manuscript> ReadAsync(IHostServices host, string title)
    {
        var chapters = new List<MsChapter>();
        foreach (var chapter in host.ProjectService.GetChaptersOrdered())
        {
            var scenes = new List<MsScene>();
            foreach (var scene in host.ProjectService.GetScenesForChapter(chapter.Guid))
            {
                var html = await host.ProjectService.ReadSceneContentAsync(chapter.Guid, scene.Id);
                scenes.Add(new MsScene(scene.Title, html, ToText(html)));
            }

            var detail = host.StoryService.GetSceneDetail(
                chapter.Guid,
                host.ProjectService.GetScenesForChapter(chapter.Guid).FirstOrDefault()?.Id ?? string.Empty);
            chapters.Add(new MsChapter(chapter.Title, detail?.Act ?? string.Empty, scenes));
        }
        return new Manuscript(title, chapters);
    }

    /// <summary>
    /// Markup to readable text. Paragraph and break boundaries become newlines
    /// first, or every paragraph would run into the next one as a single line.
    /// </summary>
    public static string ToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        // A paragraph boundary is a blank line and a <br> is a single newline.
        // Collapsing both to one newline would lose the distinction between a
        // new paragraph and a wrapped line, which is the difference between
        // prose and a poem.
        var withBreaks = Regex.Replace(
            html, @"</p\s*>|</h[1-6]\s*>", "\n\n", RegexOptions.IgnoreCase);
        withBreaks = Regex.Replace(withBreaks, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        var stripped = Regex.Replace(withBreaks, "<[^>]+>", string.Empty);
        var decoded = WebUtility.HtmlDecode(stripped);

        // Collapse the runs of blank lines that stripping tends to leave, but
        // keep one - a deliberate blank line between passages is punctuation.
        var lines = decoded.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim());
        var builder = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank = true;
                continue;
            }
            if (builder.Length > 0) builder.Append(blank ? "\n\n" : "\n");
            builder.Append(line);
            blank = false;
        }
        return builder.ToString();
    }

    /// <summary>The paragraphs of a scene, in order, as plain text.</summary>
    public static IReadOnlyList<string> Paragraphs(string html)
        => [.. ToText(html).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(block => block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)];
}
