namespace Novalist.Extensions.Publish.Site;

/// <summary>What to put on the site.</summary>
public enum SiteScope
{
    /// <summary>Codex entries only: a series bible for readers.</summary>
    World,

    /// <summary>The manuscript only: a draft to read.</summary>
    Manuscript,

    /// <summary>Both, cross-linked.</summary>
    Everything
}

/// <summary>How the site should turn out.</summary>
public sealed record SiteOptions
{
    public string Title { get; init; } = "Untitled";
    public string Subtitle { get; init; } = string.Empty;
    public SiteScope Scope { get; init; } = SiteScope.World;

    /// <summary>
    /// Whether to add a <c>robots.txt</c> asking crawlers to stay away.
    ///
    /// On by default, and that default is the important decision here. Somebody
    /// generating a site to send to three beta readers has not decided to publish
    /// their unfinished novel to the open internet, and a tool that quietly makes
    /// that decision for them has done real harm.
    /// </summary>
    public bool DiscourageCrawlers { get; init; } = true;
}

/// <summary>One page's worth of an entry.</summary>
public sealed record SiteEntry(
    string Id,
    string TypeKey,
    string Name,
    IReadOnlyList<(string Title, string Content)> Sections,
    IReadOnlyList<string> Aliases);

/// <summary>One scene of the manuscript.</summary>
public sealed record SiteScene(string Title, IReadOnlyList<string> Paragraphs);

/// <summary>One chapter of the manuscript.</summary>
public sealed record SiteChapter(string Title, string Act, IReadOnlyList<SiteScene> Scenes);

/// <summary>Everything the writer chose to publish.</summary>
public sealed record SiteContent(
    IReadOnlyList<SiteEntry> Entries,
    IReadOnlyList<SiteChapter> Chapters);

/// <summary>One file the generator produced.</summary>
public sealed record SiteFile(string RelativePath, string Content);
