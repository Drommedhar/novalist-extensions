using System.Text;

namespace Novalist.Extensions.Formats.Importers;

/// <summary>
/// Pulls the readable text out of an RTF file.
///
/// Scrivener stores prose as RTF, so importing from it means reading RTF, and a
/// full RTF parser is a large thing to take on for the narrow job of recovering
/// paragraphs. This handles what a prose document actually contains: control
/// words, groups, escaped characters, unicode escapes and paragraph breaks. It
/// deliberately ignores formatting - the writer's italics are worth less than
/// their sentences arriving intact, and guessing wrong about a control word is
/// how a naive reader turns a chapter into gibberish.
/// </summary>
public static class RtfReader
{
    /// <summary>Groups whose contents are metadata rather than prose.</summary>
    private static readonly string[] SkippedDestinations =
        ["fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata",
         "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "datastore"];

    public static string ToText(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return string.Empty;

        var output = new StringBuilder();
        var skipDepth = -1;
        var depth = 0;
        var i = 0;

        while (i < rtf.Length)
        {
            var c = rtf[i];

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (skipDepth >= 0 && depth <= skipDepth) skipDepth = -1;
                depth--;
                i++;
                continue;
            }

            if (c == '\\')
            {
                i = ReadControl(rtf, i, output, ref skipDepth, depth);
                continue;
            }

            if (skipDepth < 0 && c != '\r' && c != '\n') output.Append(c);
            i++;
        }

        return Tidy(output.ToString());
    }

    /// <summary>
    /// Reads one control word or escape starting at the backslash, appends
    /// whatever text it stands for, and returns the index just past it.
    /// </summary>
    private static int ReadControl(
        string rtf, int at, StringBuilder output, ref int skipDepth, int depth)
    {
        var i = at + 1;
        if (i >= rtf.Length) return i;

        var c = rtf[i];

        // A literal character: \\ \{ \} and friends.
        if (!char.IsLetter(c))
        {
            switch (c)
            {
                case '\\' or '{' or '}':
                    if (skipDepth < 0) output.Append(c);
                    return i + 1;
                case '\'':
                {
                    // \'xx is a byte in the document's codepage. Read it as
                    // Latin-1, which is right for the western European text this
                    // is overwhelmingly used for and wrong quietly rather than
                    // loudly for anything else.
                    if (i + 2 < rtf.Length
                        && byte.TryParse(rtf.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                            null, out var b))
                    {
                        if (skipDepth < 0) output.Append((char)b);
                        return i + 3;
                    }
                    return i + 1;
                }
                case '*':
                    // \* marks a destination a reader is allowed not to understand,
                    // which is exactly the ones whose contents are not prose.
                    skipDepth = skipDepth < 0 ? depth : skipDepth;
                    return i + 1;
                case '~':
                    if (skipDepth < 0) output.Append(' ');
                    return i + 1;
                case '-':
                    return i + 1;
                case '\r' or '\n':
                    if (skipDepth < 0) output.Append('\n');
                    return i + 1;
                default:
                    return i + 1;
            }
        }

        // A control word, optionally with a numeric parameter.
        var start = i;
        while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
        var word = rtf[start..i];

        var negative = i < rtf.Length && rtf[i] == '-';
        if (negative) i++;
        var digits = i;
        while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
        var hasParameter = i > digits;
        var parameter = hasParameter ? int.Parse(rtf[digits..i]) : 0;
        if (negative) parameter = -parameter;

        // A single space after a control word is its terminator, not text.
        if (i < rtf.Length && rtf[i] == ' ') i++;

        if (SkippedDestinations.Contains(word, StringComparer.Ordinal))
        {
            skipDepth = skipDepth < 0 ? depth : skipDepth;
            return i;
        }

        if (skipDepth >= 0) return i;

        switch (word)
        {
            case "par" or "line" or "sect":
                output.Append('\n');
                break;
            case "tab":
                output.Append('\t');
                break;
            case "emdash":
                output.Append('—');
                break;
            case "endash":
                output.Append('–');
                break;
            case "lquote":
                output.Append('‘');
                break;
            case "rquote":
                output.Append('’');
                break;
            case "ldblquote":
                output.Append('“');
                break;
            case "rdblquote":
                output.Append('”');
                break;
            case "u" when hasParameter:
            {
                // \uN with a fallback character after it that has to be dropped,
                // or every non-ASCII character arrives doubled.
                output.Append((char)(parameter < 0 ? parameter + 65536 : parameter));
                if (i < rtf.Length && rtf[i] == '?') i++;
                break;
            }
        }

        return i;
    }

    /// <summary>
    /// Paragraph breaks become blank-line separated blocks, which is what the
    /// importer's markup step expects, and runs of whitespace inside a line
    /// collapse - RTF is full of them.
    /// </summary>
    private static string Tidy(string text)
    {
        var lines = text.Split('\n').Select(l => string.Join(' ',
            l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        var output = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if (output.Length > 0) output.Append("\n\n");
            output.Append(line);
        }
        return output.ToString();
    }
}
