using System.Text;
using System.Text.RegularExpressions;

namespace Novalist.Extensions.Publish.Site;

/// <summary>
/// Turns a project into a folder of HTML files.
///
/// Static files, no build step, no JavaScript framework, no server. That is the
/// whole design: a writer who generates this can open it locally, put it on any
/// hosting, mail it as a zip, or read it off a memory stick in ten years. Every
/// page carries its own styles, so there is no stylesheet to lose and nothing
/// breaks if a file is moved.
///
/// The generator writes nothing to the writer's project and reads no network. It
/// is a pure function from what the project contains to a list of files, which is
/// also why it is straightforward to test.
/// </summary>
public static partial class SiteGenerator
{
    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|([^\]]*))?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    /// <summary>
    /// Generates every file for the site.
    /// </summary>
    public static IReadOnlyList<SiteFile> Generate(SiteContent content, SiteOptions options)
    {
        var files = new List<SiteFile>();

        var entries = options.Scope == SiteScope.Manuscript ? [] : content.Entries;
        var chapters = options.Scope == SiteScope.World ? [] : content.Chapters;

        // Slugs are worked out for everything first, because a page cannot link to
        // another until it knows what that page will be called - and two entries
        // with the same name must not overwrite each other's file.
        var slugs = Slugs(entries);

        files.Add(new SiteFile("index.html", Index(entries, chapters, slugs, options)));

        foreach (var entry in entries)
            files.Add(new SiteFile($"{slugs[entry.Id]}.html", EntryPage(entry, slugs, options)));

        for (var i = 0; i < chapters.Count; i++)
        {
            files.Add(new SiteFile(
                $"chapter-{i + 1}.html",
                ChapterPage(chapters, i, slugs, options)));
        }

        if (options.DiscourageCrawlers)
            files.Add(new SiteFile("robots.txt", "User-agent: *\nDisallow: /\n"));

        return files;
    }

    /// <summary>
    /// A file name per entry. Two entries called the same thing get numbered
    /// rather than one silently replacing the other's page.
    /// </summary>
    internal static Dictionary<string, string> Slugs(IReadOnlyList<SiteEntry> entries)
    {
        var slugs = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var stem = Slug(entry.Name);
            if (stem.Length == 0) stem = Slug(entry.TypeKey);
            if (stem.Length == 0) stem = "entry";

            var candidate = stem;
            var suffix = 2;
            while (!used.Add(candidate)) candidate = $"{stem}-{suffix++}";
            slugs[entry.Id] = candidate;
        }

        return slugs;
    }

    internal static string Slug(string text)
        => SlugRegex().Replace((text ?? string.Empty).ToLowerInvariant(), "-").Trim('-');

    // ── Pages ──

    private static string Index(
        IReadOnlyList<SiteEntry> entries,
        IReadOnlyList<SiteChapter> chapters,
        Dictionary<string, string> slugs,
        SiteOptions options)
    {
        var body = new StringBuilder();
        body.Append($"<h1>{Esc(options.Title)}</h1>");
        if (!string.IsNullOrWhiteSpace(options.Subtitle))
            body.Append($"<p class=\"subtitle\">{Esc(options.Subtitle)}</p>");

        if (chapters.Count > 0)
        {
            body.Append($"<h2>{Esc(options.Text.Contents)}</h2><ol class=\"contents\">");
            for (var i = 0; i < chapters.Count; i++)
            {
                body.Append($"<li><a href=\"chapter-{i + 1}.html\">{Esc(chapters[i].Title)}</a>");
                if (!string.IsNullOrWhiteSpace(chapters[i].Act))
                    body.Append($" <span class=\"act\">{Esc(chapters[i].Act)}</span>");
                body.Append("</li>");
            }
            body.Append("</ol>");
        }

        // Grouped by kind, because a reader looking something up knows whether
        // they want a person or a place.
        foreach (var group in entries
                     .GroupBy(e => e.TypeKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => KindOrder(g.Key)))
        {
            body.Append(
                $"<h2>{Esc(KindName(group.Key, true, options.Text))}</h2><ul class=\"entries\">");
            foreach (var entry in group.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                body.Append($"<li><a href=\"{slugs[entry.Id]}.html\">{Esc(entry.Name)}</a>");
                if (entry.Aliases.Count > 0)
                    body.Append($" <span class=\"aliases\">{Esc(string.Join(", ", entry.Aliases))}</span>");
                body.Append("</li>");
            }
            body.Append("</ul>");
        }

        if (entries.Count == 0 && chapters.Count == 0)
            body.Append($"<p class=\"empty\">{Esc(options.Text.NothingSelected)}</p>");

        return Page(options.Title, body.ToString(), options, isIndex: true);
    }

    private static string EntryPage(
        SiteEntry entry, Dictionary<string, string> slugs, SiteOptions options)
    {
        var body = new StringBuilder();
        body.Append(
            $"<p class=\"kind\">{Esc(KindName(entry.TypeKey, false, options.Text))}</p>");
        body.Append($"<h1>{Esc(entry.Name)}</h1>");

        if (entry.Aliases.Count > 0)
            body.Append($"<p class=\"aliases\">{Esc(options.Text.AlsoKnownAs)} "
                + $"{Esc(string.Join(", ", entry.Aliases))}</p>");

        // Section content is Markdown - it is what the Codex editor stores and
        // what the wiki renders - so it is rendered rather than stripped.
        // Stripping the tags and stopping is what left **bold** sitting in a
        // published page as four literal asterisks.
        var link = Linker(slugs);
        foreach (var (title, content) in entry.Sections)
        {
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (!string.IsNullOrWhiteSpace(title)) body.Append($"<h2>{Esc(title)}</h2>");
            body.Append(Markup.ToHtml(content, link));
        }

        if (entry.Sections.All(s => string.IsNullOrWhiteSpace(s.Content)))
            body.Append($"<p class=\"empty\">{Esc(options.Text.NothingWritten)}</p>");

        return Page($"{entry.Name} - {options.Title}", body.ToString(), options);
    }

    private static string ChapterPage(
        IReadOnlyList<SiteChapter> chapters, int index,
        Dictionary<string, string> slugs, SiteOptions options)
    {
        var chapter = chapters[index];
        var body = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(chapter.Act))
            body.Append($"<p class=\"kind\">{Esc(chapter.Act)}</p>");
        body.Append($"<h1>{Esc(chapter.Title)}</h1>");

        for (var s = 0; s < chapter.Scenes.Count; s++)
        {
            if (s > 0) body.Append("<hr>");
            var paragraphs = chapter.Scenes[s].Paragraphs;
            for (var p = 0; p < paragraphs.Count; p++)
            {
                // First paragraph after a heading or a break is not indented,
                // which is what a printed book does.
                var cls = p == 0 ? " class=\"first\"" : string.Empty;
                body.Append($"<p{cls}>{Links(paragraphs[p], slugs)}</p>");
            }
        }

        if (chapter.Scenes.All(s => s.Paragraphs.Count == 0))
            body.Append($"<p class=\"empty\">{Esc(options.Text.NoProse)}</p>");

        // Reading a book means going to the next chapter, so that link is the one
        // thing every page here has to get right.
        body.Append("<nav class=\"pager\">");
        if (index > 0)
            body.Append(
                $"<a href=\"chapter-{index}.html\">{Esc(options.Text.Previous)}</a>");
        body.Append($"<a href=\"index.html\">{Esc(options.Text.Contents)}</a>");
        if (index + 1 < chapters.Count)
            body.Append(
                $"<a href=\"chapter-{index + 2}.html\">{Esc(options.Text.Next)}</a>");
        body.Append("</nav>");

        return Page($"{chapter.Title} - {options.Title}", body.ToString(), options);
    }

    /// <summary>
    /// Turns [[Name]] into a link when the target was published, and into plain
    /// text when it was not.
    ///
    /// A link to a page that is not in the site would be a broken link in
    /// somebody else's browser, which is worse than no link - and a world-only
    /// site legitimately has no page for a scene.
    /// </summary>
    internal static string Links(string text, Dictionary<string, string> slugs)
    {
        var link = Linker(slugs);
        return WikiLinkRegex().Replace(Esc(text), match =>
        {
            // Escaped before matching, so the link syntax is read out of already
            // safe text and nothing inside it can inject markup.
            var target = match.Groups[1].Value.Trim();
            var label = match.Groups[2].Success && match.Groups[2].Value.Trim().Length > 0
                ? match.Groups[2].Value.Trim()
                : target;

            var href = link(target);
            return href == null ? label : $"<a href=\"{href}\">{label}</a>";
        });
    }

    /// <summary>
    /// Where a [[Name]] points, or null when it points at nothing published.
    ///
    /// A world-only site legitimately has no page for a scene, and a link into
    /// a folder that does not contain the target is a broken link in somebody
    /// else's browser - which is worse than no link at all.
    /// </summary>
    internal static Func<string, string?> Linker(Dictionary<string, string> slugs)
    {
        var published = new HashSet<string>(slugs.Values, StringComparer.OrdinalIgnoreCase);
        return target =>
        {
            var slug = Slug(target.Trim());
            return published.Contains(slug) ? $"{slug}.html" : null;
        };
    }

    /// <summary>
    /// The page shell. Styles are inline on every page on purpose: there is no
    /// stylesheet to lose, and a page mailed on its own still looks like itself.
    /// </summary>
    private static string Page(string title, string body, SiteOptions options, bool isIndex = false)
    {
        var output = new StringBuilder();
        output.AppendLine("<!doctype html>");
        output.AppendLine($"<html lang=\"{Esc(options.Language)}\">");
        output.AppendLine("<head>");
        output.AppendLine("<meta charset=\"utf-8\">");
        output.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        if (options.DiscourageCrawlers)
            output.AppendLine("<meta name=\"robots\" content=\"noindex, nofollow\">");
        output.AppendLine($"<title>{Esc(title)}</title>");
        output.AppendLine($"<style>{Css}</style>");
        output.AppendLine("</head>");
        output.AppendLine("<body>");
        if (!isIndex)
            output.AppendLine($"<p class=\"home\"><a href=\"index.html\">{Esc(options.Title)}</a></p>");
        output.AppendLine("<main>");
        output.AppendLine(body);
        output.AppendLine("</main>");
        output.AppendLine("</body>");
        output.AppendLine("</html>");
        return output.ToString();
    }

    private const string Css =
        "*{box-sizing:border-box}" +
        "body{max-width:38em;margin:0 auto;padding:3rem 1.25rem 6rem;" +
        "font:1.05rem/1.7 Georgia,'Times New Roman',serif;color:#1c1c1e;background:#fdfdfb}" +
        "h1{font-size:1.9rem;font-weight:600;margin:0 0 1.5rem;line-height:1.25}" +
        "h2{font-size:1.15rem;font-weight:600;margin:2.5rem 0 .75rem}" +
        "p{margin:0 0 .35rem;text-indent:1.4em}" +
        "p.first,p.subtitle,p.kind,p.aliases,p.home,p.empty{text-indent:0}" +
        "p.subtitle{font-size:1.1rem;opacity:.7;margin-bottom:2rem}" +
        "p.kind{font-size:.8rem;letter-spacing:.08em;text-transform:uppercase;opacity:.55;" +
        "margin-bottom:.5rem}" +
        "p.aliases,span.aliases,span.act{font-size:.85rem;opacity:.6;font-style:italic}" +
        "p.home{font-size:.85rem;margin-bottom:2.5rem}" +
        "p.empty{opacity:.6;font-style:italic}" +
        "a{color:#2b5d8a;text-decoration:none;border-bottom:1px solid rgba(43,93,138,.3)}" +
        "a:hover{border-bottom-color:#2b5d8a}" +
        "ul,ol{padding-left:1.4em}" +
        "ul.entries,ol.contents{line-height:2}" +
        "hr{border:0;margin:1.75rem 0;text-align:center}" +
        "hr:after{content:'* * *';letter-spacing:.5em;opacity:.5}" +
        "nav.pager{display:flex;gap:1.5rem;margin-top:3.5rem;padding-top:1.5rem;" +
        "border-top:1px solid rgba(0,0,0,.1);font-size:.9rem}" +
        "@media(prefers-color-scheme:dark){" +
        "body{background:#16161a;color:#e4e4e6}" +
        "a{color:#8fb8dd;border-bottom-color:rgba(143,184,221,.3)}" +
        "a:hover{border-bottom-color:#8fb8dd}" +
        "nav.pager{border-top-color:rgba(255,255,255,.12)}}";

    private static int KindOrder(string typeKey) => typeKey.ToLowerInvariant() switch
    {
        "character" => 0,
        "location" => 1,
        "item" => 2,
        "lore" => 3,
        _ => 4
    };

    private static string KindName(string typeKey, bool plural, SiteText text)
        => typeKey.ToLowerInvariant() switch
        {
            "character" => plural ? text.People : text.Character,
            "location" => plural ? text.Places : text.Location,
            "item" => plural ? text.Things : text.Item,
            "lore" => text.Lore,
            _ => plural ? text.Other : text.Entry
        };

    private static string Esc(string text) => (text ?? string.Empty)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
