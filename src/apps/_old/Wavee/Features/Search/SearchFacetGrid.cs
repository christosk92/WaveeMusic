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
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>A dedicated search facet (Albums / Playlists / …) as a COMPLETE uniform grid that pages the wire as you
/// scroll — not a horizontal shelf.
///
/// A shelf is the right shape for a curated rail of 5–20 on a composite page; it is the wrong shape for "177 albums",
/// where it hid 172 of them behind five pips and gave the facet tab's own count nothing to point at. This is the same
/// pairing the artist discography uses: a <see cref="VirtualCollection{T}"/> over the paged wire call, and a
/// <see cref="LazyGrid"/> that realizes only the visible window while reserving the full extent from the total.
///
/// Page 0 is SEEDED from the search page's own results — that fetch has already happened (it is what fills the facet
/// chip counts), so mounting this grid costs no extra request; <c>EnsureRange</c> only reaches the wire from page 1 on.</summary>
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

    VirtualCollection<SearchMediaGrid.Item>? _vc;
    string _key = "";
    System.Threading.CancellationTokenSource? _cts;   // re-scoped per (query, facet); cancelled on change + unmount
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
        string key = (int)p.Facet + ":" + p.Query;
        if (_vc is null || _key != key)
        {
            // New (query, facet) → cancel the prior one's in-flight pages, re-scope the CTS, rebuild + seed.
            _cts?.Cancel(); _cts?.Dispose();
            _cts = new System.Threading.CancellationTokenSource();
            _key = key;
            _vc = MakeVc(svc, p.Query, p.Facet, post, _cts.Token);
            // The page-0 window the search page already holds. Seed only takes effect while the total is unknown, so a
            // later props re-push cannot fight a page the wire has since corrected.
            if (p.Seed.Count > 0)
                _vc.Seed(Math.Max(p.Total, p.Seed.Count), CollectionsMarshal(p.Seed));
        }
        UseSignalEffect(() => Reactive.OnCleanup(() => { _cts?.Cancel(); _cts?.Dispose(); }));

        return Embed.Comp(() => new LazyGrid(
            count: Count,
            cell: Cell,
            ensureRange: (first, lastExclusive) => _vc!.EnsureRange(first, lastExclusive - 1),
            minColWidth: MinCol, gap: ColGap, rowExtra: CardChrome + RowGap, overscanRows: 4));
    }

    static VirtualCollection<SearchMediaGrid.Item> MakeVc(Services svc, string query, SearchFacet facet,
                                                          Action<Action> post, System.Threading.CancellationToken ct)
        => new(async (off, cnt, c) =>
        {
            var r = await svc.Library.SearchAsync(query, facet, off, cnt, c).ConfigureAwait(false);
            return new PageResult<SearchMediaGrid.Item>(TotalOf(r, facet), ItemsOf(r, facet));
        }, pageSize: SearchFacetPageSize, post: post, ct: ct);

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

    static ReadOnlySpan<SearchMediaGrid.Item> CollectionsMarshal(IReadOnlyList<SearchMediaGrid.Item> seed)
        => seed as SearchMediaGrid.Item[] ?? seed.ToArray();

    int Count()
    {
        _ = _vc?.Version.Value;                      // subscribe → the grid re-windows as pages land
        return _vc?.CountOr0 ?? 0;
    }

    Element Cell(int idx, float cardW)
    {
        var vc = _vc;
        if (vc is null || !vc.IsLoaded(idx) || vc[idx] is not { } it) return Placeholder(cardW);
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
