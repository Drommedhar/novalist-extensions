using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Novalist.Extensions.Toolkit.Services;

/// <summary>A page reduced to what is worth keeping.</summary>
public sealed record Captured(string Title, string Text, string Url);

/// <summary>
/// Pulls the readable article out of a web page.
///
/// A research note that stores only a URL is a research note that stops working
/// when the page goes away, which is most pages within a few years. Storing the
/// text is the difference between a reference and a dead link.
///
/// It is a heuristic and says so. The approach is the one every reader mode uses:
/// throw away the furniture - navigation, scripts, styles, headers, footers,
/// asides - then take the block with the most prose in it, on the theory that an
/// article's body is the densest text on the page. It is wrong sometimes. It is
/// wrong in a way the writer can see immediately, which is the important part.
/// </summary>
public static partial class ReaderMode
{
    [GeneratedRegex(
        @"<(script|style|noscript|nav|header|footer|aside|form|svg|iframe)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FurnitureRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<h1\b[^>]*>(.*?)</h1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H1Regex();

    [GeneratedRegex(@"<(article|main)\b[^>]*>(.*?)</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex(@"<(p|h[1-6]|li|blockquote)\b[^>]*>(.*?)</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    /// <summary>
    /// Reduces a page to a title and its prose.
    /// </summary>
    public static Captured Extract(string html, string url)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new Captured(FallbackTitle(url), string.Empty, url);

        var title = Title(html, url);
        var stripped = CommentRegex().Replace(FurnitureRegex().Replace(html, " "), " ");

        // A page that marks its own article is telling the truth about where the
        // prose is, and guessing when it has already been said is perverse.
        var article = ArticleRegex().Match(stripped);
        var body = article.Success ? article.Groups[2].Value : stripped;

        var blocks = BlockRegex().Matches(body)
            .Select(m => Clean(m.Groups[2].Value))
            .Where(t => t.Length > 0)
            .ToList();

        // Nothing block-level at all: a single-div page, or markup too broken to
        // read. Fall back to the whole text rather than returning nothing.
        if (blocks.Count == 0)
        {
            var whole = Clean(body);
            return new Captured(title, whole, url);
        }

        // Short blocks are almost always furniture that survived - a byline, a
        // cookie notice, a "share this". Keeping them costs more than losing the
        // occasional real one-line paragraph.
        var prose = blocks.Where(b => b.Length >= 40).ToList();
        if (prose.Count == 0) prose = blocks;

        return new Captured(title, string.Join("\n\n", prose), url);
    }

    /// <summary>
    /// The page's title. The h1 is preferred over the title element, because a
    /// title element usually carries the site name too and a research note
    /// called "Tide tables - Example Maritime Society - Home" is worse than one
    /// called "Tide tables".
    /// </summary>
    private static string Title(string html, string url)
    {
        var h1 = H1Regex().Match(html);
        if (h1.Success)
        {
            var text = Clean(h1.Groups[1].Value);
            if (text.Length > 0) return text;
        }

        var title = TitleRegex().Match(html);
        if (title.Success)
        {
            var text = Clean(title.Groups[1].Value);
            if (text.Length > 0) return text;
        }

        return FallbackTitle(url);
    }

    /// <summary>
    /// A title from the address, for a page that gives none. The last path
    /// segment is nearly always the slug, which is nearly always the headline.
    /// </summary>
    private static string FallbackTitle(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Captured page";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var slug = uri.Segments.LastOrDefault()?.Trim('/');
        if (string.IsNullOrWhiteSpace(slug)) return uri.Host;

        var words = slug.Replace('-', ' ').Replace('_', ' ');
        var withoutExtension = Path.GetFileNameWithoutExtension(words);
        return string.IsNullOrWhiteSpace(withoutExtension) ? uri.Host : withoutExtension;
    }

    private static string Clean(string fragment)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(fragment, "<[^>]+>", " "));
        return string.Join(' ', text.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Wraps captured text as the paragraph markup a research note stores.
    /// </summary>
    public static string ToHtml(Captured page)
    {
        var output = new StringBuilder();
        foreach (var paragraph in page.Text.Split(
                     "\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            output.Append("<p>").Append(Escape(paragraph)).Append("</p>");
        }
        // The address goes in the note, because a captured page without its
        // source is a quote nobody can check.
        if (!string.IsNullOrWhiteSpace(page.Url))
            output.Append("<p>").Append(Escape(page.Url)).Append("</p>");
        return output.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
