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
    private readonly record struct Format(int CodePage, int FallbackLength);

    private sealed class ReaderState
    {
        public StringBuilder Output { get; } = new();
        public int SkipDepth { get; set; } = -1;
        public int Depth { get; set; }
        public Format Format { get; set; } = new(1252, 1);
        public int Fallback { get; set; }
    }

    static RtfReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    /// <summary>Groups whose contents are metadata rather than prose.</summary>
    private static readonly string[] SkippedDestinations =
        ["fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata",
         "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "datastore"];

    public static string ToText(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return string.Empty;

        var state = new ReaderState();
        var i = 0;
        var formats = new Stack<Format>();

        while (i < rtf.Length)
        {
            var c = rtf[i];

            if (c == '{')
            {
                formats.Push(state.Format);
                state.Depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (formats.Count > 0) state.Format = formats.Pop();
                if (state.SkipDepth >= 0 && state.Depth <= state.SkipDepth) state.SkipDepth = -1;
                state.Depth--;
                i++;
                continue;
            }

            if (c == '\\')
            {
                i = ReadControl(rtf, i, state);
                continue;
            }

            if (c != '\r' && c != '\n')
            {
                if (state.Fallback > 0) state.Fallback--;
                else if (state.SkipDepth < 0) state.Output.Append(c);
            }
            i++;
        }

        return Tidy(state.Output.ToString());
    }

    /// <summary>
    /// Reads one control word or escape starting at the backslash, appends
    /// whatever text it stands for, and returns the index just past it.
    /// </summary>
    private static int ReadControl(
        string rtf, int at, ReaderState state)
    {
        var i = at + 1;
        if (i >= rtf.Length) return i;

        var c = rtf[i];

        if (!char.IsLetter(c)) return ReadSymbol(rtf, at, state);

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
            state.SkipDepth = state.SkipDepth < 0 ? state.Depth : state.SkipDepth;
            return i;
        }

        if (state.SkipDepth >= 0) return i;

        switch (word)
        {
            case "ansicpg" when hasParameter:
                _ = Encoding.GetEncoding(parameter);
                state.Format = state.Format with { CodePage = parameter };
                break;
            case "uc" when hasParameter && parameter >= 0:
                state.Format = state.Format with { FallbackLength = parameter };
                break;
            case "par" or "line" or "sect":
                state.Output.Append('\n');
                break;
            case "tab":
                state.Output.Append('\t');
                break;
            case "emdash":
                state.Output.Append('—');
                break;
            case "endash":
                state.Output.Append('–');
                break;
            case "lquote":
                state.Output.Append('‘');
                break;
            case "rquote":
                state.Output.Append('’');
                break;
            case "ldblquote":
                state.Output.Append('“');
                break;
            case "rdblquote":
                state.Output.Append('”');
                break;
            case "u" when hasParameter:
            {
                // \uN includes an ANSI fallback that must not be appended twice.
                state.Output.Append((char)(parameter < 0 ? parameter + 65536 : parameter));
                state.Fallback = state.Format.FallbackLength;
                break;
            }
        }

        return i;
    }

    private static int ReadSymbol(string rtf, int at, ReaderState state)
    {
        var i = at + 1;
        var c = rtf[i];
        if (state.Fallback > 0 && c != '*')
        {
            state.Fallback--;
            return c == '\'' ? Math.Min(rtf.Length, i + 3) : i + 1;
        }
        switch (c)
        {
            case '\\' or '{' or '}':
                if (state.SkipDepth < 0) state.Output.Append(c);
                return i + 1;
            case '\'':
            {
                var bytes = new List<byte>();
                var next = at;
                while (next + 3 < rtf.Length && rtf[next] == '\\' && rtf[next + 1] == '\''
                    && byte.TryParse(rtf.AsSpan(next + 2, 2), System.Globalization.NumberStyles.HexNumber,
                        null, out var value))
                {
                    bytes.Add(value);
                    next += 4;
                }
                if (bytes.Count == 0) return i + 1;
                if (state.SkipDepth < 0) state.Output.Append(Encoding.GetEncoding(state.Format.CodePage).GetString(bytes.ToArray()));
                return next;
            }
            case '*':
                // \* marks a destination a reader is allowed not to understand,
                // which is exactly the ones whose contents are not prose.
                state.SkipDepth = state.SkipDepth < 0 ? state.Depth : state.SkipDepth;
                return i + 1;
            case '~':
                if (state.SkipDepth < 0) state.Output.Append(' ');
                return i + 1;
            case '-':
                return i + 1;
            case '\r' or '\n':
                if (state.SkipDepth < 0) state.Output.Append('\n');
                return i + 1;
            default:
                return i + 1;
        }
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
