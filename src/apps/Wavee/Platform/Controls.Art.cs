// ── Platform/Controls.Art.cs ───────────────────────────────────────────────────────────────────────────────────────
// MediaCard, PagedShelf, chips, stat tiles, countdowns, face piles, rich text, the shimmer
//
// Role: UI
// Owner: L
// Wave: 4
// Budget: 1400 lines
// Spec: DERIVED
//
// ── THE CARD PLATE IS SHARED, AND THAT IS THE POINT (ch 02 §9.2) ─────────────────────────────────────────────────────
//
// Every rectangular media card in the app composes THIS plate instead of hand-rolling its own, so the hover grammar
// cannot drift between surfaces. §2's split of "entity rows/cards" into five `X.UI.cs` files is a drift hazard for
// exactly this surface: if Album, Artist, Playlist, Show and Search each grew a card, five Wave-5 owners would produce
// five hover physics. THE CORRECTION, stated here so it cannot be missed: the card SHELL (physics, plate, corner "…",
// the FABs, the now-playing overlay, the shelf/grid/row skins, the extent maths) is THIS file's, in Wave 4; each
// `X.UI.cs` contributes only an ADAPTER that fills in cover / title / subtitle / menu / drag for its handle.
//
// That is also why nothing in this file takes a handle. A card is given a cover URL, a title string and two elements —
// so an entity adapter reads its OWN columns at build time and this file never learns what an album is.
//
// ── WHAT MUST NOT BE SIMPLIFIED (ch 02 §9) ───────────────────────────────────────────────────────────────────────────
//
//  1. THE HOVER PLATE IS NOT A `HoverFill`. It is a separate, non-hit-testable SIBLING under the content, carrying the
//     fill, the stroke AND the shadow, cross-faded 0 → 1. Collapsing it into a HoverFill on the card root loses the
//     stroke and the halo — and a parent clip would then shave the halo, which is why there is NO `ClipToBounds` on a
//     card root: the plate carries the shadow and every child self-clips.
//  2. `HoverElevatePaint`. Without it a LATER sibling card overpaints the hovered card's lift halo. It is the design's
//     z-index and it changes neither layout nor hit-testing.
//  3. THE ZOOM CONTAINER MUST NOT PUSH ITS OWN ROUNDED CLIP. The renderer clamps image/gradient primitives to the
//     TOPMOST rounded clip only; a clip here would ride the scale out past the card and show square slivers at the
//     corners.
//  4. CIRCULAR CARDS DO NOT CLIP THE OVERLAY LAYER — the FAB sits inside the cover's RECTANGLE but outside the avatar
//     circle, so the clip is conditional on the shape.
//  5. `Grow = 1` on the shell is what makes a measured shelf stretch every card to the TALLEST card's height: uniform
//     panels, exact, with no reserved worst case.

using System.Collections.Generic;
using System.Text;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Controls
{
    // ══ 1. THE PLAYBACK RELATION SEAM ════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a card needs to know about playback, and nothing more.
    ///
    /// <para><see cref="HasActiveContext"/> is the COARSE gate and it is load-bearing: an idle overlay reads it FIRST
    /// and bails, so it never joins the hot identity fan-out and a track skip does not re-render every overlay on
    /// screen. <see cref="RelatesTo"/> is the LOOSE relation (may this card light up?) and <see cref="Owns"/> is the
    /// STRICT one (may a click here PAUSE?). The two must stay distinct: a card that relates may reveal an equalizer; a
    /// card that owns may toggle the transport.</para></summary>
    public sealed record PlaybackSeam(
        IReadSignal<bool> HasActiveContext,
        IReadSignal<bool> IsPlaying,
        Func<string, bool> RelatesTo,
        Func<string, bool> Owns);

    /// <inheritdoc cref="PlaybackSeam"/>
    public static PlaybackSeam? NowPlaying { get; set; }

    // ══ 2. THE CARD PLATE ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>THE card motion contract. Lift, press, elevate — applied by every rectangular media card (and by Home's
    /// authored skins) so the physics cannot drift between surfaces.
    /// <para>A LIFT (a −4 DIP translate), not a scale: a scaled card resamples its own cover every frame of the hover.
    /// The cover's own zoom is a SEPARATE, inner node (<see cref="CardCover"/>) precisely so the plate can lift while
    /// the art scales. The press sinks it back 3 DIP and breathes it to 0.99 — acknowledgement, not a bounce.</para>
    /// <para><c>HoverElevatePaint</c> is the design's z-index: a hovered card's halo must paint OVER its later
    /// siblings, which changes neither layout nor hit-testing.</para></summary>
    public static BoxEl CardPhysics(BoxEl card) => card with
    {
        HoverElevatePaint = true,
        WhileHover = new MotionTarget { OffsetY = -4f },
        WhilePressed = new MotionTarget { Scale = 0.99f, OffsetY = -1f },
        Transition = MotionTok.ControlNormal,
        Cursor = CursorId.Hand,
    };

    /// <summary>The card SHELL: a ZStack of [hover plate, content]. The plate is a separate, non-hit-testable sibling
    /// UNDER the content carrying fill + stroke + shadow, cross-faded 0 → 1 — see rule 1 in the file header for why it
    /// is not a <c>HoverFill</c>.
    /// <para>NO <c>ClipToBounds</c> on the root: the plate carries the shadow, and every child self-clips.</para>
    /// <para>The root IS a focus stop with a button role — 0.2.9's card body was neither, so a keyboard user could not
    /// reach a card at all (ch 02 §9.12, one of the defects this file FIXES rather than ports). Its NAME is its own
    /// title text, which the automation tree reads off the content.</para></summary>
    public static BoxEl CardShell(Element content, Action onClick, DragSource? drag = null)
        => CardPhysics(new BoxEl
        {
            ZStack = true, Grow = 1f, Corners = CornerRadius4.All(Radii.Card),
            Role = AutomationRole.Button, Focusable = true,
            FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = onClick,
            Draggable = drag,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, HitTestVisible = false,
                    Opacity = 0f, HoverOpacity = 1f,
                    HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                    Corners = CornerRadius4.All(Radii.Card),
                    Fill = Tok.FillCardDefault,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Shadow = Elevation.Card,
                },
                content,
            ],
        });

    /// <summary>The cover STACK inside a card: the art, the overlay layer, and the corner "…". The art's zoom lives
    /// HERE, on its own node, so the plate can lift while the art scales — and this node pushes NO rounded clip of its
    /// own (rule 3).
    /// <para><paramref name="circular"/> flips the clip OFF (rule 4): the FAB sits inside the cover's RECTANGLE but
    /// outside the avatar circle, so clipping it would cut the affordance in half.</para></summary>
    public static Element CardCover(Element art, float edge, bool circular, Element? overlay = null,
                                    Element? corner = null)
    {
        var kids = new List<Element>(3) { art };
        if (overlay is not null) kids.Add(overlay);
        if (corner is not null) kids.Add(corner);
        return new BoxEl
        {
            ZStack = true, Width = edge, Height = edge, ClipToBounds = !circular,
            HoverScale = Design.Reduced ? 1f : 1.04f,
            HoverDurationMs = Design.Motion.Standard, HoverEasing = Easing.SmoothOut,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The top-right "…" on a card's artwork: the scrim-plated 30-circle, revealed on hover. With no handler it
    /// re-enters the context funnel and opens the card's ATTACHED menu, anchored at the button.</summary>
    public static Element MoreCorner(Action? onClick = null) => new BoxEl
    {
        ZStack = true, Grow = 1f, HitTestPassThrough = true,
        AlignItems = FlexAlign.Start, Justify = FlexJustify.End,
        Padding = Edges4.All(Spacing.S),
        Children =
        [
            Named(CoverActionFab(Icons.More, onClick, requestsContext: onClick is null) with
                { Opacity = 0f, HoverOpacity = 1f }, Loc.Get(Strings.Common.More)).Skeletonized(false),
        ],
    };

    /// <summary>A small neutral capsule beside a row's text — the kind tag ("Podcast", "Audiobook"). On a card SURFACE,
    /// not on media: the subtle fill and tertiary ink, never the on-media scrim ladder.</summary>
    public static Element RowChip(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.S, 2f, Spacing.S, 2f),
        Corners = Radii.PillAll, Fill = Tok.FillSubtleSecondary,
        Children = [new TextEl(text) { Size = 11f, Weight = 600, Color = Tok.TextTertiary, MaxLines = 1 }],
    };

    // ══ 3. THE NOW-PLAYING OVERLAY ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The card's play affordance and — only when the card RELATES to playback — its equalizer pill.
    ///
    /// <para><b>The FAB mounts EAGERLY.</b> It was once mount-gated on the hover signal, and two failure modes followed:
    /// a card paged in under a STATIONARY pointer never armed at all, and an exit edge that fired before release
    /// unmounted the very node being clicked. One hover-faded box plus a glyph per card is the price of a control that
    /// always works.</para>
    ///
    /// <para><b>ONE SHAPE ON BOTH LEGS</b> — <c>[equalizer slot, FAB]</c>, with an empty box standing in — so the
    /// reconciler keeps the FAB's node while the equalizer mounts and unmounts beside it. A press in flight must never
    /// lose its target to a playback edge.</para>
    ///
    /// <para><b>The relation is read COARSE-FIRST.</b> An idle overlay reads only <c>HasActiveContext</c> and returns;
    /// it never subscribes to the hot identity signal, so an unrelated track skip re-runs one cheap read per card and
    /// schedules no render.</para></summary>
    public static Element NowPlayingOverlay(string uri, Action? onPlay, float fab = 44f, bool centred = true,
                                            string? playName = null)
        => Embed.Comp(new OverlayProps(uri, onPlay, fab, centred, playName ?? Loc.Get(Strings.Detail.Play)),
                      static () => new NowPlayingOverlayHost());

    sealed record OverlayProps(string Uri, Action? OnPlay, float Fab, bool Centred, string PlayName);

    sealed class NowPlayingOverlayHost : Component
    {
        public override Element Render()
        {
            var p = UsePropsOrDefault<OverlayProps>();
            if (p is null) return new BoxEl();
            var pb = NowPlaying;

            // COARSE first. `HasActiveContext` is one bool for the whole app; `RelatesTo`/`Owns` are per-card and only
            // asked when something is actually playing.
            bool anything = pb is not null && pb.HasActiveContext.Value;
            bool relates = anything && pb!.RelatesTo(p.Uri);
            bool owns = relates && pb!.Owns(p.Uri);
            bool playingHere = owns && pb!.IsPlaying.Value;

            Element eq = relates
                ? new BoxEl
                {
                    Shrink = 0f, Padding = new Edges4(6f, 4f, 6f, 4f),
                    Corners = Radii.PillAll, Fill = Design.OnMedia.ScrimRest,
                    BorderWidth = 1f, BorderColor = Design.OnMedia.Stroke,
                    Children = [Equalizer(pb!.IsPlaying, () => Design.OnMedia.Ink, 14f)],
                }
                // The STAND-IN. Keeping the slot occupied is what stops the FAB's node being re-keyed when the
                // equalizer appears beside it.
                : new BoxEl();

            Element play = Named(PlayFab(p.OnPlay ?? NoOp, glyph: playingHere ? Icons.Pause : Icons.Play, size: p.Fab) with
            {
                Opacity = playingHere ? 1f : 0f, HoverOpacity = 1f,
                HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.SmoothOut,
            }, p.PlayName).Skeletonized(false);   // a hover-only affordance is not skeleton content

            return new BoxEl
            {
                // Grow + ZStack fill, NEVER a captured width: the card re-fits wider after mount, and a captured width
                // leaves the FAB floating mid-cover.
                Grow = 1f, ZStack = true, HitTestPassThrough = true,
                AlignItems = p.Centred ? FlexAlign.Center : FlexAlign.End,
                Justify = p.Centred ? FlexJustify.Center : FlexJustify.End,
                Padding = p.Centred ? default : Edges4.All(Spacing.S),
                Children = [eq, play],
            };
        }
    }

    // ══ 4. THE THREE CARD SKINS ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Everything a card renders, filled in by an entity ADAPTER. A record, because a virtualized parent
    /// re-pushes it per bind and a frozen constructor field would keep pointing at the slot's FIRST row.</summary>
    public sealed record CardData(
        string Uri,
        string Title,
        Element? Subtitle,
        string? CoverUrl,
        Action OnClick,
        Action? OnPlay = null,
        bool Circular = false,
        DragSource? Drag = null,
        bool ShowMenu = true,
        int TitleLines = 1,
        Element? CoverOverride = null);

    /// <summary>THE virtualized shelf's cross extent: <c>cardW + 72</c> — 6 gutter + 20 plate padding + the square
    /// cover + 8 gap + 20 title + 2 + 32 subtitle.
    /// <para>The RENDERER and the ESTIMATOR must both call this. An estimate that disagrees with the rendered height
    /// makes a measured virtual list re-pin its scroll anchor mid-scroll, which reads as the feed jumping under the
    /// cursor.</para></summary>
    public static float ShelfHeight(float cardW) => cardW + 72f;

    /// <summary>The GRID cell's extra height above its square cover: the label block's own overhead. Shared by Home,
    /// Browse and Search, for the same estimator reason <see cref="ShelfHeight"/> is.</summary>
    public static float GridCardChromeFor(int titleLines, bool hasSubtitle)
        => GridLabelOverhead + titleLines * GridTitleLineH + (hasSubtitle ? GridSubtitleBlockH : 0f);

    /// <inheritdoc cref="GridCardChromeFor"/>
    public const float GridTitleLineH = 20f, GridSubtitleBlockH = 18f, GridLabelOverhead = 28f;

    /// <summary>A whole shelf ROW's height for a page estimator: the header, the shelf, and the gaps around it. The
    /// renderer and the estimator MUST return the same number.</summary>
    public static float ShelfExtent(float cardW) => 32f + ShelfHeight(cardW) + 2f * Spacing.M;

    /// <summary>The SHELF card — a fixed-width cell inside a horizontally paged shelf.</summary>
    public static Element ShelfCard(CardData d, float cardW)
        => Embed.Comp(new ShelfCardProps(d, cardW), static () => new ShelfCardHost());

    sealed record ShelfCardProps(CardData Data, float CardW);

    sealed class ShelfCardHost : Component
    {
        public override Element Render()
        {
            var p = UsePropsOrDefault<ShelfCardProps>();
            if (p is null) return new BoxEl();
            var d = p.Data;
            float inner = p.CardW - 2f * Spacing.S;
            Element art = d.CoverOverride
                ?? Artwork(d.CoverUrl, inner, inner, d.Circular ? inner / 2f : Radii.Card, decodePx: ShelfDecodePx);

            return new BoxEl
            {
                // The gutter reserves the lift's halo so a hovered card is not clipped by the shelf's own viewport.
                Padding = new Edges4(0f, Spacing.XS, 0f, 2f),
                Width = p.CardW, Shrink = 0f,
                Children =
                [
                    CardShell(new BoxEl
                    {
                        Direction = 1, Gap = Spacing.S, Grow = 1f,
                        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
                        Children =
                        [
                            CardCover(art, inner, d.Circular,
                                      overlay: NowPlayingOverlay(d.Uri, d.OnPlay, 44f, centred: true),
                                      corner: d.ShowMenu ? MoreCorner() : null),
                            Labels(d, inner),
                        ],
                    }, d.OnClick, d.Drag),
                ],
            };
        }
    }

    /// <summary>Shelf covers decode at a FIXED 256 square — stable across responsive card widths, so a resize never
    /// forces a re-decode, and a card and a detail cover that pass the same literal resolve to ONE cached texture.</summary>
    public const int ShelfDecodePx = 256;

    /// <summary>The GRID card: the width-agnostic twin of the shelf card, for a fluid grid cell whose exact width is not
    /// known at template time.</summary>
    public static Element GridCard(CardData d)
        => CardShell(new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
            Children =
            [
                new BoxEl
                {
                    ZStack = true, ClipToBounds = !d.Circular,
                    Children = d.ShowMenu
                        ? [d.CoverOverride ?? ArtworkFill(d.CoverUrl, d.Circular ? Radii.Full : Radii.Card),
                           NowPlayingOverlay(d.Uri, d.OnPlay, 44f, centred: true),
                           MoreCorner()]
                        : [d.CoverOverride ?? ArtworkFill(d.CoverUrl, d.Circular ? Radii.Full : Radii.Card),
                           NowPlayingOverlay(d.Uri, d.OnPlay, 44f, centred: true)],
                },
                Labels(d, float.NaN),
            ],
        }, d.OnClick, d.Drag);

    // The label block. The title is capped at the caller's line budget and the subtitle at TWO with an explicit width,
    // and the highlight arm gets the SAME budget as the plain title — so a filter narrowing a grid does not reflow a
    // card from two lines to one.
    static Element Labels(CardData d, float inner) => new BoxEl
    {
        Direction = 1, Gap = 2f, MinWidth = 0f,
        AlignItems = d.Circular ? FlexAlign.Center : FlexAlign.Start,
        Children = d.Subtitle is null
            ? [Design.Type.CardTitle(d.Title) with
                { Width = inner, MaxLines = d.TitleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
            : [Design.Type.CardTitle(d.Title) with
                { Width = inner, MaxLines = d.TitleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
               d.Subtitle],
    };

    /// <summary>The horizontal MEDIA ROW — search results, recents, a rail list. FOUR heights, not one: 64 plain,
    /// 112 large (a "top result" hero), auto with a 64 floor when it carries an inline detail, and auto with a 72 floor
    /// for the below-art arm.</summary>
    public static Element MediaRow(CardData d, float artEdge = 48f, Element? trailing = null, Element? eyebrow = null,
                                   Element? meta = null, bool large = false, bool plated = true)
    {
        float edge = large ? 84f : artEdge;
        var text = new List<Element>(4);
        if (eyebrow is not null) text.Add(eyebrow);
        text.Add(large
            ? Design.Type.PageHero(d.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
            : Design.Type.TrackTitle(d.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        if (d.Subtitle is not null) text.Add(d.Subtitle);
        if (meta is not null) text.Add(meta);

        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                ZStack = true, Width = edge, Height = edge, Shrink = 0f, ClipToBounds = !d.Circular,
                Children =
                [
                    d.CoverOverride ?? Artwork(d.CoverUrl, edge, edge, d.Circular ? edge / 2f : Radii.Control),
                    NowPlayingOverlay(d.Uri, d.OnPlay, large ? 44f : 30f, centred: true),
                ],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = large ? Spacing.S : 2f,
                Children = text.ToArray(),
            },
        };
        if (trailing is not null) kids.Add(trailing);

        var row = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            MinHeight = large ? 112f : 64f, MinWidth = 0f,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S),
            Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Focusable = true,
            FocusVisualMargin = Design.FocusInsetRow,
            Cursor = CursorId.Hand, OnClick = d.OnClick, Draggable = d.Drag,
            Children = kids.ToArray(),
        };
        // A near-full-width row's press acknowledgement is its PressedFill alone — never a PressScale. Even a 2% swing
        // moves each edge several DIP in opposite directions on a ~1000-px row and visibly blurs the title mid-scale.
        return plated ? row.Interactive(Interaction.ListRow) : row;
    }

    // ══ 5. THE COVER SHIMMER ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The neutral shimmer cover tile. A Component (granular re-render) so it can read the image LOAD STATE and
    /// start/stop its own opacity breathe accordingly: the keyframe effect is keyed by <c>loading</c>, so when the art
    /// becomes ready the looping pulse is replaced IN PLACE by a finite flat track and the frame loop can quiesce (the
    /// engine's "no forever-loop" rule).
    ///
    /// <para>It LATCHES <c>settled</c> and stops calling the image hook, so a loaded cover unsubscribes from the global
    /// image epoch — without that, one image's status change re-renders every loaded cover in the grid.</para>
    ///
    /// <para>WEAK GPU: the placeholder is held FLAT even while loading. A per-frame opacity loop keeps the render loop
    /// hot, and on a weak/UMA part that is exactly what starves the decode uploads it is waiting for.</para></summary>
    public sealed class CoverShimmer : Component
    {
        static readonly Keyframe[] Breathe = [new(0f, 1f), new(0.5f, 0.5f), new(1f, 1f)];
        static readonly Keyframe[] Flat = [new(0f, 1f), new(1f, 1f)];

        readonly string? _url;
        readonly int _decodeW, _decodeH;
        readonly float _w, _h, _corners;

        public CoverShimmer(string? url, int decodeW, int decodeH, float w, float h, float corners)
        { _url = url; _decodeW = decodeW; _decodeH = decodeH; _w = w; _h = h; _corners = corners; }

        public override Element Render()
        {
            var settled = UseRef(false);
            bool loading = false;
            if (!settled.Value && _url is { Length: > 0 } url)
            {
                // Share the displayed image's decode handle (same source + decode target) so this reads the SAME load
                // state and forks no second decode. The image hook consumes no hook cell, so the conditional call is
                // safe.
                var binding = UseImage(url, _decodeW, _decodeH);
                var state = binding.State;
                if (state == ImageState.Ready) settled.Value = true;
                else if (state == ImageState.Failed && binding.Failure != ImageFailureKind.Canceled) settled.Value = true;
                else loading = state is ImageState.None or ImageState.Pending;
            }
            // Deliberately NOT gated on reduced motion: this is a LOADING INDICATOR, not a flourish, and it stops on
            // settle anyway.
            bool shimmer = loading && !GpuProfile.IsWeak;
            UseKeyframes(AnimChannel.Opacity, shimmer ? Breathe : Flat, shimmer ? 1000f : 1f, shimmer,
                         DepKey.From(shimmer));
            return new BoxEl
            {
                Width = _w, Height = _h, Corners = CornerRadius4.All(_corners),
                // Tint is PAINT-ONLY: the fill bind reads the per-key watch signal, so a landed grading marks
                // PaintDirty on exactly this tile — never a re-render, and never the global epoch fan-out that used to
                // re-render every still-loading cover in the grid at once.
                Fill = Design.WatchedPlaceholder(_url),
            };
        }
    }

    // ══ 6. CHIPS ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The filter-chip rail's own metrics. <see cref="ChipRailExtent"/> is what the chip bar contributes to a
    /// stacked detail chrome column, INCLUDING its bottom semantic gap — one number, so a page's layout arithmetic and
    /// the rail itself cannot disagree.</summary>
    public const float ChipHeight = 32f, ChipRailHeight = 40f;
    /// <inheritdoc cref="ChipHeight"/>
    public const float ChipRailExtent = ChipRailHeight + Spacing.S;

    /// <summary>One filter chip. <paramref name="available"/> false renders it SHOWN AND DISABLED rather than dropping
    /// it: a curated filter set is library-scoped and routinely names concepts whose rows have not been enriched yet, so
    /// dropping those hid the whole bar on a cold list. They become live as enrichment lands.</summary>
    public static Element Chip(string label, bool selected, bool available, Action? onClick) => new BoxEl
    {
        Role = AutomationRole.Button, Focusable = available, Cursor = available ? CursorId.Hand : CursorId.Arrow,
        IsEnabled = available,
        FocusVisualMargin = Design.FocusInsetBordered,
        // Shrink 0 is LOAD-BEARING on a non-wrapping row: without it flex compresses every pill to fit the viewport and
        // the labels ellipsise instead of the rail overflowing, which is the opposite of what the scroller is for.
        Height = ChipHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
        Corners = Radii.FullAll,
        Fill = selected ? Tok.AccentDefault : Tok.FillControlDefault,
        HoverFill = !available ? Tok.FillControlDefault : selected ? Tok.AccentSecondary : Tok.FillControlSecondary,
        BorderWidth = 1f,
        BorderColor = selected ? ColorF.Transparent : Tok.StrokeControlDefault,
        HoverBorderColor = !available ? Tok.StrokeControlDefault : selected ? ColorF.Transparent : Tok.AccentDefault,
        HoverScale = Design.Motion.ScaleSubtle.HoverIf(available),
        HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
        PressScale = Design.Motion.ScaleSubtle.PressIf(available),
        OnClick = onClick,
        Children =
        [
            new TextEl(label)
            {
                Size = 13f, Weight = (ushort)(selected ? 600 : 400), MaxLines = 1,
                // 0.2.9 left this without a Trim, so a long concept name overflowed its own capsule (ch 02 §9.13).
                Trim = TextTrim.CharacterEllipsis,
                Color = selected ? Tok.TextOnAccentPrimary : available ? Tok.TextPrimary : Tok.TextDisabled,
            },
        ],
    };

    /// <summary>The chip RAIL: ONE line that scrolls, never a wrapped block. A curated set runs to 15+ concepts, which
    /// wrapped into a second and third row and pushed the list down the page.
    /// <para>The EDGE FADE is the overflow affordance — offset-driven and live per frame, so it says "more this way"
    /// without adding chrome to a row that is already dense. <paramref name="scrollKey"/> scopes the horizontal offset
    /// so each list remembers its own position and a navigation does not inherit the previous one.</para>
    /// <para>Selection is EXCLUSIVE (All + at most one chip): these are a LENS, not accumulating constraints — two
    /// genres ANDed almost always yields nothing, and users read a second tap as "switch", not "narrow".</para></summary>
    public static Element ChipRail(IReadOnlyList<Element> chips, string scrollKey)
        => ScrollView(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [.. chips],
        }, horizontal: true) with
        {
            Grow = 0f, Height = ChipRailHeight, AutoEdgeFade = true, SuppressScrollBar = true,
            Margin = new Edges4(0f, 0f, 0f, Spacing.S),
            ScrollKey = scrollKey,
        };

    // ══ 7. THE STAT TILE ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>THE 18/800-value-over-11-caption stat tile — it replaced FOUR hand copies that had drifted onto
    /// diverging motion specs.
    ///
    /// <para>The PARENT authors the width (a grid column, an equal flex share); the tile NEVER measures to its text: the
    /// value box and its run both carry <c>MinWidth = 0</c> so a long value ELLIPSISES (or, with
    /// <paramref name="wrapValue"/>, wraps to two lines) INSIDE the tile instead of running past its border. 0.2.9's
    /// <c>MaxLines</c> + ellipsis never engaged, because the stack and the run kept their own intrinsic width.</para>
    ///
    /// <para>Value swaps CROSS-FADE IN PLACE inside a clipped box whose size is the tile's, so a swap can never change
    /// measurement — and the value box is keyed on the VALUE, so a per-second countdown re-enters the numeral instead of
    /// remounting the tile.</para></summary>
    public static Element StatTile(string key, string value, string caption, bool wrapValue = false,
                                   Element? trailing = null)
    {
        Element valueRun = new TextEl(value)
        {
            Size = 18f, Weight = 800, Color = Tok.TextPrimary, MinWidth = 0f,
            MaxLines = wrapValue ? 2 : 1, Trim = TextTrim.CharacterEllipsis,
            Wrap = wrapValue ? TextWrap.Wrap : TextWrap.NoWrap,
        };
        Element valueBox = new BoxEl
        {
            // ZStack on the box itself, not through a helper: the value box must carry MinWidth, and an unconstrained
            // stack measures to the WIDER of the outgoing/incoming runs mid-swap.
            ZStack = true, MinWidth = 0f,
            Children = [new BoxEl { Key = "v:" + value, Animate = MotionRecipes.TextSwap, Children = [valueRun] }],
        };
        Element captionRun = new TextEl(caption)
            { Size = 11f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

        return new BoxEl
        {
            Key = "fact:" + key,
            Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, ClipToBounds = true,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = trailing is null ? [valueBox, captionRun] : [valueBox, captionRun, trailing],
        };
    }

    // ══ 8. THE COUNTDOWNS ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The per-unit remainder, NOT a cumulative total. Clamped at zero so a tick that lands a hair past the
    /// instant (the released gate flips on the next render, not mid-frame) can never render a negative tile.</summary>
    public static (int Days, int Hours, int Minutes, int Seconds) Breakdown(TimeSpan left)
        => (Math.Max(0, left.Days), Math.Max(0, left.Hours), Math.Max(0, left.Minutes), Math.Max(0, left.Seconds));

    /// <summary>The PRE-RELEASE countdown: four stat tiles that wrap. Days take their natural width (a wait can be 100+
    /// days); hours/minutes/seconds are zero-padded by clock convention.
    /// <para>KEY IT ON THE RELEASE INSTANT at the mount site: the anchor freezes at mount, so a new window must
    /// remount.</para></summary>
    public static Element PreReleaseCountdown(TimeSpan remaining)
    {
        var (d, h, m, s) = Breakdown(remaining);
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, Wrap = true, MinWidth = 0f,
            Children =
            [
                StatTile("days", d.ToString(), Loc.Get(Strings.Detail.PreReleaseUnitDays)),
                StatTile("hours", h.ToString("D2"), Loc.Get(Strings.Detail.PreReleaseUnitHours)),
                StatTile("minutes", m.ToString("D2"), Loc.Get(Strings.Detail.PreReleaseUnitMinutes)),
                StatTile("seconds", s.ToString("D2"), Loc.Get(Strings.Detail.PreReleaseUnitSeconds)),
            ],
        };
    }

    /// <summary>The FLIP countdown's digit-cell heights. Two page layouts restate these as literals because both are
    /// engine-free, test-included sources that cannot reference this component: changing one means changing all
    /// three.</summary>
    public const float FlipHeroRowHeight = 28f, FlipCompactRowHeight = 20f;

    /// <summary>Interned numerals: a digit cell's KEY and its TEXT. A keyed remount per tick must not also mint a string
    /// per second.</summary>
    static readonly string[] Numerals = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];

    /// <summary>ONE digit cell of the flip countdown: the old numeral slides up and out while the new one rises in (a
    /// keyed remount per cell — the reconciler keeps the exiting orphan painted UNDER the entering digit inside the
    /// clipped cell).
    /// <para>Every numeral sits CENTRED in a FIXED-WIDTH cell, because the text seam has no tabular figures: nothing
    /// reflows as the digits spin. Reduced motion degrades the slide to a cross-fade through the motion token's own
    /// policy — never a hook branch.</para></summary>
    public static Element FlipDigit(int digit, float rowHeight, ColorF ink)
    {
        string n = Numerals[Math.Clamp(digit, 0, 9)];
        return new BoxEl
        {
            Width = rowHeight * 0.62f, Height = rowHeight, ClipToBounds = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                new BoxEl
                {
                    Key = n,
                    Animate = new LayoutTransition(
                        TransitionChannels.Position | TransitionChannels.Opacity,
                        TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut),
                        Enter: new EnterExit(Dy: rowHeight * 0.6f, Opacity: 0f, Active: true),
                        Exit: new EnterExit(Dy: -rowHeight * 0.6f, Opacity: 0f, Active: true)),
                    Children = [new TextEl(n)
                        { Size = rowHeight, LineHeight = rowHeight, Weight = 350, Color = ink,
                          FontFamily = "Segoe UI Variable Display" }],
                },
            ],
        };
    }

    // ══ 9. FACE PILES ════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The overlapping-avatar strip. The constants below are what three separate piles had each hand-rolled; naming them
    // once is what stops the three drifting into three different overlaps.

    /// <summary>The portrait itself.</summary>
    public const float FaceAvatar = 28f;
    /// <summary>The ring drawn around each portrait, in the SURFACE fill, so overlapping faces stay separable.</summary>
    public const float FaceRing = 2f;
    /// <summary>The framed diameter (portrait + ring on both sides) — what the row actually reserves per face.</summary>
    public const float FaceOuter = FaceAvatar + FaceRing * 2f;
    /// <summary>How far each face after the first is pulled back over its predecessor.</summary>
    public const float FaceOverlap = 12f;
    /// <summary>Advance per extra frame. The width of n frames is <c>FaceOuter + (n − 1) × FaceStep</c>.</summary>
    public const float FaceStep = FaceOuter - FaceOverlap;
    /// <summary>The house default: four faces then a count. A surface designed around a different number passes its own
    /// — the GEOMETRY is what must not vary, not the census.</summary>
    public const int FaceMaxVisible = 4;
    /// <summary>The instant-tooltip rung the whole facts surface shares (a pile, a sparkline, a lens row). A face is not
    /// a label; without a tooltip an overlapped avatar names nobody, and a delayed one names them too late.</summary>
    public const float FaceTipDelayMs = 0f;

    /// <summary>How many overlapping frames fit in <paramref name="width"/>. Unmeasured or narrower than one frame ⇒ 1,
    /// never 0.</summary>
    public static int SlotsIn(float width)
        => width < FaceOuter ? 1 : 1 + (int)MathF.Floor((width - FaceOuter) / FaceStep);

    /// <summary>How many PORTRAITS to paint for <paramref name="total"/> people in <paramref name="width"/>. When anyone
    /// would clip, one slot stays the "+N" frame so the strip never overflows.</summary>
    public static int VisibleFaces(float width, int total)
    {
        if (total <= 0) return 0;
        int slots = SlotsIn(width);
        if (total <= slots) return total;
        return Math.Max(1, Math.Min(slots - 1, total - 1));
    }

    /// <summary>One person in a pile: a display name (the initials fallback AND the accessible label) and a portrait
    /// url, which is null far more often than not — a pile must read correctly from names alone.
    /// <para><see cref="OnClick"/> makes the face an AFFORDANCE rather than an illustration. Null is a pile that is
    /// purely a picture: no hand cursor, no focus stop, no press feedback.</para></summary>
    public readonly record struct Face(string Name, string? ImageUrl, Action? OnClick = null, bool Selected = false,
                                       string? Tip = null);

    /// <summary>The strip: up to <paramref name="maxVisible"/> framed portraits, then a "+N" frame.
    /// <para><paramref name="overflow"/> defaults to what the list carries beyond the visible cut; pass it explicitly
    /// when the count comes from somewhere the caller can see and this list cannot. An EMPTY list renders NOTHING — an
    /// empty ring is not a face pile.</para></summary>
    public static Element FacePile(IReadOnlyList<Face> faces, int maxVisible = FaceMaxVisible, int? overflow = null,
                                   Action? onOverflow = null, string? overflowTip = null)
    {
        if (faces is null || faces.Count == 0) return new BoxEl();

        int visible = Math.Min(Math.Max(1, maxVisible), faces.Count);
        int extra = Math.Max(0, overflow ?? faces.Count - visible);
        var kids = new Element[visible + (extra > 0 ? 1 : 0)];
        // Keyed BY POSITION, not by name: two credits can share a display name (and a nameless one has no key at all),
        // and a slot that keeps its identity across a recount updates its portrait in place instead of remounting.
        for (int i = 0; i < visible; i++) kids[i] = AvatarFrame(faces[i], i, i == 0);
        if (extra > 0) kids[visible] = OverflowFrame(extra, visible == 0, onOverflow, overflowTip);
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids };
    }

    static Element AvatarFrame(Face f, int index, bool first)
    {
        bool live = f.OnClick is not null;
        // The RING is the frame's own fill — that is what makes overlapping faces separable — so the SELECTED state is
        // painted by swapping that ring to accent: no extra node, and the face keeps its exact geometry whether it is
        // chosen, hovered or inert.
        Element frame = new BoxEl
        {
            Key = "face:" + index,
            Width = FaceOuter, Height = FaceOuter, Shrink = 0f, Corners = CornerRadius4.All(FaceOuter / 2f),
            Fill = f.Selected ? Tok.AccentDefault : Tok.FillSolidBase, Padding = Edges4.All(FaceRing),
            // The negative margin stays on the FRAME, under any tooltip wrapper: a flex item's outer size includes its
            // margins, so a wrapper shrink-wraps to (Outer − Overlap) and paints the frame Overlap to the left of its
            // own origin — the same overlap the unwrapped pile has, with no second geometry to keep in step.
            Margin = new Edges4(first ? 0f : -FaceOverlap, 0f, 0f, 0f),
            Role = live ? AutomationRole.Button : AutomationRole.None,
            Focusable = live,
            Cursor = live ? CursorId.Hand : CursorId.Arrow,
            // A face that is not live carries no margin because it carries no focus stop either — the conditional arm
            // this inset must keep supporting.
            FocusVisualMargin = live ? Design.FocusInsetRow : default,
            HoverFill = !live ? ColorF.Transparent : f.Selected ? Tok.AccentSecondary : Tok.AccentSubtle,
            HoverScale = live ? Design.Motion.ScaleStandard.Hover : 1f,
            PressScale = live ? Design.Motion.ScaleStandard.Press : 1f,
            HoverDurationMs = Design.Motion.Faster,
            OnClick = f.OnClick,
            Children = [PersonPicture.Create("", FaceAvatar, displayName: f.Name, imageSourcePath: f.ImageUrl)],
        };
        return f.Tip is { Length: > 0 } tip
            ? ToolTip.Wrap(frame, tip, showDelayMs: FaceTipDelayMs) with { Key = "face:" + index }
            : frame;
    }

    static Element OverflowFrame(int n, bool first, Action? onClick, string? tip)
    {
        bool live = onClick is not null;
        Element frame = new BoxEl
        {
            Key = "face:more",
            Width = FaceOuter, Height = FaceOuter, Shrink = 0f, Corners = CornerRadius4.All(FaceOuter / 2f),
            Fill = Tok.FillSolidBase, Padding = Edges4.All(FaceRing),
            Margin = new Edges4(first ? 0f : -FaceOverlap, 0f, 0f, 0f),
            Role = live ? AutomationRole.Button : AutomationRole.None,
            Focusable = live,
            Cursor = live ? CursorId.Hand : CursorId.Arrow,
            FocusVisualMargin = live ? Design.FocusInsetRow : default,
            HoverFill = live ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
            HoverScale = live ? Design.Motion.ScaleStandard.Hover : 1f,
            PressScale = live ? Design.Motion.ScaleStandard.Press : 1f,
            HoverDurationMs = Design.Motion.Faster,
            OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Width = FaceAvatar, Height = FaceAvatar, Corners = CornerRadius4.All(FaceAvatar / 2f),
                    Fill = Tok.FillCardDefault, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl("+" + n) { Size = 10f, Weight = 700, Color = Tok.TextSecondary }],
                },
            ],
        };
        return tip is { Length: > 0 }
            ? ToolTip.Wrap(frame, tip, showDelayMs: FaceTipDelayMs) with { Key = "face:more" }
            : frame;
    }

    // ══ 10. RICH TEXT ════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // A description that may be an HTML FRAGMENT — a playlist/album blurb carries anchors to artists and playlists and
    // <b> bold — rendered as ONE wrapped rich-text paragraph (a span paragraph shapes all runs as a single flow).
    // Anchors become accent HYPERLINKS that navigate through the route seam; <b>/<strong> → bold; HTML entities are
    // decoded; unknown tags are DROPPED and their text KEPT. The shaper has no italic axis, so <i>/<em> render upright.
    // Plain text with no markup → a single run, identical to a plain text node.

    /// <summary>A rich paragraph at an EXPLICIT width.</summary>
    public static Element RichText(string? html, float size, ColorF color, ColorF linkColor, float width, int maxLines,
                                   Action<string>? onNavRoute = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return new BoxEl();
        var spans = ParseRich(html!, linkColor, onNavRoute);
        if (spans.Count == 0) return new BoxEl();
        return new SpanTextEl(spans.ToArray())
        {
            Size = size, Color = color, LineHeight = LineHeightFor(size),
            Width = width, MaxLines = maxLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
        };
    }

    /// <summary>The FLEX twin: the paragraph GROWS into its flex row and wraps to the width the layout assigns, so
    /// siblings take their intrinsic space and the text yields naturally — with NO hand-computed width
    /// reservations.</summary>
    public static Element RichTextFlex(string? html, float size, ColorF color, ColorF linkColor, int maxLines,
                                       Action<string>? onNavRoute = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return new BoxEl();
        var spans = ParseRich(html!, linkColor, onNavRoute);
        if (spans.Count == 0) return new BoxEl();
        return new SpanTextEl(spans.ToArray())
        {
            Size = size, Color = color, LineHeight = LineHeightFor(size),
            Grow = 1f, Basis = 0f, MaxLines = maxLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
        };
    }

    /// <summary>A single-line, FLEX rich caption for a media ROW's subtitle: the same anchor→hyperlink parsing, growing
    /// into the row's text column and ellipsising ONE line. A span whose href the route seam can resolve is an accent
    /// hyperlink — so artist/album names are clickable on their own, independent of the row's click.</summary>
    public static Element RichTextRow(string? text, float size, ColorF color, ColorF linkColor,
                                      Action<string>? onNavRoute = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return new BoxEl();
        var spans = ParseRich(text!, linkColor, onNavRoute);
        if (spans.Count == 0) return new BoxEl();
        return new SpanTextEl(spans.ToArray())
        {
            Size = size, Color = color, LineHeight = LineHeightFor(size),
            Grow = 1f, Basis = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }

    static float LineHeightFor(float size) => size <= 12f ? 16f : size <= 14f ? 20f : float.NaN;

    /// <summary>HTML fragment → spans. PURE apart from the route seam, and that is the only thing that makes a span
    /// CLICKABLE: an href the seam cannot route renders STYLED but inert, because a link to a route nothing renders is
    /// worse than a link that does not click.</summary>
    public static List<TextSpan> ParseRich(string s, ColorF linkColor, Action<string>? onNavRoute)
    {
        var spans = new List<TextSpan>(4);
        var buf = new StringBuilder(s.Length);
        int bold = 0;
        string? href = null;

        void Flush()
        {
            if (buf.Length == 0) return;
            string t = buf.ToString();
            buf.Clear();
            ushort w = (ushort)(bold > 0 ? 700 : 0);
            if (href is { } h && onNavRoute is not null && RouteForUri?.Invoke(h) is { } key)
                spans.Add(new TextSpan(t, Weight: w, Color: linkColor, OnClick: () => onNavRoute(key)));
            else if (href is not null)
                spans.Add(new TextSpan(t, Weight: w, Color: linkColor));   // a reference we cannot route → styled, inert
            else
                spans.Add(new TextSpan(t, Weight: w));
        }

        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '<')
            {
                int gt = s.IndexOf('>', i);
                if (gt < 0) { buf.Append(s[i]); i++; continue; }   // a stray '<' — keep it as text
                string tag = s.Substring(i + 1, gt - i - 1).Trim();
                Flush();
                string lower = tag.ToLowerInvariant();
                if (lower == "/a") href = null;
                else if (lower == "a" || lower.StartsWith("a ", StringComparison.Ordinal)) href = ExtractHref(tag);
                else if (lower is "b" or "strong") bold++;
                else if (lower is "/b" or "/strong") bold = Math.Max(0, bold - 1);
                // every other tag (br, i, em, span, p, …) is DROPPED; its text content is preserved
                i = gt + 1;
            }
            else
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) lt = s.Length;
                DecodeEntities(s, i, lt, buf);
                i = lt;
            }
        }
        Flush();
        return spans;
    }

    // The href value from an anchor tag body (quoted or unquoted). Null if absent.
    static string? ExtractHref(string tag)
    {
        int h = tag.IndexOf("href", StringComparison.OrdinalIgnoreCase);
        if (h < 0) return null;
        int eq = tag.IndexOf('=', h);
        if (eq < 0) return null;
        int v = eq + 1;
        while (v < tag.Length && (tag[v] == ' ' || tag[v] == '"' || tag[v] == '\'')) v++;
        int end = v;
        while (end < tag.Length && tag[end] is not ('"' or '\'' or ' ')) end++;
        return end > v ? tag[v..end] : null;
    }

    // Decode the common HTML entities in s[start,end) into buf, leaving unknown ones literal.
    static void DecodeEntities(string s, int start, int end, StringBuilder buf)
    {
        int i = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '&')
            {
                int sc = s.IndexOf(';', i);
                if (sc > i && sc < end && sc - i <= 9)
                {
                    string ent = s.Substring(i + 1, sc - i - 1);
                    string? rep = ent switch
                    {
                        "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" or "#39" => "'", "nbsp" => " ",
                        _ when ent.Length > 1 && ent[0] == '#' && int.TryParse(ent.AsSpan(1), out int cp)
                               && cp > 0 && cp < 0xD800 => char.ConvertFromUtf32(cp),
                        _ => null,
                    };
                    if (rep is not null) { buf.Append(rep); i = sc + 1; continue; }
                }
            }
            buf.Append(c);
            i++;
        }
    }

    /// <summary>A rich paragraph with a native inline overflow suffix: the collapsed state reserves "… More" on the
    /// FINAL line only when the body actually overflows, and the expanded state appends a clickable "Less".
    /// <para>KEY IT on the whole content signature at the mount site — the body freezes at mount.</para></summary>
    public static Element ExpandableRichText(string? html, float size, ColorF color, ColorF linkColor, float width,
                                             int maxLines, string contextKey, Action<string>? onNavRoute = null)
        => Embed.Comp(() => new ExpandableRich(html, size, color, linkColor, width, maxLines, onNavRoute))
            with { Key = $"rich-expand:{contextKey}:{html}:{(int)width}:{maxLines}" };

    sealed class ExpandableRich : Component
    {
        readonly string? _html;
        readonly float _size, _width;
        readonly ColorF _color, _linkColor;
        readonly int _maxLines;
        readonly Action<string>? _onNav;
        readonly Signal<bool> _expanded = new(false);

        public ExpandableRich(string? html, float size, ColorF color, ColorF linkColor, float width, int maxLines,
                              Action<string>? onNav)
        { _html = html; _size = size; _color = color; _linkColor = linkColor; _width = width; _maxLines = maxLines; _onNav = onNav; }

        public override Element Render()
        {
            if (string.IsNullOrWhiteSpace(_html)) return new BoxEl();
            var parsed = ParseRich(_html!, _linkColor, _onNav);
            if (parsed.Count == 0) return new BoxEl();

            bool expanded = _expanded.Value;
            if (expanded)
                parsed.Add(new TextSpan(" " + Loc.Get(Strings.Common.Less), Weight: 600, Color: _linkColor,
                                        OnClick: () => _expanded.Value = false));

            return new SpanTextEl(parsed.ToArray())
            {
                Size = _size, Color = _color, LineHeight = LineHeightFor(_size),
                Width = float.IsNaN(_width) ? float.NaN : _width,
                Grow = float.IsNaN(_width) ? 1f : 0f,
                Basis = float.IsNaN(_width) ? 0f : float.NaN,
                MaxLines = expanded ? 0 : _maxLines,
                Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                OverflowSuffix = expanded
                    ? null
                    : [new TextSpan("… " + Loc.Get(Strings.Common.More), Weight: 600, Color: _linkColor,
                                    OnClick: () => _expanded.Value = true)],
            };
        }
    }
}
