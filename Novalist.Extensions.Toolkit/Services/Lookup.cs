using System.Net.Http.Json;
using System.Text.Json;

namespace Novalist.Extensions.Toolkit.Services;

/// <summary>
/// Networked lookups: what a word means, what else it could be, and what a page
/// says.
///
/// The dictionary is dictionaryapi.dev, which is free, needs no key and wraps
/// Wiktionary. That choice is worth stating plainly: it means definitions are as
/// good as Wiktionary's, and it means a lookup leaves the machine. The second
/// part is why this is an extension and not part of the app.
///
/// Nothing is cached to disk. A definition is looked at once and the alternative
/// is a growing file of words somebody once wondered about, which is a small
/// privacy problem in exchange for saving a request nobody noticed.
/// </summary>
public sealed class Lookup : IDisposable
{
    private const string DictionaryBase = "https://api.dictionaryapi.dev/api/v2/entries/en/";

    private readonly HttpClient _http;

    public Lookup(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
        // Identifying the caller is basic manners to a free service, and it is
        // what lets them tell a writing app from a scraper.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Novalist-Toolkit/1.0");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// What a word means, most common sense first. Empty when the dictionary does
    /// not have it, which for a secondary-world manuscript is most proper nouns.
    /// </summary>
    public async Task<IReadOnlyList<string>> DefineAsync(
        string word, CancellationToken cancellationToken = default)
    {
        var entries = await FetchAsync(word, cancellationToken);
        if (entries == null) return [];

        var senses = new List<string>();
        foreach (var entry in entries)
        {
            foreach (var meaning in entry.Meanings ?? [])
            {
                foreach (var definition in meaning.Definitions ?? [])
                {
                    if (string.IsNullOrWhiteSpace(definition.Definition)) continue;
                    // The part of speech is half the answer: "light" as a noun and
                    // as a verb are different words to a writer.
                    senses.Add(string.IsNullOrWhiteSpace(meaning.PartOfSpeech)
                        ? definition.Definition!
                        : $"({meaning.PartOfSpeech}) {definition.Definition}");
                    if (senses.Count >= 6) return senses;
                }
            }
        }
        return senses;
    }

    /// <summary>
    /// Other words for it, in the order the dictionary lists them.
    ///
    /// Deduplicated case-insensitively and with the word itself removed - a
    /// thesaurus that offers the word you asked about is not being helpful.
    /// </summary>
    public async Task<IReadOnlyList<string>> SynonymsAsync(
        string word, CancellationToken cancellationToken = default)
    {
        var entries = await FetchAsync(word, cancellationToken);
        if (entries == null) return [];

        var synonyms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { word };

        foreach (var entry in entries)
        {
            foreach (var meaning in entry.Meanings ?? [])
            {
                foreach (var candidate in (meaning.Synonyms ?? [])
                             .Concat((meaning.Definitions ?? []).SelectMany(d => d.Synonyms ?? [])))
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    if (!seen.Add(candidate)) continue;
                    synonyms.Add(candidate);
                    if (synonyms.Count >= 12) return synonyms;
                }
            }
        }
        return synonyms;
    }

    /// <summary>
    /// Fetches a page and reduces it to its readable text.
    /// </summary>
    public async Task<Captured> CaptureAsync(
        Uri address, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(address, cancellationToken);
        response.EnsureSuccessStatusCode();

        var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Something that is not a web page is stored as it came, because guessing
        // at the structure of an unknown format is worse than keeping the text.
        return media.Contains("html", StringComparison.OrdinalIgnoreCase)
            ? ReaderMode.Extract(body, address.ToString())
            : new Captured(address.Segments.LastOrDefault()?.Trim('/') ?? address.Host,
                body.Trim(), address.ToString());
    }

    /// <summary>
    /// One dictionary request. A word the dictionary does not have comes back 404,
    /// which is an answer rather than a failure, so it returns null instead of
    /// throwing - most names in a fantasy novel are not in any dictionary.
    /// </summary>
    private async Task<List<DictionaryEntry>?> FetchAsync(
        string word, CancellationToken cancellationToken)
    {
        var address = DictionaryBase + Uri.EscapeDataString(word.ToLowerInvariant());
        using var response = await _http.GetAsync(address, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        try
        {
            return await response.Content.ReadFromJsonAsync<List<DictionaryEntry>>(
                JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            // A service that changed its shape should look like a word that was
            // not found, not like a crash in the editor.
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class DictionaryEntry
    {
        public List<Meaning>? Meanings { get; set; }
    }

    private sealed class Meaning
    {
        public string? PartOfSpeech { get; set; }
        public List<Sense>? Definitions { get; set; }
        public List<string>? Synonyms { get; set; }
    }

    /// <summary>
    /// One sense of a word. Called a sense rather than a definition because the
    /// JSON field inside it is itself called "definition", and a type and a
    /// property of the same name read badly at every call site.
    /// </summary>
    private sealed class Sense
    {
        public string? Definition { get; set; }
        public List<string>? Synonyms { get; set; }
    }
}
