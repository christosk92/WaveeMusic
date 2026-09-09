using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Catalog;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>A complete virtualized search facet. The viewport retains live query handles for its current pages;
/// page zero also provides the total extent. Canonical updates change existing cards without reseeding a page cache.</summary>
sealed class SearchFacetGrid : Component
{
    /// <summary>Re-pushed props: the seed changes when the page-level resource refreshes (stale-while-revalidate), and a
    /// ctor arg would freeze at mount. The caller still Keys per (query, facet) so the VirtualCollection itself is
    /// rebuilt — not merely re-seeded — when the search changes.</summary>
    internal sealed record Props(string Query, SearchFacet Facet,
                                 IReadOnlyList<SearchMediaGrid.Item> Seed, int Total,
                                 Action<string, string?> Go, Action<string> Play);

    // Uniform-card geometry, the same numbers DiscoGrid uses so an album card is the same size wherever it appears:
    // GridCard is a square cover plus title + subtitle, and the row reserves its own vertical gutter.
    const float MinCol = 180f;
    const float CardChrome = 50f;                   // one title + one metadata line under the square cover
    const float RowGap = 20f;                       // vertical gutter between card rows
    static readonly float ColGap = Spacing.L;

    SearchResultPaging<SearchResults, SearchMediaGrid.Item>? _pages;
    string _key = "";
    Action<string, string?> _go = static (_, _) => { };
    Action<string> _play = static _ => { };
    ActionServices? _acts;
    IOverlayService? _overlay;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var svc = UseContext(Services.Slot);
        _acts = UseContext(ActionServices.Slot);
        _overlay = UseContext(Overlay.Service);
        if (svc is null || p.Query.Length == 0) return new BoxEl();
        _go = p.Go;
        _play = p.Play;

        var post = UsePost();
        string key = svc.CatalogScope + ":" + (int)p.Facet + ":" + p.Query;
        if (_pages is null || _key != key)
        {
            _pages?.Dispose();
            _key = key;
            var scope = svc.CatalogScope;
            _pages = new(svc.Queries,
                (offset, limit) => new Wavee.Core.Catalog.SearchQuery(scope, p.Query, p.Facet, offset, limit),
                result => ItemsOf(result, p.Facet), result => TotalOf(result, p.Facet), SearchFacetPageSize, post,
                p.Seed, p.Total);
        }
        var pages = _pages;
        var active = UseIsActive();
        UseEffect(() => { pages.SetActive(active.Peek()); return (Action?)pages.Dispose; }, DepKey.From(key.GetHashCode()));
        UseActivation(onActivated: () => _pages?.SetActive(true), onDeactivated: () => _pages?.SetActive(false));

        return Embed.Comp(() => new LazyGrid(
            count: () => _pages!.Count,
            cell: Cell,
            ensureRange: (first, lastExclusive) => _pages!.SetRange(first, lastExclusive),
            minColWidth: MinCol, gap: ColGap, rowExtra: CardChrome + RowGap, overscanRows: 4));
    }

    /// <summary>The wire page size. Matches <c>SearchPage.SearchPageSize</c> so the seeded page-0 window fills chunk 0
    /// exactly — a mismatch would leave a partially-filled page 0 that never refetches.</summary>
    internal const int SearchFacetPageSize = 50;

    static int TotalOf(SearchResults r, SearchFacet facet) => facet switch
    {
        SearchFacet.Albums => r.AlbumsTotal,
        SearchFacet.Playlists => r.PlaylistsTotal,
        _ => r.ArtistsTotal,
    };

    static SearchMediaGrid.Item[] ItemsOf(SearchResults r, SearchFacet facet) => facet switch
    {
        SearchFacet.Albums => AlbumItems(r.Albums),
        SearchFacet.Playlists => PlaylistItems(r.Playlists),
        _ => Array.Empty<SearchMediaGrid.Item>(),
    };

    internal static SearchMediaGrid.Item[] AlbumItems(IReadOnlyList<Album> albums)
    {
        var items = new SearchMediaGrid.Item[albums.Count];
        for (int i = 0; i < albums.Count; i++)
        {
            var a = albums[i];
            string sub = a.Artists.Count > 0 ? a.Artists[0].Name : Loc.Get(Strings.Search.TypeAlbum);
            items[i] = new SearchMediaGrid.Item(a.Cover, a.Name, sub, a.Uri, false, "album:" + a.Uri, WaveeResourceKind.Album);
        }
        return items;
    }

    internal static SearchMediaGrid.Item[] PlaylistItems(IReadOnlyList<Playlist> playlists)
    {
        var items = new SearchMediaGrid.Item[playlists.Count];
        for (int i = 0; i < playlists.Count; i++)
        {
            var pl = playlists[i];
            items[i] = new SearchMediaGrid.Item(pl.Cover, pl.Name, pl.OwnerName, pl.Uri, false, "pl:" + pl.Uri, WaveeResourceKind.Playlist);
        }
        return items;
    }

    Element Cell(int idx, float cardW)
    {
        if (_pages?.ItemAt(idx) is not { } it)
            return _pages?.ErrorAt(idx) is { } error
                ? ErrorState.Compact(error, () => _pages?.RetryAt(idx)) : Placeholder(cardW);
        Element card = SearchMediaGrid.CardFor(it, _acts, _overlay, _go, _play);
        // One height for every cell (square cover + chrome) so the grid's rows are uniform — LazyGrid reserves extent
        // from rowH, so a card that sized itself would desynchronise the spacers from what is painted.
        if (card is BoxEl b) card = b with { Key = it.OpenKey, Height = cardW + CardChrome };
        return card;
    }

    // A self-sizing skeleton cell the exact size of a real card, so a page landing never shifts the rows around it.
    static Element Placeholder(float cardW) => new BoxEl
    {
        Key = "search-card:placeholder",
        Direction = 1, Gap = Spacing.S, Height = cardW + CardChrome,
        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
        Corners = CornerRadius4.All(Radii.Card),
        Children =
        [
            new ImageEl { Source = "", AspectRatio = 1f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(Radii.Card), Placeholder = Tok.FillSubtleSecondary },
            new BoxEl { Height = 13f, AlignSelf = FlexAlign.Stretch, MaxWidth = 150f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 11f, Width = 92f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
        ],
    };
}
