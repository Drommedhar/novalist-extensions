using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Novalist.Extensions.Publish.Site;

/// <summary>
/// Renders the small amount of Markdown that appears in a Novalist project.
///
/// Entity sections, scene notes and synopses are written in a Markdown editor,
/// so their stored form carries `**bold**`, `*italic*`, `# headings`, lists and
/// `[[links]]`. Anything that strips tags and stops - which is what every writer
/// here did - leaves that syntax sitting in the output as literal asterisks.
///
/// This is a subset on purpose. It handles what the editor produces and nothing
/// else: no tables, no footnotes, no reference links, no raw HTML passthrough.
/// A full CommonMark implementation is a dependency, and the failure mode of
/// this one is a stray character rather than a security hole, because everything
/// is escaped before any markup is put back.
/// </summary>
public static partial class Markup
{
    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<![\*\w])\*(?!\s)([^\*]+?)(?<!\s)\*(?!\*)", RegexOptions.Singleline)]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"(?<!_)_(?!_)([^_]+?)_(?!_)", RegexOptions.Singleline)]
    private static partial Regex UnderscoreItalicRegex();

    [GeneratedRegex(@"~~(.+?)~~", RegexOptions.Singleline)]
    private static partial Regex StrikeRegex();

    [GeneratedRegex(@"`([^`]+?)`")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|([^\]]*))?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^\s*>\s?(.*)$")]
    private static partial Regex QuoteRegex();

    /// <summary>
    /// Markdown to HTML.
    ///
    /// Markup that came in with the content is reduced to text before the Markdown
    /// pass, so nothing a writer pasted into a section can reach the page as live
    /// HTML. That is deliberate rather than cautious: the Codex editor stores
    /// Markdown, the wiki renders it the same way, and a published folder is a
    /// file somebody else opens.
    ///
    /// <paramref name="link"/> is asked where a <c>[[wiki link]]</c> points. It
    /// returns an href, or null for a target that has no page - which becomes
    /// plain text, because a link to a page that is not in the folder is a broken
    /// link in somebody else's browser.
    /// </summary>
    public static string ToHtml(string? text, Func<string, string?>? link = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var output = new StringBuilder();
        var lines = Flatten(text!).Split('\n');
        var paragraph = new List<string>();
        var listItems = new List<string>();
        var listOrdered = false;
        var quote = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            output.Append("<p>").Append(Inline(string.Join(' ', paragraph), link)).Append("</p>");
            paragraph.Clear();
        }

        void FlushList()
        {
            if (listItems.Count == 0) return;
            var tag = listOrdered ? "ol" : "ul";
            output.Append('<').Append(tag).Append('>');
            foreach (var item in listItems)
                output.Append("<li>").Append(Inline(item, link)).Append("</li>");
            output.Append("</").Append(tag).Append('>');
            listItems.Clear();
        }

        void FlushQuote()
        {
            if (quote.Count == 0) return;
            output.Append("<blockquote><p>")
                .Append(Inline(string.Join(' ', quote), link))
                .Append("</p></blockquote>");
            quote.Clear();
        }

        void FlushAll()
        {
            FlushParagraph();
            FlushList();
            FlushQuote();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.Trim().Length == 0)
            {
                FlushAll();
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushAll();
                // Shifted down two: a section's own heading is already an h2 in
                // the page around it, so its "# " must not compete with the title.
                var level = Math.Min(6, heading.Groups[1].Value.Length + 2);
                output.Append("<h").Append(level).Append('>')
                    .Append(Inline(heading.Groups[2].Value, link))
                    .Append("</h").Append(level).Append('>');
                continue;
            }

            if (line.Trim() is "---" or "***" or "___")
            {
                FlushAll();
                output.Append("<hr>");
                continue;
            }

            var quoted = QuoteRegex().Match(line);
            if (quoted.Success)
            {
                FlushParagraph();
                FlushList();
                quote.Add(quoted.Groups[1].Value);
                continue;
            }

            var item = ListItemRegex().Match(line);
            if (item.Success)
            {
                FlushParagraph();
                FlushQuote();
                var ordered = char.IsDigit(item.Groups[1].Value[0]);
                // A list that changes kind mid-run is two lists.
                if (listItems.Count > 0 && ordered != listOrdered) FlushList();
                listOrdered = ordered;
                listItems.Add(item.Groups[2].Value);
                continue;
            }

            FlushList();
            FlushQuote();
            paragraph.Add(line.Trim());
        }

        FlushAll();
        return output.ToString();
    }

    /// <summary>
    /// Markdown to readable plain text: the same reading, with the syntax taken
    /// off rather than turned into tags.
    /// </summary>
    public static string ToText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var output = new List<string>();
        foreach (var raw in Flatten(text!).Split('\n'))
        {
            var line = raw.TrimEnd();
            var heading = HeadingRegex().Match(line);
            if (heading.Success) { output.Add(Strip(heading.Groups[2].Value)); continue; }

            var quoted = QuoteRegex().Match(line);
            if (quoted.Success) { output.Add(Strip(quoted.Groups[1].Value)); continue; }

            var item = ListItemRegex().Match(line);
            if (item.Success) { output.Add("- " + Strip(item.Groups[2].Value)); continue; }

            output.Add(Strip(line));
        }
        return string.Join("\n", output).Trim();
    }

    /// <summary>
    /// Inline markup inside one line. Escaped first, so a stray angle bracket in
    /// the writer's prose can never become a tag and nothing this puts back can
    /// be closed by the content.
    /// </summary>
    internal static string Inline(string text, Func<string, string?>? link = null)
    {
        // NUL is not prose, and it is the one character the placeholders below
        // rely on the text not containing.
        var escaped = Escape(text).Replace("\0", string.Empty);

        // Code is lifted out before anything else runs, because what is inside a
        // span of code is not markup - `**not bold**` is an example of asterisks,
        // and turning it bold is the tool contradicting the sentence.
        var spans = new List<string>();
        escaped = CodeRegex().Replace(escaped, m =>
        {
            spans.Add(m.Groups[1].Value);
            return $"\0{spans.Count - 1}\0";
        });

        escaped = BoldRegex().Replace(escaped, "<strong>$1</strong>");
        escaped = ItalicRegex().Replace(escaped, "<em>$1</em>");
        escaped = UnderscoreItalicRegex().Replace(escaped, "<em>$1</em>");
        escaped = StrikeRegex().Replace(escaped, "<del>$1</del>");

        // A wiki link becomes a real link when the target has a page, and its own
        // label when it does not. Nobody is served by a link to nowhere.
        escaped = WikiLinkRegex().Replace(escaped, m =>
        {
            var target = m.Groups[1].Value.Trim();
            var label = m.Groups[2].Success && m.Groups[2].Value.Trim().Length > 0
                ? m.Groups[2].Value.Trim()
                : target;
            var href = link?.Invoke(target);
            return href == null ? label : $"<a href=\"{href}\">{label}</a>";
        });

        escaped = LinkRegex().Replace(escaped, m =>
            IsSafeUrl(m.Groups[2].Value)
                ? $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>"
                : m.Groups[1].Value);

        // The code spans go back last, exactly as they were written.
        return PlaceholderRegex().Replace(escaped, m => $"<code>{spans[int.Parse(m.Groups[1].Value)]}</code>");
    }

    [GeneratedRegex("\0([0-9]+)\0")]
    private static partial Regex PlaceholderRegex();

    /// <summary>Inline syntax removed rather than rendered.</summary>
    internal static string Strip(string text)
    {
        var plain = CodeRegex().Replace(text, "$1");
        plain = BoldRegex().Replace(plain, "$1");
        plain = ItalicRegex().Replace(plain, "$1");
        plain = UnderscoreItalicRegex().Replace(plain, "$1");
        plain = StrikeRegex().Replace(plain, "$1");
        plain = WikiLinkRegex().Replace(plain, m =>
            m.Groups[2].Success && m.Groups[2].Value.Trim().Length > 0
                ? m.Groups[2].Value
                : m.Groups[1].Value);
        plain = LinkRegex().Replace(plain, "$1");
        return plain.Trim();
    }

    /// <summary>
    /// Only http, https and mailto reach the output as links. A javascript: URL
    /// in a section somebody pasted would otherwise become a live one in a file
    /// they send to a reader.
    /// </summary>
    private static bool IsSafeUrl(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Any markup in the stored content reduced to line breaks and text, ready
    /// for the Markdown pass.
    ///
    /// A section that holds editor HTML rather than Markdown - an imported entry,
    /// something pasted in - reads correctly instead of showing its tags, and
    /// nothing in it survives as live markup.
    /// </summary>
    private static string Flatten(string text)
    {
        var normalised = text.Replace("\r\n", "\n");
        // A block that ends is a paragraph break; a <br> is a single line break.
        // Collapsing both would lose the difference between a new paragraph and a
        // wrapped line, which in a poem is the whole point.
        var withBreaks = BlockEndRegex().Replace(normalised, "\n\n");
        withBreaks = LineBreakRegex().Replace(withBreaks, "\n");
        return WebUtility.HtmlDecode(TagRegex().Replace(withBreaks, string.Empty));
    }

    [GeneratedRegex(@"</(p|h[1-6]|li|blockquote|div|section)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    internal static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
