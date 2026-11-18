// ── Entities/Show.Page.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the show ROUTE's forwarder (the page is the podcast pages', `Podcasts/PodcastPages.Show.cs`) and `ShowReaderRules`, the
// pure decisions the podcast pages share with it: the rows' demand, the in-show find and the view prefs' key.

using FluentGpu.Dsl;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary><see cref="Shell.RouteKind.Show"/>: the show page (<c>Podcasts/PodcastPages.Show.cs</c>).</summary>
    public static Element Page(in Shell.Route route) => PodcastPages.ShowRoute(in route);
}

/// <summary>The show's episode-list decisions the podcast pages read: what a row asks for, the find predicate and the
/// per-show view prefs' key. Engine-free; <see cref="PrefsId"/> (once per page) allocates, nothing here runs per frame.</summary>
public static class ShowReaderRules
{
    /// <summary>What a podcast page asks for every resident member it lists (<c>PodcastPages.Show.cs</c>, <c>PodcastPages.Episode.cs</c>):
    /// what a row PAINTS (<see cref="EpisodeFields.Row"/>), its two-line blurb (<see cref="EpisodeFields.About"/>) and its progress
    /// (<see cref="EpisodeFields.Progress"/> — no route serves it per episode, so the plan seals it and the login hydrate fills it; asking is
    /// what keeps a later hydrate from being asked again). One constant, so the demand and the reveal (<c>Episode.RevealState</c>) cannot
    /// drift apart.</summary>
    public const EpisodeFields RowDemand = EpisodeFields.Row | EpisodeFields.About | EpisodeFields.Progress;

    /// <summary>The find (§4): the trimmed query in the title or the description, ordinal-ignore-case; an empty query
    /// matches everything.</summary>
    public static bool Matches(ReadOnlySpan<char> title, ReadOnlySpan<char> description, ReadOnlySpan<char> query)
    {
        var q = query.Trim();
        return q.IsEmpty || title.Contains(q, StringComparison.OrdinalIgnoreCase) || description.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The prefs key of a show: the base62 id after the uri's last ':' (<see cref="ShowViewPrefs"/> takes ids,
    /// never uris); "" when there is none.</summary>
    public static string PrefsId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        int cut = uri.LastIndexOf(':');
        string id = cut < 0 ? uri : uri[(cut + 1)..];
        return ShowViewPrefs.IsValidId(id) ? id : "";
    }
}
