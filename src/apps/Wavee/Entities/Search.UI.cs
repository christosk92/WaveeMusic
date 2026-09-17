// ── Entities/Search.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the search surface's static factories: the facet row and its 11-pill skeleton, the three loading shapes, the hit row,
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
// Every hit — All-tab list, Best matches cell, Songs cell, Podcasts / Episodes / Profiles lists — is `HitRow`, unplated
// (transparent at rest, the subtle-fill ladder on hover and press), so a podcast row looks identical wherever it came
// from (ch 13 §0.8). The title, art and subtitle read the TARGET row's columns through `HomeCard` (the same live
// handle Home's cards use); the chrome flags (lyrics match, music video) read `Search.FlagsOf`. A TRACK hit gets the
// full track menu and a depositable payload straight off its own row — the 0.2.9 `TrackOf` scan disappears because
// the edge target IS the Track handle (ch 13 §7). Artists are the one deliberately thinner row (W10), and in 0.3 they
// carry the menu, drag and follow control 0.2.9 forgot (§9 gap 8).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Search
{
    /// <summary>What every hit factory needs beyond the hit, resolved ONCE per page render.</summary>
    internal readonly record struct HitContext(string Query, Shell.NavOrigin? Origin, IOverlayService? Overlay, bool HideTrackArt);

    /// <summary>One Best-matches / rail cell: the hit plus its row version and chrome, so the shelf's value snapshot
    /// changes when the row hydrates.</summary>
    internal readonly record struct HitItem(EntityRef Ref, uint Version, SearchHitFlags Flags);

    internal const float FacetUnderlineH = 3f, FacetUnderlineMs = 260f, HeroHeight = 228f;
    internal const float ArtistRowH = 60f, HitRowH = 64f, SongsColMin = 280f;

    // ══ 1. THE FACET ROW (W3, §0.3-§0.5) ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The query echo: the committed query lowercased in the display face, one line, Shrink never Grow (§0.2).</summary>
    internal static Element QueryEcho(string lowered)
        => Design.Type.SurfaceDisplay(lowered) with
        {
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        };

    /// <summary>The eleven placeholder pills at their real widths, each with a transparent underline spacer so the
    /// row's per-tab height matches a real tab (W2, §9 must-not-simplify 3).</summary>
    internal static Element FacetRowSkeleton()
    {
        var widths = ChipSkeletonWidths;
        var tabs = new Element[widths.Length];
        for (int i = 0; i < widths.Length; i++)
            tabs[i] = new BoxEl
            {
                Direction = 1, Shrink = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.XS),
                        Children = [new BoxEl { Width = widths[i], Height = 16f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary }],
                    },
                    new BoxEl { Height = FacetUnderlineH, Shrink = 0f },
                ],
            };
        return new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.End, MinWidth = 0f, Grow = 1f, Children = tabs };
    }

    /// <summary>The real facet row over the All row's chip strip: server rank first, All always first, counts omitted for
    /// All and for 0 (W3). <paramref name="selectors"/> holds one cached click per index (the page owns them).</summary>
    internal static Element FacetRow(Search all, ReadOnlySpan<SearchFacet> facets, int selected, Action[] selectors)
    {
        var tabs = new Element[facets.Length];
        for (int i = 0; i < facets.Length; i++)
        {
            var f = facets[i];
            int count = f == SearchFacet.All ? 0 : FacetCount(all.RawTotalOf(f), LocalCountOf(all, f));
            tabs[i] = FacetTab(Loc.Get(FacetNameKey(f)), count, i == selected, selectors[i]);
        }
        return new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.End, MinWidth = 0f, Grow = 1f, Children = tabs };
    }

    /// <summary>The All row's own list count for a facet: its hits of that kind, the genre tiles for Genres.</summary>
    internal static int LocalCountOf(Search all, SearchFacet f)
    {
        if (f == SearchFacet.Genres) return all.GenreSlots.Length;
        var kind = KindOf(f);
        return kind == EntityKind.Unknown ? 0 : all.CountOf(kind);
    }

    /// <summary>One tab: label + count over a 3-DIP underline that GROWS from its left edge when selected (§0.4). Tabs
    /// are focus stops (§9 gap 3).</summary>
    static Element FacetTab(string name, int total, bool selected, Action onClick)
    {
        Element label = Body(name) with
        {
            Color = selected ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, Shrink = 0f, MaxLines = 1,
        };
        Element[] labelKids = total > 0
            ? [label, Caption(total.ToString(CultureInfo.InvariantCulture)) with { Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, Shrink = 0f, MaxLines = 1 }]
            : [label];
        return new BoxEl
        {
            Direction = 1, Shrink = 0f,
            Role = AutomationRole.Tab, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
            Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                    Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.XS),
                    Children = labelKids,
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
                    : new BoxEl { Height = FacetUnderlineH, Shrink = 0f },
            ],
        };
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

    /// <summary>The facet body's shimmer, in the shape of the facet it stands in for — never one bar stack.</summary>
    internal static Element Shimmer(SearchFacet facet) => ShimmerFor(facet) switch
    {
        ShimmerShape.Hero => ShimmerAll(),
        ShimmerShape.CardGrid => ShimmerCardGrid(),
        _ => ShimmerRows(),
    };

    static Element ShimmerRows()
    {
        var rows = new Element[10];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, Height = HitRowH, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Width = 48f, Height = 48f, Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, Gap = Spacing.XXS, Justify = FlexJustify.Center,
                        Children =
                        [
                            new BoxEl { Width = 220f, Height = 14f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                            new BoxEl { Width = 120f, Height = 12f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleTertiary },
                        ],
                    },
                ],
            };
        return new BoxEl { Direction = 1, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, Children = rows };
    }

    static Element ShimmerCardGrid()
    {
        var cards = new Element[12];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Width = 168f,
                Children =
                [
                    new BoxEl { Width = 168f, Height = 168f, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = 140f, Height = 14f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = 92f, Height = 12f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleTertiary },
                ],
            };
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
        return string.IsNullOrWhiteSpace(detail) ? word : word + " • " + detail;
    }

    /// <summary>Where opening a hit goes: a page for an album / artist / playlist / show (with the search LOOKUP origin, so
    /// the trail reads <c>"q" › X</c>); a track or an episode plays; a profile does nothing (0.2.9 parity).</summary>
    internal static void OpenHit(EntityRef hit, Shell.NavOrigin? origin)
    {
        if (hit.IsNone) return;
        if (hit.Kind is EntityKind.Track or EntityKind.Episode) { PlayHit(hit); return; }
        if (hit.Kind == EntityKind.User) return;
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
        if (hit.IsNone || hit.Kind == EntityKind.User) return null;
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
    {
        if (uri.Length == 0) return null;
        return hit.Kind switch
        {
            EntityKind.Artist or EntityKind.Playlist or EntityKind.Show
                => Embed.Comp(() => new Controls.FollowButton { Uri = uri, Name = name }) with { Key = "follow:" + uri },
            EntityKind.Track or EntityKind.Album
                => Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = name, Glyph = 16f, Box = compact ? 32f : 40f }) with { Key = "save:" + uri },
            _ => null,
        };
    }

    // ══ 4. THE HIT ROW (W9, §3 "Search rows") ════════════════════════════════════════════════════════════════════════

    /// <summary>THE search row. 64 DIP small / 112 large, 48 (large 84) art — circular for people — hidden on a TRACK hit
    /// when the app hides track artwork; eyebrow "Lyrics match" on a small row; the trailing follow / save; unplated.</summary>
    internal static Element HitRow(EntityRef hit, SearchHitFlags flags, bool large, in HitContext ctx)
    {
        if (hit.IsNone) return new BoxEl();
        string uri = hit.Id.Text;
        string title = TitleOf(hit);
        bool round = RoundArt(hit.Kind);
        bool showArt = hit.Kind != EntityKind.Track || !ctx.HideTrackArt;
        float edge = large ? 84f : 48f;
        var h = hit;
        var origin = ctx.Origin;
        Action open = () => OpenHit(h, origin);
        Action? play = CanPlay(hit.Kind) ? () => PlayHit(h) : null;

        var text = new List<Element>(4);
        if (!large && (flags & SearchHitFlags.MatchedLyrics) != 0)
            text.Add(Design.Type.Eyebrow(Loc.Get(Strings.Search.LyricsMatch)) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        text.Add(large
            ? Design.Type.PageHero(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
            : Design.Type.TrackTitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        text.Add(new TextEl(SubtitleOf(hit, flags))
        {
            Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        });

        var kids = new List<Element>(3);
        if (showArt)
            kids.Add(new BoxEl
            {
                ZStack = true, Width = edge, Height = edge, Shrink = 0f, ClipToBounds = !round,
                Children =
                [
                    Controls.Artwork(CoverOf(hit), edge, edge, round ? edge / 2f : (large ? Radii.Card : Radii.Control)),
                    play is null ? new BoxEl() : Controls.NowPlayingOverlay(uri, play, large ? 44f : 30f, centred: true),
                ],
            });
        kids.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = large ? Spacing.S : 2f, Children = text.ToArray() });
        if (TrailingOf(hit, uri, title, compact: true) is { } trailing) kids.Add(trailing);

        var row = new BoxEl
        {
            Direction = 0, Gap = large ? Spacing.L : Spacing.M, AlignItems = FlexAlign.Center,
            MinHeight = large ? 112f : HitRowH, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = large ? new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M) : new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.ControlAll,
            Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
            Cursor = CursorId.Hand, OnClick = open, Draggable = DragOf(hit),
            Children = kids.ToArray(),
        };
        return MenuOf(hit) is { } menu && !Controls.IsNullOverlay(ctx.Overlay) ? ContextMenu.Attach(row, ctx.Overlay, menu) : row;
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
        bool isTrack = hit.Kind == EntityKind.Track;
        Action open = () => OpenHit(h, origin);
        var menu = MenuOf(hit);

        var actions = new List<Element>(4);
        if (CanPlay(hit.Kind)) actions.Add(Controls.Play(accent, () => PlayHit(h)) with { Shrink = 0f });
        if (CanOpen(hit.Kind) && !isTrack)
            actions.Add(Controls.Pill(Loc.Get(Strings.Search.OpenPage), open, ButtonAppearance.Standard) with { Shrink = 0f });
        if (TrailingOf(hit, uri, title, compact: false) is { } trailing) actions.Add(trailing);
        if (menu is not null) actions.Add(Controls.Named(Controls.MoreButton(null, requestsContext: true) with { BlocksDragArm = true }, Loc.Get(Strings.Common.More)));

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
            OnClick = open, ZStack = true, Draggable = DragOf(hit),
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
        }.Interactive(Interaction.Card);
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
            cardHeight: Controls.ShelfHeight,
            header: TickHeader(Loc.Get(Strings.Search.Playlists), openFacet),
            pager: ShelfPager.Chevrons | ShelfPager.Pips,
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax, gap: Spacing.M,
            snap: ShelfSnap.Page, cardWidthAgnostic: true, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            keyOf: static (item, _) => KeyOf(item.Ref)));
    }

    /// <summary>A shelf card for a hit (the rail), with its card menu wrapped around the embed.</summary>
    static Element ShelfCardOf(EntityRef hit, float cardW, in HitContext ctx)
    {
        var data = CardDataOf(hit, ctx.Origin, out var menu);
        Element card = Controls.ShelfCard(data, cardW);
        return menu is not null && !Controls.IsNullOverlay(ctx.Overlay)
            ? ContextMenu.Attach(new BoxEl { Direction = 1, MinWidth = 0f, Children = [card] }, ctx.Overlay, menu)
            : card;
    }

    /// <summary>The card skin's data for a hit: title, the owner / artist line, cover, open, play, drag, menu gate.</summary>
    static Controls.CardData CardDataOf(EntityRef hit, Shell.NavOrigin? origin, out Func<ContextMenuModel?>? menu)
    {
        var h = hit;
        menu = MenuOf(hit);
        var card = new HomeCard(hit);
        string? sub = hit.Kind is EntityKind.Playlist or EntityKind.Collection ? card.OwnerName : card.Subtitle;
        return new Controls.CardData(
            hit.Id.Text, TitleOf(hit),
            sub is { Length: > 0 } ? Design.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f } : null,
            CoverOf(hit), () => OpenHit(h, origin), CanPlay(hit.Kind) ? () => PlayHit(h) : null,
            Circular: RoundArt(hit.Kind), Drag: DragOf(hit), ShowMenu: menu is not null);
    }

    /// <summary>The Songs facet: a wrapping AutoGrid(280, 12, 64) of hit rows scrolling with the page — no pager (W6).</summary>
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
        return Stretch(AutoGrid(SongsColMin, Spacing.M, HitRowH, cells));
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
                Overscan = 2,
                Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
            } with { Key = "search-facet-grid:" + cols.ToString(CultureInfo.InvariantCulture) };
        }, fallback: HomeModuleLayout.FallbackWidth, grow: 1f);

    static Element GridCell(Search row, int index, in HitContext ctx)
    {
        var hit = row.ResultRef(index);
        if (hit.IsNone) return GridPlaceholder();
        var data = CardDataOf(hit, ctx.Origin, out var menu);
        // The menu travels IN the data: GridCard is a component (its cover chrome mounts lazily), and its host attaches
        // the menu to the shell through the overlay in context — the same null-overlay skip this cell used to do by hand.
        return Controls.GridCard(menu is null ? data : data with { Menu = menu });
    }

    /// <summary>An unrealised cell: exactly a real card's box (square + two bars), so a landing page shifts nothing.</summary>
    static Element GridPlaceholder() => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Grow = 1f, MinWidth = 0f,
        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M), Corners = CornerRadius4.All(Radii.Card),
        Children =
        [
            new BoxEl { AspectRatio = 1f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 13f, AlignSelf = FlexAlign.Stretch, MaxWidth = 150f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 11f, Width = 92f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
        ],
    };

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

    /// <summary>The Artists facet: the thinner 60-DIP circular-art rows with a trailing "Artist" capsule (W10) — and, in
    /// 0.3, the menu, drag and follow control 0.2.9's version lacked (§9 gap 8).</summary>
    internal static Element ArtistsList(Search row, in HitContext ctx)
    {
        int n = row.ResultCount;
        var rows = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            var hit = row.ResultRef(i);
            if (!hit.IsNone) rows.Add(ArtistRow(hit, in ctx));
        }
        return rows.Count == 0 ? Empty(SearchFacet.Artists, ctx.Query) : HitColumn(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
    }

    static Element ArtistRow(EntityRef hit, in HitContext ctx)
    {
        string title = TitleOf(hit);
        string uri = hit.Id.Text;
        string word = Loc.Get(Strings.Search.TypeArtist);
        var h = hit;
        var origin = ctx.Origin;
        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                Width = 48f, Height = 48f, Shrink = 0f, Corners = CornerRadius4.All(24f), ClipToBounds = true,
                Children = [Controls.Artwork(CoverOf(hit), 48f, 48f, 24f)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(word) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
            new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary,
                Children = [Design.Type.Eyebrow(word) with { Color = Tok.TextTertiary }],
            },
        };
        if (TrailingOf(hit, uri, title, compact: true) is { } follow) kids.Add(follow);
        var row = new BoxEl
        {
            Direction = 0, Height = ArtistRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
            Cursor = CursorId.Hand, OnClick = () => OpenHit(h, origin), Draggable = DragOf(hit),
            Children = kids.ToArray(),
        };
        return MenuOf(hit) is { } menu && !Controls.IsNullOverlay(ctx.Overlay) ? ContextMenu.Attach(row, ctx.Overlay, menu) : row;
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
