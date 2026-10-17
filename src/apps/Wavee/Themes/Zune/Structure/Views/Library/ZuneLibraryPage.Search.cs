// ── Themes/Zune/Structure/Views/Library/ZuneLibraryPage.Search.cs — the library's search columns (themes plan 6.3; P5 Z7) ───────────────────
//
// A filter typed in the find field swaps the navigator and the pane for the hit columns (the matcher, the selection commit and the debounce stay the host's; this file
// draws the lists it is handed): artist / album hits on the shared media surface (the cover, the title with the match lit, the line under it, the "why" eyebrow), track
// hits on the shared track row, a facet header (a lowercase word and its count), and the zero-hit message, under ONE skeleton group so the columns settle together.
//
//   Albums · Podcasts · Audiobooks   album hits (the navigator's column) | the selected album's songs (the pane's)
//   Artists                          artist hits (the 220 column) | the selected artist's albums | the selected album's songs
//
// The columns take their widths from the tier record (no grips); a hit keeps the layout transition of a real insert / remove / reorder only.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using L = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

internal static partial class ZuneLibraryPage
{
    static readonly Func<bool> s_true = static () => true, s_false = static () => false;

    // The shimmer rows are the surface's own seed face at each hit's shape (an artist and an album hit share one).
    static readonly Func<Element> s_skelNameRow = static () => Controls.Surface(Controls.CardData.Seed, L.NameShape);
    static readonly Func<Element> s_skelTrackRow = static () => Controls.Surface(Controls.CardData.Seed, L.TrackShape);

    /// <summary>Retained hits stay put; only a real insert/remove/reorder animates (90 ms in, 70 ms out).</summary>
    static readonly LayoutTransition s_searchRowChange = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(L.HitEnterMs, Easing.SmoothOut),
        Enter: new EnterExit(Dy: L.HitEnterDy, Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(L.HitExitMs, Easing.SmoothOut));

    // ══ THE COLUMNS ══════════════════════════════════════════════════════════════════════════════════════════════════

    static Element LeftSearchBody(LibraryPageProps p, PageCache c)
    {
        var search = p.Search!;
        if (p.Entity != EntityKind.Artist) return AlbumHits(p, explain: true);
        var artists = search.ArtistHits();
        int selected = search.SelectedArtist;
        var rows = new Element[artists.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var a = artists[i];
            int slot = a.Slot;
            rows[i] = SearchRow("search:a" + slot, EntityKind.Artist, slot, a.MatchStart, a.MatchLen, a.Subtitle, a.Eyebrow, slot == selected, () => search.SelectArtist(slot));
        }
        return SearchSkel(p, s_skelNameRow, rows.Length == 0 ? () => SearchMessage(Loc.Get(Strings.Library.NoMatch)) : () => SearchScroll(rows));
    }

    /// <summary>Album hits: the albums view's LEFT column (explained, with its zero-hit message) or the artists view's middle column (browse context under an explained artist: no
    /// eyebrow, no message).</summary>
    static Element AlbumHits(LibraryPageProps p, bool explain)
    {
        var search = p.Search!;
        var albums = explain ? search.AlbumHits() : search.ArtistAlbumHits();
        int selected = search.SelectedAlbum;
        var rows = new Element[albums.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var al = albums[i];
            int slot = al.Slot;
            rows[i] = SearchRow("search:al" + slot, EntityKind.Album, slot, al.MatchStart, al.MatchLen, al.Subtitle, al.Eyebrow, slot == selected, () => search.SelectAlbum(slot));
        }
        Func<Element> content = explain && rows.Length == 0 ? () => SearchMessage(Loc.Get(Strings.Library.NoMatch)) : () => SearchScroll(rows);
        return SearchSkel(p, s_skelNameRow, content);
    }

    static Element TrackHits(LibraryPageProps p)
    {
        var rows = TrackRows(p.Search!);
        return SearchSkel(p, s_skelTrackRow, () => SearchScroll(rows));
    }

    /// <summary>The artists view's two columns right of the artist hits: the selected artist's albums, then the selected album's songs.</summary>
    static Element SearchArtistColumns(LibraryPageProps p, PageCache c)
    {
        var search = p.Search!;
        var l = c.Layout;
        int albumCount = search.ArtistAlbumHits().Count;
        var trackRows = TrackRows(search);
        Element albumPane = Section with
        {
            Key = "s:albums", Basis = L.SearchMidDefault, MinWidth = p.RailOpen ? L.SearchAlbumsMinRailOpen : L.SearchAlbumsMin,
            Shrink = 1f, Grow = 0f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Albums), p.Shimmer ? -1 : albumCount, p.Awaiting), AlbumHits(p, explain: false)],
        };
        Element trackPane = Section with
        {
            Key = "s:tracks", Grow = 1f, Basis = 0f, MinWidth = L.SearchTracksMin, Shrink = 1f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Songs), p.Shimmer ? -1 : trackRows.Length, p.Awaiting),
                        SearchSkel(p, s_skelTrackRow, () => SearchScroll(trackRows))],
        };
        // Keyed like the other arms of `right` (lib:reader / lib:detail / lib:empty): this row's children must never pair across a branch swap by ordinal alone.
        return new BoxEl
        {
            Key = L.SearchKey, Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true, Gap = l.ColGap,
            Children = [albumPane, trackPane],
        };
    }

    /// <summary>The albums view's right column under a filter: the hit album's songs, in the pane's width.</summary>
    static Element SearchAlbumDetail(LibraryPageProps p, PageCache c)
    {
        var rows = TrackRows(p.Search!);
        return PaneSection("s:detail", c.Layout) with
        {
            Children = [FacetHeader(Loc.Get(Strings.Search.Songs), p.Shimmer ? -1 : rows.Length, p.Awaiting),
                        SearchSkel(p, s_skelTrackRow, () => SearchScroll(rows))],
        };
    }

    static Element[] TrackRows(LibrarySearchProps search)
    {
        var tracks = search.TrackHits();
        var rows = new Element[tracks.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var t = tracks[i];
            rows[i] = TrackHitRow("search:t" + t.TrackSlot, t.TrackSlot, t.MatchStart, t.MatchLen, t.Play);
        }
        return rows;
    }

    /// <summary>The engine's skeleton boundary: shimmer rows DERIVED from the same row builder, a staggered reveal, and ONE group so the columns settle together. Shimmer only
    /// while there is nothing worth keeping on screen.</summary>
    static Element SearchSkel(LibraryPageProps p, Func<Element> row, Func<Element> content) => new SkelRegionEl(
        Pending: p.Shimmer ? s_true : s_false, Failed: s_false, Content: content,
        ShimmerSource: () => ShimmerStack(row), OnFailed: null,
        Reveal: SkelReveal.StaggerRows, Style: SkeletonStyle.Default, Group: p.SkelGroup);

    static Element ShimmerStack(Func<Element> row)
    {
        var kids = new Element[L.SkelRows];
        for (int i = 0; i < kids.Length; i++) kids[i] = row() with { Key = "skel:" + i };
        return new BoxEl { Direction = 1, Gap = L.HitGap, Children = kids };
    }

    static Element SearchScroll(Element[] rows) => ScrollView(new BoxEl
    {
        Direction = 1, Gap = L.HitGap, Padding = new Edges4(0f, 0f, 0f, R.Space.DockReserve),
        Children = rows,
    }) with { Grow = 1f };

    // ══ THE SEARCH-ROW SHAPES ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>An artist / album hit: the app's shared media row fed by what the library knows of the row: a circular cover for an artist, an optional "why" eyebrow, the title
    /// with the search match highlighted, the line under it. The surface brings the now-playing mark, the "…" and the right-click menu (<c>Search.MenuOf</c>), and the drag a
    /// navigator row carries (<see cref="User.HitDrag"/>). <paramref name="selected"/> wears the surface's accent skin; the row sits in the layout-transition wrapper so only a
    /// real insert / remove / reorder animates.</summary>
    static Element SearchRow(string key, EntityKind kind, int slot, int matchStart, int matchLen, string subtitle, string? eyebrow, bool selected, Action onClick)
    {
        var data = new Controls.CardData(
            LibraryRows.IdOf(kind, slot).Text, LibraryRows.TitleOf(kind, slot),
            subtitle.Length > 0
                ? R.Type.MicroMeta(subtitle) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                : null,
            Controls.ArtUrl(LibraryRows.ImageOf(kind, slot)), onClick,
            Circular: L.IsCircular(kind), Drag: User.HitDrag(kind, slot))
        {
            Menu = Search.MenuOf(new EntityRef(kind, slot)),
            Eyebrow = string.IsNullOrEmpty(eyebrow)
                ? null
                : R.Type.MicroMeta(eyebrow) with { Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Highlight = (matchStart, matchLen), Selected = selected,
        };
        return HitSurface(key, data, L.NameShape);
    }

    /// <summary>A track hit: the shared track row (<c>Track.RowData</c>: the cover, the artists, the play affordance, the "…" and the right-click track menu, and the single-track
    /// drag) with the search match highlighted. A click, and the play affordance, plays the album from this track in place (<paramref name="onClick"/>), never just the one track.</summary>
    static Element TrackHitRow(string key, int trackSlot, int matchStart, int matchLen, Action onClick)
    {
        var data = Track.RowData(new Track(trackSlot), new Track.RowDataOptions(OnClick: onClick, OnPlay: onClick, ShowArtwork: true))
            with { Highlight = (matchStart, matchLen) };
        return HitSurface(key, data, L.TrackShape);
    }

    // The hit's keyed layout-transition wrapper around its surface: retained hits stay put (`s_searchRowChange`).
    static Element HitSurface(string key, Controls.CardData data, SurfaceShape shape) => new BoxEl
    {
        Key = key, Animate = s_searchRowChange, Direction = 1, MinWidth = 0f,
        Children = [Controls.Surface(data, shape)],
    };

    /// <summary>A facet header: the lowercase word and its count (hidden when &lt; 0; faded while a newer answer is coming).</summary>
    static Element FacetHeader(string label, int count, bool refining) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = L.WordChevronGap, Shrink = 0f, Padding = new Edges4(0f, 0f, 0f, L.PaneMetaBottom),
        Children =
        [
            ZuneLibraryKit.LabelText(label),
            count >= 0
                ? R.Type.DenseMeta(FormatCache.Int(count)) with
                {
                    Weight = 600, BrushTransitionMs = Design.Motion.Faster,
                    Color = refining ? Tok.TextTertiary with { A = Tok.TextTertiary.A * L.RefiningAlpha } : Tok.TextTertiary,
                }
                : new BoxEl(),
        ],
    };

    /// <summary>The left search column's zero-hit copy (13, tertiary, left-aligned): not the browse empty state.</summary>
    static Element SearchMessage(string text) => new BoxEl
    {
        Padding = new Edges4(0f, L.PaneMetaBottom, 0f, L.PaneMetaBottom),
        Children = [R.Type.DenseMeta(text) with { Color = Tok.TextTertiary }],
    };
}
