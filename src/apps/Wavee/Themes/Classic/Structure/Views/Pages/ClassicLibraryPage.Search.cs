// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryPage.Search.cs — the library's search columns (themes plan 6.1, T10c1) ──────────────────
//
// From `Entities\User.Page.Library.cs` (search mode: W11-W13, W23, W26 — UNCHANGED by the rework: still three columns under MidW) and `Entities\User.UI.cs` §7 (the search-row shapes): the
// artist / album hit rows on the shared media surface, the track hit rows, the facet header and the zero-hit message, under one skeleton group so the columns settle together. The matcher
// (`LibraryHits`), the selection commit and the debounce stay the host's: this file draws the lists it is handed.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicLibraryPage
{
    static readonly Func<bool> s_true = static () => true, s_false = static () => false;

    // The shimmer rows are the surface's own seed face at each hit's shape (an artist and an album hit share one).
    static readonly Func<Element> s_skelNameRow = static () => Controls.Surface(Controls.CardData.Seed, ClassicLibrarySearchRules.NameShape);
    static readonly Func<Element> s_skelTrackRow = static () => Controls.Surface(Controls.CardData.Seed, ClassicLibrarySearchRules.TrackShape(showArtwork: true));

    /// <summary>Retained hits stay put; only a real insert/remove/reorder animates (90 ms in, 70 ms out).</summary>
    static readonly LayoutTransition s_searchRowChange = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(ClassicLibraryRules.HitEnterMs, Easing.SmoothOut),
        Enter: new EnterExit(Dy: ClassicLibraryRules.HitEnterDy, Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ClassicLibraryRules.HitExitMs, Easing.SmoothOut));

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

    /// <summary>Album hits: the albums view's LEFT column (explained, with its zero-hit message) or the artists view's middle column (browse context under an explained artist: no eyebrow,
    /// no message — W23).</summary>
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

    static Element SearchArtistColumns(LibraryPageProps p, PageCache c)
    {
        var search = p.Search!;
        int albumCount = search.ArtistAlbumHits().Count;
        var trackRows = TrackRows(search);
        Element albumPane = Section with
        {
            Key = "s:albums", Basis = p.MidW().Value, MinWidth = p.RailOpen ? ClassicLibraryRules.SearchAlbumsMinRailOpen : ClassicLibraryRules.SearchAlbumsMin,
            MaxWidth = p.MidW().Value, Shrink = 1f, Grow = 0f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Albums), p.Shimmer ? -1 : albumCount, p.Awaiting), AlbumHits(p, explain: false)],
        };
        Element trackPane = Section with
        {
            Key = "s:tracks", Grow = 1f, Basis = 0f, MinWidth = ClassicLibraryRules.SearchTracksMin, Shrink = 1f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Songs), p.Shimmer ? -1 : trackRows.Length, p.Awaiting),
                        SearchSkel(p, s_skelTrackRow, () => SearchScroll(trackRows))],
        };
        // Keyed like the other two arms of `right` (lib:reader / lib:detail / lib:empty), and the seam keyed like the panes beside it: this row's children must never pair across a branch
        // swap by ordinal alone, because the seam carries a bound Fill and a Splitter bound to MidW.
        return new BoxEl
        {
            Key = "lib:search",
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
            Children = [albumPane, c.MidGrip ??= MidGrip(p), trackPane],
        };
    }

    static Element SearchAlbumDetail(LibraryPageProps p, PageCache c)
    {
        var rows = TrackRows(p.Search!);
        return Section with
        {
            Key = "s:detail", Grow = 1f, Basis = 0f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Songs), p.Shimmer ? -1 : rows.Length, p.Awaiting),
                        SearchSkel(p, s_skelTrackRow, () => SearchScroll(rows))],
        };
    }

    static Element[] TrackRows(LibrarySearchProps search)
    {
        var tracks = search.TrackHits();
        bool showArt = !ClassicOptions.HideTrackArt();
        var rows = new Element[tracks.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var t = tracks[i];
            rows[i] = TrackHitRow("search:t" + t.TrackSlot, t.TrackSlot, t.MatchStart, t.MatchLen, showArt, t.Play);
        }
        return rows;
    }

    /// <summary>The engine's skeleton boundary: shimmer rows DERIVED from the same row builder, a staggered reveal, and ONE group so the columns settle together. Shimmer only while there is
    /// nothing worth keeping on screen.</summary>
    static Element SearchSkel(LibraryPageProps p, Func<Element> row, Func<Element> content) => new SkelRegionEl(
        Pending: p.Shimmer ? s_true : s_false, Failed: s_false, Content: content,
        ShimmerSource: () => ShimmerStack(row), OnFailed: null,
        Reveal: SkelReveal.StaggerRows, Style: SkeletonStyle.Default, Group: p.SkelGroup);

    static Element ShimmerStack(Func<Element> row)
    {
        var kids = new Element[ClassicLibraryRules.SkelRows];
        for (int i = 0; i < kids.Length; i++) kids[i] = row() with { Key = "skel:" + i };
        return new BoxEl { Direction = 1, Gap = ClassicLibraryRules.HitGap, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Children = kids };
    }

    static Element SearchScroll(Element[] rows) => ScrollView(new BoxEl
    {
        Direction = 1, Gap = ClassicLibraryRules.HitGap, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, R.Space.DockReserve + Spacing.XL),
        Children = rows,
    }) with { Grow = 1f };

    // ══ THE SEARCH-ROW SHAPES (W11-W13, W23, W26) ════════════════════════════════════════════════════════════════════

    /// <summary>An artist / album hit: the app's shared media row (<c>Controls.Surface</c> over <c>Shape.Row(40)</c> at the 56 floor a hit has always had) fed by what the library knows of
    /// the row — a circular cover for an artist, an optional "why" eyebrow, the title with the search match highlighted, the subtitle. The surface brings what a hit was missing: the
    /// now-playing pill, the hot-revealed "…" and the right-click menu (the search card grammar, <c>Search.MenuOf</c>), and the drag a navigator row carries
    /// (<see cref="User.PayloadOf"/>). <paramref name="selected"/> wears the surface's accent skin; the row sits in the layout-transition wrapper so only a real insert / remove /
    /// reorder animates.</summary>
    static Element SearchRow(string key, EntityKind kind, int slot, int matchStart, int matchLen, string subtitle, string? eyebrow, bool selected, Action onClick)
    {
        var data = new Controls.CardData(
            LibraryRows.IdOf(kind, slot).Text, LibraryRows.TitleOf(kind, slot),
            subtitle.Length > 0
                ? new TextEl(subtitle)
                  {
                      Size = ClassicLibraryRules.RowSubtitleSize, LineHeight = ClassicLibraryRules.RowSubtitleLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                  }
                : null,
            Controls.ArtUrl(LibraryRows.ImageOf(kind, slot)), onClick,
            Circular: ClassicLibrarySearchRules.IsCircular(kind), Drag: User.HitDrag(kind, slot))
        {
            Menu = Search.MenuOf(new EntityRef(kind, slot)),
            Eyebrow = string.IsNullOrEmpty(eyebrow)
                ? null
                : new TextEl(eyebrow)
                  {
                      Size = ClassicLibraryRules.RowSubtitleSize, LineHeight = ClassicLibraryRules.RowSubtitleLine, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                  },
            Highlight = (matchStart, matchLen), Selected = selected,
        };
        return HitSurface(key, data, ClassicLibrarySearchRules.NameShape);
    }

    /// <summary>A track hit: the shared track row (<c>Track.RowData</c> — the cover or, with track artwork hidden, the play glyph; the artists; the play FAB, the "…" and the right-click
    /// track menu, and the single-track drag) with the search match highlighted. A click — and the FAB — plays the album from this track in place (<paramref name="onClick"/>), never just
    /// the one track.</summary>
    static Element TrackHitRow(string key, int trackSlot, int matchStart, int matchLen, bool showArt, Action onClick)
    {
        var data = Track.RowData(new Track(trackSlot), new Track.RowDataOptions(OnClick: onClick, OnPlay: onClick, ShowArtwork: showArt))
            with { Highlight = (matchStart, matchLen) };
        return HitSurface(key, data, ClassicLibrarySearchRules.TrackShape(showArt));
    }

    // The hit's keyed layout-transition wrapper around its surface: retained hits stay put (`s_searchRowChange`).
    static Element HitSurface(string key, Controls.CardData data, SurfaceShape shape) => new BoxEl
    {
        Key = key, Animate = s_searchRowChange, Direction = 1, MinWidth = 0f,
        Children = [Controls.Surface(data, shape)],
    };

    /// <summary>A facet header: eyebrow + count (hidden when &lt; 0; faded to 40 % while a newer answer is coming).</summary>
    static Element FacetHeader(string label, int count, bool refining) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f,
        Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.S),
        Children =
        [
            R.Type.Eyebrow(label) with { Color = Tok.TextTertiary },
            count >= 0
                ? new TextEl(FormatCache.Int(count))
                  {
                      Size = ClassicLibraryRules.RowSubtitleSize, LineHeight = ClassicLibraryRules.RowSubtitleLine, Weight = 600, BrushTransitionMs = Design.Motion.Faster,
                      Color = refining ? Tok.TextTertiary with { A = Tok.TextTertiary.A * ClassicLibraryRules.RefiningAlpha } : Tok.TextTertiary,
                  }
                : new BoxEl(),
        ],
    };

    /// <summary>The left search column's zero-hit copy (14/20 tertiary, left-aligned) — NOT the browse EmptyState.</summary>
    static Element SearchMessage(string text) => new BoxEl
    {
        Padding = new Edges4(Spacing.M, Spacing.XL, Spacing.M, Spacing.XL),
        Children = [new TextEl(text) { Size = ClassicLibraryRules.CountSize, LineHeight = ClassicLibraryRules.CountLine, Color = Tok.TextTertiary }],
    };
}
