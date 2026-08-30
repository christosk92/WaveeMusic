using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Reusable media cards composed from existing primitives. SIZE-REACTIVE: a Shelf card is built at a caller-supplied
// dynamic width (PagedShelf measures the viewport and fills it with equal cards). Every clamped label gets an EXPLICIT
// width (column context) or Grow+Basis=0 (row context) so text NEVER contributes its full single-line width to parent
// measure (Element.cs:443) — that is what made text bleed out of cards and pushed grids past the viewport edge.
public static class MediaCard
{
    public const float QuickW = 64f;     // quick-pick cover edge
    public const float QuickH = 64f;     // quick-pick tile height
    internal const float ShelfDecodePx = 256f;    // stable across responsive card widths, avoids resize-time redecodes
    internal const float FabSize  = 44f;
    internal const float FabInset = 8f;
    internal const float Pad      = Spacing.S;

    // Every value that identifies or invokes the card rides the PROPS channel, not the factory closure. These card
    // builders are STATIC element factories and virtualized parents deliberately reuse their ComponentEl slots: a
    // ctor-captured uri/onPlay would therefore keep pointing at the slot's FIRST row even after its title/art had been
    // rebound (the visible-row/played-row split on Recents). Re-pushed props preserve the component instance while
    // keeping hover, playback identity, geometry and callbacks coherent with the row currently occupying the slot.
    internal static Element LazyOverlay(Signal<bool> hovered, string uri, Action onPlay, float fab, bool cover, float inner,
                               Action? onNavigate = null, bool centered = false)
        => Embed.Comp(new LazyNowPlayingOverlay.Props(
                          hovered, uri, onPlay, fab, cover, inner, onNavigate, centered),
                      static () => new LazyNowPlayingOverlay())
            .Skeletonized(false);

    /// <summary>Wide Home destination used by the concert feature. It keeps one responsive layered tree and avoids the
    /// stateful portrait editorial card's image zoom, acrylic, and shelf-specific clipping behavior.</summary>
    public static Element WideEditorialDestination(Image? artwork, string eyebrow, string title, string subtitle,
        string actionLabel, Action onClick, float fallbackWidth = 1000f) =>
        ConcertUi.WideEditorialDestination(artwork, eyebrow, title, subtitle, actionLabel, onClick, fallbackWidth);

    static ColorF AccentCardFill(ColorF? accent) =>
        accent is { } a
            ? ColorF.Lerp(Tok.FillCardDefault, a, Tok.Theme == ThemeKind.Dark ? 0.12f : 0.08f)
            : Tok.FillCardDefault;

    static ColorF AccentCardHoverFill(ColorF? accent) =>
        accent is { } a
            ? ColorF.Lerp(Tok.FillControlSecondary, a, Tok.Theme == ThemeKind.Dark ? 0.18f : 0.12f)
            : Tok.FillControlSecondary;

    // ── The one hover-plate card shell: resting-borderless content over a hover-revealed plate (fill + 1px stroke +
    // elevation halo) with the shared lift motion. Every rectangular media card composes THIS instead of hand-rolling
    // its own plate, so the hover grammar can't drift between surfaces. NO ClipToBounds: an element's own shadow
    // escapes its own clip, but a PARENT clip shaves a child's halo (SceneRecorder's shadow-before-own-clip contract)
    // — the plate carries the shadow and every child self-clips. HoverElevatePaint: paint above sibling cards while
    // hovered so the lift halo isn't overpainted by a later card (the design's z-index:2); layout/hit-testing
    // unchanged. The returned box is the card ROOT — callers may still override Grow / Height / Border / Fill for
    // owner states (e.g. the discography drawer owner's accent border).
    /// <summary>The application-wide card motion contract. Home's authored skins call this too, so changing the card
    /// lift in one place cannot leave Search/Library and Home with different physics.</summary>
    internal static BoxEl ApplyCardPhysics(BoxEl card) => card with
    {
        HoverElevatePaint = true,
        WhileHover = new MotionTarget { OffsetY = -4f },
        WhilePressed = new MotionTarget { Scale = 0.99f, OffsetY = -1f },
        Transition = MotionTok.ControlNormal,
    };

    internal static BoxEl CardShell(Element content, Action onClick, ColorF? plateFill = null, bool persistent = false)
        => ApplyCardPhysics(new BoxEl
    {
        ZStack = true, Corners = CornerRadius4.All(Radii.Card),
        OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Grow = 1f, Corners = CornerRadius4.All(Radii.Card),
                Fill = plateFill ?? Tok.FillCardDefault,
                HoverFill = persistent ? Tok.FillCardSecondary : default,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                // Elevation.Card on BOTH legs. The hover plate used to lift to Elevation.CardHover (blur 16 dark) —
                // the same blur band as Elevation.Flyout/Tooltip — so a merely-hovered card claimed a popup-class
                // shadow and out-shouted real flyouts sitting above it. A card is a card hovered or not: the −4px
                // lift and the plate reveal carry the state change; the shadow band does not have to.
                Shadow = Elevation.Card,
                Opacity = persistent ? 1f : 0f, HoverOpacity = 1f,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs,
                HoverEasing = MotionTok.ControlFaster.Easing,
                HitTestVisible = false,
            },
            content,
        ],
    });

    // Hover-revealed corner "…" (top-right of the cover — the FAB's opposite corner): opens the card's attached context
    // menu (the WithMenu at the card root) anchored at the button — the engine's ClickRequestsContext re-enters the
    // context-request funnel here and the walk finds the card's OnContextRequested. Same dark-glass chrome as
    // CoverActionFab; hover-revealed like the play FAB. Rendered only when the card actually carries a menu.
    // Skeletonized(false): a hover-only affordance is not skeleton content (the NowPlayingOverlay rule).
    internal static Element MoreCorner(bool show, bool persistent = false) => show
        ? new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.End,
            Padding = new Edges4(0f, FabInset, FabInset, 0f),
            Opacity = persistent ? 1f : 0f, HoverOpacity = 1f, HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
            Children =
            [
                new BoxEl
                {
                    Width = 30f, Height = 30f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = Radii.Circle(30f),
                    Fill = WaveeOnMedia.ScrimRest,
                    HoverFill = WaveeOnMedia.ScrimHover,
                    PressedFill = WaveeOnMedia.ScrimPressed,
                    BorderWidth = 1f, BorderColor = WaveeOnMedia.Stroke,
                    Shadow = Elevation.Card,
                    HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
                    ClickRequestsContext = true, Cursor = CursorId.Hand, Role = AutomationRole.Button,
                    // Pressing the "…" opens the menu; it must never double as a handle for dragging the card.
                    BlocksDragArm = true,
                    Children = [ FabGlyph(Icons.More, 13f, WaveeOnMedia.Ink) ],
                },
            ],
        }.Skeletonized(false)
        : new BoxEl();

    static Element MoreInline(bool show, bool onDark = false, float size = 36f) => show
        ? new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(size),
            Fill = onDark ? WaveeOnMedia.ScrimRest : ColorF.Transparent,
            HoverFill = onDark ? WaveeOnMedia.ScrimHover : Tok.FillSubtleSecondary,
            PressedFill = onDark ? WaveeOnMedia.ScrimPressed : Tok.FillSubtleTertiary,
            BorderWidth = onDark ? 1f : 0f,
            BorderColor = onDark ? WaveeOnMedia.Stroke : ColorF.Transparent,
            ClickRequestsContext = true, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            BlocksDragArm = true,   // its own affordance — see MoreCorner
            Children = [ FabGlyph(Icons.More, 15f, onDark ? WaveeOnMedia.Ink : Tok.TextSecondary) ],
        }.Skeletonized(false)
        : new BoxEl();

    static Element KindChip(HomeCardKind kind)
    {
        string label = kind switch
        {
            HomeCardKind.Track => Loc.Get(Strings.Search.TypeSong),
            HomeCardKind.Artist => Loc.Get(Strings.Search.TypeArtist),
            HomeCardKind.Album => Loc.Get(Strings.Search.TypeAlbum),
            _ => Loc.Get(Strings.Search.TypePlaylist),
        };
        return new BoxEl
        {
            // Capsule by construction (Radii.Full clamps to half the box) rather than the hand-picked 12 that HAPPENED
            // to be half of the old chip height — so the chip stays a capsule when its type or padding moves again.
            Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
            Fill = WaveeOnMedia.ScrimRest, BorderWidth = 1f,
            BorderColor = WaveeOnMedia.Stroke, HitTestVisible = false,
            Children = [ WaveeType.Eyebrow(label) with { Color = WaveeOnMedia.Ink } ],
        };
    }

    static Element ArtworkOrLiked(Image? cover, string uri, float width, float height, float radius, string? morphKey = null, int decodePx = 0, Element? diagnostics = null)
    {
        // The DYNAMIC liked cover, never the picker: a card is not the place to change a collection's artwork, and
        // below 140 DIP the component collapses to the flat 2x2 collection mosaic (or the stock PNG) by itself.
        // The URI decides, and the caller's `cover` is deliberately not consulted — see LikedSongsArtwork.For.
        var art = LikedSongsArtwork.For(uri, width, height, radius, morphKey)
            ?? Surfaces.Artwork(cover, Seed(uri), width, height, radius, morphKey, decodePx);
        return diagnostics is null
            ? art
            : new BoxEl
            {
                Width = width,
                Height = height,
                ZStack = true,
                ClipToBounds = true,
                Children = [ art, diagnostics ],
            };
    }

    // ── Shelf card: square (album/playlist) or circular (artist) cover, sized to fill `cardW`. ───────────
    // `menu` (all five factories): an optional attached context menu (right-click / Menu key / long-press) — the
    // calling component resolves the overlay service + builds the lazy model (Menus.CardAttach); null = no menu.
    // `drag` (all eight factories, the same seam shape as `menu`): the card as a DRAG SOURCE. The caller supplies it
    // because only the call site knows which entity the card stands for — a card factory sees a uri and a title, never
    // "this is an album". It attaches to the card ITSELF, never to a padding/gutter wrapper: the wrapper is the shelf's
    // spacing, and lifting it would drag a rectangle of empty margin. Null = the card is not draggable (the default,
    // so every unconverted call site is byte-identical).
    public static Element Shelf(Image? cover, string title, string subtitle, string uri,
                                Action onClick, Action onPlay, float cardW, bool circular = false, string? morphKey = null,
                                Action<string>? onNavUri = null, MenuAttach? menu = null, DragSource? drag = null)
        // Component owns a mount-stable hovered signal so LazyNowPlayingOverlay props stay reference-equal across
        // parent re-renders (a fresh Signal per static-factory call was defeating the overlay's equality gate).
        => Embed.Comp(new ShelfCard.Props(cover, title, subtitle, uri, onClick, onPlay, cardW, circular, morphKey,
                                          onNavUri, menu, drag),
                      () => new ShelfCard());

    /// <summary>The fixed cross extent allocated by a virtualized shelf for its maximum two-line text shape:
    /// 6 outer gutter + 20 plate padding + (cardW - 16) cover + 8 content gap + 20 title + 2 text gap + 32 subtitle.
    /// Home passes this same delegate to the shelf and its initial extent table.</summary>
    internal static float ShelfHeight(float cardW) => cardW + 72f;

    // ── Grid card: fills the grid cell width (no cardW), square or circular cover. For AutoGrid/UniformGrid cells. ──
    // Mirrors the Shelf card but is width-AGNOSTIC: the cover fills the cell (Surfaces.ArtworkFill, CSS aspect-ratio 1)
    // and the labels truncate to the engine-measured slot width (the proven NavCardContent pattern) — so it drops into a
    // responsive grid whose track width isn't known at template time.
    /// <remarks>The cover placeholder resolves its colour from <c>CoverColorPlane</c> inside <c>Surfaces</c> — a grid
    /// is where that matters most, since a whole screen of covers decodes at once.</remarks>
    /// <param name="titleLines">How many lines the title may wrap to. 1 (the default) keeps every existing grid on its
    /// single ellipsized line; &gt;1 wraps, and the CALLER must widen its cell reserve to match
    /// (<c>HomeModuleLayout.GridCardChromeFor</c>) or the extra lines land under the next row.</param>
    public static Element GridCard(Image? cover, string title, string subtitle, string uri,
                                   Action onClick, Action onPlay, bool circular = false, Action? onNavigate = null,
                                   ColorF? accent = null, MenuAttach? menu = null, DragSource? drag = null,
                                   int matchStart = 0, int matchLen = 0, int titleLines = 1)
    {
        var hovered = new Signal<bool>(false);
        float r = circular ? Radii.Full : Radii.Card;
        var coverStack = new BoxEl
        {
            // Surfaces.ArtworkFill owns the circular image crop. Keep the overlay layer rectangular so artist FABs and
            // the corner menu are not clipped by the avatar circle.
            ZStack = true, ClipToBounds = !circular, Corners = CornerRadius4.All(r),
            Children =
            [
                // The liked collection takes the user's treatment here too. This cell is width-agnostic, so it goes
                // through the responsive arm rather than Fitted — see LikedSongsArtwork.Fill. Before it, a drill-in
                // from "Jump back in" painted Liked Songs as a bare tinted square (ArtworkFill's null path), i.e. not
                // even the stock PNG, two clicks away from the quick tile that showed the full treatment.
                LikedSongsArtwork.Fill(uri, r) ?? Surfaces.ArtworkFill(cover, r),
                LazyOverlay(hovered, uri, onPlay, FabSize, cover: true, 0f, onNavigate),
                MoreCorner(menu is not null),
            ],
        };
        // A grid row may reserve trailing space as its vertical gutter. Do not flex-grow into that space: doing so
        // stretches the card past its square-cover + two-label geometry, creates a dead footer, and consumes the
        // intended gap before the next row.
        var content = new BoxEl
        {
            Direction = 1, Gap = Pad,
            Padding = new Edges4(Pad, Pad, Pad, Spacing.M),
            Children =
            [
                coverStack,
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XXS, AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
                    Children =
                    [
                        // The highlight gets the SAME line budget as the plain title, so a filter narrowing the grid
                        // does not re-flow a card from two lines to one ellipsized one (SearchHighlight.Row wraps its
                        // run row rather than shaping a paragraph — see its maxLines doc for why).
                        matchLen > 0
                            ? SearchHighlight.Row(title, matchStart, matchLen, 14f, 600, Tok.TextPrimary,
                                                  maxLines: titleLines < 1 ? 1 : titleLines)
                            : WaveeType.TrackTitle(title) with
                              {
                                  Wrap = titleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap,
                                  MaxLines = titleLines < 1 ? 1 : titleLines,
                                  Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                              },
                        subtitle.Length == 0 ? new BoxEl()
                            : WaveeType.TrackMeta(subtitle) with { Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
            ],
        };
        // Keep the hover surface neutral. The cover already carries the release palette; tinting the whole plate muddies
        // saturated artwork colours. The expanded drawer owner still gets its explicit accent border from the caller.
        return (CardShell(content, onClick) with
        {
            Draggable = drag,
            OnPointerMoveWithin = _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
        }).WithMenu(menu);
    }

    // ── Artist Pick: the "type on tone" panel (D). Quote leads on an accent-soft wash, photograph follows, the
    // record(s) are footer rows. Photography is a field (no overlays, no Acrylic) — shown ONLY when a real wide image
    // exists (the pin's authored campaign art, else the artist's header banner). The old blurred-square stand-in of
    // the cover/avatar is gone on purpose: on this tone panel the wash carries identity when there is no wide art.
    // Do not reintroduce overlays here, Acrylic included — that is still chrome on the photograph.
    //
    // ONE record per panel: an upcoming release that is not the pin never rides this card as a second footer row
    // (tried, rejected — two stacked capsule rows read as a mini list, not a pick). When both exist, the upcoming
    // announcement takes its own full-width band above Latest release (ArtistPage.cs Body → UpcomingCard).
    const float PickPhotoHeight = 150f;    // rail photograph band; the column supplies the width
    const float PickPhotoColumnW = 300f;   // horizontal arm: photography as a right column, stretched to panel height

    /// <summary>The artist-authored pinned item as one tone panel: accent-soft wash, the artist speaking, optional
    /// wide photograph, then the pinned record as a footer row. Photography is a field — no scrims, pills, FABs, or
    /// on-media ink. Nested Play / Pre-save clicks win over the panel's navigate, same contract as PlayFab inside
    /// CardShell.</summary>
    public static Element ArtistPick(PinnedItem pinned, string artistName, Image? artistImage, Image? artistBackground,
                                     Action onClick, Action onPlay, Func<ColorF> accent,
                                     bool horizontal = false, DragSource? drag = null)
    {
        ColorF accentColor = accent();   // build-time read: wash, date ink, and the S1 Play capsule
        Image? background = pinned.BackgroundImage?.Url is { Length: > 0 }
            ? pinned.BackgroundImage
            : artistBackground?.Url is { Length: > 0 } ? artistBackground : null;

        // ── Wash: the engine's accent-subtle rung (Tok.AccentSubtle alpha = 0.16), not a new alpha ladder.
        var wash = new BoxEl
        {
            Grow = 1f, HitTestVisible = false,
            Gradient = GradientDown(
                new GradientStop(0f, accentColor with { A = 0.16f }),
                new GradientStop(0.55f, accentColor with { A = 0.05f }),
                new GradientStop(0.85f, accentColor with { A = 0f })),
        };

        Element head = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, 0f),
            Children =
            [
                PersonPicture.Create("", 32f, displayName: artistName, imageSourcePath: artistImage?.Url) with
                {
                    BorderColor = Tok.StrokeCardDefault, Shrink = 0f,
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Grow = 1f, Basis = 0f,
                    Children =
                    [
                        Ui.BodyStrong(artistName) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        Ui.Caption(Loc.Get(Strings.Artist.ArtistPick)) with
                        {
                            Color = Tok.TextSecondary, MaxLines = 1,
                        },
                    ],
                },
            ],
        };

        string comment = pinned.Comment.Length > 0 ? pinned.Comment : pinned.Eyebrow;
        Element quote = new BoxEl
        {
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.L),
            Grow = horizontal ? 1f : 0f,   // featured arm: quote eats leftover height so the foot rows sit on the bottom
            Children =
            [
                WaveeType.PickQuote(comment) with
                {
                    Wrap = TextWrap.Wrap, MaxLines = 4, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };

        // Photography only when a real wide image exists. Square corners — the panel clips. The band is a ZStack:
        // that is what gives the stretch-fitted aspect image its extent (a plain flex box hands it no box at all
        // and the placeholder renders forever — the pre-rework full-bleed hero used the same ZStack idiom).
        Element? photo = background is { Url: { Length: > 0 } bgUrl }
            ? new BoxEl
            {
                Height = horizontal ? float.NaN : PickPhotoHeight,
                Width = horizontal ? PickPhotoColumnW : float.NaN,
                Shrink = 0f,
                AlignSelf = horizontal ? FlexAlign.Stretch : FlexAlign.Auto,
                ZStack = true, ClipToBounds = true,
                Children =
                [
                    Ui.Image(bgUrl, ImageFit.Cover, aspect: 1.6f, decodePx: 640, corners: 0f,
                        placeholder: Surfaces.ArtworkPlaceholder, blurHash: background.BlurHash) with
                    {
                        AlignSelf = FlexAlign.Stretch,
                        JustifySelf = FlexAlign.Stretch,
                    },
                ],
            }
            : null;

        // S1 Play / S2 Pre-save. PROPS FREEZE AT MOUNT: the embed is keyed on the uri because this panel is rebuilt
        // whenever extras arrive, and an unkeyed PreSaveButton would keep resolving (and pre-saving) whichever uri
        // happened to be current at mount — the UpcomingMasthead contract.
        Element pinnedTrailing = pinned.IsUpcoming
            ? Embed.Comp(() => new PreSaveButton { Uri = pinned.TargetUri, Name = pinned.Title, Accent = accent })
                with { Key = "pick-presave:" + pinned.TargetUri }
            : WaveeCta.Play(accentColor, onPlay);
        Element pinnedRow = PickFootRow(pinned.Cover, pinned.Uri, pinned.Title, pinned.Subtitle,
            accentColor, pinned.ReleaseAt, pinnedTrailing);

        Element copy = new BoxEl
        {
            Direction = 1,
            Grow = horizontal ? 1f : 0f,
            Basis = horizontal ? 0f : float.NaN,
            MinWidth = horizontal ? 0f : float.NaN,
            Children = !horizontal && photo is not null
                ? [head, quote, photo, pinnedRow]
                : [head, quote, pinnedRow],
        };
        Element content = horizontal && photo is not null
            ? new BoxEl { Direction = 0, Children = [copy, photo] }
            : copy;

        return ApplyCardPhysics(new BoxEl
        {
            ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Shadow = Elevation.Card, OnClick = onClick, Cursor = CursorId.Hand, Draggable = drag,
            Children = [wash, content],
        });
    }

    // The pinned record's footer grammar (S1 Play / S2 Pre-save): 44 cover · title · kind (+ accent date when one
    // exists) · trailing capsule.
    static Element PickFootRow(Image? cover, string uri, string title, string kind,
                               ColorF accentColor, DateTimeOffset? releaseAt, Element trailing)
    {
        Element metaKind = WaveeType.TrackMeta(kind) with
        {
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, Shrink = 0f, ClipToBounds = true,
                    Corners = Radii.ControlAll,
                    Children = [Surfaces.Artwork(cover, Seed(uri), 44f, 44f, Radii.Control, decodePx: 96)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        WaveeType.TrackTitle(title) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                            Children = releaseAt is { } date
                                ? [
                                    metaKind,
                                    Ui.Caption(Strings.Detail.ReleasesOn(DetailFormat.ShortDate(date))) with
                                    {
                                        Color = accentColor, Weight = 600, MaxLines = 1,
                                    },
                                ]
                                : [metaKind],
                        },
                    ],
                },
                new BoxEl { Shrink = 0f, Children = [trailing] }.Skeletonized(false),
            ],
        };
    }

    // Editorial home card: intentionally reserved for HomeFeedBaselineSectionData. Normal home sections keep the regular
    // Shelf card; this is the periodic Apple-Music-style visual interruption. Structure mirrors Apple Music's editorial
    // card: FULL-BLEED portrait artwork with a frosted-glass band pinned to the bottom carrying eyebrow + title + subtitle
    // OVER the art. The band is an acrylic backdrop-blur sized to just the copy (lower third) — not half the card — so the
    // artwork stays the hero. (The engine acrylic composite now clips to the scroll viewport, so this band no longer bleeds
    // over the pinned top nav / player bar; that was the earlier overlap bug, not a reason to drop the frosted look.)
    //
    // Stateful (EditorialCardCore): hover zooms the ARTWORK inside the card's own rounded clip (a root HoverScale pushed
    // the outermost shelf cards past the viewport's exact-bounds clip — squared corners), expands the description, and —
    // after a swept countdown ring — peeks the recommendation's preview tracks (previewsOf, the feedBaselineLookup cache).
    // Component props freeze at mount. Identity is the component key; width changes flow through the responsive shelf's
    // retained layout and no longer remount the entire editorial subtree in 16px buckets.
    // `drag` crosses the ComponentEl boundary as a frozen ctor field ON PURPOSE: a DragSource is gesture-COLD config
    // (a kind string + a payload FACTORY that runs once at promotion), so freezing it at mount freezes nothing live —
    // the factory closure reads the app's state when the drag actually starts. That is the component-props contract's
    // "config, not data" case, unlike the hover signal above.
    public static Element EditorialCard(Image? cover, string? eyebrow, string title, string subtitle, string uri, HomeCardKind kind,
                                        Action onClick, Action onPlay, float cardW, MenuAttach? menu = null,
                                        Func<string, IReadOnlyList<HomePreviewTrack>?>? previewsOf = null,
                                        IReadSignal<int>? previewsEpoch = null,
                                        DragSource? drag = null)
        => Embed.Comp(() => new EditorialCardCore(cover, eyebrow, title, subtitle, uri, kind, onClick, onPlay, cardW, menu,
                                                  previewsOf, previewsEpoch, drag))
           with
           {
               Key = $"edcard:{uri}",
               // The deriver can't see into the component — hand it the resting card shape (no hover, no peek).
               SkeletonProxy = () => EditorialCardCore.Build(cover, eyebrow, title, subtitle, uri, kind, onClick, onPlay,
                   MathF.Min(cardW, 360f), menu, hovered: false, peek: null, counting: false,
                   arcCapture: null, spotlightCenter: new Point2(0.5f, 0.35f), pointerMove: null, pointerExit: null,
                   drag: null),   // a SKELETON is not a drag source: there is no entity behind it yet
           };

    // The stateful editorial-card core. Hover choreography (every channel animated — no snaps):
    //   • the artwork zooms 1.045 INSIDE the card's rounded clip (rides the card's inherited hover progress);
    //   • the description expands 2 → 5 lines while the frosted band grows to make space (CardResizeHeight reflow);
    //   • a thin countdown ring sweeps once (StrokeTrimEnd 0→1); when it completes — and the preview batch is cached —
    //     the description swaps to the recommendation's preview tracks, each row fading in (PageFade enter).
    // Pointer exit rewinds everything; the countdown is epoch-guarded so a quick re-hover never resurrects a stale peek.
    internal sealed class EditorialCardCore : Component
    {
        const int CountdownMs = 1400;
        const int PeekRows = 5;

        readonly Image? _cover; readonly string? _eyebrow; readonly string _title; readonly string _subtitle;
        readonly string _uri; readonly HomeCardKind _kind; readonly Action _onClick; readonly Action _onPlay; readonly float _cardW;
        readonly MenuAttach? _menu;
        readonly DragSource? _drag;
        readonly Func<string, IReadOnlyList<HomePreviewTrack>?>? _previewsOf;
        readonly IReadSignal<int>? _previewsEpoch;

        readonly Signal<bool> _hovered = new(false);
        readonly Signal<bool> _revealed = new(false);
        readonly Signal<Point2> _spotlightCenter = new(new Point2(0.5f, 0.35f));
        int _hoverEpoch;                                   // bumped on every hover edge — abandons stale countdown tails
        NodeHandle _arcNode = NodeHandle.Null;
        float _liveCardW;

        public EditorialCardCore(Image? cover, string? eyebrow, string title, string subtitle, string uri, HomeCardKind kind,
                                 Action onClick, Action onPlay, float cardW, MenuAttach? menu,
                                 Func<string, IReadOnlyList<HomePreviewTrack>?>? previewsOf, IReadSignal<int>? previewsEpoch,
                                 DragSource? drag)
        {
            _cover = cover; _eyebrow = eyebrow; _title = title; _subtitle = subtitle; _uri = uri; _kind = kind;
            _onClick = onClick; _onPlay = onPlay; _cardW = cardW; _menu = menu; _drag = drag;
            _previewsOf = previewsOf; _previewsEpoch = previewsEpoch;
            _liveCardW = cardW;
        }

        void HoverStart()
        {
            if (_hovered.Peek()) return;
            _hovered.Value = true;
            int epoch = ++_hoverEpoch;
            _ = RevealAfterCountdownAsync(epoch);
        }

        async Task RevealAfterCountdownAsync(int epoch)
        {
            await Task.Delay(CountdownMs).ConfigureAwait(false);
            if (epoch == _hoverEpoch && _hovered.Peek() && !_revealed.Peek()) _revealed.Value = true;
        }

        void HoverEnd()
        {
            _hoverEpoch++;
            if (_hovered.Peek()) _hovered.Value = false;
            if (_revealed.Peek()) _revealed.Value = false;
        }

        void PointerMove(Point2 local)
        {
            HoverStart();
            if (Motion.ReducedMotion) return;
            float w = MathF.Max(1f, _liveCardW);
            float h = MathF.Max(360f, _liveCardW * 1.25f);
            _spotlightCenter.Value = new Point2(Math.Clamp(local.X / w, 0f, 1f), Math.Clamp(local.Y / h, 0f, 1f));
        }

        public override Element Render()
        {
            bool hovered = _hovered.Value;
            bool revealed = _revealed.Value;
            _ = _previewsEpoch?.Value;                    // subscribe: re-render the moment the preview batch lands
            var previews = _previewsOf?.Invoke(_uri);
            bool hasPeek = previews is { Count: > 0 };
            bool counting = hovered && !revealed && hasPeek;

            // Sweep the countdown ring exactly once per hover: the ring child mounts with `counting`, its OnRealized
            // captures the arc node, and this post-commit effect seeds the one-shot trim (the ProgressRing pattern —
            // UseKeyframes only targets the host node, so a child arc is driven through Context.Anim directly).
            UseLayoutEffect(() =>
            {
                if (!counting || Motion.ReducedMotion) return;
                var anim = Context.Anim; var scene = Context.Scene;
                var arc = _arcNode;
                if (anim is null || scene is null || arc.IsNull || !scene.IsLive(arc)) return;
                anim.Keyframes(arc, AnimChannel.StrokeTrimEnd,
                    new Keyframe[] { new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.Linear) }, CountdownMs, false);
            }, (_hoverEpoch, counting));

            // ResponsiveBox owns the live fitted width. Its retained delegate reads the same state signals, so hover /
            // preview changes and shelf refits rebuild only this card's content without remounting EditorialCardCore.
            return Responsive.Of(BuildAtWidth, fallback: _cardW);
        }

        Element BuildAtWidth(float cardW)
        {
            _liveCardW = cardW;
            bool hovered = _hovered.Value;
            bool revealed = _revealed.Value;
            _ = _previewsEpoch?.Value;
            var previews = _previewsOf?.Invoke(_uri);
            bool hasPeek = previews is { Count: > 0 };
            bool counting = hovered && !revealed && hasPeek;
            return Build(_cover, _eyebrow, _title, _subtitle, _uri, _kind, _onClick, _onPlay, cardW, _menu,
                hovered, revealed && hasPeek ? previews : null, counting,
                arcCapture: h => _arcNode = h, spotlightCenter: Prop<Point2>.FromSignal(_spotlightCenter),
                pointerMove: PointerMove, pointerExit: HoverEnd, drag: _drag);
        }

        internal static Element Build(Image? cover, string? eyebrow, string title, string subtitle, string uri, HomeCardKind kind,
                                      Action onClick, Action onPlay, float cardW, MenuAttach? menu,
                                      bool hovered, IReadOnlyList<HomePreviewTrack>? peek, bool counting,
                                      Action<NodeHandle>? arcCapture, Prop<Point2> spotlightCenter,
                                      Action<Point2>? pointerMove, Action? pointerExit, DragSource? drag)
        {
            float artH = MathF.Max(360f, cardW * 1.25f);
            float aspect = cardW / artH;
            float inset = Math.Clamp(cardW * 0.055f, Spacing.L, Spacing.XL);
            // Empty frosted space above the copy the feather ramps across. PROPORTIONAL to the art (≈ a third of the
            // card), not a fixed 52px: a fixed pad on a tall editorial card left a short, abrupt wash — the frost has to
            // own the lower third of the artwork for the dissolve to read as gradual (the Apple editorial gradient zone).
            // The old `editorialScale = 1.25f` multiplier is GONE: it existed to scale the copy off the type ramp
            // (12.5/17/13 × 1.25), which is exactly the bypass this file no longer has. The dissolve zone it also scaled
            // is preserved as its own proportion (0.24 × 1.25 = 0.30) with the bounds snapped to the 4-grid.
            float featherPad = Math.Clamp(artH * 0.30f, 88f, 248f);
            float textW = MathF.Max(32f, cardW - 2f * inset);
            const float radius = Radii.Card;
            bool showCountdown = counting && !Motion.ReducedMotion;

            var copy = new List<Element>(5);
            // Eyebrow row also hosts the countdown ring (trailing) so the sweep reads as part of the copy header.
            if (eyebrow is { Length: > 0 } || showCountdown)
                copy.Add(new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Width = textW, HitTestPassThrough = true,
                    Children =
                    [
                        eyebrow is { Length: > 0 }
                            ? WaveeType.Eyebrow(eyebrow) with
                            {
                                Color = WaveeOnMedia.InkSecondary,
                                Grow = 1f, Basis = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                            }
                            : new BoxEl { Grow = 1f },
                        showCountdown ? CountdownRing(arcCapture) : new BoxEl(),
                    ],
                });
            copy.Add(Ui.Subtitle(title) with
            {
                Color = WaveeOnMedia.Ink, Width = textW,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            });
            if (peek is not null)
            {
                // The preview-track peek replaces the description: up to five compact cover+name rows, each fading in
                // (PageFade enter), the container staggering them so the list cascades rather than popping at once.
                // The card itself has a fixed portrait height, so smaller fitted cards cannot safely hold all five rows
                // plus the title and actions. Reserve the complete action/footer geometry first and fit only whole rows
                // in the remainder; this keeps both buttons inside the rounded bottom clip at every shelf width.
                // 116 = the reserved eyebrow row (18) + title (28) + the action row (8 pad + 48 FAB) + three 4px stack
                // gaps. It survived the type convergence unchanged: the title grew 21→28 while the eyebrow shrank
                // 21→18 and the ring now sets that row's height.
                float rowBudget = MathF.Max(36f, artH - featherPad - inset - 116f);
                // Whole rows only: (budget + one gap) / (row height + one gap), with the row now 40 art + 8 gap.
                int fittingRows = Math.Clamp((int)MathF.Floor((rowBudget + Spacing.S) / (WaveeSize.Thumb40 + Spacing.S)), 1, PeekRows);
                var rows = new Element[Math.Min(peek.Count, fittingRows)];
                for (int i = 0; i < rows.Length; i++) rows[i] = PeekRow(peek[i], textW);
                copy.Add(new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Width = textW, HitTestPassThrough = true, Stagger = 45f,
                    Padding = new Edges4(0f, Spacing.S, 0f, 0f), Key = "peek",
                    Children = rows,
                });
            }
            else if (subtitle.Length > 0)
                // The playlist description is an HTML fragment (may carry <a>/<b>) — RichText parses it (decoded, tags
                // not shown raw); links share the copy colour so they read as prose, not clickable chrome (the card owns
                // the tap). Hover relaxes the clamp to 5 lines; the band's CardResizeHeight animates the space it takes.
                copy.Add(RichText.Of(subtitle, 14f, WaveeOnMedia.Ink, WaveeOnMedia.Ink,
                    textW, hovered ? (artH < 440f ? 4 : 5) : 2));

            copy.Add(new BoxEl
            {
                Direction = 0, Width = textW, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Padding = new Edges4(0f, Spacing.S, 0f, 0f),
                Children =
                [
                    NowPlayingOverlay.Create(uri, onPlay, 48f, cover: false, 48f, persistent: true, light: true).Skeletonized(false),
                    Embed.Comp(() => new CardLibraryAction(uri, title, kind, onDark: true)).Skeletonized(false),
                    new BoxEl { Grow = 1f },
                ],
            });

            var card = new BoxEl
            {
                Height = artH, ZStack = true, ClipToBounds = true,
                Corners = CornerRadius4.All(radius), Shadow = Elevation.Card,
                Children =
                [
                    // Artwork hover zoom, clipped by the CARD's rounded SDF — deliberately NO clip of its own: the RHI
                    // clamps Image/Gradient primitives to the TOPMOST rounded clip only (D3D12Device rounded-clip
                    // stack), so if this container pushed its own rounded clip, the hover SCALE would carry that clip
                    // out past the card and the zoomed cover would show square slivers at the card corners (the card
                    // boundary then being scissor-only). With no clip here, the card root's (unscaled) rounded clip
                    // stays active and the zoomed cover clamps to the card's corners.
                    new BoxEl
                    {
                        Height = artH, ZStack = true,
                        // Standard rung, per the shelf card's zoom above: a full-bleed editorial cover is the LARGEST
                        // moving surface in the app, so it takes the smaller multiplier, not the Emphatic one.
                        HoverScale = WaveeMotion.ScaleStandard.Hover,
                        HoverDurationMs = MotionTok.StandardEnter.DurationMs, HoverEasing = Easing.FluentDecelerate,
                        // Opaque, cover-coloured placeholder — NOT Tok.FillCardDefault. The card brushes are
                        // deliberately translucent, so using one here let the page show straight through and an
                        // editorial card read as an empty hole until its 512px art decoded.
                        Children = [ Ui.Image(cover?.Url ?? "", ImageFit.Cover, aspect, 512, radius,
                                              Surfaces.PlaceholderFor(cover?.Url), cover?.BlurHash) ],
                    },
                    new BoxEl
                    {
                        Height = artH, HitTestVisible = false, Corners = CornerRadius4.All(radius),
                        Gradient = new GradientSpec(GradientShape.Radial, 0f,
                        [
                            new GradientStop(0f, WaveeOnMedia.SpotlightInner),
                            new GradientStop(0.48f, WaveeOnMedia.SpotlightMid),
                            new GradientStop(1f, ColorF.Transparent),
                        ])
                        {
                            RadialCenter = new Point2(0.5f, 0.35f),
                            RadialRadius = new Point2(Math.Clamp(Math.Clamp(cardW * 0.46f, 140f, 190f) / MathF.Max(cardW, 1f), 0.01f, 2f),
                                                      Math.Clamp(Math.Clamp(cardW * 0.46f, 140f, 190f) / artH, 0.01f, 2f)),
                        },
                        RadialGradientCenter = spotlightCenter,
                        Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
                    },
                    new BoxEl
                    {
                        Height = artH, HitTestVisible = false, Corners = CornerRadius4.All(radius),
                        Gradient = Tok.ScrimBottom,   // byte-exact extraction source of the canonical footer scrim
                    },
                    // Bottom-pinned frosted copy band, cross-stretched to the full card width; it auto-sizes to the copy
                    // plus the feather ramp space (featherPad), so the frost covers the text zone and fades up into the
                    // art. Height changes (line expand / peek swap) tween through real layout (CardResizeHeight).
                    // The artwork frost is a persistent image derivative: decode once, bake a downsampled separable
                    // Gaussian once off the scroll path, then draw it as an ordinary image quad. Its cache key is based
                    // on source + output bucket + blur parameters, never card position, so scrolling only changes the
                    // quad transform. Until the bake is ready the same draw falls back to the crisp source image.
                    new BoxEl
                    {
                        Height = artH, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Stretch,
                        HitTestPassThrough = true,
                        Children =
                        [
                            new BoxEl
                            {
                                ZStack = true, ClipToBounds = true, HitTestPassThrough = true,
                                Corners = CornerRadius4.All(radius),
                                Animate = MotionRecipes.CardResizeHeight,
                                Children =
                                [
                                    // The image has no explicit/aspect size, so it measures to zero and the copy drives
                                    // the band height. Overlay and top feather are image-shader channels, keeping steady
                                    // paint to one DrawImage with no PushLayer/PopLayer or per-frame intermediate RT.
                                    new BoxEl
                                    {
                                        ZStack = true, HitTestVisible = false,
                                        Corners = CornerRadius4.All(radius),
                                        Children =
                                        [
                                            // The 512 request deduplicates with the crisp cover; the derivative is a
                                            // half-resolution σ26 bake. FocusY=1 keeps the cover crop on the bottom slice.
                                            Ui.Image(cover?.Url ?? "", ImageFit.Cover, float.NaN, 512, radius,
                                                Surfaces.PlaceholderFor(cover?.Url), cover?.BlurHash) with
                                            {
                                                FocusY = 1f,
                                                BakedBlur = new BakedBlurSpec(26f, 0.5f),
                                                ColorOverlay = WaveeOnMedia.BackdropDim,
                                                Mask = new ImageMaskSpec(EdgeMask.Top, featherPad),
                                            },
                                        ],
                                    },
                                    new BoxEl
                                    {
                                        Direction = 1, Gap = Spacing.XS, HitTestPassThrough = true,
                                        Padding = new Edges4(inset, featherPad, inset, inset),
                                        Children = copy.ToArray(),
                                    },
                                ],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Grow = 1f, Direction = 0, AlignItems = FlexAlign.Start,
                        Padding = new Edges4(inset, inset, inset, 0f), HitTestPassThrough = true,
                        Children = [ KindChip(kind), new BoxEl { Grow = 1f }, MoreInline(menu is not null, onDark: true, size: 36f) ],
                    },
                ],
            };
            // Interactivity + hover elevation live on a NON-clipping wrapper (the shelf-card pattern): the surface above
            // must keep its ClipToBounds (crisp art / spotlight / frost all clamp to the rounded card), and a parent clip
            // shaves a child's shadow halo — so the hover halo rides a sibling panel BEHIND the opaque surface, where its
            // own clip can't touch it. The wrapper is the hit target, so the surface's hover channels (artwork zoom,
            // spotlight, FAB reveal) and the panel's HoverOpacity all ride the same ancestor hover; the lift moves halo
            // and surface together. The shelf's LiftClearance provides the headroom this paints into.
            return new BoxEl
            {
                ZStack = true,
                OnClick = onClick, PressScale = WaveeMotion.ScaleSubtle.Press, Draggable = drag,
                // Elevate above sibling editorial cards while hovered so the lift halo survives (design z-index:2).
                HoverElevatePaint = true,
                OnPointerMoveWithin = pointerMove,
                OnPointerExit = pointerExit,
                WhileHover = Motion.ReducedMotion ? null : new MotionTarget { OffsetY = -4f },
                WhilePressed = Motion.ReducedMotion ? null : new MotionTarget { Scale = 0.99f, OffsetY = -1f },
                Transition = MotionTok.ControlNormal,
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Corners = CornerRadius4.All(radius),
                        Shadow = Elevation.Card, Opacity = 0f, HoverOpacity = 1f,   // card band, not the flyout band — see CardShell
                        HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate, HitTestVisible = false,
                    },
                    card,
                ],
            }.WithMenu(menu);
        }

        // The countdown ring: an 18px track circle + a round-capped sweep arc whose StrokeTrimEnd the core animates
        // 0→1 over the countdown (the ProgressRing determinate look, white-on-frost).
        static Element CountdownRing(Action<NodeHandle>? arcCapture) => new BoxEl
        {
            ZStack = true, Width = 18f, Height = 18f, Shrink = 0f, HitTestPassThrough = true,
            Children =
            [
                new BoxEl { Width = 18f, Height = 18f, Arc = new ArcSpec(WaveeOnMedia.Stroke, 2f, 0f, 360f, RoundCaps: false) },
                new BoxEl
                {
                    Width = 18f, Height = 18f,
                    Arc = new ArcSpec(WaveeOnMedia.Ink, 2f, 0f, 360f, RoundCaps: true),
                    OnRealized = arcCapture,
                },
            ],
        };

        static Element PeekRow(HomePreviewTrack t, float textW)
        {
            const float art = WaveeSize.Thumb40;
            float nameW = MathF.Max(24f, textW - art - Spacing.M);
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                Animate = MotionRecipes.PageFade,
                Children =
                [
                    new BoxEl
                    {
                        Width = art, Height = art, Shrink = 0f, ClipToBounds = true, Corners = Radii.ControlAll,
                        Children = [ Surfaces.Artwork(t.Cover, Seed(t.Uri), art, art, Radii.Control, decodePx: 64) ],
                    },
                    // Caption at 600, not a bespoke 12.5: the ramp's small-strong rung, with the line height that
                    // comes with it. (A peek row is a compact list line, not a card title.)
                    Caption(t.Name) with
                    {
                        Weight = 600, Color = WaveeOnMedia.Ink,
                        Width = nameW, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };
        }
    }

    // ── 16:9 video card (sized to a supplied cardW from a measured shelf): wide thumbnail + title + duration. ──
    public static Element VideoCard(Image? thumb, string title, string duration, string uri,
                                    Action onClick, Action onPlay, float cardW, MenuAttach? menu = null,
                                    DragSource? drag = null)
    {
        var hovered = new Signal<bool>(false);
        float inner = MathF.Max(64f, cardW - 2f * Pad);
        float ar = inner * 9f / 16f;
        var card = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, ClipToBounds = true,
            Padding = new Edges4(Pad, Pad, Pad, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Shadow = Elevation.Card,
            HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press, OnClick = onClick, Draggable = drag,
            OnPointerMoveWithin = _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
            Children =
            [
                new BoxEl
                {
                    ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Control),
                    Children =
                    [
                        Surfaces.Artwork(thumb, Seed(uri), inner, ar, Radii.Control, decodePx: 480),
                        LazyOverlay(hovered, uri, onPlay, FabSize, cover: true, 0f),
                        MoreCorner(menu is not null),
                    ],
                },
                WaveeType.TrackTitle(title) with { Width = inner, Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                duration.Length == 0 ? new BoxEl()
                    : WaveeType.TrackMeta(duration) with { Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        return card.WithMenu(menu);
    }

    // ── Wide "jump back in" tile: cover + title (fills, ellipsised) + trailing now-playing/play overlay ───
    public static Element QuickPick(Image? cover, string title, string uri, Action onClick, Action onPlay, ColorF? accent = null, Element? diagnostics = null, MenuAttach? menu = null, DragSource? drag = null)
    {
        var hovered = new Signal<bool>(false);
        var card = new BoxEl
        {
            Direction = 0, Height = QuickH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Corners = CornerRadius4.All(Radii.Card), Fill = AccentCardFill(accent), HoverFill = AccentCardHoverFill(accent),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true, OnClick = onClick,
            Shadow = Elevation.Card, Draggable = drag,
            OnPointerMoveWithin = _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
            Children =
            [
                // Surfaces.Artwork = a neutral shimmer/placeholder tile + the real art on top (graceful when the cover
                // is missing or on an auth-gated host that fails to fetch).
                ArtworkOrLiked(cover, uri, QuickW, QuickH, 0f, diagnostics: diagnostics),
                // Grow + Basis=0: take the remaining width (never the title's intrinsic width) → ellipsis, no overflow.
                WaveeType.TrackTitle(title) with { Grow = 1f, Basis = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(0f, 0f, Spacing.M, 0f),
                    Children = [ LazyOverlay(hovered, uri, onPlay, 36f, cover: false, 36f) ],
                },
            ],
        };
        return card.WithMenu(menu);
    }

    // ── List row: a HORIZONTAL media row (search / "All" lists). The SAME factory + the SAME now-playing/play affordance
    // (the shared NowPlayingOverlay) as the grid/shelf cards — only the SKIN differs (a row vs a tile). `large` is the
    // Top-Result hero variant (bigger art + title + card chrome). Optional eyebrow ("Lyrics match" / "Included in Premium"),
    // a trailing type chip, and a trailing action (save / follow). One home for a future shared context menu.
    public static Element Row(Image? cover, string title, string subtitle, string uri, bool circular,
                              Action onClick, Action onPlay,
                              string? eyebrow = null, ColorF? eyebrowColor = null, string? typeChip = null, Element? trailing = null, bool large = false,
                              string? detail = null, Action<string>? onSubtitleNav = null, string? meta = null, bool detailBelowArt = false,
                              MenuAttach? menu = null, DragSource? drag = null, string? morphKey = null,
                              Func<Element, Element>? leadingArtwork = null, Element? metaContent = null,
                              bool plated = true, bool showArtwork = true)
    {
        var hovered = new Signal<bool>(false);
        float art = large ? 84f : WaveeSize.Thumb48;
        // The ART's radius: a small row thumb takes the control rung (4) like every other 48px thumb in the app; the
        // hero's 84px cover takes the card rung. The old 6 was on neither.
        float r = circular ? art / 2f : (large ? Radii.Card : Radii.Control);
        float fab = large ? 44f : 30f;
        bool hasMeta = !large && (meta is { Length: > 0 } || metaContent is not null);
        bool hasDetail = !large && detail is { Length: > 0 };   // the audiobook blurb line under the subtitle (Spotify shows a 2-line description)
        bool belowArt = detailBelowArt && (hasMeta || hasDetail);
        var coverStack = new BoxEl
        {
            Width = art, Height = art, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(r),
            Children =
            [
                // morphKey (default null ⇒ byte-identical to the pre-seam row) tags this cover as a connected-animation
                // participant, exactly like Shelf/Grid already do. A caller must guarantee the key is UNIQUE among the
                // live nodes — two rows carrying one MorphId is a duplicate-key bug, not a nicer transition.
                ArtworkOrLiked(cover, uri, art, art, r, morphKey),
                LazyOverlay(hovered, uri, onPlay, fab, cover: true, art, centered: true),
            ],
        };
        Element leading = leadingArtwork is null ? coverStack : leadingArtwork(coverStack);
        var textKids = new System.Collections.Generic.List<Element>(3);
        if (eyebrow is { Length: > 0 })
            textKids.Add(WaveeType.Eyebrow(eyebrow) with
            { Color = eyebrowColor ?? Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        textKids.Add(large
            ? WaveeType.PageHero(title) with { Grow = 1f, Basis = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
            : WaveeType.TrackTitle(title) with { Grow = 1f, Basis = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        // Subtitle as a rich caption (matches the TrackMeta Caption style: 12px / secondary): anchor spans (artist/album)
        // become accent hyperlinks that navigate on their own, independent of the row's click. Plain text renders identically.
        if (subtitle.Length > 0)
            textKids.Add(RichText.OfRow(subtitle, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, onSubtitleNav));
        if (hasMeta && !belowArt)
            textKids.Add(metaContent ?? Caption(meta!) with
            { Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        if (hasDetail && !belowArt)
            textKids.Add(Caption(detail!) with
            { Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis });
        var kids = new System.Collections.Generic.List<Element>(4);
        if (showArtwork) kids.Add(leading);
        kids.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Gap = large ? Spacing.S : Spacing.XXS, Children = textKids.ToArray(),
        });
        if (typeChip is { Length: > 0 }) kids.Add(RowChip(typeChip));
        if (trailing is not null) kids.Add(trailing);
        if (belowArt)
        {
            var belowKids = new System.Collections.Generic.List<Element>(2);
            if (hasMeta) belowKids.Add(metaContent ?? Caption(meta!) with { Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            if (hasDetail) belowKids.Add(Caption(detail!) with { Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis });

            return new BoxEl
            {
                Direction = 1, Height = float.NaN, MinHeight = 72f, Gap = Spacing.S,
                Padding = Edges4.All(Spacing.S),
                Corners = plated ? Radii.CardAll : Radii.ControlAll,
                Fill = plated ? Tok.FillCardSecondary : ColorF.Transparent,
                HoverFill = plated ? Tok.FillCardDefault : Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                BorderWidth = plated ? 1f : 0f,
                BorderColor = plated ? Tok.StrokeCardDefault : ColorF.Transparent,
                Role = AutomationRole.Button, OnClick = onClick, Draggable = drag,
                OnPointerMoveWithin = _ => { if (!hovered.Peek()) hovered.Value = true; },
                OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
                Children =
                [
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = kids.ToArray() },
                    new BoxEl { Direction = 1, Gap = Spacing.XXS, Children = belowKids.ToArray() },
                ],
            }.WithMenu(menu);
        }

        return new BoxEl
        {
            // A detail row auto-sizes (Height NaN + MinHeight) so the blurb can take two lines; plain rows stay a tidy 64px.
            // The hero is roomier (taller card, generous inset) so the big title + subtitle aren't cramped against the cover.
            Direction = 0, Height = large ? 112f : (hasDetail ? float.NaN : 64f), MinHeight = hasDetail ? 64f : float.NaN,
            AlignItems = FlexAlign.Center, Gap = large ? Spacing.L : Spacing.M,
            Padding = large ? new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M)
                    : hasDetail ? Edges4.All(Spacing.S)
                    : new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            // Plated (home/search) keeps the filled card. Recents and other list surfaces pass plated:false —
            // transparent rest, subtle hover wash, no stroke — matching the Zune×Fluent row prototype.
            Corners = plated ? Radii.CardAll : Radii.ControlAll,
            Fill = plated ? Tok.FillCardSecondary : ColorF.Transparent,
            HoverFill = plated ? Tok.FillCardDefault : Tok.FillSubtleSecondary,
            PressedFill = plated ? (large ? Tok.FillCardDefault : Tok.FillSubtleTertiary) : Tok.FillSubtleTertiary,
            BorderWidth = plated ? 1f : 0f,
            BorderColor = plated ? Tok.StrokeCardDefault : ColorF.Transparent,
            // The row is the interactive ancestor (OnClick + a no-op pointer-exit), so the cover's hover-revealed play FAB
            // resolves off ROW hover — identical to the card behavior.
            Role = AutomationRole.Button, OnClick = onClick, Draggable = drag,
            OnPointerMoveWithin = _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
            Children = kids.ToArray(),
        }.WithMenu(menu);
    }

    static Element RowChip(string text) => new BoxEl
    {
        // Capsule by construction (Radii.Full clamps to half the box), not a hand-picked 11 that tracked the old height.
        Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
        Fill = Tok.FillSubtleSecondary,
        Children = [ WaveeType.Eyebrow(text) with { Color = Tok.TextTertiary } ],
    };

    // A stable-ish placeholder seed from the card's context uri (so each card gets its own gradient cover tone).
    static int Seed(string s) => (s ?? string.Empty).GetHashCode() & 0x7fffffff;

    // ── Accent Play/Pause FAB (own hover/press feedback) — glyph supplied by the caller (play vs pause). ──
    internal static Element PlayFab(Action onClick, string glyph, float size = FabSize) => new BoxEl
    {
        Width = size, Height = size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(size),
        Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
        // The FAB already lives inside a clipped cover. Scaling its rounded plate past its retained paint bounds caused
        // the lower-right sector to be cut out (the visible "Pac-Man" wedge). Keep the plate geometry stable; color and
        // the card's own press response still provide hover/press feedback.
        OnClick = onClick, Cursor = CursorId.Hand,
        // A press on the play FAB is a PLAY, never the start of a card drag (the arm walk stops here).
        BlocksDragArm = true,
        Children = [ FabGlyph(glyph, size * 0.42f, Tok.TextOnAccentPrimary) ],
    };

    internal static Element CoverActionFab(Action onClick, string glyph, string tooltip, float size)
        => Embed.Comp(new CoverActionFabCore.Props(onClick, glyph, tooltip, size), () => new CoverActionFabCore());

    internal static TextEl FabGlyph(string glyph, float size, ColorF color) => new(glyph)
    {
        Width = size,
        Height = size,
        Size = size,
        LineHeight = size,
        FontFamily = Theme.IconFont,
        Color = color,
    };
}

/// <summary>Shelf card as a Component so <see cref="_hovered"/> is mount-stable — LazyNowPlayingOverlay props then
/// compare reference-equal across parent re-renders (see MediaCard.Shelf).</summary>
sealed class ShelfCard : Component
{
    internal sealed record Props(
        Image? Cover, string Title, string Subtitle, string Uri,
        Action OnClick, Action OnPlay, float CardW, bool Circular, string? MorphKey,
        Action<string>? OnNavUri, MenuAttach? Menu, DragSource? Drag);

    readonly Signal<bool> _hovered = new(false);

    public override Element Render()
    {
        var p = UseProps<Props>();
        float inner = MathF.Max(48f, p.CardW - 2f * MediaCard.Pad);
        float r = p.Circular ? inner / 2f : Radii.Card;

        Element face = p.Circular
            // A missing artist photo must still be an intentional card, not a blank gray rectangle. PersonPicture gives
            // us WinUI initials/contact fallback and the same circular crop when a real URL is present.
            ? PersonPicture.Create("", inner, displayName: p.Title, imageSourcePath: p.Cover?.Url)
            // Same gating as ArtworkOrLiked above: the dynamic treatment, no picker, self-collapsing when small.
            // This is the RECENTS RAIL's card. Its liked tile used to arrive WITH a cover — Spotify's recents feed
            // serves misc.scdn.co/liked-songs/liked-songs-300.png, i.e. a CDN copy of the very stock PNG the user's
            // chosen treatment replaces — so the old `p.Cover is null` half of this test was false and the tile kept
            // painting the purple heart. The uri alone decides now.
            : LikedSongsArtwork.For(p.Uri, inner, inner, r, p.MorphKey) is { } liked
                ? liked
                : p.Cover?.MosaicTiles is { Count: >= 4 } mtiles
                    ? Surfaces.Mosaic(mtiles, inner, inner, r)
                    : ZStack(
                        // A neutral shimmer tile sits behind the art so a card is never an empty box — it breathes while
                        // the real art loads and settles once it lands. The tile carries the cover's own graded colour
                        // (CoverColorPlane) when it is known, so a loading card is that album's colour rather than a
                        // neutral hole. The Image keeps a TRANSPARENT placeholder on purpose — the tile IS the backdrop.
                        Surfaces.Shimmer(p.Cover?.Url, (int)MediaCard.ShelfDecodePx, (int)MediaCard.ShelfDecodePx, inner, inner, r),
                        Image(p.Cover?.Url ?? "", ImageFit.Cover, 1f, MediaCard.ShelfDecodePx, r, placeholder: ColorF.Transparent)
                            with { MorphId = p.MorphKey });

        var coverStack = new BoxEl
        {
            // The artwork already owns its square/circular crop. Do not apply that CIRCLE clip to the action layer:
            // a bottom-right FAB is inside the cover's rectangular slot but outside the avatar circle, so clipping the
            // whole stack shears its accent background. Square covers retain the old bounds clip.
            Width = inner, Height = inner, ZStack = true, ClipToBounds = !p.Circular, Corners = CornerRadius4.All(r),
            // Artwork ZOOM, not a button: a ~200px cover on the Standard rung already travels ~4px per edge, where the
            // Emphatic rung on a 32px FAB travels ~1px. Perceived travel — not the tier's name — is what must match, so
            // every full-artwork zoom takes the Standard hover value (and has no press partner: a zoom has no pressed
            // state; the card root owns the press).
            HoverScale = WaveeMotion.ScaleStandard.Hover,
            HoverDurationMs = MotionTok.StandardEnter.DurationMs, HoverEasing = Easing.FluentDecelerate,
            Children =
            [
                face,
                // The now-playing equalizer (bottom-left, when this card's context is playing) + the play/pause FAB
                // (bottom-right, REVEALED ON HOVER). Reactive: subscribes to the playback bridge. The container carries
                // NO OnClick, so the hit walks up to the card (its HoverScale fires + the FAB reveals off the card's
                // hover); only the FAB itself is a hit target.
                MediaCard.LazyOverlay(_hovered, p.Uri, p.OnPlay, MediaCard.FabSize, cover: true, inner),
                MediaCard.MoreCorner(p.Menu is not null),
            ],
        };

        var content = new BoxEl
        {
            // No explicit Width: the shelf cell (a column container) cross-stretches the card to the cell's LIVE width.
            // Grow=1 fills the cell's HEIGHT too: in a measured shelf the engine sizes the cell to the TALLEST card's
            // natural height and every card fills it → uniform panels, exact, no reserved worst case; content stays
            // top-aligned (cover, then text) with any slack below. The card itself just sizes to its content.
            Direction = 1, Gap = MediaCard.Pad, Grow = 1f,
            Padding = new Edges4(MediaCard.Pad, MediaCard.Pad, MediaCard.Pad, Spacing.M),
            Children =
            [
                coverStack,
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XXS, AlignItems = p.Circular ? FlexAlign.Center : FlexAlign.Start,
                    Children =
                    [
                        // Explicit Width clamps the run to the card (no overflow, ellipsis at the edge). MaxLines caps
                        // how tall a verbose card can grow (and thus the whole uniform row).
                        WaveeType.TrackTitle(p.Title) with { Width = inner, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        // The description can be an HTML fragment (links to artists/playlists, bold) — parse → rich
                        // spans (links accent + clickable via onNavUri, bold rendered), capped at two lines.
                        RichText.Of(p.Subtitle, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, inner, 2, p.OnNavUri),
                    ],
                },
            ],
        };
        // Grow=1 on the shell: a measured shelf cell stretches every card to the tallest card's height.
        var hovered = _hovered;
        var card = (MediaCard.CardShell(content, p.OnClick) with
        {
            Grow = 1f,
            Draggable = p.Drag,
            OnPointerMoveWithin = _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
        }).WithMenu(p.Menu);
        // The padding box is the shelf's GUTTER, not the card — the drag source above deliberately sits inside it.
        return new BoxEl { Grow = 1f, Direction = 1, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XXS), Children = [card] };
    }
}

sealed class CoverActionFabCore : Component
{
    internal sealed record Props(Action OnClick, string Glyph, string Tooltip, float Size);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var live = UseRef(p);
        live.Value = p;
        var factory = UseMemo(() => (Func<Element>)(() =>
        {
            var cur = live.Value;
            return new BoxEl
            {
                Width = cur.Size, Height = cur.Size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.Circle(cur.Size),
                Fill = WaveeOnMedia.ScrimRest,
                HoverFill = WaveeOnMedia.ScrimHover,
                PressedFill = WaveeOnMedia.ScrimPressed,
                BorderWidth = 1f, BorderColor = WaveeOnMedia.Stroke,
                Shadow = Elevation.Card,
                HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
                OnClick = () => live.Value.OnClick(), Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                BlocksDragArm = true,   // a cover action button is its own affordance, not a card drag handle
                Children = [MediaCard.FabGlyph(cur.Glyph, cur.Size * 0.40f, WaveeOnMedia.Ink)],
            };
        }), DepKey.Empty);
        return ToolTip.WrapStable(factory, p.Tooltip);
    }
}

sealed class CardLibraryAction : Component
{
    readonly string _uri;
    readonly string _name;
    readonly HomeCardKind _kind;
    readonly bool _onDark;

    public CardLibraryAction(string uri, string name, HomeCardKind kind, bool onDark)
    { _uri = uri; _name = name; _kind = kind; _onDark = onDark; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        var live = UseRef<(LibraryBridge? lib, bool saved, ColorF idle)>(default);
        var factory = UseMemo(() => (Func<Element>)(() =>
        {
            var s = live.Value;
            return new BoxEl
            {
                Width = 40f, Height = 40f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.Circle(40f),
                Fill = _onDark ? WaveeOnMedia.ScrimRest : ColorF.Transparent,
                HoverFill = _onDark ? WaveeOnMedia.ScrimHover : Tok.FillSubtleSecondary,
                PressedFill = _onDark ? WaveeOnMedia.ScrimPressed : Tok.FillSubtleTertiary,
                BorderWidth = _onDark ? 1f : 0f,
                BorderColor = _onDark ? WaveeOnMedia.Stroke : ColorF.Transparent,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                OnClick = () => s.lib?.ToggleSaved(_uri, _name),
                BlocksDragArm = true,   // save/follow is its own affordance, not a card drag handle
                Children = [Icon(s.saved ? Icons.HeartFill : Icons.Heart, 17f, s.saved ? Tok.AccentTextPrimary : s.idle)],
            };
        }), DepKey.Empty);
        if (lib is null || _kind == HomeCardKind.Liked) return new BoxEl();
        bool saved = lib.IsSaved(_uri);
        bool follow = _kind is HomeCardKind.Artist or HomeCardKind.Playlist;
        string tip = follow
            ? Loc.Get(saved ? Strings.Artist.Following : Strings.Artist.Follow)
            : Loc.Get(saved ? Strings.Detail.Edit.Saved : Strings.Detail.Edit.Save);
        ColorF idle = _onDark ? WaveeOnMedia.Ink : Tok.TextSecondary;
        live.Value = (lib, saved, idle);
        return ToolTip.WrapStable(factory, tip);
    }
}

// Cheap cold-card sentinel. The full play/equalizer subtree is mounted only for the card under the pointer or the
// playback-matching card; Home Ready therefore pays one tiny component/effect per card instead of every FAB, tooltip,
// equalizer, and icon tree in the first flush.
sealed class LazyNowPlayingOverlay : Component
{
    /// <summary>All row-varying data is live. A virtual slot is mount-stable while every one of these values may change
    /// when that slot is rebound to another item.</summary>
    internal sealed record Props(
        IReadSignal<bool> Hovered, string Uri, Action OnPlay, float Fab, bool Cover, float Inner,
        Action? OnNavigate, bool Centered);

    public override Element Render()
    {
        var props = UseProps<Props>();
        var bridge = UseContext(PlaybackBridge.Slot);
        // "Is my card the playing one?" is DERIVED, never written. It used to be a UseSignal that a UseSignalEffect wrote
        // and Render read back — a render↔effect cycle with an ordering hazard: when a KeepAlive unpark scheduled this
        // component's render ahead of the playback effect in the same flush, Render read a STALE `active`, the effect then
        // wrote it, and Render ran a second time (the doubled overlay mount count on card-heavy pages). A Memo removes the
        // cycle: Render pulls the value, so it cannot observe it stale, and the Memo's equality cut-off means an upstream
        // write that leaves the answer unchanged resolves to CLEAN without re-rendering behind it.
        //
        // Cold-path decoupling is PRESERVED verbatim. Read the COARSE HasActiveContext bool FIRST and bail before touching
        // the hot Identity signal: while nothing is playing no card can match (Matches is false with an empty
        // context+track), so an idle overlay must NOT join Identity's ~70-way fanout nor run Matches. A Memo re-tracks its
        // sources on EVERY recompute (RunComputation re-links), exactly like the effect did, so this early return leaves
        // the memo subscribed to HasActiveContext ALONE while idle — Identity is only linked on the runs whose branch
        // actually reads it, i.e. once a context goes active.
        var active = UseComputed(() =>
        {
            var live = UseProps<Props>();
            if (bridge is not { } b || !b.HasActiveContext.Value) return false;
            var identity = b.Identity.Value;
            return NowPlayingOverlay.Matches(live.Uri, identity.ContextUri, identity.Track);
        });

        // Hover, read through the props channel and gated on the VALUE. UseProps is non-positional (it just reads the
        // injected props signal), so calling it INSIDE the memo subscribes the MEMO to the re-push rather than this
        // render: a parent rebuild that hands over a fresh-but-same-state hover signal recomputes the memo, finds the
        // same bool, and resolves CLEAN — no re-render, so the cold-card laziness this component exists for is intact.
        // A Memo also re-links its sources on every recompute, so the read always lands on the CURRENT signal.
        var hovered = UseComputed(() => UseProps<Props>().Hovered.Value);
        // Stable signal identity for the EQ pause prop (hooks before any early return).
        var hoverSig = props.Hovered;

        if (!hovered.Value && !active.Value)
        {
            if (props.Cover)
                return props.Centered
                    ? new BoxEl { Width = props.Inner, Height = props.Inner, HitTestVisible = false }
                    : new BoxEl { Grow = 1f, HitTestVisible = false };
            return new BoxEl { Width = props.Fab, Height = props.Fab, Shrink = 0f, HitTestVisible = false };
        }

        return NowPlayingOverlay.Create(
            props.Uri, props.OnPlay, props.Fab, props.Cover, props.Inner, props.OnNavigate, props.Centered, hovered: hoverSig);
    }
}

// The reactive now-playing / play affordance on a content card (mirrors WaveeMusic's ContentCard state model):
//   • the play/pause FAB is REVEALED ON HOVER (and shows PAUSE when this card's context is the one playing);
//   • when this card's context IS playing, the now-playing EQUALIZER shows (bottom-left on a cover; in the trailing
//     slot, hidden on hover so the FAB takes over). "Am I the playing context?" = my uri == the playing context uri,
//     or the current track's album/artist uri (so album/artist cards light up too). Clicking the FAB toggles
//     pause/resume when it's the active context, else plays this context.
sealed class NowPlayingOverlay : Component
{
    internal sealed record Props(
        string Uri, Action OnPlay, float Fab, bool Cover, float Inner, Action? OnNavigate,
        bool Centered, bool Persistent, bool Light, IReadSignal<bool>? Hovered);

    internal static Element Create(
        string uri, Action onPlay, float fab, bool cover, float inner, Action? onNavigate = null,
        bool centered = false, bool persistent = false, bool light = false, IReadSignal<bool>? hovered = null)
        => Embed.Comp(new Props(uri, onPlay, fab, cover, inner, onNavigate, centered, persistent, light, hovered),
            static () => new NowPlayingOverlay());

    public override Element Render()
    {
        var props = UseProps<Props>();
        // Optional — ArtCard mounts this without a hover signal; EQ then keeps ticking under HoverOpacity (no pause signal).
        var hoverPaused = props.Hovered;
        var b = UseContext(PlaybackBridge.Slot);
        // RecentsPage provides this (it wraps its page in the slot); every other page leaves it null, in which case the
        // Tok.AccentTextPrimary fallback below is a pure no-op.
        var ctx = UseContext(WaveeAccentCtx.Slot);
        // Re-render only when THIS card's own visual state changes — not on every track skip / play-pause of OTHER
        // contexts. Reading CurrentContext/CurrentTrack/IsPlaying directly here would re-render EVERY visible card's
        // overlay on any playback change (N small element-tree allocations per event). Instead, a UseSignalEffect bridges
        // those hot playback signals into a COARSE retained (active, playingHere) signal whose setter suppresses on
        // equality (Signal.cs) — so an unrelated change re-runs only this cheap effect (Matches, zero-alloc) and, when
        // the pair is unchanged (the common case for a non-playing card), schedules NO re-render. `playing` is only ever
        // read when active, where it equals playingHere, so this coarse pair fully captures the overlay's visual state.
        var vis = UseSignal((active: false, playingHere: false));
        UseSignalEffect(() =>
        {
            var live = UseProps<Props>();
            // Read the COARSE HasActiveContext bool FIRST and bail before touching the hot Identity signal — the same
            // cold-path decoupling LazyNowPlayingOverlay's memo above documents. While nothing is playing no overlay
            // can match (Matches is false against an empty context+track), so an idle overlay must not join Identity's
            // fanout. The effect re-links its sources on every run, so Identity re-attaches when the bool flips true.
            if (b is not { } bridge || !bridge.HasActiveContext.Value) { vis.Value = (false, false); return; }
            var identity = bridge.Identity.Value;
            bool a = Matches(live.Uri, identity.ContextUri, identity.Track);
            vis.Value = (a, a && bridge.IsPlaying.Value);   // short-circuit: a non-active card never subscribes to IsPlaying
        });
        var (active, playingHere) = vis.Value;
        bool playing = playingHere;   // the equalizer animates iff this card's context is the one actively playing

        void Toggle()
        {
            if (b is null) { props.OnPlay(); return; }
            var identity = b.Identity.Peek();
            if (Matches(props.Uri, identity.ContextUri, identity.Track))
            {
                bool p = b.IsPlaying.Peek();
                b.IsPlaying.Value = !p;                              // optimistic, then the player reconciles
                if (p) _ = b.Player.PauseAsync(); else _ = b.Player.ResumeAsync();
            }
            else props.OnPlay();
        }

        if (props.Persistent)
        {
            ColorF fill = props.Light ? WaveeOnMedia.LightButton : Tok.AccentDefault;
            ColorF hover = props.Light ? WaveeOnMedia.LightButtonHover : Tok.AccentSecondary;
            ColorF pressed = props.Light ? WaveeOnMedia.LightButtonPressed : Tok.AccentTertiary;
            ColorF ink = props.Light ? WaveeOnMedia.LightButtonInk : Tok.TextOnAccentPrimary;
            // ToolTip.Wrap (not WrapStable): this branch is behind `_persistent` and already past earlier hooks —
            // a WrapStable UseMemo here would violate stable hook order vs the non-persistent path.
            return ToolTip.Wrap(new BoxEl
            {
                Width = props.Fab, Height = props.Fab, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.Circle(props.Fab), Fill = fill, HoverFill = hover, PressedFill = pressed,
                Shadow = Elevation.Card, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = Toggle,
                BlocksDragArm = true,
                Children = [Icon(playingHere ? Icons.Pause : Icons.Play, props.Fab * 0.38f, ink)],
            }, Loc.Get(playingHere ? Strings.Home.Pause : Strings.Home.Play));
        }

        // FAB revealed on card hover: the wrapper is non-interactive, so its HoverOpacity resolves off the CARD's
        // hover (the FAB inside keeps its own click). Pause glyph when this context is the one playing. A gentle
        // ~180ms decelerate fade (not the snappy default) so the button eases in rather than popping.
        Element reveal = new BoxEl
        {
            Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
            Direction = 1, AlignItems = FlexAlign.End, Gap = Spacing.S,
            Children = props.OnNavigate is null
                ? [ MediaCard.PlayFab(Toggle, playingHere ? Icons.Pause : Icons.Play, props.Fab) ]
                : [
                    MediaCard.CoverActionFab(props.OnNavigate, Icons.OpenInNewWindow, "Go to album", MathF.Max(34f, props.Fab - 8f)),
                    MediaCard.PlayFab(Toggle, playingHere ? Icons.Pause : Icons.Play, props.Fab)
                  ],
        };
        Element EqPill(bool pauseOnHover) => new BoxEl
        {
            Padding = Edges4.All(Spacing.XS), Corners = Radii.ControlAll, Fill = WaveeOnMedia.ScrimRest,
            Children =
            [
                WaveeEqualizer.Of(playing, () => ctx is {} a ? a.Value.Ink : Tok.AccentTextPrimary, 14f,
                    paused: pauseOnHover ? hoverPaused : null),
            ],
        };

        if (props.Cover && props.Centered)
        {
            // Small ROW art (search "All" rows): the equalizer CENTERED at rest (hidden on hover), the play FAB centered
            // over a hover scrim — Spotify's row affordance. SAME component, a row-fit layout (vs the card's bottom corners).
            // A single-child centering flex box: it carries the hover scrim fill as well as the centering, so it stays a
            // flex box rather than folding into the enclosing ZStack.
            Element rowFab = new BoxEl
            {
                Width = props.Inner, Height = props.Inner, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
                Fill = WaveeOnMedia.CoverScrim,
                Children = [ MediaCard.PlayFab(Toggle, playingHere ? Icons.Pause : Icons.Play, props.Fab) ],
            };
            return new BoxEl
            {
                Width = props.Inner, Height = props.Inner, ZStack = true,
                Children =
                [
                    active
                        ? new BoxEl { Width = props.Inner, Height = props.Inner, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f, Children = [ EqPill(pauseOnHover: true) ] }
                        : new BoxEl(),
                    rowFab,
                ],
            };
        }

        if (props.Cover)
            // FILL the cover rather than sizing to a captured `_inner`: this overlay rides in an Embed.Comp whose
            // template closure FREEZES at first mount, so a captured width goes stale when the card later re-fits wider
            // (the FAB then floats at the center of the grown cover). Grow=1 + ZStack-fill children always match the
            // live cover box, no matter the fitted width.
            return new BoxEl
            {
                Grow = 1f, ZStack = true,
                Children =
                [
                    new BoxEl   // equalizer — bottom-left, only when this card is the active context
                    {
                        Grow = 1f, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
                        Padding = new Edges4(MediaCard.FabInset, 0f, 0f, MediaCard.FabInset),
                        Children = [ active ? EqPill(pauseOnHover: false) : new BoxEl() ],
                    },
                    new BoxEl   // FAB — bottom-right, revealed on hover
                    {
                        Grow = 1f, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                        Padding = new Edges4(0f, 0f, MediaCard.FabInset, MediaCard.FabInset),
                        Children = [ reveal ],
                    },
                ],
            };

        // Inline trailing slot (QuickPick): the equalizer at rest (hidden on hover) under the hover-revealed FAB.
        return new BoxEl
        {
            Width = props.Fab, Height = props.Fab, ZStack = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                active
                    ? new BoxEl
                    {
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f,
                        Children =
                        [
                            WaveeEqualizer.Of(playing, () => ctx is {} a ? a.Value.Ink : Tok.AccentTextPrimary, 14f, paused: hoverPaused),
                        ],
                    }
                    : new BoxEl(),
                reveal,
            ],
        };
    }

    internal static bool Matches(string uri, string? contextUri, Track? track)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (!string.IsNullOrEmpty(contextUri) && string.Equals(uri, contextUri, StringComparison.OrdinalIgnoreCase)) return true;
        if (track is null) return false;
        if (string.Equals(uri, track.Uri, StringComparison.OrdinalIgnoreCase)) return true;   // a TRACK row lights up when ITS track is the one playing
        if (string.Equals(uri, track.Album.Uri, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var a in track.Artists)
            if (string.Equals(uri, a.Uri, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
