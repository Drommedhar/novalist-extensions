using NSubstitute;
using Novalist.Extensions.Toolkit;
using Novalist.Sdk.Hooks;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.Tests;

public class ToolkitLifecycleTests
{
    [Fact]
    public async Task LookupActionsAreAvailableOnlyWhileToolkitIsRunning()
    {
        var settings = Directory.CreateTempSubdirectory("novalist-toolkit-");
        var host = Substitute.For<IHostServices>();
        var localization = Substitute.For<IExtensionLocalization>();
        localization.T(Arg.Any<string>()).Returns(call => call.Arg<string>());
        host.GetLocalization(Arg.Any<string>()).Returns(localization);
        host.GetExtensionSettingsPath(Arg.Any<string>()).Returns(settings.FullName);
        var contributors = new HashSet<IInlineActionContributor>();
        host.When(h => h.RegisterInlineActionContributor(Arg.Any<IInlineActionContributor>()))
            .Do(call => contributors.Add(call.Arg<IInlineActionContributor>()));
        host.When(h => h.UnregisterInlineActionContributor(Arg.Any<IInlineActionContributor>()))
            .Do(call => contributors.Remove(call.Arg<IInlineActionContributor>()));
        host.GetInlineActionContributors().Returns(_ => contributors.ToArray());
        var extension = new ToolkitExtension();
        try
        {
            extension.Initialize(host);
            var registered = Assert.Single(host.GetInlineActionContributors());
            Assert.Equal(new[] { "toolkit.define", "toolkit.synonyms" },
                registered.GetInlineActions().Select(action => action.Id));
            var result = await registered.ExecuteAsync("toolkit.define",
                new InlineActionRequest(), CancellationToken.None);
            Assert.Equal("toolkit.selectWord", result.Error);

            extension.Shutdown();
            Assert.Empty(host.GetInlineActionContributors());

            extension.Initialize(host);
            Assert.Single(host.GetInlineActionContributors());
        }
        finally
        {
            extension.Shutdown();
            settings.Delete(recursive: true);
        }
        Assert.Empty(host.GetInlineActionContributors());
    }
}
