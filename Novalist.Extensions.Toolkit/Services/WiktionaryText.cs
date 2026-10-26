using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Novalist.Extensions.Toolkit.Services;

internal static partial class WiktionaryText
{
    [GeneratedRegex(@"^== ([^=]+) ==$", RegexOptions.None, 1000)]
    private static partial Regex LanguageHeading();

    [GeneratedRegex(@"^\[[^\]]+\]\s*", RegexOptions.None, 1000)]
    private static partial Regex SenseNumber();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, 1000)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\([^()]*\)", RegexOptions.None, 1000)]
    private static partial Regex Qualifier();

    public static IReadOnlyList<string> EnglishDefinitions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid dictionary response.");
        if (!root.TryGetProperty("en", out var entries)) return [];
        if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid dictionary entries.");
        var definitions = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            var partOfSpeech = Property(entry, "partOfSpeech", JsonValueKind.String).GetString();
            foreach (var sense in Property(entry, "definitions", JsonValueKind.Array).EnumerateArray())
            {
                var html = Property(sense, "definition", JsonValueKind.String).GetString() ?? string.Empty;
                if (html.Contains("mw-cite-backlink", StringComparison.Ordinal)) continue;
                var text = WebUtility.HtmlDecode(HtmlTag().Replace(html, string.Empty)).Trim();
                if (text.Length == 0) continue;
                definitions.Add($"({partOfSpeech}) {text}");
                if (definitions.Count == 6) return definitions;
            }
        }
        return definitions;
    }

    public static IReadOnlyList<string> GermanEntries(string extract, bool synonyms)
    {
        var entries = new List<string>();
        var german = false;
        var collecting = false;
        foreach (var line in Lines(extract))
        {
            var heading = LanguageHeading().Match(line);
            if (heading.Success)
            {
                german = heading.Groups[1].Value.EndsWith("(Deutsch)", StringComparison.Ordinal);
                collecting = false;
            }
            else if (line.StartsWith("==", StringComparison.Ordinal) || line.EndsWith(':'))
                collecting = german && line == (synonyms ? "Synonyme:" : "Bedeutungen:");
            else if (collecting && SenseNumber().IsMatch(line))
                entries.Add(SenseNumber().Replace(line, string.Empty));
        }
        return entries;
    }

    public static IReadOnlyList<string> EnglishSynonyms(string extract)
    {
        var entries = new List<string>();
        var english = false;
        var collecting = false;
        foreach (var line in Lines(extract))
        {
            var heading = LanguageHeading().Match(line);
            if (heading.Success)
            {
                english = heading.Groups[1].Value == "English";
                collecting = false;
            }
            else if (line.StartsWith("==", StringComparison.Ordinal))
                collecting = english && line.Trim('=').Trim() == "Synonyms";
            else if (english && (line.StartsWith("Synonyms:", StringComparison.Ordinal)
                || line.StartsWith("Synonym:", StringComparison.Ordinal)))
                entries.Add(line[(line.IndexOf(':') + 1)..]);
            else if (collecting)
                entries.Add(line);
        }
        return entries;
    }

    public static IReadOnlyList<string> SynonymTerms(IEnumerable<string> entries, string word)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { word };
        foreach (var entry in entries)
        {
            if (entry.TrimStart().StartsWith("see ", StringComparison.OrdinalIgnoreCase)
                || entry.TrimStart().StartsWith("siehe ", StringComparison.OrdinalIgnoreCase)) continue;
            var text = Qualifier().Replace(entry.Split(';')[0], string.Empty);
            var colon = text.IndexOf(':');
            if (colon >= 0) text = text[(colon + 1)..];
            foreach (var candidate in text.Split(','))
            {
                var term = candidate.Trim();
                if (term.Length == 0 || term.StartsWith("see ", StringComparison.OrdinalIgnoreCase)
                    || term.StartsWith("siehe ", StringComparison.OrdinalIgnoreCase) || !seen.Add(term)) continue;
                terms.Add(term);
            }
        }
        return terms;
    }

    private static IEnumerable<string> Lines(string extract)
        => extract.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static JsonElement Property(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind != kind) throw new InvalidDataException("Invalid dictionary entry.");
        return value;
    }
}
