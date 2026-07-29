using System.Text;
using Novalist.Extensions.Formats.Services;

namespace Novalist.Extensions.Formats.Writers;

/// <summary>
/// The formats that are a single text file: plain text, HTML, Fountain.
///
/// Each is a pure function from a <see cref="Manuscript"/> to a string, which is
/// why they sit together - there is nothing to share but the shape.
/// </summary>
public static class TextWriters
{
    /// <summary>
    /// Plain text, laid out the way a manuscript is read rather than the way it
    /// is stored: chapter headings on their own line, a blank line between
    /// paragraphs, and a scene break where the file has a scene boundary.
    /// </summary>
    public static string PlainText(Manuscript book)
    {
        var output = new StringBuilder();
        output.AppendLine(book.Title).AppendLine();

        foreach (var chapter in book.Chapters)
        {
            output.AppendLine(chapter.Title).AppendLine();
            for (var i = 0; i < chapter.Scenes.Count; i++)
            {
                // A scene break is a real mark in a printed book, so it is a
                // real mark here rather than an accident of spacing.
                if (i > 0) output.AppendLine("* * *").AppendLine();
                output.AppendLine(chapter.Scenes[i].Text).AppendLine();
            }
        }
        return output.ToString();
    }

    /// <summary>
    /// A single self-contained HTML file: no stylesheet to lose, no script, and
    /// readable in any browser twenty years from now. The scene prose is
    /// re-emitted rather than passed through, so nothing from the editor's own
    /// markup leaks into a file the writer is going to send someone.
    /// </summary>
    public static string Html(Manuscript book)
    {
        var output = new StringBuilder();
        output.AppendLine("<!doctype html>");
        output.AppendLine("<html lang=\"en\">");
        output.AppendLine("<head>");
        output.AppendLine("<meta charset=\"utf-8\">");
        output.AppendLine($"<title>{Escape(book.Title)}</title>");
        output.AppendLine("<style>");
        output.AppendLine("body{max-width:34em;margin:4rem auto;padding:0 1rem;");
        output.AppendLine("font:1rem/1.7 Georgia,'Times New Roman',serif;color:#1a1a1a}");
        output.AppendLine("h1{font-size:1.9rem;font-weight:600;margin:0 0 3rem}");
        output.AppendLine("h2{font-size:1.3rem;font-weight:600;margin:3.5rem 0 1.5rem}");
        output.AppendLine("p{margin:0;text-indent:1.4em}");
        output.AppendLine("p.first{text-indent:0}");
        output.AppendLine("hr{border:0;margin:2rem 0;text-align:center}");
        output.AppendLine("hr:after{content:'* * *';letter-spacing:0.4em}");
        output.AppendLine("@media(prefers-color-scheme:dark){body{background:#16161a;color:#e6e6e6}}");
        output.AppendLine("</style>");
        output.AppendLine("</head>");
        output.AppendLine("<body>");
        output.AppendLine($"<h1>{Escape(book.Title)}</h1>");

        foreach (var chapter in book.Chapters)
        {
            output.AppendLine($"<h2>{Escape(chapter.Title)}</h2>");
            for (var i = 0; i < chapter.Scenes.Count; i++)
            {
                if (i > 0) output.AppendLine("<hr>");
                var paragraphs = Manuscript.Paragraphs(chapter.Scenes[i].Html);
                for (var p = 0; p < paragraphs.Count; p++)
                {
                    // The first paragraph after a heading or a break is not
                    // indented. Every book does this; a file that does not looks
                    // like a draft.
                    var cls = p == 0 ? " class=\"first\"" : string.Empty;
                    output.AppendLine($"<p{cls}>{Escape(paragraphs[p])}</p>");
                }
            }
        }

        output.AppendLine("</body>");
        output.AppendLine("</html>");
        return output.ToString();
    }

    /// <summary>
    /// Fountain, the plain-text screenplay format.
    ///
    /// Prose is not a screenplay and this does not pretend otherwise: chapters
    /// become scene headings and paragraphs become action. It exists because a
    /// writer adapting their own novel wants a starting point in the format
    /// their screenwriting tool reads, not because a novel is secretly a script.
    /// </summary>
    public static string Fountain(Manuscript book)
    {
        var output = new StringBuilder();
        output.AppendLine($"Title: {book.Title}").AppendLine();

        foreach (var chapter in book.Chapters)
        {
            foreach (var scene in chapter.Scenes)
            {
                // A forced scene heading, because a chapter title is not an
                // INT./EXT. line and guessing one would be inventing staging
                // the writer never wrote.
                output.AppendLine($".{chapter.Title.ToUpperInvariant()}").AppendLine();
                foreach (var paragraph in Manuscript.Paragraphs(scene.Html))
                    output.AppendLine(paragraph).AppendLine();
            }
        }
        return output.ToString();
    }

    /// <summary>
    /// Rich Text Format. Written by hand rather than through a library because
    /// RTF is a small enough language to emit correctly and a large enough
    /// dependency to avoid.
    /// </summary>
    public static string Rtf(Manuscript book)
    {
        var output = new StringBuilder();
        output.Append(@"{\rtf1\ansi\ansicpg1252\deff0");
        output.Append(@"{\fonttbl{\f0\froman\fcharset0 Times New Roman;}}");
        output.Append(@"\f0\fs24");

        output.Append(@"\qc\b\fs36 ").Append(RtfEscape(book.Title)).Append(@"\b0\fs24\par\par");
        output.Append(@"\ql");

        foreach (var chapter in book.Chapters)
        {
            output.Append(@"\page\qc\b\fs28 ").Append(RtfEscape(chapter.Title));
            output.Append(@"\b0\fs24\par\par\ql");

            for (var i = 0; i < chapter.Scenes.Count; i++)
            {
                if (i > 0) output.Append(@"\qc * * *\par\par\ql");
                var paragraphs = Manuscript.Paragraphs(chapter.Scenes[i].Html);
                for (var p = 0; p < paragraphs.Count; p++)
                {
                    output.Append(p == 0 ? @"\fi0 " : @"\fi360 ");
                    output.Append(RtfEscape(paragraphs[p])).Append(@"\par");
                }
            }
        }

        output.Append('}');
        return output.ToString();
    }

    /// <summary>
    /// FictionBook 2, which Russian and eastern European readers expect and
    /// which a number of e-readers handle better than EPUB.
    /// </summary>
    public static string Fb2(Manuscript book)
    {
        var output = new StringBuilder();
        output.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        output.AppendLine("<FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\">");
        output.AppendLine("  <description>");
        output.AppendLine("    <title-info>");
        output.AppendLine($"      <book-title>{Escape(book.Title)}</book-title>");
        output.AppendLine("      <lang>en</lang>");
        output.AppendLine("    </title-info>");
        output.AppendLine("  </description>");
        output.AppendLine("  <body>");

        foreach (var chapter in book.Chapters)
        {
            output.AppendLine("    <section>");
            output.AppendLine($"      <title><p>{Escape(chapter.Title)}</p></title>");
            for (var i = 0; i < chapter.Scenes.Count; i++)
            {
                if (i > 0) output.AppendLine("      <empty-line/>");
                foreach (var paragraph in Manuscript.Paragraphs(chapter.Scenes[i].Html))
                    output.AppendLine($"      <p>{Escape(paragraph)}</p>");
            }
            output.AppendLine("    </section>");
        }

        output.AppendLine("  </body>");
        output.AppendLine("</FictionBook>");
        return output.ToString();
    }

    internal static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>
    /// RTF is ASCII with escapes. A curly quote or an em dash - which prose is
    /// full of - has to go out as a numeric entity or it arrives as mojibake.
    /// </summary>
    internal static string RtfEscape(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': output.Append(@"\\"); break;
                case '{': output.Append(@"\{"); break;
                case '}': output.Append(@"\}"); break;
                default:
                    if (c < 128) output.Append(c);
                    else output.Append(@"\u").Append((int)c).Append('?');
                    break;
            }
        }
        return output.ToString();
    }
}
