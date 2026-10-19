namespace Novalist.Extensions.Toolkit.Services;

/// <summary>Online dictionary lookups and web-page capture. No results are stored on disk.</summary>
public sealed class Lookup : IDisposable
{
    private readonly HttpClient _http;
    private readonly WiktionaryDictionary _dictionary;

    public Lookup(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Novalist-Toolkit/1.0 (+https://github.com/Drommedhar/novalist-extension)");
        _dictionary = new WiktionaryDictionary(_http);
    }

    public void Dispose() => _http.Dispose();

    public Task<DictionaryLookup> DefineAsync(string word, string language, CancellationToken cancellationToken = default)
        => _dictionary.FindAsync(word, language, synonyms: false, cancellationToken);

    public Task<DictionaryLookup> SynonymsAsync(string word, string language, CancellationToken cancellationToken = default)
        => _dictionary.FindAsync(word, language, synonyms: true, cancellationToken);

    public async Task<Captured> CaptureAsync(Uri address, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(address, cancellationToken);
        response.EnsureSuccessStatusCode();
        var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return media.Contains("html", StringComparison.OrdinalIgnoreCase)
            ? ReaderMode.Extract(body, address.ToString())
            : new Captured(address.Segments.LastOrDefault()?.Trim('/') ?? address.Host,
                body.Trim(), address.ToString());
    }
}
