using NSubstitute;
using Novalist.Extensions.Toolkit;
using Novalist.Extensions.Toolkit.Services;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.Tests;

public class ToolkitLookupTests
{
    [Theory]
    [InlineData("toolkit.define", "Vorrede, Einführung zu einem folgenden Text")]
    [InlineData("toolkit.synonyms", "Vorrede")]
    public async Task GermanWritingUsesGermanEntriesEvenWithEnglishMenus(string actionId, string expected)
    {
        var settings = Directory.CreateTempSubdirectory("novalist-lookup-");
        var host = Substitute.For<IHostServices>();
        var localization = Substitute.For<IExtensionLocalization>();
        localization.T(Arg.Any<string>()).Returns(call => call.Arg<string>());
        host.GetLocalization(Arg.Any<string>()).Returns(localization);
        host.GetExtensionSettingsPath(Arg.Any<string>()).Returns(settings.FullName);
        host.CurrentLanguage.Returns("en");
        host.WritingLanguage.Returns("de-DE");
        var handler = new GermanEntryHandler();
        var extension = new ToolkitExtension(() => new Lookup(handler));
        try
        {
            extension.Initialize(host);
            var result = await extension.ExecuteAsync(actionId,
                new InlineActionRequest { SelectedText = "Prolog" }, CancellationToken.None);
            Assert.Null(result.Error);
            Assert.Contains(expected, result.Text);
            Assert.Equal("de.wiktionary.org", handler.Address!.Host);
            Assert.Contains("titles=Prolog", handler.Address.Query);
            if (actionId == "toolkit.synonyms")
            {
                Assert.Equal(new[] { "Vorrede", "Vorwort" }, result.Alternatives);
                Assert.True(result.AsSuggestion);
            }
        }
        finally
        {
            extension.Shutdown();
            settings.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("toolkit.define", false, "toolkit.lookupTimedOut")]
    [InlineData("toolkit.define", true, "toolkit.cancelled")]
    [InlineData("toolkit.synonyms", false, "toolkit.lookupTimedOut")]
    [InlineData("toolkit.synonyms", true, "toolkit.cancelled")]
    public async Task ATimeoutIsDistinguishedFromCallerCancellation(string actionId, bool cancel, string expectedError)
    {
        var settings = Directory.CreateTempSubdirectory("novalist-lookup-");
        var host = Substitute.For<IHostServices>();
        var localization = Substitute.For<IExtensionLocalization>();
        localization.T(Arg.Any<string>()).Returns(call => call.Arg<string>());
        host.GetLocalization(Arg.Any<string>()).Returns(localization);
        host.GetExtensionSettingsPath(Arg.Any<string>()).Returns(settings.FullName);
        var extension = new ToolkitExtension(() => new Lookup(new CancelledRequestHandler()));
        host.WritingLanguage.Returns("en");
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        try
        {
            extension.Initialize(host);
            var result = await extension.ExecuteAsync(actionId,
                new InlineActionRequest { SelectedText = "happy" }, cancellation.Token);
            Assert.Equal(expectedError, result.Error);
        }
        finally
        {
            extension.Shutdown();
            settings.Delete(recursive: true);
        }
    }

    private sealed class CancelledRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Request timed out."));
    }

    private sealed class GermanEntryHandler : HttpMessageHandler
    {
        public Uri? Address { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Address = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"query":{"pages":[{"title":"Prolog","extract":"== Prolog (Deutsch) ==\n=== Substantiv, m ===\nBedeutungen:\n[1] Vorrede, Einführung zu einem folgenden Text\nHerkunft:\nDo not include etymology.\nSynonyme:\n[1] Vorrede, Vorwort, Prolog, Vorrede\nGegenwörter:\n[1] Epilog\n== Prolog (Französisch) ==\nBedeutungen:\n[1] Wrong language\nSynonyme:\n[1] Wrong language"}]}}
                    """)
            });
        }
    }
}
