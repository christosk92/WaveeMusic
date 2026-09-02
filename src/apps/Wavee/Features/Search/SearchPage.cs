using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Browse;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The shared omnibar query signal, provided once at the shell root (WaveeShell). Live typing updates the box; committed
// queries navigate to a SearchPage slot. The KeepAlive boundary is the only route subscriber.
static class SearchQuery
{
    public static readonly Context<Signal<string>?> Slot = new(null);
}

// The Search page — results for one committed query. The query is the route snapshot at mount (a new query is a new
// keep-alive slot). Fetch keys off that frozen Arg, not the live omnibar (typing must not fire searchTopResultsList).
sealed class SearchPage : Component
{
    readonly string _query;
    readonly Signal<int> _chip = new(0);   // index into _facets (0 is always All)
    SearchFacet[] _facets = [SearchFacet.All];
    SearchResults? _chipSource;
    int _prevChip;
    bool _slideArmed;
    const int SearchPageSize = 50;
    const float FacetUnderlineH = 3f;
    const float FacetUnderlineMs = 260f;

    public SearchPage(Route route) => _query = (route.Arg ?? "").Trim();

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        if (svc is null) return new BoxEl { Grow = 1f };
        string q = _query;
        int chip = _chip.Value;
        int facetCount = _facets.Length;
        UseEffect(() => { if (_chip.Peek() >= facetCount) _chip.Value = 0; }, facetCount);
        var facet = FacetAt(chip);
        var pageScroll = UseSignal(0f);
        var results = UseResource(ct => q.Length == 0
            ? System.Threading.Tasks.Task.FromResult(SearchResults.Empty)
            : svc.Library.SearchAsync(q, facet, 0, SearchPageSize, ct), SearchResults.Empty, (int)facet).Loadable;

        (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Publish) scrollPub =
            (g => (long)(g.OffsetY / 24f), g => pageScroll.Value = g.OffsetY);

        bool slide = _slideArmed && _prevChip != chip;
        bool forward = chip > _prevChip;
        _prevChip = chip;
        _slideArmed = true;

        var resultBody = new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, PlayerDock.Reserve + Spacing.XXL),
            Children =
            [
                new BoxEl
                {
                    Key = "facet-body:" + (int)facet,
                    Animate = slide
                        ? (forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack)
                        : null,
                    Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        // smoothResize:false — a facet body is not a section whose height nudges by a row or two. It
                        // swaps a fixed shimmer for a COMPLETE result set (a virtualized grid reserves extent for all
                        // 700 albums), and that height changes again every time a page lands. Easing 0 -> thousands of
                        // DIP makes the region clip its own content into a strip that grows line by line. The reveal
                        // (below) is what should carry the entrance; the layout height must land at once.
                        Skel.Region(results, SearchShimmer, r => ResultsFor(r, chip, q, svc, go),
                            reveal: facet == SearchFacet.Tracks ? SkelReveal.None : SkelReveal.StaggerRows,
                            onFailed: () => ErrorState.Build(results.Error),
                            smoothResize: false),
                    ],
                },
            ],
        };

        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll, new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            MinWidth = 0f,
            MinHeight = 0f,
            AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Shrink = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = Spacing.S,
                    Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.S),
                    Children =
                    [
                        // Shrink, never Grow: Grow + MinWidth=0 + ellipsis is the "R..." collapse (a Basis-0 / grow
                        // title in a definite-width row reports one glyph as its min size). Stretch gives the query the
                        // pane width; ellipsis only kicks in when that width is actually tight.
                        WaveeType.SurfaceDisplay(q.ToLowerInvariant()) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            Shrink = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                        },
                        ChipBar(results),
                    ],
                },
                ScrollView(resultBody) with
                {
                    Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                    ScrollKey = "search:" + (int)facet,
                    OnScrollGeometryChanged = scrollPub,
                },
            ],
        });
    }

    // Lightweight loading skeleton (finding #7): a fixed list of result-row placeholders so the pending edge doesn't build
    // the full results tree just to derive a skeleton. Sized childless boxes → shimmer bars; SmoothResize eases the swap.
    static Element SearchShimmer()
    {
        var rows = new Element[10];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Height = 48f, AlignSelf = FlexAlign.Stretch,
                Corners = CornerRadius4.All(Radii.Control),
                Fill = Tok.FillSubtleSecondary,
            };
        return new BoxEl { Direction = 1, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, Children = rows };
    }

    Element ChipBar(Loadable<SearchResults> results)
    {
        _ = results.State.Value;
        var r = results.Value.Value;
        if (r.ChipOrder is { Count: > 0 } || r.TopHits is { Count: > 0 })
            _chipSource = r;
        var source = _chipSource ?? r;
        _facets = FacetsFrom(source);
        int selected = _chip.Value;
        int n = _facets.Length;
        var tabs = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var f = _facets[i];
            tabs[i] = FacetTab(i, f == SearchFacet.All ? Loc.Get(Strings.Search.All) : FacetName(f),
                f == SearchFacet.All ? 0 : FacetCount(f, source), i == selected);
        }
        return new BoxEl
        {
            Direction = 0, Wrap = true, AlignItems = FlexAlign.End, MinWidth = 0f, Grow = 1f,
            Children = tabs,
        };
    }

    Element FacetTab(int index, string name, int total, bool selected)
    {
        int i = index;
        var labelKids = new List<Element>(2)
        {
            Body(name) with
            {
                Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
                HoverColor = Tok.TextSecondary,
                Wrap = TextWrap.NoWrap, Shrink = 0f, MaxLines = 1,
            },
        };
        if (total > 0)
            labelKids.Add(Caption(total.ToString(System.Globalization.CultureInfo.InvariantCulture)) with
            {
                Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, Shrink = 0f, MaxLines = 1,
            });
        return new BoxEl
        {
            Direction = 1, Shrink = 0f,
            Role = AutomationRole.Tab, Cursor = CursorId.Hand, OnClick = () => _chip.Value = i,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                    Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.XS),
                    Children = labelKids.ToArray(),
                },
                selected
                    ? new BoxEl
                    {
                        Key = "underline",
                        Height = FacetUnderlineH, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
                        Corners = Radii.FullAll, Fill = Tok.AccentDefault,
                        TransformOriginX = 0f,
                        Enter = new EnterExit(Sx: 0f, Active: true),
                        Transition = MotionTokenDef.Eased(FacetUnderlineMs, Easing.SmoothOut),
                    }
                    : new BoxEl { Height = FacetUnderlineH, Shrink = 0f, Fill = ColorF.Transparent },
            ],
        };
    }

    SearchFacet FacetAt(int chip) => (uint)chip < (uint)_facets.Length ? _facets[chip] : SearchFacet.All;

    static SearchFacet[] FacetsFrom(SearchResults r)
    {
        var list = new List<SearchFacet>(12) { SearchFacet.All };
        var seen = new HashSet<SearchFacet> { SearchFacet.All };
        if (r.ChipOrder is { Count: > 0 } order)
        {
            // chipOrder is the server's tab list. Do NOT require All-payload HasAny/TotalFor — those nested
            // collections are often empty even when the chip is real (episodes/genres/podcasts/…). Clicking the
            // chip runs the dedicated op.
            for (int i = 0; i < order.Count; i++)
            {
                var f = order[i].Facet;
                if (seen.Add(f)) list.Add(f);
            }
        }
        SearchFacet[] fallback =
        [
            SearchFacet.Tracks, SearchFacet.Albums, SearchFacet.Playlists, SearchFacet.Audiobooks,
            SearchFacet.Podcasts, SearchFacet.Artists, SearchFacet.Episodes, SearchFacet.Profiles,
            SearchFacet.Genres, SearchFacet.Authors,
        ];
        for (int i = 0; i < fallback.Length; i++)
        {
            var f = fallback[i];
            if (!seen.Add(f)) continue;
            if (r.HasAny(f) || r.TotalFor(f) > 0) list.Add(f);
        }
        return list.ToArray();
    }

    static int FacetCount(SearchFacet f, SearchResults r)
    {
        int n = r.TotalFor(f);
        if (n > 0) return n;
        if (r.ChipOrder is { Count: > 0 } chips)
        {
            for (int i = 0; i < chips.Count; i++)
                if (chips[i].Facet == f && chips[i].Total > 0) return chips[i].Total;
        }
        return 0;
    }

    static string FacetName(SearchFacet f) => f switch
    {
        SearchFacet.Tracks => Loc.Get(Strings.Search.Songs),
        SearchFacet.Albums => Loc.Get(Strings.Search.Albums),
        SearchFacet.Playlists => Loc.Get(Strings.Search.Playlists),
        SearchFacet.Audiobooks => Loc.Get(Strings.Search.Audiobooks),
        SearchFacet.Podcasts => Loc.Get(Strings.Search.PodcastsShows),
        SearchFacet.Artists => Loc.Get(Strings.Search.Artists),
        SearchFacet.Episodes => Loc.Get(Strings.Search.Episodes),
        SearchFacet.Profiles => Loc.Get(Strings.Search.Profiles),
        SearchFacet.Genres => Loc.Get(Strings.Search.Genres),
        SearchFacet.Authors => Loc.Get(Strings.Search.Authors),
        _ => Loc.Get(Strings.Search.All),
    };

    void SelectFacet(SearchFacet facet)
    {
        for (int i = 0; i < _facets.Length; i++)
            if (_facets[i] == facet) { _chip.Value = i; return; }
    }

    // ── results dispatch (All composite vs a flat per-type list) ──
    Element ResultsFor(SearchResults r, int chip, string q, Services svc, Action<string, string?> go)
    {
        void Play(string uri) => _ = svc.Player.PlayAsync(uri, 0);
        void PlayTrack(string uri) => _ = svc.Player.PlayTrackAsync(uri);
        void PlayKnownTrack(Track track) => _ = svc.Player.PlayTrackAsync(track);

        var facet = FacetAt(chip);
        if (facet == SearchFacet.Audiobooks)
            return HitsList(r.Audiobooks, Loc.Get(Strings.Search.NoAudiobookResults), r, go, Play, PlayTrack, PlayKnownTrack);
        if (facet == SearchFacet.Podcasts)
            return HitsList(ShowHits(r.Shows), Loc.Get(Strings.Search.NoPodcastResults), r, go, Play, PlayTrack, PlayKnownTrack);
        if (facet == SearchFacet.Episodes)
            return HitsList(EpisodeHits(r.Episodes), Loc.Get(Strings.Search.NoEpisodeResults), r, go, Play, PlayTrack, PlayKnownTrack);
        if (facet == SearchFacet.Profiles)
            return HitsList(r.Profiles, Loc.Get(Strings.Search.NoProfileResults), r, go, Play, PlayTrack, PlayKnownTrack);
        if (facet == SearchFacet.Authors)
            return HitsList(r.Authors, Loc.Get(Strings.Search.NoAuthorResults), r, go, Play, PlayTrack, PlayKnownTrack);
        if (facet == SearchFacet.Genres)
            return SearchGenreTiles.Grid(r.Genres, go, header: false);

        if (facet != SearchFacet.All && r.Tracks.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count == 0)
            return EmptyState.Build(Loc.Get(Strings.Search.NoResults), Strings.Search.NoResultsSub(q));

        return facet switch
        {
            SearchFacet.Tracks => SongsGrid(r, go, Play, PlayTrack, PlayKnownTrack),
            SearchFacet.Albums => AlbumGrid(r, go, Play, q),
            SearchFacet.Playlists => PlaylistGrid(r, go, Play, q),
            SearchFacet.Artists => FlatList(r.Artists.Select(a => ResultRow(a.Image, a.Id.GetHashCode(), a.Name, Loc.Get(Strings.Search.TypeArtist), Loc.Get(Strings.Search.TypeArtist), true, () => go("artist:" + a.Uri, a.Name)))),
            _ => AllView(r, q, go, Play, PlayTrack, PlayKnownTrack),
        };
    }

    Element AllView(SearchResults r, string q, Action<string, string?> go, Action<string> play, Action<string> playTrack, Action<Track> playKnownTrack)
    {
        var model = new SearchAllList.Model(r, go, playTrack, play, playKnownTrack);
        var kids = new List<Element>(8);

        var hits = r.TopHits;
        if (hits is { Count: > 0 })
        {
            kids.Add(Embed.Comp(() => new SearchHero(hits[0])) with { Key = "hero:" + hits[0].Uri });
            if (hits.Count > 1)
            {
                var rest = new SearchTopHit[hits.Count - 1];
                for (int i = 1; i < hits.Count; i++) rest[i - 1] = hits[i];
                kids.Add(FillCross(Ctx.Provide(SearchAllList.Props,
                    new SearchAllList.Model(r, go, playTrack, play, playKnownTrack, Hits: rest),
                    Embed.Comp(new SearchHitsGrid.Props(ShelfPager.Chevrons | ShelfPager.Pips), () => new SearchHitsGrid()) with { Key = "best:" + rest[0].Uri + ":" + rest.Length })));
            }
        }
        else
        {
            kids.Add(Ctx.Provide(SearchAllList.Props, model, Embed.Comp(() => new SearchAllList())));
        }

        var playlists = PlaylistShelfItems(r, hits is { Count: > 0 } ? hits[0].Uri : null);
        if (playlists.Length > 0)
        {
            var items = new SearchMediaGrid.Item[playlists.Length];
            for (int i = 0; i < playlists.Length; i++)
            {
                var p = playlists[i];
                items[i] = new SearchMediaGrid.Item(p.Cover, p.Name, p.Owner, p.Uri, false, "pl:" + p.Uri, WaveeResourceKind.Playlist);
            }
            kids.Add(FillCross(Embed.Comp(
                new SearchMediaGrid.Props(items, go, play, ShelfPager.Chevrons | ShelfPager.Pips,
                    SearchChrome.TickHeader(Loc.Get(Strings.Search.Playlists), () => SelectFacet(SearchFacet.Playlists))),
                () => new SearchMediaGrid()) with { Key = "all-pl:" + items[0].Uri + ":" + items.Length }));
        }

        kids.Add(FillCross(Embed.Comp(() => new SearchGenreTiles(q, go)) with { Key = "genres:" + q }));
        kids.Add(FillCross(Embed.Comp(() => new SearchRelatedQueries(q, go)) with { Key = "related:" + q }));

        return Ctx.Provide(SearchAllList.Props, model,
            new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() });
    }

    /// <summary><see cref="ComponentEl"/> has no layout props. Wrap it in a column box so All-tab sections
    /// cross-stretch to the pane; otherwise a TickHeader's ellipsis title measures as one glyph ("R...").</summary>
    static BoxEl FillCross(Element child) => new()
    {
        Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        Children = [child],
    };

    static (Image? Cover, string Name, string Owner, string Uri)[] PlaylistShelfItems(SearchResults r, string? skipUri)
    {
        var list = new List<(Image? Cover, string Name, string Owner, string Uri)>(8);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (skipUri is { Length: > 0 }) seen.Add(skipUri);

        void Add(Image? cover, string name, string owner, string uri)
        {
            if (uri.Length == 0 || !seen.Add(uri)) return;
            list.Add((cover, name, owner, uri));
        }

        var hits = r.TopHits;
        if (hits is { Count: > 0 })
        {
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                if (h.Kind == SearchHitKind.Playlist) Add(h.Image, h.Name, PlaylistOwnerOf(h.Subtitle), h.Uri);
            }
        }
        for (int i = 0; i < r.Playlists.Count; i++)
        {
            var p = r.Playlists[i];
            Add(p.Cover, p.Name, p.OwnerName, p.Uri);
        }
        return list.ToArray();
    }

    static string PlaylistOwnerOf(string subtitle)
    {
        const string sep = " • ";
        int i = subtitle.IndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? subtitle : subtitle[(i + sep.Length)..];
    }

    /// <summary>Render an explicit hit list (a dedicated facet's results) through the SAME row factory the All tab
    /// uses, so a search row looks and behaves identically regardless of which operation produced it.</summary>
    Element HitsList(IReadOnlyList<SearchTopHit>? hits, string emptyTitle, SearchResults r,
                     Action<string, string?> go, Action<string> play, Action<string> playTrack, Action<Track> playKnownTrack)
        => Ctx.Provide(SearchAllList.Props,
            new SearchAllList.Model(r, go, playTrack, play, playKnownTrack, Filter: null, EmptyTitle: emptyTitle,
                                    Hits: hits ?? Array.Empty<SearchTopHit>()),
            Embed.Comp(() => new SearchAllList()));

    Element SongsGrid(SearchResults r, Action<string, string?> go, Action<string> play, Action<string> playTrack, Action<Track> playKnownTrack)
    {
        if (r.Tracks.Count == 0) return EmptyState.Build(Loc.Get(Strings.Search.NoResults));
        var hits = TrackHits(r.Tracks);
        return FillCross(Ctx.Provide(SearchAllList.Props,
            new SearchAllList.Model(r, go, playTrack, play, playKnownTrack, Hits: hits),
            Embed.Comp(() => new SearchSongsGrid()) with { Key = "songs-grid:" + hits[0].Uri + ":" + hits.Length }));
    }

    // A dedicated facet tab is the COMPLETE result set, not a rail: a uniform grid that pages the wire as you scroll.
    // The horizontal PagedShelf these used to be showed five of 177 albums behind five pips, which made the facet chip's
    // own count point at nothing. SearchMediaGrid (the shelf) stays — it is still the right shape for the All tab's
    // curated playlist rail.
    Element AlbumGrid(SearchResults r, Action<string, string?> go, Action<string> play, string q)
    {
        if (r.Albums.Count == 0) return EmptyState.Build(Loc.Get(Strings.Search.NoResults));
        return FacetGrid(SearchFacet.Albums, SearchFacetGrid.AlbumItems(r.Albums), r.AlbumsTotal, go, play, q);
    }

    Element PlaylistGrid(SearchResults r, Action<string, string?> go, Action<string> play, string q)
    {
        if (r.Playlists.Count == 0) return EmptyState.Build(Loc.Get(Strings.Search.NoResults));
        return FacetGrid(SearchFacet.Playlists, SearchFacetGrid.PlaylistItems(r.Playlists), r.PlaylistsTotal, go, play, q);
    }

    // The page-0 window is SEEDED into the grid's VirtualCollection, so mounting it costs no extra request — the search
    // page has already made exactly this call (it is what fills the chip counts).
    static Element FacetGrid(SearchFacet facet, SearchMediaGrid.Item[] seed, int total,
                             Action<string, string?> go, Action<string> play, string q)
        => FillCross(Embed.Comp(new SearchFacetGrid.Props(q, facet, seed, total, go, play),
            () => new SearchFacetGrid()) with { Key = "facet-grid:" + (int)facet + ":" + q });

    static SearchTopHit[] TrackHits(IReadOnlyList<Track> tracks)
    {
        var hits = new SearchTopHit[tracks.Count];
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            hits[i] = new SearchTopHit(SearchHitKind.Track, t.Uri, t.Title, SearchAllList.Names(t.Artists),
                Loc.Get(Strings.Search.TypeSong), t.Image, RoundImage: false, Followable: false,
                MatchedLyrics: false, AccessLabel: null);
        }
        return hits;
    }

    // Show/Episode are real domain records (the app has podcast surfaces to route into); the search LIST renders them
    // through the shared hit row, so they are projected here instead of duplicating the row factory.
    static IReadOnlyList<SearchTopHit> ShowHits(IReadOnlyList<Show>? shows)
    {
        if (shows is not { Count: > 0 }) return Array.Empty<SearchTopHit>();
        var hits = new SearchTopHit[shows.Count];
        for (int i = 0; i < shows.Count; i++)
        {
            var sh = shows[i];
            hits[i] = new SearchTopHit(SearchHitKind.Podcast, sh.Uri, sh.Name, sh.Publisher,
                Loc.Get(Strings.Search.TypePodcast), sh.Cover, RoundImage: false, Followable: true,
                MatchedLyrics: false, AccessLabel: null);
        }
        return hits;
    }

    static IReadOnlyList<SearchTopHit> EpisodeHits(IReadOnlyList<Episode>? episodes)
    {
        if (episodes is not { Count: > 0 }) return Array.Empty<SearchTopHit>();
        var hits = new SearchTopHit[episodes.Count];
        for (int i = 0; i < episodes.Count; i++)
        {
            var ep = episodes[i];
            hits[i] = new SearchTopHit(SearchHitKind.Episode, ep.Uri, ep.Title, ep.ShowName,
                Loc.Get(Strings.Search.TypeEpisode), ep.Image, RoundImage: false, Followable: false,
                MatchedLyrics: false, AccessLabel: null, Detail: ep.Description);
        }
        return hits;
    }

    // ── flat unified results list (per chip) ──
    static Element FlatList(IEnumerable<Element> rows) => new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows.ToArray() };

    static Element ResultRow(Image? cover, int seed, string title, string subtitle, string type, bool circular, Action open) => new BoxEl
    {
        // A ROW, not a card. Twenty of these stacked put twenty hairlines down the page, which reads as a table with
        // its rules drawn twice - and a filled plate per row leaves no quiet ground for hover to move against. Rows are
        // transparent at rest and take the subtle-fill ladder on interaction, like every other list in the app.
        Direction = 0, Height = 60f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
        Fill = ColorF.Transparent,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = open,
        Children =
        [
            new BoxEl { Width = 48f, Height = 48f, Shrink = 0f, Corners = CornerRadius4.All(circular ? 24f : 6f), ClipToBounds = true,
                Children = [Surfaces.Artwork(cover, seed & 0x7fffffff, 48f, 48f, circular ? 24f : 6f)] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
                Children =
                [
                    new TextEl(title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(subtitle) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
            TypePill(type),
        ],
    };

    static Element TypePill(string type) => new BoxEl
    {
        // The eyebrow alias in a Radii.Full capsule. The old 11 radius was a hand-computed half-height.
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary,
        Children = [WaveeType.Eyebrow(type) with { Color = Tok.TextTertiary }],
    };

}

// The "All" tab body — the modern Spotify layout: a FULL-WIDTH Top Result card, then a unified "best results" list that
// interleaves songs and artists (each with a type chip + a per-row action: save a song ♥ / follow an artist). A Component
// so the row actions re-skin live on save/follow.
sealed class SearchAllList : Component
{
    internal sealed record Model(
        SearchResults Results,
        Action<string, string?> Go,
        Action<string> PlayTrack,
        Action<string> PlayContext,
        Action<Track> PlayKnownTrack,
        Func<SearchTopHit, bool>? Filter = null,
        string? EmptyTitle = null,
        // An EXPLICIT hit list (a dedicated facet's own results). Takes precedence over Filter, which only ever slices
        // the All-tab top hits.
        IReadOnlyList<SearchTopHit>? Hits = null);
    internal static readonly Context<Model?> Props = new(null);

    public override Element Render()
    {
        var model = UseContext(Props);
        if (model is null) return new BoxEl();
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);      // row context menus (Menus.Card / Menus.TrackAttach)
        var menuOverlay = UseContext(Overlay.Service);
        var svc = UseContext(Services.Slot);
        var goOrigin = UseContext(HistoryStore.GoWithOrigin);
        bool hideTrackArtwork = AppearancePrefs.TrackArtworkHidden(svc?.Settings);
        string q = (UseContext(SearchQuery.Slot)?.Peek() ?? "").Trim();
        var origin = q.Length == 0 ? (NavOrigin?)null : new NavOrigin(q, "search", q);
        if (model.Hits is { } explicitHits)
            return BuildHits(explicitHits, lib, model, model.EmptyTitle ?? Loc.Get(Strings.Search.NoResults), acts, menuOverlay, origin, goOrigin,
                hideTrackArtwork);
        return model.Filter is { } filter
            ? BuildFiltered(model.Results, lib, model, filter, model.EmptyTitle ?? Loc.Get(Strings.Search.NoResults), acts, menuOverlay, origin,
                goOrigin, hideTrackArtwork)
            : Build(model.Results, lib, model, acts, menuOverlay, origin, goOrigin, hideTrackArtwork);
    }

    // A uri-only card menu (top hits carry uri + name, no domain object); null when the action system isn't provided.
    static MenuAttach? CardMenu(ActionServices? acts, IOverlayService? overlay, string uri, string name,
        Image? image = null, string? subtitle = null, bool circular = false)
        => acts is null || overlay is null
            ? null
            : Menus.CardAttach(acts, overlay, uri, name, image, subtitle, circular);

    // A full-track menu (fallback rows DO carry the Track — album/artist rows included).
    static MenuAttach? TrackMenu(ActionServices? acts, IOverlayService? overlay, Track t)
        => acts is null || overlay is null ? null : Menus.TrackAttach(acts, overlay, t);

    /// <summary>The menu for a unified top-results row. A TRACK hit looks itself up in the page's own results first
    /// (<see cref="TrackOf"/> — the same track that produced the hit is in <c>SearchResults.Tracks</c>) and gets the FULL
    /// track menu: Go to album, Go to artist(s) and credits need the track's album/artist URIs, which a uri-only hit does
    /// not carry, and without this a song right-clicked in search offered a strictly smaller menu than the same song
    /// right-clicked on a detail page. Exactly the resolution the row's DRAG source already performs, for the same
    /// reason, at the same cost: cold, once per gesture. A miss falls back to the uri-only card menu.</summary>
    internal static MenuAttach? HitMenu(ActionServices? acts, IOverlayService? overlay, Model model, SearchTopHit h)
        => h.Kind == SearchHitKind.Track && TrackOf(model, h.Uri) is { } t
            ? TrackMenu(acts, overlay, t)
            : CardMenu(acts, overlay, h.Uri, h.Name, h.Image, h.Subtitle, h.RoundImage);

    // ── drag sources ────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A uri-only hit row's drag source. A TRACK hit looks itself up in the page's own results first (the same
    /// track that produced the hit is in <c>SearchResults.Tracks</c>) — that is what turns an otherwise inert track
    /// payload into one a playlist can actually take. Album/playlist hits resolve their tracks on drop through the
    /// library reader; artist/show/episode hits carry none by design and are refused with a cue. The lookup runs inside
    /// the payload factory, so it is cold: once per gesture, never per render.</summary>
    internal static DragSource EntityDrag(ActionServices? acts, Model model, SearchTopHit h)
        => Drag.Source(WaveeDragKinds.Resource, () =>
            h.Kind == SearchHitKind.Track && TrackOf(model, h.Uri) is { } t
                ? WaveeResourceDragPayload.ForTrack(t)
                : WaveeResourceDragPayload.ForEntity(WaveeDragKindMap.Of(h.Kind), h.Uri, h.Name, h.Image, acts));

    static DragSource TrackDrag(Track t) => Drag.Source(WaveeDragKinds.Resource,
        () => WaveeResourceDragPayload.ForTrack(t));

    internal static Element Build(SearchResults r, LibraryBridge? lib, Model model,
                                  ActionServices? acts = null, IOverlayService? menuOverlay = null,
                                  NavOrigin? origin = null, Action<string, string?, NavOrigin?>? goOrigin = null,
                                  bool hideTrackArtwork = false)
    {

        // Spotify's unified "All" tab: render topResultsV2.itemsV2 IN ORDER — the FIRST item is the Top Result (the `large`
        // hero skin), the rest a flat list of mixed types. EVERY row is the SAME MediaCard.Row factory — the shared
        // now-playing/play affordance (NowPlayingOverlay; the home of a future context menu) — differing only by skin + the
        // search extras (eyebrow label, type chip, save/follow trailing).
        var hits = r.TopHits;
        if (hits is { Count: > 0 })
        {
            var rows = new List<Element>(hits.Count);
            rows.Add(HitRow(hits[0], lib, model, large: true, acts, menuOverlay, origin, goOrigin, hideTrackArtwork));
            for (int i = 1; i < hits.Count; i++)
                rows.Add(HitRow(hits[i], lib, model, large: false, acts, menuOverlay, origin, goOrigin, hideTrackArtwork));
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = rows.ToArray() };
        }

        var fallback = FallbackRows(r, lib, model, acts, menuOverlay, hideTrackArtwork);
        if (fallback.Count > 0)
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = fallback.ToArray() };

        // No unified top-results and no facet rows.
        return EmptyState.Build(Loc.Get(Strings.Search.NoResults));
    }

    internal static Element BuildFiltered(SearchResults r, LibraryBridge? lib, Model model, Func<SearchTopHit, bool> include, string emptyTitle,
                                          ActionServices? acts = null, IOverlayService? menuOverlay = null,
                                          NavOrigin? origin = null, Action<string, string?, NavOrigin?>? goOrigin = null,
                                          bool hideTrackArtwork = false)
        => BuildHits(r.TopHits?.Where(include).ToArray() ?? Array.Empty<SearchTopHit>(), lib, model, emptyTitle, acts, menuOverlay,
            origin, goOrigin, hideTrackArtwork);

    /// <summary>Render a hit list through the shared row factory — one code path for the All tab's filtered slice and
    /// for a dedicated facet's own results, so a podcast row is identical wherever it came from.</summary>
    internal static Element BuildHits(IReadOnlyList<SearchTopHit> hits, LibraryBridge? lib, Model model, string emptyTitle,
                                      ActionServices? acts = null, IOverlayService? menuOverlay = null,
                                      NavOrigin? origin = null, Action<string, string?, NavOrigin?>? goOrigin = null,
                                      bool hideTrackArtwork = false)
    {
        if (hits.Count == 0) return EmptyState.Build(emptyTitle);
        var rows = new Element[hits.Count];
        for (int i = 0; i < hits.Count; i++)
            rows[i] = HitRow(hits[i], lib, model, large: false, acts, menuOverlay, origin, goOrigin, hideTrackArtwork);
        return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = rows };
    }

    // ── every row is MediaCard.Row (the shared factory); these supply the per-kind data + actions only ───────────────────
    internal static Element HitRow(SearchTopHit h, LibraryBridge? lib, Model model, bool large,
                          ActionServices? acts = null, IOverlayService? menuOverlay = null,
                          NavOrigin? origin = null, Action<string, string?, NavOrigin?>? goOrigin = null,
                          bool hideTrackArtwork = false)
    {
        bool isTrack = h.Kind == SearchHitKind.Track;
        Element? trailing =
            // The SHARED FollowButton component, not a local look-alike: it owns the followed state, the accent
            // border that carries it, and the capsule geometry it shares with the Play CTA.
            h.Followable ? Embed.Comp(() => new FollowButton(h.Uri, h.Name)) with { Key = "follow:" + h.Uri }
            : isTrack ? SaveButton(lib?.IsSaved(h.Uri) ?? false, () => { if (h.Uri.Length > 0) lib?.ToggleSaved(h.Uri, h.Name); })
            : null;
        Action play = isTrack ? () => model.PlayTrack(h.Uri) : () => model.PlayContext(h.Uri);
        Action open = isTrack ? play : OpenFor(model, h.Kind, h.Uri, h.Name, origin, goOrigin);
        string? eyebrow = large ? null : (h.MatchedLyrics ? Loc.Get(Strings.Search.LyricsMatch) : h.AccessLabel);
        bool isPremiumEyebrow = !h.MatchedLyrics && h.AccessLabel is { Length: > 0 };
        return MediaCard.Row(h.Image, h.Name, h.Subtitle, h.Uri, h.RoundImage, open, play,
            eyebrow: eyebrow,
            eyebrowColor: isPremiumEyebrow ? WaveeColors.PremiumText : Tok.AccentTextPrimary,
            typeChip: null, detail: large ? null : h.Detail, trailing: trailing, large: large,
            meta: large ? null : h.Meta, detailBelowArt: h.Kind == SearchHitKind.Audiobook,
            onSubtitleNav: key => model.Go(key, null),   // artist/album names in the subtitle are individually clickable
            menu: HitMenu(acts, menuOverlay, model, h),
            drag: EntityDrag(acts, model, h),
            plated: false,
            showArtwork: !isTrack || !hideTrackArtwork);
    }

    /// <summary>The page's own results ARE the track resolver for a uri-only track hit: the same track that produced
    /// the top hit is in <c>SearchResults.Tracks</c>. There is no by-uri track read in the library seam, so this
    /// (bounded, cold, once per gesture) scan is the honest way to give a track hit a depositable payload; a miss just
    /// leaves the drag an entity payload.</summary>
    static Track? TrackOf(Model model, string uri)
    {
        if (uri.Length == 0) return null;
        var tracks = model.Results.Tracks;
        for (int i = 0; i < tracks.Count; i++)
            if (string.Equals(tracks[i].Uri, uri, StringComparison.Ordinal)) return tracks[i];
        return null;
    }

    static List<Element> FallbackRows(SearchResults r, LibraryBridge? lib, Model model,
                                      ActionServices? acts = null, IOverlayService? menuOverlay = null,
                                      bool hideTrackArtwork = false)
    {
        var rows = new List<Element>(Math.Min(r.Tracks.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count, 18));

        bool topIsArtist = r.Artists.Count > 0;
        bool topIsAlbum = !topIsArtist && r.Albums.Count > 0;
        bool topIsPlaylist = !topIsArtist && !topIsAlbum && r.Playlists.Count > 0;

        if (topIsArtist) rows.Add(ArtistRow(r.Artists[0], lib, model, large: true, acts, menuOverlay));
        else if (topIsAlbum) rows.Add(AlbumRow(r.Albums[0], model, large: true, acts, menuOverlay));
        else if (topIsPlaylist) rows.Add(PlaylistRow(r.Playlists[0], model, large: true, acts, menuOverlay));

        int artistIndex = topIsArtist ? 1 : 0;
        int trackCount = Math.Min(r.Tracks.Count, 8);
        for (int i = 0; i < trackCount; i++)
        {
            rows.Add(TrackRowFb(r.Tracks[i], lib, model, acts, menuOverlay, hideTrackArtwork));
            if ((i == 2 || i == 5) && artistIndex < r.Artists.Count)
                rows.Add(ArtistRow(r.Artists[artistIndex++], lib, model, large: false, acts, menuOverlay));
        }

        while (artistIndex < r.Artists.Count && rows.Count < 14)
            rows.Add(ArtistRow(r.Artists[artistIndex++], lib, model, large: false, acts, menuOverlay));

        int albumStart = topIsAlbum ? 1 : 0;
        for (int i = albumStart; i < r.Albums.Count && i < albumStart + 4; i++)
            rows.Add(AlbumRow(r.Albums[i], model, large: false, acts, menuOverlay));

        int playlistStart = topIsPlaylist ? 1 : 0;
        for (int i = playlistStart; i < r.Playlists.Count && i < playlistStart + 4; i++)
            rows.Add(PlaylistRow(r.Playlists[i], model, large: false, acts, menuOverlay));

        return rows;
    }

    static Element TrackRowFb(Track t, LibraryBridge? lib, Model model,
                              ActionServices? acts = null, IOverlayService? menuOverlay = null,
                              bool hideTrackArtwork = false) => MediaCard.Row(
        t.Image, t.Title, (VideoPresence.HasVideo(t) ? "Music video" : "Song") + " • " + Names(t.Artists), t.Uri, false,
        () => model.PlayKnownTrack(t), () => model.PlayKnownTrack(t),
        trailing: SaveButton(t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false), () => { if (t.Uri.Length > 0) lib?.ToggleSaved(t.Uri, t.Title); }),
        menu: TrackMenu(acts, menuOverlay, t),
        drag: TrackDrag(t), plated: false, showArtwork: !hideTrackArtwork);

    static Element ArtistRow(Artist a, LibraryBridge? lib, Model model, bool large,
                             ActionServices? acts = null, IOverlayService? menuOverlay = null) => MediaCard.Row(
        a.Image, a.Name, Loc.Get(Strings.Search.TypeArtist), a.Uri, true,
        () => model.Go("artist:" + a.Uri, a.Name), () => model.PlayContext(a.Uri),
        trailing: Embed.Comp(() => new FollowButton(a.Uri, a.Name)) with { Key = "follow:" + a.Uri }, large: large,
        plated: false,
        menu: CardMenu(acts, menuOverlay, a.Uri, a.Name, a.Image, Loc.Get(Strings.Search.TypeArtist), circular: true),
        // An artist is PINNABLE but carries no tracks — dropping it on a playlist is refused with a cue, never a guess.
        drag: Drag.Source(WaveeDragKinds.Resource,
            () => WaveeResourceDragPayload.ForEntity(WaveeResourceKind.Artist, a.Uri, a.Name, a.Image, acts)));

    static Element AlbumRow(Album a, Model model, bool large,
                            ActionServices? acts = null, IOverlayService? menuOverlay = null) => MediaCard.Row(
        a.Cover, a.Name, Loc.Get(Strings.Search.TypeAlbum) + (a.Artists.Count > 0 ? " • " + a.Artists[0].Name : ""), a.Uri, false,
        () => model.Go("album:" + a.Uri, a.Name), () => model.PlayContext(a.Uri),
        large: large, plated: false,
        menu: CardMenu(acts, menuOverlay, a.Uri, a.Name, a.Cover,
            a.Artists.Count > 0 ? a.Artists[0].Name : Loc.Get(Strings.Search.TypeAlbum)),
        drag: Drag.Source(WaveeDragKinds.Resource,
            () => WaveeResourceDragPayload.ForEntity(WaveeResourceKind.Album, a.Uri, a.Name, a.Cover, acts)));

    static Element PlaylistRow(Playlist p, Model model, bool large,
                               ActionServices? acts = null, IOverlayService? menuOverlay = null) => MediaCard.Row(
        p.Cover, p.Name, Loc.Get(Strings.Search.TypePlaylist), p.Uri, false,
        () => model.Go("pl:" + p.Uri, p.Name), () => model.PlayContext(p.Uri),
        large: large, plated: false,
        menu: CardMenu(acts, menuOverlay, p.Uri, p.Name, p.Cover, Loc.Get(Strings.Search.TypePlaylist)),
        drag: Drag.Source(WaveeDragKinds.Resource,
            () => WaveeResourceDragPayload.ForEntity(WaveeResourceKind.Playlist, p.Uri, p.Name, p.Cover, acts)));

    internal static Action OpenFor(Model model, SearchHitKind kind, string uri, string name,
        NavOrigin? origin = null, Action<string, string?, NavOrigin?>? goOrigin = null) => kind switch
    {
        SearchHitKind.Artist => () => model.Go("artist:" + uri, name),
        SearchHitKind.Album => () => model.Go("album:" + uri, name),
        SearchHitKind.Playlist => () => model.Go("pl:" + uri, name),
        SearchHitKind.Audiobook or SearchHitKind.Podcast => () => model.Go("show:" + uri, name),
        SearchHitKind.Episode => () => model.PlayContext(uri),
        SearchHitKind.Genre => () => SearchRoutes.OpenGenre(uri, name, model.Go, origin, goOrigin),
        _ => static () => { },
    };

    internal static Element SaveTrailing(bool saved, Action toggle) => SaveButton(saved, toggle);

    static Element SaveButton(bool saved, Action toggle) => new BoxEl
    {
        Width = 32f, Height = 32f, Shrink = 0f, Corners = Radii.Circle(32f),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverFill = Tok.FillSubtleSecondary, HoverScale = WaveeMotion.ScaleEmphatic.Hover, OnClick = toggle,
        BlocksDragArm = true,   // the row drags; this button saves — a press here is never a drag handle
        Children = [Icon(saved ? Icons.Accept : Icons.Add, 16f, saved ? Tok.AccentDefault : Tok.TextSecondary)],
    };

    internal static string Names(IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < artists.Count && i < 3; i++) { if (i > 0) sb.Append(", "); sb.Append(artists[i].Name); }
        return sb.ToString();
    }
}

/// <summary>Songs facet: a wrapping <see cref="Ui.AutoGrid"/> of <see cref="MediaCard.Row"/> cells. Page size is 50,
/// so this scrolls with the page — no nested pager, chevrons, or pips. All-tab Best matches stays on
/// <see cref="SearchHitsGrid"/>.</summary>
sealed class SearchSongsGrid : Component
{
    const float MinColW = 280f;
    const float RowH = 64f;

    public override Element Render()
    {
        var model = UseContext(SearchAllList.Props);
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);
        var overlay = UseContext(Overlay.Service);
        var svc = UseContext(Services.Slot);
        bool hideTrackArtwork = AppearancePrefs.TrackArtworkHidden(svc?.Settings);
        if (model?.Hits is not { Count: > 0 } hits) return new BoxEl();
        var cells = new Element[hits.Count];
        for (int i = 0; i < hits.Count; i++)
        {
            cells[i] = new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Children = [SearchAllList.HitRow(hits[i], lib, model, large: false, acts, overlay,
                    hideTrackArtwork: hideTrackArtwork)],
            };
        }
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [AutoGrid(MinColW, Spacing.M, RowH, cells)],
        };
    }
}

/// <summary>All-tab "Best matches" after the hero: a width-fitted N×N page of <see cref="MediaCard.Row"/> cells with
/// the same pips pager as the artist Top-tracks chart. Column count comes from the measured slot (hysteresis so a
/// resize does not flicker); row count matches it (1-wide stays 3 rows so a single column is not a 1-cell pager).</summary>
sealed class SearchHitsGrid : Component
{
    internal sealed record Props(ShelfPager Pager, bool ShowHeader = true);

    const float CellGap = Spacing.M;
    const float MinCellW = 280f;
    const float RowH = 64f;
    // The audiobook variant of MediaCard.Row (detailBelowArt: true, SearchAllList.HitRow below) stacks a header
    // (art + title/subtitle) above a meta+detail block instead of beside it, so it auto-sizes well past the flat
    // 64px every other hit kind fits in. PagedShelf's grid reserves ONE row height for every cell (it measures
    // nothing — see cardHeight below), so a page holding an audiobook hit needs the taller reservation or the
    // card's real content bleeds into the row beneath it.
    const float AudiobookRowH = 128f;
    const int MaxCols = 3;
    const int SingleColRows = 3;

    IReadOnlyList<SearchTopHit> _hits = Array.Empty<SearchTopHit>();
    SearchAllList.Model? _model;
    LibraryBridge? _lib;
    ActionServices? _acts;
    IOverlayService? _overlay;
    bool _hideTrackArtwork;
    ShelfPager _pager = ShelfPager.Chevrons | ShelfPager.Pips;
    bool _showHeader = true;
    int _cols = 2;
    bool _colsInit;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var model = UseContext(SearchAllList.Props);
        _lib = UseContext(LibraryBridge.Slot);
        _acts = UseContext(ActionServices.Slot);
        _overlay = UseContext(Overlay.Service);
        var svc = UseContext(Services.Slot);
        _hideTrackArtwork = AppearancePrefs.TrackArtworkHidden(svc?.Settings);
        _pager = p.Pager;
        _showHeader = p.ShowHeader;
        if (model?.Hits is not { Count: > 0 } hits) return new BoxEl();
        _hits = hits;
        _model = model;
        // Wrapper: PagedShelf's Key must be a CHILD (ReconcileSingleChild ignores Key on this component's root).
        // Responsive.Of picks the N×N from the measured slot; PagedShelf then self-fits cards inside that grid.
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                Responsive.Of(w =>
                {
                    int n = _hits.Count;
                    int cols = ColsFor(w);
                    int rows = cols <= 1 ? SingleColRows : cols;
                    int maxCols = Math.Min(cols, Math.Max(1, (n + rows - 1) / rows));
                    string first = n > 0 ? _hits[0].Uri : "";
                    // One reservation for the whole page (PagedShelf grids don't measure per cell — see AudiobookRowH
                    // above), so a single audiobook hit anywhere on the page raises every cell's height on it.
                    bool hasAudiobook = false;
                    for (int i = 0; i < _hits.Count; i++)
                        if (_hits[i].Kind == SearchHitKind.Audiobook) { hasAudiobook = true; break; }
                    float rowH = hasAudiobook ? AudiobookRowH : RowH;
                    return PagedShelf.Create(
                        n,
                        cardAt: Card,
                        cardHeight: _ => rowH,
                        header: _showHeader ? SearchChrome.TickHeader(Loc.Get(Strings.Search.BestMatches)) : null,
                        pager: _pager,
                        minCardW: MinCellW,
                        maxCardW: 9999f,
                        gap: CellGap,
                        rows: rows,
                        maxColumns: maxCols,
                        snap: ShelfSnap.Page,
                        cardWidthAgnostic: true,
                        edgeFade: 16f,
                        keyOf: i => (uint)i < (uint)_hits.Count ? _hits[i].Uri : i.ToString())
                        with { Key = "hits-shelf:" + n + ":" + maxCols + ":" + rows + ":" + (int)_pager + ":" +
                            first + ":" + _hideTrackArtwork };
                }, fallback: 0f),
            ],
        };
    }

    int ColsFor(float w)
    {
        int nominal = w <= 0f ? 1 : Math.Clamp((int)MathF.Floor((w + CellGap) / (MinCellW + CellGap)), 1, MaxCols);
        if (!_colsInit) { _colsInit = true; return _cols = nominal; }
        if (nominal >= _cols) return _cols = nominal;
        float need = _cols * MinCellW + (_cols - 1) * CellGap;
        return w < need - DetailLayoutBreakpoints.TierHysteresisDip ? (_cols = nominal) : _cols;
    }

    Element Card(int i, float _)
    {
        if (_model is null || (uint)i >= (uint)_hits.Count) return new BoxEl();
        return SearchAllList.HitRow(_hits[i], _lib, _model, large: false, _acts, _overlay,
            hideTrackArtwork: _hideTrackArtwork);
    }
}

/// <summary>Album / playlist shelf: the same 148–188 DIP <see cref="MediaCard.GridCard"/> rail Home Recents uses.</summary>
sealed class SearchMediaGrid : Component
{
    internal sealed record Item(Image? Cover, string Name, string Subtitle, string Uri, bool Circular, string OpenKey, WaveeResourceKind Kind);
    internal sealed record Props(IReadOnlyList<Item> Items, Action<string, string?> Go, Action<string> Play, ShelfPager Pager, Element? Header = null);

    const float CellGap = Spacing.M;

    IReadOnlyList<Item> _items = Array.Empty<Item>();
    Action<string, string?> _go = static (_, _) => { };
    Action<string> _play = static _ => { };
    ActionServices? _acts;
    IOverlayService? _overlay;
    ShelfPager _pager = ShelfPager.Chevrons | ShelfPager.Pips;

    public override Element Render()
    {
        var p = UseProps<Props>();
        _acts = UseContext(ActionServices.Slot);
        _overlay = UseContext(Overlay.Service);
        if (p.Items.Count == 0) return new BoxEl();
        _items = p.Items;
        _go = p.Go;
        _play = p.Play;
        _pager = p.Pager;
        int n = _items.Count;
        string first = n > 0 ? _items[0].Uri : "";
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                PagedShelf.Create(
                    n,
                    cardAt: Card,
                    cardHeight: MediaCard.ShelfHeight,
                    header: p.Header,
                    pager: _pager,
                    minCardW: HomeModuleLayout.ShelfCardMin,
                    maxCardW: HomeModuleLayout.ShelfCardMax,
                    gap: CellGap,
                    snap: ShelfSnap.Page,
                    cardWidthAgnostic: true,
                    edgeFade: HomeModuleLayout.ShelfEdgeFade,
                    keyOf: i => (uint)i < (uint)_items.Count ? _items[i].Uri : i.ToString())
                    with { Key = "media-shelf:" + n + ":" + first },
            ],
        };
    }

    Element Card(int i, float _)
        => (uint)i >= (uint)_items.Count ? new BoxEl() : CardFor(_items[i], _acts, _overlay, _go, _play);

    /// <summary>The ONE search card factory — shared with <see cref="SearchFacetGrid"/> so a facet tab's grid card and
    /// this shelf's card cannot drift in artwork, menu or drag payload.</summary>
    internal static Element CardFor(Item it, ActionServices? acts, IOverlayService? overlay,
                                    Action<string, string?> go, Action<string> play)
    {
        MenuAttach? menu = acts is null || overlay is null
            ? null
            : Menus.CardAttach(acts, overlay, it.Uri, it.Name, it.Cover, it.Subtitle, it.Circular);
        return MediaCard.GridCard(it.Cover, it.Name, it.Subtitle, it.Uri,
            () => go(it.OpenKey, it.Name), () => play(it.Uri),
            circular: it.Circular, menu: menu,
            drag: Drag.Source(WaveeDragKinds.Resource,
                () => WaveeResourceDragPayload.ForEntity(it.Kind, it.Uri, it.Name, it.Cover, acts)));
    }
}

/// <summary>All-tab section label: a vertical accent pip beside a rail header. Not
/// <see cref="Surfaces.AccentHeader"/> (that one is a horizontal rule under the title).</summary>
static class SearchChrome
{
    internal static Element TickHeader(string title, Action? open = null)
    {
        BoxEl label = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            MinWidth = 0f, Shrink = 1f,
            Children =
            [
                // The tick is BrowseLayout-owned now (T9 dedup): Browse's own band-label tick (BrowseDirectory.
                // BandLabel) is the SAME 3×14 accent mark, so both read the size off BrowseLayout.TickW/TickH rather
                // than each carrying its own copy of the literals. Corner 2 (Spacing.XXS) vs the old 1.5 is
                // imperceptible at this size.
                new BoxEl
                {
                    Width = BrowseLayout.TickW, Height = BrowseLayout.TickH, Shrink = 0f,
                    Corners = CornerRadius4.All(Spacing.XXS), Fill = Tok.AccentDefault,
                },
                // Shrink, never Grow — same contract as Surfaces.SectionHeader. A PagedShelf already grows a
                // trailing spacer; Grow + MinWidth=0 + ellipsis here makes the cluster's intrinsic width one glyph.
                WaveeType.RailHeader(title) with
                {
                    Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
        if (open is null) return label with { AlignSelf = FlexAlign.Stretch };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
            Shrink = 1f, MinWidth = 0f, OnClick = open,
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
            Children = [label, Icon(Icons.ChevronRight, 12f, Tok.TextTertiary) with { Shrink = 0f }],
        };
    }
}
