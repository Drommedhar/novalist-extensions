using System.Net;
using System.Text.Json;
using Novalist.Extensions.Toolkit.Services;
using Xunit;

namespace Novalist.Extensions.Tests;

public class WiktionaryLookupTests
{
    [Fact]
    public async Task EnglishDefinitionsUseWiktionaryAndExcludeReferencesAndOtherLanguages()
    {
        var handler = new ResponseHandler(_ => JsonResponse("""
            {"en":[{"partOfSpeech":"Adjective","definitions":[
              {"definition":"Feeling <a href='/wiki/joy'>joy</a> &amp; contentment."},
              {"definition":"<span class='mw-cite-backlink'>Reference citation</span>"},
              {"definition":""}]}],"de":[{"partOfSpeech":"Noun","definitions":[{"definition":"Wrong language"}]}]}
            """));
        using var lookup = new Lookup(handler);

        var result = await lookup.DefineAsync("Happy", "en-GB");

        Assert.Equal(new[] { "(Adjective) Feeling joy & contentment." }, result.Items);
        Assert.Equal("https://en.wiktionary.org/wiki/happy", result.SourceUrl);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/rest_v1/page/definition/happy", request.AbsolutePath);
        Assert.Equal("en.wiktionary.org", request.Host);
    }

    [Fact]
    public async Task EnglishSynonymsExcludeAntonymsOtherLanguagesAndUsageLabels()
    {
        const string extract = """
            == English ==
            === Adjective ===
            Synonyms: happy, glad, cheerful (informal), glad; see also Thesaurus:happy
            Antonyms: unhappy, sad
            ==== Synonyms ====
            (feeling joy): delighted, content
            See also Thesaurus:joyful
            ==== Antonyms ====
            miserable
            == German ==
            Synonyms: Wrong language
            """;
        using var lookup = new Lookup(new ResponseHandler(_ => Page("happy", extract)));

        var result = await lookup.SynonymsAsync("happy", "en");

        Assert.Equal(new[] { "glad", "cheerful", "delighted", "content" }, result.Items);
    }

    [Fact]
    public async Task MissingSentenceInitialGermanAdjectiveRetriesItsLowercaseTitle()
    {
        var handler = new ResponseHandler(request => request.RequestUri!.Query.Contains("titles=Gl%C3%BCcklich")
            ? JsonResponse("""{"query":{"pages":[{"title":"Glücklich","missing":true}]}}""")
            : Page("glücklich", "== glücklich (Deutsch) ==\nBedeutungen:\n[1] Von Freude erfüllt\nHerkunft:\nOther text"));
        using var lookup = new Lookup(handler);

        var result = await lookup.DefineAsync("Glücklich", "de-CH");

        Assert.Equal(new[] { "Von Freude erfüllt" }, result.Items);
        Assert.Equal("https://de.wiktionary.org/wiki/gl%C3%BCcklich", result.SourceUrl);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task OnlyNotFoundIsAnEmptyResult(HttpStatusCode status, bool shouldFail)
    {
        using var lookup = new Lookup(new ResponseHandler(_ => new HttpResponseMessage(status)));
        if (shouldFail)
            await Assert.ThrowsAsync<HttpRequestException>(() => lookup.DefineAsync("happy", "en"));
        else
            Assert.Empty((await lookup.DefineAsync("happy", "en")).Items);
    }

    [Theory]
    [InlineData("de", "{\"error\":{\"code\":\"maxlag\"}}")]
    [InlineData("de", "{\"query\":{\"pages\":[{\"title\":\"Prolog\"}]}}")]
    [InlineData("de", "{\"query\":{\"pages\":[null]}}")]
    [InlineData("en", "{\"en\":[{\"definitions\":[]}]} ")]
    [InlineData("en", "{\"en\":null}")]
    public async Task MalformedResponsesAreErrorsRatherThanMissingWords(string language, string json)
    {
        using var lookup = new Lookup(new ResponseHandler(_ => JsonResponse(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() => lookup.DefineAsync("Prolog", language));
    }

    [Fact]
    public async Task GermanEntryMustBelongToGermanAndUnsupportedLanguagesMakeNoRequest()
    {
        var handler = new ResponseHandler(_ => Page("Prolog", "== Prolog (Französisch) ==\nBedeutungen:\n[1] Wrong language"));
        using var lookup = new Lookup(handler);
        Assert.Empty((await lookup.DefineAsync("Prolog", "de")).Items);
        await Assert.ThrowsAsync<NotSupportedException>(() => lookup.DefineAsync("Prolog", "fr"));
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage Page(string title, string extract)
        => JsonResponse(JsonSerializer.Serialize(new { query = new { pages = new[] { new { title, extract } } } }));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("https://github.com/Drommedhar/novalist-extension", request.Headers.UserAgent.ToString());
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException("Request has no address."));
            return Task.FromResult(respond(request));
        }
    }
}
