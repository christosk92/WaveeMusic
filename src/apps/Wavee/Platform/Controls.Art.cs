// ── Platform/Controls.Art.cs ───────────────────────────────────────────────────────────────────────────────────────
// The media surface's shared PARTS (cover stack, corner "…", the now-playing overlay, the card data contract, the
// chrome rules), chips, stat tiles, countdowns, face piles, rich text, the shimmer
//
// Role: UI
// Owner: L
// Wave: 4
// Budget: 1400 lines
// Spec: DERIVED; the ONE surface: docs/plans/wavee/shared-media-surface-implementation.md
//
// ── THE SURFACE IS SHARED, AND THAT IS THE POINT (ch 02 §9.2) ────────────────────────────────────────────────────────
//
// Every media card, tile and row in the app is ONE surface — `Controls.Surface(CardData, SurfaceShape)` (Surface.Host.cs)
// — instead of a hand-rolled box, so the hover grammar, the chrome, the menu, the drag, the cursor and the focus stop
// cannot drift between surfaces. WHAT a surface shows is `CardData` (below: an adapter's data, compared by data); HOW it
// is laid out is a `SurfaceShape` preset (Surface.Rules.cs); the tree is `SurfaceParts` (Surface.Parts.cs). Each
// `X.UI.cs` contributes only an ADAPTER that fills in cover / title / subtitle / slots / menu / drag for its handle.
//
// That is also why nothing here takes a handle. A surface is given a cover URL, a title string and a few elements — so
// an entity adapter reads its OWN columns at build time and this file never learns what an album is.
//
// ── WHAT MUST NOT BE SIMPLIFIED (ch 02 §9) ───────────────────────────────────────────────────────────────────────────
//
//  1. THE HOVER PLATE IS A SIBLING, NOT A `HoverFill` ON THE ROOT. Every surface's shell is a ZStack [plate, content]:
//     the plate is a non-hit-testable REVEAL under the content carrying its kind's hover/press fills (`SurfacePlate.For`
//     — ONE table: the card's subtle plate, the list row's, the tile's opaque card fill, the outline's), cross-faded over
//     the 83 ms rung and deepened while the surface is held. The ROOT's own Fill/Border belong to the kind's STROKE (the
//     tile's playback-bound hairline, the outline's dashes) and to the SELECTED skin, and a reveal child fades on its own
//     authored rung whatever the root carries. Every child self-clips, so there is NO `ClipToBounds` on a surface root.
//  2. NO LIFT, NO ELEVATION, NO SCALE. A hovered surface does not move, scale or cast a halo: the plate's fill IS the
//     hover. That is what makes it read immediately (the old 250 ms lift tween plus a 1.04 cover zoom read as late), and
//     why no surface needs a paint-order z-index. A ROW in particular never press-scales: even a 2% swing moves each edge
//     of a ~1000-px row several DIP and visibly blurs the title mid-scale.
//  3. THE ZOOM CONTAINER MUST NOT PUSH ITS OWN ROUNDED CLIP. The renderer clamps image/gradient primitives to the
//     TOPMOST rounded clip only; a clip here would ride the scale out past the card and show square slivers at the
//     corners. Only a NON-SQUARE cover zooms at all (`CardCover`): a square cover is still.
//  4. CIRCULAR COVERS DO NOT CLIP THE OVERLAY LAYER — the FAB sits inside the cover's RECTANGLE but outside the avatar
//     circle, so the clip is conditional on the shape, and a circular card centres its labels under the circle. Only a
//     SQUARE cover can be circular (`CoverShape.IsCircular`): a wide tile asked to be round would become a pill.
//  5. `Grow = 1` on a STACK shell is what makes a measured shelf stretch every card to the TALLEST card's height: uniform
//     panels, exact, with no reserved worst case. A grid whose ROW is taller than its card (the discography grid folds
//     its 20-DIP row gap into the row height) must therefore pin the card with `CardData.Height`, which also zeroes
//     Grow/Shrink — Height alone is only the flex BASIS, and a growing card paints its hover plate into the gap.
//  6. THE CHROME MOUNTS LAZILY, ON EVERY SHAPE, AND THE SLOT ROOT OWNS THE CLICK. The now-playing overlay (a host + a
//     ToolTip around the FAB) and the "…" (another ToolTip) cost ~1 ms and ~68 KB per surface to mount, and 19 of them
//     made the artist page's navigation frame. They exist only while the surface is HOT (pointer within, focus within,
//     or its bound slot focused) or RELATES to playback (the equalizer pill) — `CardChromeRules.Mounted`; a video
//     (`PlayReveal.Always`) mounts its FAB at rest. The engine made lazy safe: enter/exit are SUBTREE edges (a press on
//     the FAB is never an exit), and a reveal mounting into an already-hovered surface is seeded on mount through its
//     transparent ToolTip wrapper. Inside a bound `ItemsView`/`PagedShelf` slot the host learns slot mode from
//     `ItemsView.SlotRow` and renders click-less and focus-less: the slot root is the ONE gesture owner and the ONE tab
//     stop (input-a11y.md §6.5) — a surface that also owned them is the shelf's old double tab stop.

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

    // ══ 2. THE COVER STACK AND ITS CORNER ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The cover STACK inside a card: the art, the overlay layer, and the corner "…". Any zoom the art takes
    /// lives HERE, on its own node, never on the card root — and this node pushes NO rounded clip of its own (rule 3).
    /// <para>ONLY A NON-SQUARE COVER ZOOMS (width ≠ height: the 16:9 wide tiles, a 2-span lead): 1.02 over the
    /// <see cref="Design.Motion.Standard"/> rung with a decelerate ease — the prototype's slow drift into a wide
    /// picture. A square cover stays still; its hover is the plate's fill alone (rule 2). Reduced motion collapses the
    /// zoom to 1 (<see cref="Design.ScaleTier.Hover"/>).</para>
    /// <para><paramref name="circular"/> flips the clip OFF (rule 4): the FAB sits inside the cover's RECTANGLE but
    /// outside the avatar circle, so clipping it would cut the affordance in half. It is honoured only on a SQUARE slot
    /// (<see cref="CoverShape.IsCircular"/>).</para>
    /// <para><paramref name="width"/> × <paramref name="height"/>, not one edge: a wide tile (16:9 header art) is the
    /// same stack over a non-square slot — <see cref="SurfaceGeometry.CoverHeight"/> derives the height the shelf hands
    /// in.</para></summary>
    public static Element CardCover(Element art, float width, float height, bool circular, Element? overlay = null,
                                    Element? corner = null)
    {
        var kids = new List<Element>(3) { art };
        if (overlay is not null) kids.Add(overlay);
        if (corner is not null) kids.Add(corner);
        bool wide = width != height;
        return new BoxEl
        {
            ZStack = true, Width = width, Height = height,
            ClipToBounds = !CoverShape.IsCircular(circular, width / MathF.Max(1f, height)),
            HoverScale = wide ? Design.Motion.ScaleSubtle.Hover : 1f,
            HoverDurationMs = Design.Motion.Slow, HoverEasing = Easing.FluentDecelerate,
            Children = kids.ToArray(),
        };
    }

    /// <summary>A cover's SHAPE rule, pure so it can be pinned: a card asks for a circle (an artist, a radio station)
    /// and gets one only when its cover is SQUARE. A wide tile (a 16:9 cover, a 2-span lead) that inherited a
    /// <c>Circular</c> flag would round with half its WIDTH and render as a pill, so a non-square cover is never
    /// circular — its radius, its clip and its centred labels all fall back to the rectangular card's.</summary>
    public static class CoverShape
    {
        /// <summary>True only when <paramref name="circular"/> is asked for AND <paramref name="aspect"/> (width ÷
        /// height) is 1 within a thousandth.</summary>
        public static bool IsCircular(bool circular, float aspect) => circular && MathF.Abs(aspect - 1f) < 0.001f;
    }

    /// <summary>The top-right "…" on a card's artwork: the scrim-plated 30-circle, revealed on hover. It carries no
    /// handler of its own: it re-enters the context funnel and opens the card's ATTACHED menu, anchored at the button.
    /// <para>The tooltip is the DEFERRED form (<see cref="ToolTip.WrapStable"/>) over <see cref="s_moreCornerFab"/>: the
    /// FAB has no per-card data at all, so one static factory serves every card in the process and a card host that
    /// re-renders re-pushes a reference-equal factory plus the same string — the ToolTip core never re-renders for
    /// it.</para></summary>
    public static Element MoreCorner() => new BoxEl
    {
        ZStack = true, Grow = 1f, HitTestPassThrough = true,
        AlignItems = FlexAlign.Start, Justify = FlexJustify.End,
        Padding = Edges4.All(Spacing.S),
        Children = [ToolTip.WrapStable(s_moreCornerFab, Loc.Get(Strings.Common.More)).Skeletonized(false)],
    };

    /// <summary>MOUNT-STABLE by construction (a static readonly field — one delegate instance for the whole process), which
    /// is what <see cref="ToolTip.WrapStable"/> requires. Runs inside the ToolTip's own render; reads no signal, so a
    /// mounted "…" tooltip re-renders only when its text (the culture) changes.</summary>
    static readonly Func<Element> s_moreCornerFab = static ()
        => CoverActionFab(Icons.More, null, requestsContext: true) with { Opacity = 0f, HoverOpacity = 1f };

    /// <summary>A small neutral capsule beside a row's text — the kind tag ("Podcast", "Audiobook"). On a card SURFACE,
    /// not on media: the subtle fill and tertiary ink, never the on-media scrim ladder.</summary>
    public static Element RowChip(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.S, 2f, Spacing.S, 2f),
        Corners = Radii.PillAll, Fill = Tok.FillSubtleSecondary,
        Children = [Design.Type.MicroMeta(text) with { Weight = 600, Color = Tok.TextTertiary, MaxLines = 1 }],
    };

    // ══ 3. THE NOW-PLAYING OVERLAY ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The now-playing equalizer glyph's height — ONE size for "this is the one playing" on the media surface's
    /// overlay pill and on a sidebar row's accent equalizer, so the cards and the rows cannot drift apart.</summary>
    public const float EqualizerH = 14f;

    /// <summary>The surface's play affordance and — only when the surface RELATES to playback — its equalizer pill.
    ///
    /// <para><b>The media surface mounts this overlay LAZILY</b> (file header, rule 6): only while the surface is hot or
    /// relates, or at rest on a video (<paramref name="atRest"/>). An early hover-gated FAB failed twice — a card paged in
    /// under a STATIONARY pointer never armed, and an exit edge fired before release unmounted the node being clicked —
    /// and the engine has since made the lazy mount safe: enter/exit are delivered on the surface's SUBTREE edges
    /// (<c>InputDispatcher.UpdateHoverWithin</c>), so a press on the FAB is never an exit from the surface; a reveal
    /// mounting into an ALREADY-hovered surface is seeded on mount through the transparent tooltip wrapper, so it does
    /// not need a hover edge to fade in. The dispatcher does NOT synthesize hover for content appearing under a stationary
    /// pointer — a card paged in under a still cursor arms on the next real move.</para>
    ///
    /// <para><b>ONE SHAPE ON BOTH LEGS</b> — <c>[equalizer slot, FAB]</c>, with an empty box standing in — so the
    /// reconciler keeps the FAB's node while the equalizer mounts and unmounts beside it. A press in flight must never
    /// lose its target to a playback edge.</para>
    ///
    /// <para><b>The relation is read COARSE-FIRST.</b> An idle overlay reads only <c>HasActiveContext</c> and returns;
    /// it never subscribes to the hot identity signal, so an unrelated track skip re-runs one cheap read per card and
    /// schedules no render.</para>
    ///
    /// <para><paramref name="atRest"/>: the FAB is visible at rest instead of fading in on hover — a video
    /// (<see cref="PlayReveal.Always"/>), whose whole point is play.</para></summary>
    public static Element NowPlayingOverlay(string uri, Action? onPlay, float fab = 44f, bool centred = true,
                                            string? playName = null, bool atRest = false)
        => Embed.Comp(new OverlayProps(uri, onPlay, fab, centred, playName ?? Loc.Get(Strings.Detail.Play), atRest),
                      static () => new NowPlayingOverlayHost()).Skeletonized(false);

    /// <summary>The overlay's re-pushed props. Equality is DATA-ONLY: <see cref="OnPlay"/> counts by PRESENCE, never by
    /// identity (the <c>Track.TableProfile</c> / <c>GridProps</c> idiom) — a parent rebuilds its closures on every render,
    /// and comparing them by reference re-rendered every overlay on the page 13× a frame. The live handler still reaches
    /// the FAB: the host routes clicks through a trampoline that reads the NEWEST pushed props at invocation time.
    /// <para>Public only so the equality rule can be pinned by a fact (no <c>InternalsVisibleTo</c>).</para></summary>
    public sealed record OverlayProps(string Uri, Action? OnPlay, float Fab, bool Centred, string PlayName,
                                      bool AtRest = false)
    {
        public bool Equals(OverlayProps? other)
            => other is not null && (ReferenceEquals(this, other)
               || (Uri == other.Uri && (OnPlay is null) == (other.OnPlay is null) && Fab.Equals(other.Fab)
                   && Centred == other.Centred && PlayName == other.PlayName && AtRest == other.AtRest));

        public override int GetHashCode() => HashCode.Combine(Uri, OnPlay is not null, Fab, Centred, PlayName, AtRest);
    }

    /// <summary>An <see cref="IPropsHost"/>: EVERY re-push lands in <see cref="_latest"/> (so a delegate is never stale
    /// when invoked), while renders gate on <see cref="_props"/>, a signal whose default comparer is the record's
    /// data-only <c>Equals</c> — a fresh-but-equal re-push writes nothing and renders nothing.</summary>
    sealed class NowPlayingOverlayHost : Component, IPropsHost
    {
        OverlayProps? _latest;
        readonly Signal<OverlayProps?> _props = new(null);
        // Trampolines and factories are built ONCE per host: reference-stable across renders, reading `_latest` /
        // `_props` at invocation time.
        readonly Action _onPlay;
        readonly Func<Element> _playFab;

        public NowPlayingOverlayHost()
        {
            _onPlay = () => _latest?.OnPlay?.Invoke();
            _playFab = BuildPlayFab;
        }

        public void ApplyProps(object props)
        {
            _latest = (OverlayProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            var p = _props.Value;
            if (p is null) return new BoxEl();
            var pb = NowPlaying;

            // COARSE first. `HasActiveContext` is one bool for the whole app; `RelatesTo` is per-card and only asked when
            // something is actually playing. `Owns`/`IsPlaying` are NOT read here any more: only the FAB depends on
            // them, and the FAB is built inside its ToolTip's render (below), so a play/pause flip re-renders that one
            // node and leaves this host — which only decides the equalizer slot — alone.
            bool anything = pb is not null && pb.HasActiveContext.Value;
            bool relates = anything && pb!.RelatesTo(p.Uri);

            Element eq = relates
                ? new BoxEl
                {
                    Shrink = 0f, Padding = new Edges4(6f, 4f, 6f, 4f),
                    Corners = Radii.PillAll, Fill = Design.OnMedia.ScrimRest,
                    BorderWidth = 1f, BorderColor = Design.OnMedia.Stroke,
                    // Centred on the cover in the stack's own terms, and handed over to the FAB on hover: at rest the
                    // playing surface shows THIS (the equalizer on its small scrim), never a pause button (owner, #160).
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                    HoverOpacity = 0f, HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.SmoothOut,
                    Children = [Equalizer(pb!.IsPlaying, () => Design.OnMedia.Ink, EqualizerH)],
                }
                // The STAND-IN. Keeping the slot occupied is what stops the FAB's node being re-keyed when the
                // equalizer appears beside it.
                : new BoxEl();

            // The DEFERRED tooltip form: `_playFab` is a field (mount-stable), so this re-push is one ReferenceEquals plus
            // a string compare, and the ToolTip core re-renders only when the play NAME changes — or when a signal the
            // factory reads does. A hover-only affordance is not skeleton content.
            Element play = ToolTip.WrapStable(_playFab, p.PlayName).Skeletonized(false);

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

        /// <summary>The FAB, built INSIDE the ToolTip's render. Every read here subscribes the TOOLTIP: <see cref="_props"/>
        /// (so a rebind to another uri or FAB size rebuilds the glyph with no re-push at all) and the playback signals (so
        /// play/pause re-skins exactly this node). The click goes through <see cref="_onPlay"/>, never a captured
        /// delegate, so a data-equal re-push with a fresh handler is honoured on the next click.</summary>
        Element BuildPlayFab()
        {
            var p = _props.Value;
            // No handler, no FAB: a surface with nothing to play (the listening-history tile) must not grow a dead
            // affordance. Presence is part of the props' equality, so a rebind that gains a handler rebuilds this.
            if (p is null || p.OnPlay is null) return new BoxEl();
            var pb = NowPlaying;
            bool anything = pb is not null && pb.HasActiveContext.Value;
            bool owns = anything && pb!.RelatesTo(p.Uri) && pb!.Owns(p.Uri);
            bool playingHere = owns && pb!.IsPlaying.Value;
            return PlayFab(_onPlay, glyph: playingHere ? Icons.Pause : Icons.Play, size: p.Fab) with
            {
                // Hover-only even while THIS surface is the one playing: at rest the equalizer pill states it (above), and
                // the pause appears under the pointer. Only a video's FAB (AtRest) shows at rest.
                Opacity = p.AtRest ? 1f : 0f, HoverOpacity = 1f,
                HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.SmoothOut,
            };
        }
    }

    // ══ 4. THE SURFACE'S DATA ════════════════════════════════════════════════════════════════════════════════════════
    //
    // What a media surface SHOWS (an adapter's `CardData`) and the pure rules its host feeds. HOW it is laid out is the
    // shape (Surface.Rules.cs), the tree is `SurfaceParts` (Surface.Parts.cs), and the ONE host is Surface.Host.cs.

    /// <summary>Everything a media surface renders, filled in by an entity ADAPTER. A record, because a virtualized parent
    /// re-pushes it per bind and a frozen constructor field would keep pointing at the slot's FIRST row.
    ///
    /// <para><b>Equality is DATA-ONLY.</b> The adapters build <see cref="OnClick"/>, <see cref="OnPlay"/> and
    /// <see cref="Drag"/>'s payload factory as fresh closures on every parent render, so the compiler's record equality
    /// (reference on delegates) never held and every surface host re-rendered 13× a frame. Here a delegate counts by
    /// PRESENCE only (a null <see cref="OnClick"/> is a DISPLAY-ONLY surface, so presence is behaviour: it decides the hand,
    /// the Button role and the tab stop) and a <see cref="DragSource"/>
    /// by its data (<c>Kind</c>, <c>Style</c>); the host invokes the NEWEST pushed delegates through trampolines, so a
    /// data-equal re-push with fresh handlers is still honoured. The <see cref="Element"/> slots compare by record VALUE —
    /// <see cref="Subtitle"/> is a <c>TextEl</c> over a string and a shared static brush, so a rebuilt-but-identical
    /// subtitle is equal, while a real text change is not; an element that genuinely differs (or carries a per-render
    /// closure, or a children array) simply re-renders as before, which is the safe direction.</para>
    ///
    /// <para><see cref="OnClick"/> null = a DISPLAY-ONLY surface (<c>SurfaceRules.Ownership(inSlot: false, hasClick: false)</c>):
    /// no click, no tab stop, no Button role, no hand. An entry with nowhere to go says so here, never with a no-op.</para>
    ///
    /// <para>The title's LINE BUDGET is not data: it is the shape's <see cref="SurfaceShape.TitleLines"/> dial.</para></summary>
    public sealed record CardData(
        string Uri,
        string Title,
        Element? Subtitle,
        string? CoverUrl,
        Action? OnClick,
        Action? OnPlay = null,
        bool Circular = false,
        DragSource? Drag = null,
        bool ShowMenu = true,
        Element? CoverOverride = null)
    {
        // ── The shell controls (init-only, defaulted, so the positional adapters keep compiling) ────────────────────
        // A surface is a component, not the shell BoxEl: a caller that used to post-process the shell (a fixed height,
        // the "selected" skin, an attached context menu) states its intent here and the host applies it to the node it
        // must sit on.

        /// <summary>A pinned shell height. NaN (the default) leaves the shell growing to its cell; a number pins it AND
        /// zeroes Grow/Shrink — Height alone is only the flex basis, and a growing card stretches its hover plate into a
        /// row that is taller than the card (the discography grid's folded row gap: file header, rule 5).</summary>
        public float Height { get; init; } = float.NaN;

        /// <summary>The OPENED / chosen surface: the 2-DIP <see cref="SelectedAccent"/> border over the brighter card fill,
        /// so a card answers "where did it open" beside its drawer.</summary>
        public bool Selected { get; init; }

        /// <summary>The accent the selected border paints (null = the theme accent), read INSIDE the host's render — a
        /// palette landing repaints the one selected surface and no other. Counts by presence in equality (a fresh thunk
        /// is behaviour, not data).</summary>
        public Func<ColorF>? SelectedAccent { get; init; }

        /// <summary>The surface's context menu factory. The host attaches it to the shell through the overlay service in
        /// context (skipped under the null overlay) — in free AND slot mode, because the right-click funnel is not a
        /// gesture owner — and the "…" the shape places (corner or trailing) re-enters that same funnel. Counts by
        /// presence.</summary>
        public Func<ContextMenuModel?>? Menu { get; init; }

        /// <summary>The cover's width ÷ height. 1 (the default) is the square every entity card has always had; a wide
        /// tile (16:9 header art) states its ratio here and the stack derives the cover height from it
        /// (<see cref="SurfaceGeometry.CoverHeight"/>), so the label block never moves.</summary>
        public float CoverAspect { get; init; } = 1f;

        /// <summary>An optional THIRD line under a stack's subtitle — tertiary, one line ("Spotify · 200 songs"). Null (the
        /// default) renders nothing, so every existing card keeps its two-line label block.</summary>
        public string? Meta { get; init; }

        /// <summary>A stack's second line as a PLAIN string the surface renders itself: ONE secondary 12/16 paragraph
        /// capped at <see cref="CaptionLines"/>, with <see cref="Meta"/> riding INLINE at its tail as a tertiary " · meta"
        /// span (the prototype's <c>.lead-meta</c> inside <c>.g-cap</c>) instead of its own line. Null (the default) keeps
        /// the <see cref="Subtitle"/> element and the separate meta line. A string, not an element, so the data-only
        /// equality holds across re-pushes (a span paragraph's array never compares equal).</summary>
        public string? Caption { get; init; }

        /// <summary>The line cap of an inline <see cref="Caption"/> (1 or 2 — what <c>ShelfHeight</c>'s
        /// <c>captionLines</c> reserves). Ignored without a <see cref="Caption"/>.</summary>
        public int CaptionLines { get; init; } = 1;

        // ── The row slots and the rest of the surface's data ──────────────────────────────────────────────────────────

        /// <summary>A row's EYEBROW, over its title ("Lyrics match").</summary>
        public Element? Eyebrow { get; init; }

        /// <summary>A row's META ROW, under its subtitle (the episode "E · ▣ · 3 Oct · 42 min" row).</summary>
        public Element? MetaRow { get; init; }

        /// <summary>A row's TRAILING cluster (a duration, a chip, a Follow toggle). The host wraps it in
        /// <see cref="SurfaceParts.Action"/> — a <c>BlocksDragArm</c> barrier — so a press on a control inside it never
        /// arms the row's drag; a clickable child is its own gesture owner by the dispatcher's rule.</summary>
        public Element? Trailing { get; init; }

        /// <summary>A row's last text-column line, under the meta row (an episode's progress bar).</summary>
        public Element? Below { get; init; }

        /// <summary>A title match to highlight (Browse's chart search, the library search): <c>(0, 0)</c> = none. The
        /// title renders through <see cref="SearchHighlight"/> at the shape's line budget.</summary>
        public (int Start, int Length) Highlight { get; init; }

        /// <summary>The drop target the surface IS (a playlist tile accepting tracks). Counts by presence.</summary>
        public DropTargetSpec? Drop { get; init; }

        /// <summary>A SEED (blank / unresolved) surface: the host renders its bone face from the shape's own geometry — no
        /// title, no chrome, disabled. The one skeleton description every shape has.</summary>
        public bool IsSeed { get; init; }

        /// <summary>THE seed. Inside a bound slot the caller also disables the row (<c>IsItemEnabledTyped</c>), so the
        /// slot root dims and refuses invoke.</summary>
        public static readonly CardData Seed = new("", "", null, null, null) { IsSeed = true };

        public bool Equals(CardData? other)
            => other is not null && (ReferenceEquals(this, other)
               || (Uri == other.Uri && Title == other.Title && CoverUrl == other.CoverUrl
                   && Circular == other.Circular && ShowMenu == other.ShowMenu
                   && CoverAspect.Equals(other.CoverAspect) && Meta == other.Meta
                   && Caption == other.Caption && CaptionLines == other.CaptionLines
                   && Highlight == other.Highlight && IsSeed == other.IsSeed
                   && (OnClick is null) == (other.OnClick is null) && (OnPlay is null) == (other.OnPlay is null)
                   && Drag?.Kind == other.Drag?.Kind && Equals(Drag?.Style, other.Drag?.Style)
                   // `float.Equals`, not `==`: NaN (the "unpinned" default) must equal NaN.
                   && Height.Equals(other.Height) && Selected == other.Selected
                   && (SelectedAccent is null) == (other.SelectedAccent is null) && (Menu is null) == (other.Menu is null)
                   && (Drop is null) == (other.Drop is null)
                   && Equals(Subtitle, other.Subtitle) && Equals(CoverOverride, other.CoverOverride)
                   && Equals(Eyebrow, other.Eyebrow) && Equals(MetaRow, other.MetaRow)
                   && Equals(Trailing, other.Trailing) && Equals(Below, other.Below)));

        // The hash stays cheap and consistent with Equals: it walks no Element tree (presence stands in for every element
        // slot) — equal records still hash equal, unequal ones merely may collide.
        public override int GetHashCode()
            => HashCode.Combine(Uri, Title, CoverUrl, Circular, ShowMenu, Highlight,
                                HashCode.Combine(Height, CoverAspect, Meta, Caption, CaptionLines),
                                (OnPlay is not null ? 1 : 0) | (Drag is not null ? 2 : 0) | (Subtitle is not null ? 4 : 0)
                                | (CoverOverride is not null ? 8 : 0) | (Selected ? 16 : 0)
                                | (SelectedAccent is not null ? 32 : 0) | (Menu is not null ? 64 : 0)
                                | (Eyebrow is not null ? 128 : 0) | (MetaRow is not null ? 256 : 0)
                                | (Trailing is not null ? 512 : 0) | (Below is not null ? 1024 : 0)
                                | (Drop is not null ? 2048 : 0) | (IsSeed ? 4096 : 0) | (OnClick is not null ? 8192 : 0));
    }

    /// <summary>Shelf covers decode at a FIXED 256 square — stable across responsive card widths, so a resize never
    /// forces a re-decode, and a card and a detail cover that pass the same literal resolve to ONE cached texture.</summary>
    public const int ShelfDecodePx = 256;

    /// <summary>A NON-square (wide) shelf cover's decode target: 512 square, cover-fit into the 16:9 slot. Wider than
    /// <see cref="ShelfDecodePx"/> because a wide tile spans 330-440 DIP; still ONE literal so every wide tile shares a
    /// texture per url.</summary>
    public const int WideDecodePx = 512;

    /// <summary>The pure decisions behind the lazy chrome (file header, rule 6): the overlay and the "…" exist while the
    /// surface is HOT or RELATES to playback (the equalizer pill has to show on a surface nobody points at). Pinned by
    /// facts; the host only feeds them.</summary>
    public static class CardChromeRules
    {
        public static bool Mounted(bool hot, bool relates) => hot || relates;

        /// <summary>Hot = pointer inside the surface's subtree, OR keyboard focus within it.</summary>
        public static bool Hot(bool pointerIn, bool focusWithin) => pointerIn || focusWithin;

        /// <summary>Focus WITHIN the surface, from the two ROUTED focus edges the host hears (<c>InputDispatcher.SetFocus</c>:
        /// an ancestor's <c>OnFocusChanged</c> fires on focus entering/leaving its SUBTREE; the focused node itself keeps
        /// SELF semantics). <paramref name="self"/> is the shell's own edge (free mode — the shell is the tab stop);
        /// <paramref name="inner"/> is the non-focusable wrapper around the content, which hears a Tab from the shell onto
        /// the surface's own FAB as focus ENTERING — exactly the move the shell itself reads as a LOSS. Their OR is focus
        /// within, with no scene walk.</summary>
        public static bool FocusWithin(bool self, bool inner) => self || inner;
    }

    /// <summary>Does <paramref name="uri"/> relate to what is playing RIGHT NOW — the coarse <c>HasActiveContext</c>
    /// gate first (one app-wide bool, so an idle read never joins the hot identity fan-out), then the loose
    /// <c>RelatesTo</c>. Read inside a render or a <c>Prop.Of</c> thunk so the caller re-skins on a playback edge; false
    /// with no seam installed.</summary>
    public static bool RelatesNow(string uri)
        => NowPlaying is { } pb && pb.HasActiveContext.Value && pb.RelatesTo(uri);

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
    /// hot, and on a weak/UMA part that is exactly what starves the decode uploads it is waiting for.</para>
    ///
    /// <para>The url and the decode target are RE-PUSHED PROPS (<see cref="ShimmerProps"/>), not constructor fields: a
    /// recycled virtual row that rebinds to another cover UPDATES this component in place instead of remounting it (hook
    /// cells, effects and the keyframe track survive; <see cref="Shimmer"/> keys it by decode size only). The settle
    /// latch is therefore keyed to WHAT it settled for — a rebind re-arms the breathe for the new cover exactly as the
    /// old per-url remount did.</para></summary>
    public sealed class CoverShimmer : Component
    {
        static readonly Keyframe[] Breathe = [new(0f, 1f), new(0.5f, 0.5f), new(1f, 1f)];
        static readonly Keyframe[] Flat = [new(0f, 1f), new(1f, 1f)];

        // the settle latch, and the (url, decode target) it settled for
        bool _settled;
        string? _settledUrl;
        int _settledW, _settledH;
        // the placeholder bind, memoized per url: `Prop.Of` is a closure allocation, and this component used to pay it
        // on every render
        string? _placeholderFor;
        Prop<ColorF> _placeholder;

        public override Element Render()
        {
            var p = UseProps<ShimmerProps>();
            if (_settledUrl != p.Url || _settledW != p.DecodeW || _settledH != p.DecodeH)
            {
                _settledUrl = p.Url; _settledW = p.DecodeW; _settledH = p.DecodeH;
                _settled = false;
            }
            bool loading = false;
            if (!_settled)
            {
                // Share the displayed image's decode handle (same source + decode target) so this reads the SAME load
                // state and forks no second decode. The image hook consumes no hook cell, so the conditional call is
                // safe.
                var binding = UseImage(p.Url, p.DecodeW, p.DecodeH);
                var state = binding.State;
                if (state == ImageState.Ready) _settled = true;
                else if (state == ImageState.Failed && binding.Failure != ImageFailureKind.Canceled) _settled = true;
                else loading = state is ImageState.None or ImageState.Pending;
            }
            // Deliberately NOT gated on reduced motion: this is a LOADING INDICATOR, not a flourish, and it stops on
            // settle anyway. Nor on the GPU tier (owner decision, 2026-10-05): every tier gets the same shimmer; a saturated GPU
            // is the engine's measured governor's business, not a reason to show a different placeholder.
            bool shimmer = loading;
            UseKeyframes(AnimChannel.Opacity, shimmer ? Breathe : Flat, shimmer ? 1000f : 1f, shimmer,
                         DepKey.From(shimmer));
            if (_placeholderFor != p.Url) { _placeholderFor = p.Url; _placeholder = Design.WatchedPlaceholder(p.Url); }
            return new BoxEl
            {
                Width = p.Width, Height = p.Height, Corners = CornerRadius4.All(p.Corners),
                // Tint is PAINT-ONLY: the fill bind reads the per-key watch signal, so a landed grading marks
                // PaintDirty on exactly this tile — never a re-render, and never the global epoch fan-out that used to
                // re-render every still-loading cover in the grid at once.
                Fill = _placeholder,
            };
        }
    }

    /// <summary><see cref="CoverShimmer"/>'s re-pushed props. All value fields plus one string, so the compiler's record
    /// equality is exactly the data gate wanted: a rebind to the same cover at the same size coalesces.</summary>
    sealed record ShimmerProps(string Url, int DecodeW, int DecodeH, float Width, float Height, float Corners);

    // ══ 6. CHIPS ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The filter-chip rail's own metrics. <see cref="ChipRailExtent"/> is what the chip bar contributes to a
    /// stacked detail chrome column, INCLUDING its bottom semantic gap — one number, so a page's layout arithmetic and
    /// the rail itself cannot disagree.</summary>
    public const float ChipHeight = 32f, ChipRailHeight = 40f;
    /// <summary>The chip's horizontal padding and label size: the capsule geometry <see cref="Chip"/> and
    /// <see cref="LinkChip"/> share. 13 is DenseTitle/DenseMeta's size (13/18), the chip rail's own rung.</summary>
    public const float ChipPadX = Spacing.M, ChipFontSize = 13f;
    /// <inheritdoc cref="ChipHeight"/>
    public const float ChipRailExtent = ChipRailHeight + Spacing.S;

    /// <summary>One filter chip — a <see cref="ToggleButton.Controlled"/> (Workstream B: the grammar table's "Filter /
    /// mode toggles" row, stock checked = accent). <paramref name="available"/> false renders it SHOWN AND DISABLED
    /// rather than dropping it: a curated filter set is library-scoped and routinely names concepts whose rows have not
    /// been enriched yet, so dropping those hid the whole bar on a cold list. They become live as enrichment lands.
    /// <para>The selected value is entirely CALLER-owned (the live filter state), so this is <c>Controlled</c>: a click
    /// only invokes <paramref name="onClick"/> — the re-render that follows is what actually flips <paramref
    /// name="selected"/>.</para></summary>
    public static Element Chip(string label, bool selected, bool available, Action? onClick)
        => ToggleButton.Controlled(label, selected, _ => onClick?.Invoke(),
            style: AccentToggleStyle(Tok.AccentDefault) with
            {
                // The chip keeps its own capsule geometry — the 32/r4 ladder is for the labeled/icon button grammar,
                // not this scrolling rail, which has always read as pills.
                CornerRadius = Radii.Full,
                MinHeight = ChipHeight,
                Padding = new Edges4(ChipPadX, 0f, ChipPadX, 0f),
                FontSize = ChipFontSize,
                FocusVisualMargin = Design.FocusInsetBordered,
            },
            isEnabled: available, parts: RootNoShrink);
            // RootNoShrink (Shrink = 0 on the toggle's own root) is LOAD-BEARING on a non-wrapping row: without it
            // flex compresses every pill to fit the viewport and the labels ellipsise instead of the rail
            // overflowing, which is the opposite of what the scroller is for.

    /// <summary>A destination chip: a stock standard <see cref="Button"/> in the chip capsule (<see cref="Chip"/>'s geometry),
    /// announced as a link. For a row of pills that NAVIGATE (Browse's Top and For-you bands), where
    /// <see cref="Chip"/> is a toggle. Shrink 0: on a wrapping row the row breaks to a new line rather than ellipsising
    /// every pill. Always <see cref="ChipHeight"/> tall, so a chip row is a fixed 32 per line.</summary>
    public static Element LinkChip(string label, Action onClick, string? key = null)
        => Button.Standard(label, onClick, style: Button.StandardStyle with
        {
            CornerRadius = Radii.Full,
            MinHeight = ChipHeight,
            Padding = new Edges4(ChipPadX, 0f, ChipPadX, 0f),
            FontSize = ChipFontSize,
            FocusVisualMargin = Design.FocusInsetBordered,
        }) with { Key = key, Role = AutomationRole.Hyperlink, Cursor = CursorId.Hand, Shrink = 0f, MinWidth = 0f };

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
    /// remounting the tile. EXCEPT <paramref name="wrapValue"/>: a wrapping value's line count CAN change mid-swap (the
    /// Released tile going "4" → "October 4, 2023"), and cross-fading two different-height runs bleeds the outgoing one
    /// through the incoming one's gaps for the swap's duration — so a wrapping tile keeps a STABLE key and mutates the
    /// `TextEl` in place instead.</para></summary>
    public static Element StatTile(string key, string value, string caption, bool wrapValue = false,
                                   Element? trailing = null)
    {
        Element valueRun = new TextEl(value)
        {
            Size = 18f, Weight = 800, Color = Tok.TextPrimary, MinWidth = 0f,
            MaxLines = wrapValue ? 2 : 1, Trim = TextTrim.CharacterEllipsis,
            Wrap = wrapValue ? TextWrap.Wrap : TextWrap.NoWrap,
        };
        // A wrapping value (the Released tile: "4" → "October 4, 2023" can grow from one line to two) must not
        // cross-fade: TextSwap overlays the outgoing run UNDER the incoming one for its 150ms, and a swap that also
        // changes line count bleeds the old text through the new one's gaps. Wrapping tiles get a STABLE key so the
        // TextEl mutates in place instead of remounting; non-wrapping tiles (Songs, Length, the countdowns) keep the
        // keyed cross-fade — their measurement never changes mid-swap.
        Element valueBox = new BoxEl
        {
            // ZStack on the box itself, not through a helper: the value box must carry MinWidth, and an unconstrained
            // stack measures to the WIDER of the outgoing/incoming runs mid-swap.
            ZStack = true, MinWidth = 0f,
            Children = wrapValue
                ? [new BoxEl { Key = "v", Children = [valueRun] }]
                : [new BoxEl { Key = "v:" + value, Animate = MotionRecipes.TextSwap, Children = [valueRun] }],
        };
        Element captionRun = Design.Type.MicroMeta(caption)
            with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

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

    /// <summary>A flip cell's numeral swap: the old numeral slides up and fades out while the new one rises and fades in.
    /// <paramref name="reduced"/> (the caller passes <c>Design.Reduced</c>) ⇒ NO transition at all, so the numeral swaps
    /// in place: a LayoutTransition carries no reduced-motion policy, and the engine's default for one (KeepFade) would
    /// still cross-fade every digit every second — the ticking motion reduced motion asks to be rid of.</summary>
    public static LayoutTransition? FlipTransition(float rowHeight, bool reduced) => reduced ? null : new LayoutTransition(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut),
        Enter: new EnterExit(Dy: rowHeight * 0.6f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -rowHeight * 0.6f, Opacity: 0f, Active: true));

    /// <summary>ONE digit cell of the flip countdown: the old numeral slides up and out while the new one rises in (a
    /// keyed remount per cell — the reconciler keeps the exiting orphan painted UNDER the entering digit inside the
    /// clipped cell).
    /// <para>Every numeral sits CENTRED in a FIXED-WIDTH cell, because the text seam has no tabular figures: nothing
    /// reflows as the digits spin. Reduced motion swaps the numeral INSTANTLY (<see cref="FlipTransition"/>): a VALUE on
    /// the element, never a hook branch.</para></summary>
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
                    Animate = FlipTransition(rowHeight, Design.Reduced),
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
            else if (href is { } url && Actions.PlayLinkRules.IsWebUrl(url))
                spans.Add(new TextSpan(t, Weight: w, Color: linkColor, OnClick: () =>
                {
                    if (Actions.Services.OpenExternal is { } open) open(url);
                    else InputHooks.Current.Default.OpenUri?.Invoke(url);
                }));
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
                else if (lower is "b" or "strong" or "em" or "i") bold++;
                else if (lower is "/b" or "/strong" or "/em" or "/i") bold = Math.Max(0, bold - 1);
                else if (lower is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") bold++;
                else if (lower is "br" or "br/" or "br /" or "/li") buf.Append('\n');
                else if (lower is "/p" or "/div" or "/ul" or "/ol"
                    or "/h1" or "/h2" or "/h3" or "/h4" or "/h5" or "/h6")
                {
                    if (lower.Length == 3 && lower[1] == 'h') bold = Math.Max(0, bold - 1);   // closes the heading's bold run
                    buf.Append("\n\n");
                }
                else if (lower == "li" || lower.StartsWith("li ", StringComparison.Ordinal)) buf.Append("\u2022 ");
                // every other tag (span, p, ul, ol, div, …) is DROPPED; its text content is preserved
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

    // Decode the HTML entities in s[start,end) into buf, leaving unknown ones literal. HtmlEntities is the ONE decoder
    // (ArtistText.StripHtml / Lead use the same table), so the about panel and the hero's lead spell the same characters.
    static void DecodeEntities(string s, int start, int end, StringBuilder buf)
    {
        for (int i = start; i < end;)
        {
            char c = s[i];
            if (c == '&')
            {
                int cp = HtmlEntities.Decode(s.AsSpan(i, end - i), out int consumed);   // bounded to this text run
                if (cp >= 0)
                {
                    HtmlEntities.AppendCodePoint(buf, cp);
                    i += consumed;
                    continue;
                }
            }
            buf.Append(c);
            i++;
        }
    }

    /// <summary>A rich paragraph with a native inline overflow suffix: the collapsed state reserves "… More" on the
    /// FINAL line only when the body actually overflows (the engine's own clip decision — <c>SpanTextEl.OverflowSuffix</c>
    /// is hidden while the body fits), and the expanded state appends a clickable "Less".
    /// <para>Every input is a LIVE prop (re-pushed, never frozen at mount — a late page accent reaches the links); the key
    /// is the CONTENT's identity only, so a new body resets the expansion and a resize or a new line cap does not.</para>
    /// <para><paramref name="fullTextTip"/>: while collapsed, hovering the paragraph shows its whole text in a tooltip
    /// (<see cref="RichTextTip"/>) — the detail description's opt-in; a long show-notes body (Episode) does not ask.</para></summary>
    public static Element ExpandableRichText(string? html, float size, ColorF color, ColorF linkColor, float width,
                                             int maxLines, string contextKey, Action<string>? onNavRoute = null,
                                             bool fullTextTip = false)
        => Embed.Comp(new ExpandableRichProps(html, size, color, linkColor, width, maxLines, onNavRoute, fullTextTip),
                      static () => new ExpandableRich())
            with { Key = $"rich-expand:{contextKey}:{html}" };

    public static Element ExpandableRichTextFlex(string? html, float size, ColorF color, ColorF linkColor,
                                                int maxLines, string contextKey, Action<string>? onNavRoute = null)
        => new BoxEl
        {
            Direction = 1, MinWidth = 0, Children =
            [ExpandableRichText(html, size, color, linkColor, float.NaN, maxLines, contextKey, onNavRoute)],
        };

    sealed record ExpandableRichProps(string? Html, float Size, ColorF Color, ColorF LinkColor, float Width, int MaxLines,
                                      Action<string>? OnNav, bool FullTextTip);

    sealed class ExpandableRich : Component
    {
        readonly Signal<bool> _expanded = new(false);
        readonly Action _expand, _collapse;

        public ExpandableRich()
        {
            _expand = () => _expanded.Value = true;
            _collapse = () => _expanded.Value = false;
        }

        public override Element Render()
        {
            var p = UseProps<ExpandableRichProps>();
            bool expanded = _expanded.Value;
            if (string.IsNullOrWhiteSpace(p.Html)) return new BoxEl();
            var parsed = ParseRich(p.Html!, p.LinkColor, p.OnNav);
            if (parsed.Count == 0) return new BoxEl();

            // The tip is the paragraph's OWN text, taken before the "Less" affordance joins it.
            string? tip = p.FullTextTip ? RichTextTip.For(expanded, PlainTextOf(parsed)) : null;
            if (expanded)
                parsed.Add(new TextSpan(" " + Loc.Get(Strings.Common.Less), Weight: 600, Color: p.LinkColor, OnClick: _collapse));

            Element body = new SpanTextEl(parsed.ToArray())
            {
                Size = p.Size, Color = p.Color, LineHeight = LineHeightFor(p.Size),
                Width = p.Width,
                Grow = 0f,
                MinWidth = 0f,
                MaxLines = expanded ? 0 : p.MaxLines,
                Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                OverflowSuffix = expanded
                    ? null
                    : [new TextSpan("… " + Loc.Get(Strings.Common.More), Weight: 600, Color: p.LinkColor, OnClick: _expand)],
            };
            if (!p.FullTextTip) return body;
            // The BOUND-text wrap keeps the tree shape across collapse/expand: a null tip mounts no tooltip wiring.
            Prop<string?> tipText = tip;
            return ToolTip.Wrap(body, tipText);
        }

        static string PlainTextOf(List<TextSpan> spans)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < spans.Count; i++) sb.Append(spans[i].Text);
            return sb.ToString();
        }
    }
}

/// <summary>The full-text tooltip a clamped rich paragraph carries (the detail description, 2026-09-30: "on hover it should
/// show the tooltip showing the full text"). Engine-free: the paragraph's plain text in, the tip out.
/// <para>SCOPE (honest): the tip is gated on the COLLAPSED state, not on the paragraph actually being clipped — the
/// engine decides the clip inside the text seam (the <c>OverflowSuffix</c> shows only then) but exposes no "trimmed" fact
/// to the app, so a collapsed body short enough to fit still offers its (identical) text on hover.</para></summary>
public static class RichTextTip
{
    /// <summary>A tooltip is a glance, not a reader: past this many characters the tip ends in an ellipsis at a word
    /// boundary and the inline "More" is the way to the rest. A Spotify playlist description (≤ 300) always fits.</summary>
    public const int MaxChars = 500;

    /// <summary>The tip for a paragraph whose plain text is <paramref name="plain"/>: its whole text (trimmed, runs of
    /// blank lines folded to one) while collapsed, capped at <see cref="MaxChars"/>; null once expanded (the paragraph
    /// shows everything) and for a blank body.</summary>
    public static string? For(bool expanded, string? plain)
    {
        if (expanded || string.IsNullOrWhiteSpace(plain)) return null;
        string text = plain.Trim().Replace("\r\n", "\n");
        while (text.Contains("\n\n\n", StringComparison.Ordinal)) text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        if (text.Length <= MaxChars) return text;
        int cut = text.LastIndexOf(' ', MaxChars - 1);
        if (cut < MaxChars / 2) cut = MaxChars - 1;
        return text[..cut].TrimEnd() + "…";
    }
}

// ── show-notes CHAPTERS(hh:mm:ss) Title recognition (Wave E, podcast-ui-repair plan §5.2) ────────────────────────────
//
// A show-notes timestamp reference, recognised out of episode description/show-notes text (Episode.Reader/Page's
// About panel). Two conventions land in the wild: a "CHAPTERS(00:00:00) Title" line that opens the run, and a bare
// "(hh:mm:ss) Title" line for every entry after it. <see cref="StartMs"/> lets the caller build a seek action with
// the existing episode play-at-position intent (Episode.StartAt); <see cref="Title"/> is never empty (a line whose
// text after the timestamp is blank is not a chapter reference).
public readonly record struct ChapterLink(int StartMs, string Title);

/// <summary>Engine-free splitter for episode show-notes text: separates the prose body from the show-notes timestamp
/// convention, so <c>ParseRich</c> renders the remaining prose exactly as before and the caller (<c>Episode.About</c>)
/// renders the recognised timestamps as a seek list instead of inert flattened text. Pure text in, pure data out — no
/// FluentGpu / TextSpan dependency, so it is unit-tested directly (RichTextBlocksTests.cs) with no engine present.
/// <para>A chapter line may itself be HTML-wrapped ("&lt;p&gt;CHAPTERS(00:00:00) Introduction&lt;/p&gt;") — tags are
/// stripped per line before the timestamp pattern is matched, so the convention is recognised whether the wire sends
/// plain text or a paragraph-wrapped show notes body.</para></summary>
public static class RichTextBlocks
{
    /// <summary>Splits <paramref name="text"/> into the prose that is left (with every recognised chapter line
    /// removed, and the blank separators around a chapter run swallowed) and the ordered chapter links found, in the
    /// order they appeared. Recognises nothing (prose unchanged, an empty chapters array) when the input carries no
    /// show-notes timestamp line at all.</summary>
    public static (string Prose, ChapterLink[] Chapters) Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (text ?? "", []);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var chapters = new List<ChapterLink>();
        var prose = new List<string>();
        bool inChapterRun = false;
        foreach (var raw in lines)
        {
            if (TryParseChapterLine(raw, out var link))
            {
                chapters.Add(link);
                inChapterRun = true;
                continue;
            }
            if (inChapterRun && StripTags(raw).Trim().Length == 0) continue;   // swallow blank lines inside the run
            inChapterRun = false;
            prose.Add(raw);
        }
        return (string.Join('\n', prose).Trim(), chapters.ToArray());
    }

    /// <summary>One line → a chapter link, if it matches "CHAPTERS(hh:mm:ss) Title" or a bare "(hh:mm:ss) Title" once
    /// its HTML tags are stripped and it is trimmed.</summary>
    internal static bool TryParseChapterLine(string rawLine, out ChapterLink link)
    {
        link = default;
        string line = StripTags(rawLine).Trim();
        int i = 0;
        if (line.StartsWith("CHAPTERS", StringComparison.OrdinalIgnoreCase)) i = 8;
        while (i < line.Length && line[i] == ' ') i++;
        if (i >= line.Length || line[i] != '(') return false;
        int close = line.IndexOf(')', i);
        if (close < 0) return false;
        if (!TryParseTimestamp(line[(i + 1)..close], out int ms)) return false;
        string title = line[(close + 1)..].Trim();
        if (title.Length == 0) return false;
        link = new ChapterLink(ms, title);
        return true;
    }

    /// <summary>"hh:mm:ss" or "mm:ss" → milliseconds. Any other shape (missing/extra parts, non-digits) fails.
    /// Public so the parsing rule is pinned by a fact directly (RichTextBlocksTests.cs), not only through
    /// <see cref="Split"/> — this assembly carries no <c>InternalsVisibleTo</c> (see Controls.cs / Playlist.UI.cs).</summary>
    public static bool TryParseTimestamp(string s, out int ms)
    {
        ms = 0;
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        Span<int> v = stackalloc int[3];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out v[i])) return false;
        int h = parts.Length == 3 ? v[0] : 0;
        int m = parts.Length == 3 ? v[1] : v[0];
        int sec = parts.Length == 3 ? v[2] : v[1];
        ms = ((h * 3600) + (m * 60) + sec) * 1000;
        return true;
    }

    /// <summary>The minimal tag strip a show-notes line needs before timestamp matching — not a general HTML parser
    /// (ParseRich stays the one real parser), just enough to see through "&lt;p&gt;…&lt;/p&gt;" wrapping.</summary>
    internal static string StripTags(string s)
    {
        if (s.IndexOf('<') < 0) return s;
        var buf = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '<')
            {
                int gt = s.IndexOf('>', i);
                if (gt < 0) { buf.Append(s[i]); i++; continue; }
                i = gt + 1;
            }
            else { buf.Append(s[i]); i++; }
        }
        return buf.ToString();
    }
}
