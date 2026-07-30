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

/// <summary>
/// The words the generator puts on a page that the writer did not write.
///
/// They arrive from outside rather than being looked up in here, because the
/// generator is a pure function with no host to ask - and because a site made
/// from a German project reading "Previous / Contents / Next" is the same bug as
/// a German book that claims to be in English.
/// </summary>
public sealed record SiteText
{
    public string Contents { get; init; } = "Contents";
    public string Previous { get; init; } = "Previous";
    public string Next { get; init; } = "Next";
    public string AlsoKnownAs { get; init; } = "Also:";
    public string NothingSelected { get; init; } = "Nothing was selected to publish.";
    public string NothingWritten { get; init; } = "Nothing written here yet.";
    public string NoProse { get; init; } = "This chapter has no prose yet.";

    public string People { get; init; } = "People";
    public string Places { get; init; } = "Places";
    public string Things { get; init; } = "Things";
    public string Lore { get; init; } = "Lore";
    public string Other { get; init; } = "Other";

    public string Character { get; init; } = "Character";
    public string Location { get; init; } = "Location";
    public string Item { get; init; } = "Item";
    public string Entry { get; init; } = "Entry";
}

/// <summary>How the site should turn out.</summary>
public sealed record SiteOptions
{
    public string Title { get; init; } = "Untitled";
    public string Subtitle { get; init; } = string.Empty;
    public SiteScope Scope { get; init; } = SiteScope.World;

    /// <summary>
    /// The BCP-47 tag every page declares. A browser hyphenates, a screen reader
    /// pronounces, and a translation tool decides whether to offer, all from this.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>The site's own wording, in the writer's language.</summary>
    public SiteText Text { get; init; } = new();

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
