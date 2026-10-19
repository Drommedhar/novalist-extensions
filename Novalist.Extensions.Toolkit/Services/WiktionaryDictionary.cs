using System.Net;
using System.Text.Json;

namespace Novalist.Extensions.Toolkit.Services;

public sealed record DictionaryLookup(IReadOnlyList<string> Items, string SourceUrl);

internal sealed class WiktionaryDictionary(HttpClient http)
{
    public async Task<DictionaryLookup> FindAsync(string word, string language, bool synonyms,
        CancellationToken cancellationToken)
    {
        var code = language.Split('-', '_')[0].ToLowerInvariant();
        if (code is not ("en" or "de")) throw new NotSupportedException(language);
        var title = code == "en" ? word.ToLowerInvariant() : word;
        title = title.Normalize();
        var source = $"https://{code}.wiktionary.org/wiki/{Uri.EscapeDataString(title)}";
        if (code == "en" && !synonyms)
        {
            using var definition = await FetchAsync(
                $"https://en.wiktionary.org/api/rest_v1/page/definition/{Uri.EscapeDataString(title)}", cancellationToken);
            return new DictionaryLookup(definition == null ? [] : WiktionaryText.EnglishDefinitions(definition.RootElement), source);
        }

        var page = await ReadPageAsync(title, code, cancellationToken);
        // Wiktionary titles are case-sensitive; a sentence-initial adjective may be capitalized.
        if (page == null && code == "de" && title != title.ToLowerInvariant())
            page = await ReadPageAsync(title.ToLowerInvariant(), code, cancellationToken);
        if (page == null) return new DictionaryLookup([], source);

        source = $"https://{code}.wiktionary.org/wiki/{Uri.EscapeDataString(page.Value.Title)}";
        var items = code == "de"
            ? WiktionaryText.GermanEntries(page.Value.Extract, synonyms)
            : WiktionaryText.EnglishSynonyms(page.Value.Extract);
        if (synonyms)
            items = WiktionaryText.SynonymTerms(items, word);
        return new DictionaryLookup(items.Take(synonyms ? 12 : 6).ToArray(), source);
    }

    private async Task<(string Title, string Extract)?> ReadPageAsync(string title, string language,
        CancellationToken cancellationToken)
    {
        using var document = await FetchAsync(
            $"https://{language}.wiktionary.org/w/api.php?action=query&prop=extracts&explaintext=1&exsectionformat=wiki&format=json&formatversion=2&redirects=1&titles={Uri.EscapeDataString(title)}",
            cancellationToken);
        if (document == null) return null;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("query", out var query)
            || query.ValueKind != JsonValueKind.Object || !query.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() != 1)
            throw new InvalidDataException("Invalid dictionary response.");
        var page = pages[0];
        if (page.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid dictionary page.");
        if (page.TryGetProperty("missing", out _) || page.TryGetProperty("invalid", out _)) return null;
        if (!page.TryGetProperty("title", out var name) || !page.TryGetProperty("extract", out var extract)
            || name.ValueKind != JsonValueKind.String || extract.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid dictionary entry.");
        return (name.GetString() ?? throw new InvalidDataException("Missing dictionary title."),
            extract.GetString() ?? throw new InvalidDataException("Missing dictionary extract."));
    }

    private async Task<JsonDocument?> FetchAsync(string address, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(address, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
