using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;
using Wavee.Features.Browse;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Committed-query genre results. Fetches <see cref="SearchFacet.Genres"/> (a separate Pathfinder op) so the
/// All-tab list is not capped by the unified top-results page.
///
/// Rendered through <see cref="BrowseTiles.Link"/> — the same cell Browse's directory renders its Genres band with, and
/// deliberately indistinguishable from a browse-category link: a genre result IS a browse-category link, so it wears
/// the same cell rather than a second link grammar for the identical fact. The genre's accent still means something
/// on the page it opens, which is where it stays; a wall of 30 coloured plates here was decoration, not navigation.</summary>
sealed class SearchGenreTiles : Component
{
    readonly string _query;
    readonly Action<string, string?> _go;

    public SearchGenreTiles(string query, Action<string, string?> go)
    {
        _query = query;
        _go = go;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var goOrigin = UseContext(HistoryStore.GoWithOrigin);
        if (svc is null || _query.Length == 0) return new BoxEl();
        var results = UseResource(
            ct => svc.Library.SearchAsync(_query, SearchFacet.Genres, 0, 30, ct),
            SearchResults.Empty, _query).Loadable;
        // The shimmer must stay NON-zero-height and smoothResize must stay false — both are load-bearing for a
        // separate engine issue. Deriving the shimmer from Grid itself (rather than a hand-authored bar list) means
        // it is built from the SAME BrowseTiles.Link cells the real content renders — 8 varied-length seed names
        // read as a plausible genre list at every column count, and SkeletonDeriver turns their TextEl runs into
        // bars sized from that same text, never a second hand-tuned width table.
        return SearchSectionRoot.Stretch(Skel.Region(results, () => Grid(SeedGenres, _go, originQ: _query, goOrigin: goOrigin),
            r => Grid(r.Genres, _go, originQ: _query, goOrigin: goOrigin),
            isEmpty: r => r.Genres is not { Count: > 0 },
            onEmpty: () => new BoxEl(),
            onFailed: () => ErrorState.Compact(results.Error),
            smoothResize: false));
    }

    internal static Element Grid(IReadOnlyList<SearchGenre>? genres, Action<string, string?> go, bool header = true,
        string? originQ = null, Action<string, string?, NavOrigin?>? goOrigin = null)
    {
        if (genres is not { Count: > 0 } list) return new BoxEl();
        Element body = Responsive.Of(width => Columns(list, go, width, originQ, goOrigin), fallback: BrowseLayout.DirectoryFallbackWidth);
        if (!header) return body;
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [SearchChrome.TickHeader(Loc.Get(Strings.Search.Genres)), body],
        };
    }

    // The SAME 3-column pip grid Browse's Genres band renders — BrowseLayout.LinkColumns for the column count,
    // BrowseLayout.StarGrid for the track/grid shape, BrowseTiles.Link for the cell: a genre result and a
    // browse-category link share the cell, so they share its column AND track math too, not just the cell.
    // The Key stays this class's own (search-genres-grid, not BrowseTiles.LinkGrid's browse-link-grid) — a genre
    // search result is a different subtree than the directory's Genres band, so there is no key to converge on.
    static Element Columns(IReadOnlyList<SearchGenre> genres, Action<string, string?> go, float width,
        string? originQ = null, Action<string, string?, NavOrigin?>? goOrigin = null)
    {
        int cols = BrowseLayout.LinkColumns(width > 0f ? width : BrowseLayout.DirectoryFallbackWidth);
        var cells = new Element[genres.Count];
        var origin = originQ is { Length: > 0 } q ? new NavOrigin(q, "search", q) : (NavOrigin?)null;
        for (int i = 0; i < genres.Count; i++)
        {
            var g = genres[i];
            cells[i] = BrowseTiles.Link(new BrowseTileModel(g.Name, g.Uri, null, null,
                () => SearchRoutes.OpenGenre(g.Uri, g.Name, go, origin, goOrigin)));
        }
        return BrowseLayout.StarGrid(cols, Spacing.M, Spacing.S, cells) with { Key = "search-genres-grid:" + cols };
    }

    static readonly IReadOnlyList<SearchGenre> SeedGenres =
    [
        new SearchGenre("seed:genre:0", "Alternative", 0),
        new SearchGenre("seed:genre:1", "Jazz", 0),
        new SearchGenre("seed:genre:2", "Hip-Hop", 0),
        new SearchGenre("seed:genre:3", "Classical", 0),
        new SearchGenre("seed:genre:4", "R&B", 0),
        new SearchGenre("seed:genre:5", "Electronic", 0),
        new SearchGenre("seed:genre:6", "Indie", 0),
        new SearchGenre("seed:genre:7", "Metal", 0),
    ];
}

/// <summary>Related autocomplete queries from the same typeahead op, issued once on commit.</summary>
sealed class SearchRelatedQueries : Component
{
    readonly string _query;
    readonly Action<string, string?> _go;

    public SearchRelatedQueries(string query, Action<string, string?> go)
    {
        _query = query;
        _go = go;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        if (svc is null || _query.Length == 0) return new BoxEl();
        var suggestions = UseResource(
            ct => svc.Library.SuggestRichAsync(_query, ct),
            SearchSuggestions.Empty, _query).Loadable;
        return SearchSectionRoot.Stretch(Skel.Region(suggestions, () => new BoxEl(),
            s => Body(s.Queries, _query, _go),
            isEmpty: s => s.Queries.Count == 0,
            onEmpty: () => new BoxEl(),
            onFailed: () => new BoxEl()));
    }

    static Element Body(IReadOnlyList<string> queries, string typed, Action<string, string?> go)
    {
        var links = new List<Element>(queries.Count);
        for (int i = 0; i < queries.Count; i++)
        {
            string q = queries[i];
            if (q.Equals(typed, StringComparison.OrdinalIgnoreCase)) continue;
            string committed = q;
            links.Add(RelatedLink(committed, go) with { Key = "rel:" + committed });
        }
        if (links.Count == 0) return new BoxEl();
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                SearchChrome.TickHeader(Loc.Get(Strings.Search.RelatedSearches)),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, Wrap = true, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                    Children = links.ToArray(),
                },
            ],
        };
    }

    static BoxEl RelatedLink(string q, Action<string, string?> go) => new()
    {
        Shrink = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Button,
        OnClick = () => go("search", q),
        Children =
        [
            WaveeType.ModuleHeader(q.ToLowerInvariant()) with
            {
                Color = Tok.TextSecondary,
                HoverColor = Tok.AccentTextPrimary,
                Weight = 400,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            },
        ],
    };
}

/// <summary><see cref="ComponentEl"/> has no layout props — stretch has to live on the rendered root so a TickHeader
/// actually receives pane width instead of measuring as one glyph.</summary>
static class SearchSectionRoot
{
    internal static BoxEl Stretch(Element child) => new()
    {
        Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        Children = [child],
    };
}
