// ── Entities/Search.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the search surface's static factories: the facet label and the 11-pill facet skeleton, the three loading shapes, the hit row,
// the top-result hero, Best matches, the playlist rail, the Songs grid, the Albums / Playlists facet grid, the flat hit
// lists, the Artists row, the genre links and the related queries
//
// Role: UI
// Owner: P (stream P3)
// Wave: 5
// Budget: 1100 lines
// Spec: ch 13 §1.1, §1.5, W1-W12, §3-§6.1, §9 (must-not-simplify 1-3, 7, 8; traps; gaps 1-3, 7-9)
//
// ── EVERY FACTORY IS A STATIC FUNCTION OVER HANDLES ─────────────────────────────────────────────────────────────────
//
// 0.2.9 built SearchHero(hit), SearchGenreTiles(q, go), SearchRelatedQueries(q, go), SearchHitsGrid and SearchAllList as
// Components with ctor args, and survived props-freeze only by keying every one of them on a data value
// ("hero:<uri>", "best:<uri>:<n>", "hits-shelf:<n>:<cols>:…"). Those keys remount on every table publish and would trip
// the ReuseGuard (ch 13 §9 traps). Here each is a static function over the search row and the hit's EntityRef, run by
// the page's own render, so a hydrating row REBINDS. The three key families that survive are the facet body
// (Search.Page.cs), the column-count keys on the Responsive grids ("search-genres-grid:<cols>",
// "search-facet-grid:<cols>") and — not a data key — the per-uri Save / Follow embeds, whose props freeze by design
// (Controls.cs §4).
//
// ── ONE HIT ROW ──────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Every hit — All-tab list, Best matches cell, Songs cell, Artists / Podcasts / Episodes / Profiles lists — is `HitRow`:
// the app's ONE media surface (`Controls.Surface`) at `Shape.Row(48)` (`Shape.RowLarge` for a fallback list's lead row)
// over `HitData`, the hit's data. The surface owns the hover and press plate, the hand cursor, the focus ring, the play
// FAB and the hot-revealed "…", so a podcast row looks identical wherever it came from (ch 13 §0.8) and nothing here
// paints a row. The title, art and subtitle read the TARGET row's columns through `HomeCard` (the same live handle
// Home's cards use); a TRACK hit takes the track adapter (`Track.RowData`: the cover fallback, the hidden-artwork look,
// the play leg) and keeps its search subtitle; the chrome flags (lyrics match, music video) read `Search.FlagsOf`. A
// TRACK hit gets the full track menu and a depositable payload straight off its own row — the 0.2.9 `TrackOf` scan
// disappears because the edge target IS the Track handle (ch 13 §7). Artists are no longer a thinner row (W10): they
// are this row, circular, with the menu, drag and follow control 0.2.9 forgot (§9 gap 8). Which kind follows, saves,
// drags or wears the eyebrow is `SearchHitRules` — pure, pinned by SearchHitDataTests.
//
// ── CARDS ARE THE ONE SURFACE ────────────────────────────────────────────────────────────────────────────────────────
//
// The playlist rail's shelf card and the Albums / Playlists grid's cell are `Controls.Surface(data, Shape.Shelf() /
// Shape.Grid)` — the app's one media surface, like the hit row above — and the context menu travels IN the data
// (`CardDataOf`, `HitData`): the host attaches it and the card's own "…" re-enters it, so nothing here wraps a card. A
// blank grid cell is the same host on `CardData.Seed`; the loading shapes' cards and rows are `SurfaceParts.Seed`, the
// live surface's own geometry in bones — never a second description of a card
// (docs/plans/wavee/shared-media-surface-implementation.md, waves 1c and 2b). A shelf's cards sit in a bound slot, so
// the SHELF owns their click: the rail and Best matches pass `onInvoke`.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Search
{
    /// <summary>What every hit factory needs beyond the hit, resolved ONCE per page render.</summary>
    internal readonly record struct HitContext(string Query, Shell.NavOrigin? Origin, IOverlayService? Overlay, bool HideTrackArt);

    /// <summary>One Best-matches / rail cell: the hit plus its row version and chrome, so the shelf's value snapshot
    /// changes when the row hydrates.</summary>
    internal readonly record struct HitItem(EntityRef Ref, uint Version, SearchHitFlags Flags);

    internal const float HeroHeight = 228f;
    internal const float SongsColMin = 280f;

    // ══ 1. THE FACET ROW (W3, §0.3-§0.5) ═════════════════════════════════════════════════════════════════════════════
    //
    // The facet row is the page head's VIEWS bar (PageHead, Search.Page.cs): the stock SelectorBar in Design.PageViewsStyle,
    // one fixed 48-DIP row that never wraps and scrolls horizontally when the eleven facets outgrow it. The query echo is
    // the head's title. This file owns only the two things the head cannot know: a facet's label and the skeleton.

    /// <summary>A facet's views label: the name, then its count — omitted for 0 (so All, which never carries one, and a facet
    /// the server counted as empty read as the bare name; W3).</summary>
    public static string FacetLabel(string name, int count)
        => count <= 0 ? name : name + " " + FormatCache.Int(count);

    /// <summary>The eleven placeholder pills at their real widths in ONE non-wrapping row (W2, §9 must-not-simplify 3). Each
    /// pill sits in a box of <see cref="PageGeometry.ViewsItemH"/> with the stock SelectorBar item's padding (12, 10, 12, 7)
    /// and the row takes the bar's <see cref="PageGeometry.ViewsLeadingInset"/>, so the first pill lands on the gutter and
    /// the row is exactly as tall as the real bar — the skeleton matches the bar by construction. The row is a placeholder
    /// inside the head's reserved views row: the head's height never depends on which one shows.</summary>
    internal static Element FacetRowSkeleton()
    {
        var widths = ChipSkeletonWidths;
        var pills = new Element[widths.Length];
        for (int i = 0; i < widths.Length; i++)
            pills[i] = new BoxEl
            {
                Direction = 0, Shrink = 0f, Height = PageGeometry.ViewsItemH, AlignItems = FlexAlign.Center,
                Padding = new Edges4(12f, 10f, 12f, 7f),
                Children = [new BoxEl { Width = widths[i], Height = 16f, Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary }],
            };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, AlignItems = FlexAlign.Center,
            Margin = new Edges4(PageGeometry.ViewsLeadingInset, 0f, 0f, 0f), Children = pills,
        };
    }

    /// <summary>The All row's own list count for a facet: its hits of that kind, the genre tiles for Genres.</summary>
    internal static int LocalCountOf(Search all, SearchFacet f)
    {
        if (f == SearchFacet.Genres) return all.GenreSlots.Length;
        var kind = KindOf(f);
        return kind == EntityKind.Unknown ? 0 : all.CountOf(kind);
    }

    /// <summary>A section label: the 3×14 accent tick (BrowseLayout's, shared with Browse's band label) beside a rail
    /// header, with a 12-DIP chevron and a hyperlink role only when there is somewhere to go.</summary>
    internal static Element TickHeader(string title, Action? open = null)
    {
        var label = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Shrink = 1f,
            Children =
            [
                new BoxEl { Width = BrowseLayout.TickW, Height = BrowseLayout.TickH, Shrink = 0f, Corners = CornerRadius4.All(Spacing.XXS), Fill = Tok.AccentDefault },
                Design.Type.RailHeader(title) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        if (open is null) return label with { AlignSelf = FlexAlign.Stretch };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f, OnClick = open,
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
            Children = [label, Icon(Icons.ChevronRight, 12f, Tok.TextTertiary) with { Shrink = 0f }],
        };
    }

    /// <summary>A column that cross-stretches its child to the pane, so a section's ellipsised header receives width.</summary>
    internal static BoxEl Stretch(Element child) => new() { Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [child] };

    // ══ 2. THE THREE LOADING SHAPES (§0.6, W2) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The facet body's shimmer, in the shape of the facet it stands in for — never one bar stack. Its cards and
    /// rows are the media surface's own seed face (<see cref="SeedFace"/>), so a hydrating page shifts nothing.</summary>
    internal static Element Shimmer(SearchFacet facet) => ShimmerFor(facet) switch
    {
        ShimmerShape.Hero => ShimmerAll(),
        ShimmerShape.CardGrid => ShimmerCardGrid(),
        _ => ShimmerRows(),
    };

    /// <summary>One seed card or row: <see cref="SurfaceParts.Seed"/> — the live surface's geometry in bones — handed to the
    /// region's deriver as its OWN override. A seed is already bones, so deriving it again would only lose the title bar's
    /// 150 cap (a row's bar would run the whole text column); a static tree also mounts no <c>SurfaceHost</c> during load
    /// (<c>Skel.Region</c>'s explicit-shimmer rule).</summary>
    static Element SeedFace(SurfaceShape shape, float width = float.NaN)
    {
        Element seed = SurfaceParts.Seed(in shape, width);
        return seed.Skel(seed);
    }

    /// <summary>Ten seed rows at the hit row's own geometry — 48 art in the 64 floor — on the 8-DIP list gap.</summary>
    static Element ShimmerRows()
    {
        var rows = new Element[10];
        for (int i = 0; i < rows.Length; i++) rows[i] = SeedFace(Shape.Row(48f));
        return new BoxEl { Direction = 1, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, Children = rows };
    }

    /// <summary>The seed cards' width: inside the rail's 148-188 card range.</summary>
    const float ShimmerCardW = 168f;

    /// <summary>Twelve seed shelf cards at the playlist rail's card width, wrapping.</summary>
    static Element ShimmerCardGrid()
    {
        var cards = new Element[12];
        for (int i = 0; i < cards.Length; i++) cards[i] = SeedFace(s_railCard, ShimmerCardW);
        return new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch, Children = cards };
    }

    static Element ShimmerAll()
    {
        var hero = new BoxEl
        {
            Height = HeroHeight, MinWidth = 0f, Grow = 1f, AlignSelf = FlexAlign.Stretch, Direction = 0,
            ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S, Justify = FlexJustify.End,
                    Padding = new Edges4(Spacing.L, Spacing.L, Spacing.XL, Spacing.L),
                    Children =
                    [
                        new BoxEl { Width = 90f, Height = 12f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                        new BoxEl { Width = 260f, Height = 28f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                        new BoxEl { Width = 170f, Height = 16f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleTertiary },
                        new BoxEl { Width = 100f, Height = 32f, Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary },
                    ],
                },
                new BoxEl { Width = 300f, Height = 260f, Shrink = 0f, AlignSelf = FlexAlign.Center, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillSubtleTertiary },
            ],
        };
        return new BoxEl { Direction = 1, Gap = Spacing.L, AlignSelf = FlexAlign.Stretch, Children = [hero, ShimmerCardGrid()] };
    }

    // ══ 3. WHAT A HIT SAYS ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The hit's title: the target row's own name column (a profile's display name).</summary>
    internal static string TitleOf(EntityRef hit)
        => hit.Kind == EntityKind.User ? Entities.Strings.Resolve(new User(hit.Slot).NameId) : new HomeCard(hit).Title;

    /// <summary>The hit's art url, or null.</summary>
    internal static string? CoverOf(EntityRef hit)
        => hit.Kind == EntityKind.User ? Controls.ArtUrl(new User(hit.Slot).ImageId) : new HomeCard(hit).ImageUrl;

    /// <summary>The kind word a hit's subtitle leads with — "Song" / "Music video" for a track (§9 gap 7: the loc keys,
    /// never the hardcoded English 0.2.9 used).</summary>
    internal static string KindWordOf(EntityKind kind, SearchHitFlags flags) => Loc.Get(kind switch
    {
        EntityKind.Track => (flags & SearchHitFlags.VideoMedia) != 0 ? Strings.Search.SubtitleMusicVideo : Strings.Search.SubtitleSong,
        EntityKind.Album => Strings.Search.TypeAlbum,
        EntityKind.Artist => Strings.Search.TypeArtist,
        EntityKind.Playlist or EntityKind.Collection => Strings.Search.TypePlaylist,
        EntityKind.Show => Strings.Search.TypePodcast,
        EntityKind.Episode => Strings.Search.TypeEpisode,
        _ => Strings.Search.TypeUser,
    });

    /// <summary>"Song • A, B" / "Album • A" / "Playlist • owner" / "Podcast • publisher" / "Episode • show" / "Artist" /
    /// "Profile" — the kind word, then what the target row knows (0.2.9's server subtitle shape, rebuilt from columns).</summary>
    internal static string SubtitleOf(EntityRef hit, SearchHitFlags flags)
    {
        string word = KindWordOf(hit.Kind, flags);
        if (hit.Kind is EntityKind.Artist or EntityKind.User) return word;
        var card = new HomeCard(hit);
        string? detail = hit.Kind is EntityKind.Playlist or EntityKind.Collection ? card.OwnerName : card.Subtitle;
        return SubtitleText(word, detail);
    }

    /// <summary>Where opening a hit goes: a page for an album / artist / playlist / show (with the search LOOKUP origin, so
    /// the trail reads <c>"q" › X</c>); a track or an episode plays; a profile opens its page (#161 — the same origin, so
    /// the masthead reads <c>"q" › Name</c>).</summary>
    internal static void OpenHit(EntityRef hit, Shell.NavOrigin? origin)
    {
        if (hit.IsNone) return;
        if (hit.Kind is EntityKind.Track or EntityKind.Episode) { PlayHit(hit); return; }
        if (hit.Kind == EntityKind.User)
        {
            var profile = ProfileRoute.For(new User(hit.Slot));
            if (!profile.IsNone) Shell.GoTo(profile, origin);
            return;
        }
        var card = new HomeCard(hit);
        HomeCardNav.Open(in card, origin);
    }

    /// <summary>The hit's play: a track / episode as the item, a container as its context (HomeCardNav's one path).</summary>
    internal static void PlayHit(EntityRef hit)
    {
        if (hit.IsNone || !CanPlay(hit.Kind)) return;
        var card = new HomeCard(hit);
        HomeCardNav.Play(in card);
    }

    /// <summary>The hit's context menu, or null (the "…" and the right-click then do not exist, §6.1): a TRACK gets the
    /// full track menu off its own row; an album / artist / playlist / show the card grammar; episode / profile none.</summary>
    internal static Func<ContextMenuModel?>? MenuOf(EntityRef hit)
    {
        if (hit.IsNone || !HasMenu(hit.Kind)) return null;
        if (hit.Kind == EntityKind.Track)
        {
            int slot = hit.Slot;
            return () =>
            {
                Track.EnsureActions();
                return Track.Menu([new Track(slot)], new Track.MenuOptions(ShowGoToAlbum: true));
            };
        }
        var card = new HomeCard(hit);
        return HomeCardNav.MenuOf(in card);
    }

    /// <summary>The hit's drag source: a track carries itself (depositable on a playlist); anything else the entity
    /// payload the drop target resolves or refuses with a cue. A profile is not a drag source.</summary>
    internal static DragSource? DragOf(EntityRef hit)
    {
        if (hit.IsNone || !SearchHitRules.Drags(hit.Kind)) return null;
        var h = hit;
        return Drag.Source(() =>
        {
            string uri = h.Id.Text;
            string name = TitleOf(h);
            return h.Kind == EntityKind.Track
                ? new DragPayload(DragKind.Track, uri, uri, name, h, Tracks: [new Track(h.Slot)], ArtUrl: CoverOf(h))
                : new DragPayload(Drag.KindOf(h.Kind), uri, uri, name, h, ArtUrl: CoverOf(h));
        });
    }

    /// <summary>The trailing control: Follow for an artist / playlist / show, the heart for a track / album, none else.
    /// Keyed on the uri — the embed's props freeze by design (Controls.cs §4).</summary>
    internal static Element? TrailingOf(EntityRef hit, string uri, string name, bool compact)
        => SearchHitRules.TrailingOf(hit.Kind, hasUri: uri.Length > 0) switch
        {
            SearchHitRules.Trailing.Follow
                => Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name }) with { Key = "follow:" + uri },
            SearchHitRules.Trailing.Save
                => Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = name, Glyph = 16f }) with { Key = "save:" + uri },
            _ => null,
        };

    // ══ 4. THE HIT ROW (W9, §3 "Search rows") ════════════════════════════════════════════════════════════════════════

    /// <summary>The surface's data for a hit of ANY kind — the one adapter behind every search row. Title, the "Song • A, B"
    /// line, cover (circular for people), open and play; the eyebrow "Lyrics match" on a small row whose hit matched
    /// lyrics; the trailing follow / save; the context menu (<see cref="MenuOf"/>) and the drag (<see cref="DragOf"/>) — null
    /// for a hit with none, and the host then grows no "…" and no right-click. A TRACK hit is the track adapter
    /// (<see cref="Track.RowData"/>) under search's own subtitle, menu and drag: it brings the cover fallback, the
    /// hidden-artwork look (a play glyph where the cover was — pair it with <see cref="SearchHitRules.ShapeOf"/>) and the
    /// play leg that toggles pause on the playing track. Runs inside a render: a track hit's table read subscribes it, so a
    /// hydrating row re-describes itself.</summary>
    internal static Controls.CardData HitData(EntityRef hit, SearchHitFlags flags, bool large, in HitContext ctx)
    {
        var h = hit;
        var origin = ctx.Origin;
        string uri = hit.Id.Text;
        string title = TitleOf(hit);
        Action open = () => OpenHit(h, origin);
        Action? play = CanPlay(hit.Kind) ? () => PlayHit(h) : null;

        Controls.CardData data = hit.Kind == EntityKind.Track
            ? Track.RowData(new Track(hit.Slot), new Track.RowDataOptions(
                OnClick: open, OnPlay: play, ShowArtwork: SearchHitRules.ShowsArt(hit.Kind, ctx.HideTrackArt),
                ShowExplicit: false, Draggable: false))
            : new Controls.CardData(uri, title, null, CoverOf(hit), open, play, Circular: RoundArt(hit.Kind));
        return data with
        {
            Subtitle = Design.Type.TrackMeta(SubtitleOf(hit, flags)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            Eyebrow = SearchHitRules.LyricsEyebrow(large, flags)
                ? Design.Type.Eyebrow(Loc.Get(Strings.Search.LyricsMatch)) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                : null,
            Trailing = TrailingOf(hit, uri, title, compact: true),
            Menu = MenuOf(hit),
            Drag = DragOf(hit),
        };
    }

    /// <summary>THE search row: <see cref="HitData"/> on the one media surface at <see cref="SearchHitRules.ShapeOf"/> — 48
    /// art in the 64 floor (a hidden-artwork track row: the 32 play square), 84 art in the 112 floor when <paramref name="large"/>.
    /// Keyed by the hit's kind + slot, so a result list that reorders carries each row's hover and menu with it.</summary>
    internal static Element HitRow(EntityRef hit, SearchHitFlags flags, bool large, in HitContext ctx)
    {
        if (hit.IsNone) return new BoxEl();
        var shape = SearchHitRules.ShapeOf(large, artHidden: !SearchHitRules.ShowsArt(hit.Kind, ctx.HideTrackArt));
        return Controls.Surface(HitData(hit, flags, large, in ctx), shape) with { Key = "hit:" + KeyOf(hit) };
    }

    /// <summary>A column of hit rows at the 8-DIP list gap.</summary>
    internal static Element HitColumn(ReadOnlySpan<Element> rows)
        => new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = rows.ToArray() };

    // ══ 5. THE TOP RESULT (W4) ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The 228-DIP top-result card: copy left, a 300×260 cover cropped off the right at −2.5°, a radial wash of
    /// the cover's own chrome grading (none when ungradeable), Play on that accent, Open page for a destination, the
    /// trailing control and the "…" only when a menu resolved. A static function, never a Component — the hit is read
    /// from the edge on every render (§1.5).</summary>
    internal static Element TopResult(EntityRef hit, SearchHitFlags flags, in HitContext ctx)
    {
        if (hit.IsNone) return new BoxEl();
        string uri = hit.Id.Text;
        string title = TitleOf(hit);
        string? url = CoverOf(hit);
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;                 // a landed grading re-renders the page
        Scheme? scheme = url is { Length: > 0 } ? Design.ChromeSchemeFor(url) : null;
        ColorF accent = scheme is { } s ? Design.Palette.ChromeAccent(s) : Tok.AccentDefault;

        var h = hit;
        var origin = ctx.Origin;
        Action open = () => OpenHit(h, origin);
        var menu = MenuOf(hit);

        var actions = new List<Element>(4);
        if (CanPlay(hit.Kind)) actions.Add(Controls.PlayButton(accent, () => PlayHit(h)) with { Shrink = 0f });
        if (TrailingOf(hit, uri, title, compact: false) is { } trailing) actions.Add(trailing);
        // The "…" is a hover/focus REVEAL on the card (the Surface host's own mechanism, which a hand-built card does not
        // get for free): the wrapper is NON-interactive, so the engine's hover cascade resolves to the card and the pointer
        // anywhere on it reveals the button (HoverOpacity); keyboard focus within the card reveals it through the folded
        // focus edges (Controls.CardChromeRules.FocusWithin — the shell's own edge and the wrapper's subtree edge, so Tab from the
        // card onto the "…" does not read as a loss). The button itself rests fully opaque inside the wrapper.
        Action<bool>? cardFocus = null;
        if (menu is not null)
        {
            var revealed = new Signal<bool>(false);
            bool selfFocus = false, innerFocus = false;
            cardFocus = f => { selfFocus = f; revealed.Value = Controls.CardChromeRules.FocusWithin(selfFocus, innerFocus); };
            actions.Add(new BoxEl
            {
                Shrink = 0f, Opacity = Prop.Of(() => revealed.Value ? 1f : 0f), HoverOpacity = 1f,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                OnFocusChanged = f => { innerFocus = f; revealed.Value = Controls.CardChromeRules.FocusWithin(selfFocus, innerFocus); },
                Children = [Controls.Named(Controls.MoreButton(null, requestsContext: true, restOpacity: 1f), Loc.Get(Strings.Common.More))],
            });
        }

        var chips = new List<Element>(2);
        if ((flags & SearchHitFlags.MatchedTitle) != 0) chips.Add(HeroChip(Loc.Get(Strings.Search.MatchedTitle)));
        if ((flags & SearchHitFlags.MatchedLyrics) != 0) chips.Add(HeroChip(Loc.Get(Strings.Search.LyricsMatch)));

        var copy = new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S, Justify = FlexJustify.End,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.XL, Spacing.L),
            Children =
            [
                Design.Type.Eyebrow(Loc.Get(Strings.Search.TopResult) + " · " + KindWordOf(hit.Kind, flags)) with
                    { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Design.Type.PageHero(title) with { MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                hit.Kind is EntityKind.Artist or EntityKind.User
                    ? new BoxEl()
                    : new TextEl(SubtitleOf(hit, flags)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                chips.Count == 0 ? new BoxEl() : new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, MinWidth = 0f, Children = chips.ToArray() },
                actions.Count == 0 ? new BoxEl() : new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = actions.ToArray() },
            ],
        };
        var cover = new BoxEl
        {
            Width = 300f, Height = 260f, Shrink = 0f, AlignSelf = FlexAlign.Center,
            OffsetX = 40f, OffsetY = -16f, Rotation = -2.5f,
            HitTestVisible = false, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
            // Unscaled decode (no explicit decodePx): buckets to the 300x260 slot's own aspect, same as 0.2.10 — the
            // square decodePx: 512 this replaced forced a 512x512 decode/upload (~3.4x the bytes) for one hero image.
            Children = [Controls.Artwork(url, 300f, 260f, RoundArt(hit.Kind) ? 130f : Radii.Card)],
        };
        var card = new BoxEl
        {
            Height = HeroHeight, MinWidth = 0f, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = open, ZStack = true, Draggable = DragOf(hit), OnFocusChanged = cardFocus,
            Children =
            [
                scheme is { } graded
                    ? new BoxEl
                    {
                        HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Height = HeroHeight,
                        Gradient = new GradientSpec(GradientShape.Radial, 0f,
                        [
                            new GradientStop(0f, Design.Palette.ChromeAccent(graded) with { A = 0.55f }),
                            new GradientStop(0.58f, Design.Palette.ChromeAccent(graded) with { A = 0f }),
                        ])
                        {
                            RadialCenter = new Point2(0.88f, 0.40f), RadialRadius = new Point2(1.2f, 0.8f),
                        },
                    }
                    : new BoxEl { HitTestVisible = false },
                new BoxEl { Direction = 0, MinWidth = 0f, Height = HeroHeight, AlignSelf = FlexAlign.Stretch, Children = [copy, cover] },
            ],
        }.Interactive(Interaction.Card);   // OnClick is set above, so Interactive gives the box the hand (the engine's clickable default)
        return menu is not null && !Controls.IsNullOverlay(ctx.Overlay) ? ContextMenu.Attach(card, ctx.Overlay, menu) : card;
    }

    /// <summary>An outlined capsule — transparent fill, 1px stroke, never filled (parity 18).</summary>
    static Element HeroChip(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
        Fill = ColorF.Transparent, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children = [Design.Type.Eyebrow(text) with { Color = Tok.TextSecondary }],
    };

    // ══ 6. BEST MATCHES, THE PLAYLIST RAIL, THE SONGS GRID (W5, W6, W1) ═════════════════════════════════════════════

    /// <summary>The hits after the Top Result as a width-fitted N×N PagedShelf of hit rows with chevrons and pips.
    /// <paramref name="colsFor"/> is the page's hysteretic column count (<see cref="ColsFor"/> over its own fields).
    /// The shelf sits in a wrapper box: a Key on PagedShelf's component root is ignored (§9 traps).</summary>
    internal static Element BestMatches(IReadOnlyList<HitItem> items, Func<float, int> colsFor, HitContext ctx)
    {
        if (items.Count == 0) return new BoxEl();
        return Stretch(Responsive.Of(w =>
        {
            int cols = colsFor(w);
            int rows = RowsFor(cols);
            int maxCols = MaxColumns(cols, rows, items.Count);
            return PagedShelf.Create(items,
                cardAt: (item, i, _) => HitRow(item.Ref, item.Flags, large: false, in ctx),
                onInvoke: (item, _) => OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the row inside is click-less
                cardHeight: static _ => BestMatchRowH,
                header: TickHeader(Loc.Get(Strings.Search.BestMatches)),
                pager: ShelfPager.Chevrons | ShelfPager.Pips,
                minCardW: BestMatchCellMin, maxCardW: 9999f, gap: BestMatchGap, rows: rows, maxColumns: maxCols,
                snap: ShelfSnap.Page, cardWidthAgnostic: true, edgeFade: 16f,
                prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
                keyOf: static (item, _) => KeyOf(item.Ref));
        }, fallback: 0f));
    }

    /// <summary>A hit's shelf key: kind + slot, stable across hydration (never a data value).</summary>
    internal static string KeyOf(EntityRef hit)
        => ((int)hit.Kind).ToString(CultureInfo.InvariantCulture) + ":" + hit.Slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>The All tab's playlist rail: 148-188 DIP grid cards with a chevron header that SWITCHES to the Playlists
    /// tab (not a navigation, parity 24), never repeating the hero's own item.</summary>
    internal static Element PlaylistRail(IReadOnlyList<HitItem> items, Action? openFacet, HitContext ctx)
    {
        if (items.Count == 0) return new BoxEl();
        return Stretch(PagedShelf.Create(items,
            cardAt: (item, i, cardW) => ShelfCardOf(item.Ref, cardW, in ctx),
            onInvoke: (item, _) => OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the card inside is click-less
            cardHeight: static w => SurfaceGeometry.StackExtent(in s_railCard, w, 1f),   // the card's own shape: one-line subtitle
            header: TickHeader(Loc.Get(Strings.Search.Playlists), openFacet),
            pager: ShelfPager.Chevrons | ShelfPager.Pips,
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax, gap: HomeModuleLayout.ShelfGap,
            // NOT cardWidthAgnostic: the shelf card takes its width literally (its column and cover are sized from it), so
            // it needs the fitted width hint — an agnostic shelf hands every card maxCardW and the cards overrun their cells.
            snap: ShelfSnap.Page, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            keyOf: static (item, _) => KeyOf(item.Ref)));
    }

    /// <summary>The rail card's shape: the shelf card with one caption line — what <c>cardHeight</c> above reserves and the
    /// seed cards of the shimmer draw.</summary>
    static readonly SurfaceShape s_railCard = Shape.Shelf(captionLines: 1, metaLine: false);

    /// <summary>A shelf card for a hit (the rail): the one surface at the shelf's card width. Inside the shelf's slot it
    /// renders click- and focus-less (the slot owns both); the host attaches the menu carried in the data.</summary>
    static Element ShelfCardOf(EntityRef hit, float cardW, in HitContext ctx)
        => Controls.Surface(CardDataOf(hit, ctx.Origin), s_railCard, cardW);

    /// <summary>The surface's data for a hit: title, the owner / artist line, cover, open, play, drag and the context menu
    /// (null for a hit with none — the "…" and the right-click then do not exist).</summary>
    static Controls.CardData CardDataOf(EntityRef hit, Shell.NavOrigin? origin)
    {
        var h = hit;
        var card = new HomeCard(hit);
        string? sub = hit.Kind is EntityKind.Playlist or EntityKind.Collection ? card.OwnerName : card.Subtitle;
        return new Controls.CardData(
            hit.Id.Text, TitleOf(hit),
            sub is { Length: > 0 } ? Design.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f } : null,
            CoverOf(hit), () => OpenHit(h, origin), CanPlay(hit.Kind) ? () => PlayHit(h) : null,
            Circular: RoundArt(hit.Kind), Drag: DragOf(hit)) { Menu = MenuOf(hit) };
    }

    /// <summary>The Songs facet: a wrapping AutoGrid(280, 12) of hit rows scrolling with the page — no pager (W6). Its row
    /// height is the tallest cell (auto): a row that wears the "Lyrics match" eyebrow is 72 where the rest are 64.</summary>
    internal static Element SongsGrid(Search row, in HitContext ctx)
    {
        int n = row.ResultCount;
        var cells = new Element[n];
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            var hit = row.ResultRef(i);
            if (hit.IsNone) continue;
            cells[m++] = Stretch(HitRow(hit, row.FlagsOf(hit), large: false, in ctx));
        }
        if (m < n) Array.Resize(ref cells, m);
        return Stretch(AutoGrid(SongsColMin, Spacing.M, float.NaN, cells));
    }

    // ══ 7. THE ALBUMS / PLAYLISTS FACET GRID (W7) ════════════════════════════════════════════════════════════════════

    /// <summary>The dedicated Albums / Playlists tab: a uniform virtualized card grid over the facet row's OWN edge. The
    /// extent comes from the stated total (capped at <see cref="FacetHitCeiling"/>), so an index whose page has not landed
    /// renders a card-shaped placeholder and scrolling never shifts the rows; the page pages the edge from its demand
    /// (Search.Page.cs), never from the visible window.</summary>
    internal static Element FacetGrid(Search row, HitContext ctx)
        => Responsive.Of(w =>
        {
            float width = w > 0f ? w : HomeModuleLayout.FallbackWidth;
            int cols = FacetGridColumns(width);
            int count = Math.Min(Math.Max(row.ResultCount, row.ResultTotal), FacetHitCeiling);
            var r = row;
            var c = ctx;
            return new VirtualListEl
            {
                ItemCount = count,
                // A square cover + the 50-DIP label chrome; the 4 extra DIP turn the 16 column gap into the 20 row gap.
                ItemLayout = new AspectGridVirtualLayout(cols, 1f, FacetGridChrome + (FacetGridRowGap - FacetGridGap), FacetGridGap),
                RenderItem = i => GridCell(r, i, in c),
                KeyOf = i => r.ResultRef(i) is { IsNone: false } hit ? KeyOf(hit) : "search-card:placeholder:" + i.ToString(CultureInfo.InvariantCulture),
                Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
            } with { Key = "search-facet-grid:" + cols.ToString(CultureInfo.InvariantCulture) };
        }, fallback: HomeModuleLayout.FallbackWidth, grow: 1f);

    /// <summary>One grid cell: the one surface at the grid shape (the host attaches the menu carried in the data) — or, for
    /// an unrealised index, the SAME host on <see cref="Controls.CardData.Seed"/>: exactly a real card's box, so a landing
    /// page shifts nothing and the cell is not a different component type when its hit arrives.</summary>
    static Element GridCell(Search row, int index, in HitContext ctx)
    {
        var hit = row.ResultRef(index);
        return Controls.Surface(hit.IsNone ? Controls.CardData.Seed : CardDataOf(hit, ctx.Origin), Shape.Grid);
    }

    // ══ 8. THE FLAT LISTS: hits, artists (W9, W10) ═══════════════════════════════════════════════════════════════════

    /// <summary>A dedicated facet's hits (Podcasts / Episodes / Profiles) through the ONE row factory, or that facet's own
    /// empty sentence (parity 36).</summary>
    internal static Element HitsList(Search row, SearchFacet facet, in HitContext ctx)
    {
        int n = row.ResultCount;
        var rows = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            var hit = row.ResultRef(i);
            if (!hit.IsNone) rows.Add(HitRow(hit, row.FlagsOf(hit), large: false, in ctx));
        }
        return rows.Count == 0 ? Empty(facet, ctx.Query) : HitColumn(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
    }

    /// <summary>The Artists facet: the hit row, circular art, its subtitle the kind word "Artist" — with the menu, drag and
    /// follow control 0.2.9's thinner row lacked (W10, §9 gap 8).</summary>
    internal static Element ArtistsList(Search row, in HitContext ctx)
    {
        int n = row.ResultCount;
        var rows = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            var hit = row.ResultRef(i);
            if (!hit.IsNone) rows.Add(HitRow(hit, row.FlagsOf(hit), large: false, in ctx));
        }
        return rows.Count == 0 ? Empty(SearchFacet.Artists, ctx.Query) : HitColumn(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
    }

    // ══ 9. GENRES AND RELATED QUERIES (W8, W1) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The eight seed names the All tab's genre section shimmers from (W8) — the SAME Link cells, so the bars are
    /// sized from real text runs.</summary>
    static readonly string[] s_seedGenres = ["Alternative", "Jazz", "Hip-Hop", "Classical", "R&B", "Electronic", "Indie", "Metal"];

    /// <summary>The genre tiles as browse-category LINKS — the directory's own cell, column count and star-grid math, with
    /// every pip the semantic accent (Color null, §0.15). A genre opens its browse page with the search LOOKUP origin.
    /// <paramref name="header"/> false is the dedicated Genres tab; an empty list is an empty box, no sentence (parity 38).</summary>
    internal static Element GenreGrid(Search all, bool header, Shell.NavOrigin? origin)
    {
        var slots = all.GenreSlots;
        if (slots.Length == 0) return new BoxEl();
        var models = new BrowseTileModel[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            var node = new Browse(slots[i]);
            string uri = node.Id.Text;
            string name = Entities.Strings.Resolve(node.TitleId);
            models[i] = new BrowseTileModel(name, uri, null, null, () => OpenGenre(uri, name, origin));
        }
        return GenreBody(models, header);
    }

    /// <summary>The genre section's shimmer: the eight seed names through the same cells.</summary>
    internal static Element GenreSeed()
    {
        var models = new BrowseTileModel[s_seedGenres.Length];
        for (int i = 0; i < models.Length; i++)
            models[i] = new BrowseTileModel(s_seedGenres[i], "seed:genre:" + i.ToString(CultureInfo.InvariantCulture), null, null, static () => { });
        return GenreBody(models, header: true);
    }

    static Element GenreBody(BrowseTileModel[] models, bool header)
    {
        Element body = Responsive.Of(width =>
        {
            int cols = BrowseLayout.LinkColumns(width > 0f ? width : BrowseLayout.DirectoryFallbackWidth);
            var cells = new Element[models.Length];
            for (int i = 0; i < cells.Length; i++) cells[i] = BrowseTiles.Link(models[i]);
            return BrowseLayout.StarGrid(cols, Spacing.M, Spacing.S, cells) with { Key = "search-genres-grid:" + cols.ToString(CultureInfo.InvariantCulture) };
        }, fallback: BrowseLayout.DirectoryFallbackWidth);
        if (!header) return Stretch(body);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [TickHeader(Loc.Get(Strings.Search.Genres)), body],
        };
    }

    /// <summary>Open a genre: its browse page (or a re-committed search on its name), carrying the lookup origin so the
    /// trail reads <c>"q" › Genre</c> with no Browse rung.</summary>
    static void OpenGenre(string uri, string name, Shell.NavOrigin? origin)
    {
        var route = GenreRoute(uri, name);
        if (route.IsNone) return;
        Shell.GoTo(route, route.Kind == Shell.RouteKind.Search ? null : origin);
    }

    /// <summary>The related queries: lowercase links at the 20/28 rung, Weight 400, accent on hover, the typed query
    /// dropped (parity 27-28). Committing one is a new search.</summary>
    internal static Element RelatedQueries(Search all, string typed)
    {
        var related = all.Related;
        var links = new List<Element>(related.Length);
        for (int i = 0; i < related.Length; i++)
        {
            string q = Entities.Strings.Resolve(related[i].Query);
            if (!KeepsRelated(q, typed)) continue;
            string committed = q;
            links.Add(new BoxEl
            {
                Shrink = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
                Key = "rel:" + committed,
                OnClick = () => Shell.GoTo(Shell.Parse("search".AsSpan(), committed.AsSpan())),
                Children =
                [
                    Design.Type.ModuleHeader(committed.ToLowerInvariant()) with
                    {
                        Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary, Weight = 400,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            });
        }
        if (links.Count == 0) return new BoxEl();
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                TickHeader(Loc.Get(Strings.Search.RelatedSearches)),
                new BoxEl { Direction = 0, Gap = Spacing.M, Wrap = true, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = links.ToArray() },
            ],
        };
    }

    // ══ 10. EMPTY AND ERROR (W11, §9 gaps 1, 2, 9) ═══════════════════════════════════════════════════════════════════

    /// <summary>A facet with nothing: its own sentence for Audiobooks / Podcasts / Episodes / Profiles / Authors, an
    /// empty box for Genres, and the ONE captioned generic empty for everything else (gap 1 picks the captioned one).</summary>
    internal static Element Empty(SearchFacet facet, string query)
    {
        if (facet == SearchFacet.Genres) return new BoxEl();
        return EmptyKey(facet) is { } key
            ? Controls.Vacancy(Controls.VacancyVoice.NoMatch, title: Loc.Get(key), subtitle: "")
            : Controls.Vacancy(Controls.VacancyVoice.NoMatch, title: Loc.Get(Strings.Search.NoResults), subtitle: Strings.Search.NoResultsSub(query));
    }

    /// <summary>The facet body's failure: the error vacancy WITH a Retry (gap 9 — 0.2.9's was terminal).</summary>
    internal static Element Failed(Action retry)
        => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: retry);

    /// <summary>A section-level failure on the All tab (the genre section and the related queries alike, gap 2): the
    /// compact error vacancy inside the section's own slot.</summary>
    internal static Element SectionFailed(Action retry)
        => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry);
}

/// <summary>The search hit row's decisions, engine-free so a fact pins each (SearchHitDataTests): which art edge and floor a
/// row has (<see cref="ShapeOf"/>), whether a hit's art shows (<see cref="ShowsArt"/>), whether the row wears the "Lyrics
/// match" eyebrow (<see cref="LyricsEyebrow"/>), which trailing control a kind carries (<see cref="TrailingOf"/>) and
/// which kind is a drag source (<see cref="Drags"/>). Whether a kind is circular, plays or has a menu is
/// <see cref="Search.RoundArt"/> / <see cref="Search.CanPlay"/> / <see cref="Search.HasMenu"/> — already pure rules of the
/// search surface (Search.Rules.cs); the surface itself owns the plate, the cursor, the focus ring and the FAB.</summary>
public static class SearchHitRules
{
    /// <summary>The art square of a search row (the media row's default).</summary>
    public const float RowArt = 48f;

    /// <summary>The surface shape of a hit row: <see cref="Shape.RowLarge"/> when <paramref name="large"/> (the lead row of
    /// the no-top-hits fallback list — 84 art, the 112 floor, the page-hero title), else the 48-art row in the 64 floor.
    /// A TRACK row whose artwork is hidden (<paramref name="artHidden"/>) holds the play-glyph square instead of the art
    /// (<see cref="TrackRowRules.ArtEdge"/>), still in the 64 floor. Every shape carries <see cref="MenuPlacement"/>
    /// = <see cref="RowMenu"/>.</summary>
    public static SurfaceShape ShapeOf(bool large, bool artHidden)
        => (large ? Shape.RowLarge : Shape.Row(TrackRowRules.ArtEdge(RowArt, showArtwork: !artHidden))) with { Menu = RowMenu };

    /// <summary>Where a hit row's "…" goes: its OWN lane after the trailing control
    /// (<see cref="MenuPlacement.TrailingLane"/>), never the overlay over the row's end. Every kind that has a menu
    /// (<see cref="Search.HasMenu"/>) also ends in a control (<see cref="TrailingOf"/>: the heart or the Follow pill), so
    /// the overlay would always land on that control — hiding and blocking the heart, cutting "Follow" to "Fol". The Top
    /// Result card already places its "…" in a slot of its own beside the same control; the rows (search page and the
    /// search flyout alike, <c>OmnibarRowRules.RowShape</c>) now do the same.</summary>
    public const MenuPlacement RowMenu = MenuPlacement.TrailingLane;

    /// <summary>A hit's art shows unless the app hides TRACK artwork and the hit is a track — albums, artists, playlists,
    /// shows, episodes and people keep theirs.</summary>
    public static bool ShowsArt(EntityKind kind, bool hideTrackArt) => kind != EntityKind.Track || !hideTrackArt;

    /// <summary>The eyebrow "Lyrics match" sits over the title of a SMALL row whose hit matched lyrics; the large lead row
    /// leaves it to the Top Result's chip.</summary>
    public static bool LyricsEyebrow(bool large, SearchHitFlags flags) => !large && (flags & SearchHitFlags.MatchedLyrics) != 0;

    /// <summary>The control at the end of a hit row.</summary>
    public enum Trailing : byte { None, Follow, Save }

    /// <summary>Follow for an artist / playlist / show, the heart (Save) for a track / album, none for the rest — and none
    /// for a hit with no uri to act on.</summary>
    public static Trailing TrailingOf(EntityKind kind, bool hasUri)
    {
        if (!hasUri) return Trailing.None;
        return kind switch
        {
            EntityKind.Artist or EntityKind.Playlist or EntityKind.Show => Trailing.Follow,
            EntityKind.Track or EntityKind.Album => Trailing.Save,
            _ => Trailing.None,
        };
    }

    /// <summary>Every hit but a profile (a person with no Wavee resource behind them) and an unresolved one is a drag
    /// source.</summary>
    public static bool Drags(EntityKind kind) => kind is not (EntityKind.User or EntityKind.Unknown);
}
