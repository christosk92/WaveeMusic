using System;
using System.Diagnostics;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The IMMERSIVE STAGE: the fullscreen now-playing surface, and the fullscreen twin of the rail's lyrics panel it grew
/// out of. Two regions over one drifting, baked-blur cover backdrop:
/// <list type="bullet">
///   <item>LEFT — <see cref="StageIdentity"/>: the cover, the identity, the seek, the transport, volume and the output
///     device, centred in a fixed-width column (or a header row below <see cref="StageLayout.WideEnterW"/>).</item>
///   <item>RIGHT — <see cref="StagePanes"/>: the SAME <see cref="LyricsView"/> at <c>large: true</c> and the queue,
///     cross-faded in place, with the "Lyrics · Queue" pivot in the band along the bottom edge.</item>
/// </list>
///
/// <para>Mounted by <c>WaveeShell</c> as a full-bleed overlay layer while <see cref="ShellUi.ImmersiveLyrics"/> is true
/// (above the content card, below the engine's toast / teaching-tip lane). Entry is the expand button in the rail's
/// lyrics header; exit is Escape or the close button in this surface's top corner — both UNCHANGED by the stage.</para>
///
/// <para>CHROME BANDS. The surface deliberately leaves two strips of the shell live: the window caption band at the top
/// (drag + minimize/maximize/close belong to the OS chrome, and a surface that ate them would strand the user — the
/// lesson from the deleted fullscreen now-playing view, git ba43abbde) and the docked player bar at the bottom. The bar
/// stays reachable even though the stage now carries a full transport of its own: it is the app's ONE persistent
/// transport, and a surface that hid it would make closing the stage feel like losing playback control.</para>
///
/// <para>MATERIAL — THE STAGE FOLLOWS THE THEME, through ONE seam. Every colour on this surface (chrome AND lyrics)
/// resolves through <see cref="StageInk"/>, which is the only file that knows the stage's polarity; there is no theme
/// branch anywhere in this one. The stack is: the opaque <c>StageInk.Floor</c> → the σ80 baked-blur artwork,
/// full-bleed and drifting →
/// <see cref="StageChrome.Scrim"/>, ONE continuous vertical gradient over the whole body → <see
/// cref="StageChrome.ColumnShade"/>, ONE left-anchored layer that deepens the ground under the identity column and
/// feathers to exactly zero → content. An EARLIER light arm did read as a collage — a theme-flipping scrim under ink
/// that stayed white, so every region brought its own boxed dark veil and the white title vanished on the pale part.
/// The difference is that the scrim and the ink now flip TOGETHER, from one source, which is what lets the two paint
/// layers stay two.</para>
/// </summary>
sealed class ImmersiveLyricsSurface : Component
{
    // ── the reading column ───────────────────────────────────────────────────────────────────────────────────────────
    // A lyric line at 36 DIP wants a bounded measure; an ultra-wide window would otherwise lay a chorus out as one
    // 2000-DIP ribbon. The column is centred horizontally and clamped; LyricsView's own RowSidePad (64 DIP at large)
    // is the gutter INSIDE it, so the text block is ~570 DIP wide — roughly the reference's line length.
    // Internal: StagePanes owns the lyrics column now and clamps against the PANE's width, not the viewport's.
    internal const float ColumnMaxW = 700f;
    // The reading column's TRAILING air — the pane's right-hand gutter, so a long line never runs to the window edge.
    // It was 96 split as two half-gutters back when the column was centred; the leading air is StageLayout.RegionGapW
    // now, so this is one-sided and half the size.
    internal const float ColumnGutter = 48f;

    // ── the animated cover backdrop ──────────────────────────────────────────────────────────────────────────────────
    // The cover is drawn OVERSIZED and re-centred so the drift can never expose an edge: 130 % of the viewport leaves a
    // 15 % margin on every side, against a ≤4 % translation + ≤2 % scale wobble.
    const float Overscale = 1.30f;
    // Blur baked ONCE per art change into a derived image (BakedBlurSpec — no scene layer, no per-frame Gaussian): after
    // the bake the backdrop is an ordinary textured quad, so every frame of the drift is a pure transform write.
    const float BackdropSigmaDip = 80f;
    const float BackdropResolutionScale = 0.5f;
    // The scrim's own numbers live in StageLayout (the pure allocator) and are spelled as GradientSpecs by StageChrome.
    // There is deliberately no alpha constant here any more, and no theme branch to pick between two of them.

    // Drift: two INCOMMENSURATE sinusoids (37 s and 53 s never re-phase inside a listening session), so the motion has
    // no discernible loop and — deliberately — no relation to the beat. Amplitude is a fraction of the viewport so the
    // felt speed is the same on a laptop and a 4K panel. ~30 Hz is plenty for a 4 %-amplitude, 37-second sweep.
    const float DriftPeriodASec = 37f;
    const float DriftPeriodBSec = 53f;
    const float DriftAmpFrac = 0.04f;
    const float DriftScaleAmp = 0.02f;
    const float DriftIntervalMs = 33f;
    // Write gates. The transform folds into no cache key here (the bake is already done), but a write still dirties
    // paint, so ticks whose delta is invisible are dropped — which is most of them near each sinusoid's turning point.
    const float DriftWriteEps = 0.15f;     // DIP
    const float DriftScaleEps = 0.0004f;   // ≈0.5 DIP of edge travel on a 1300-DIP viewport
    const float Tau = 6.2831853f;

    // The drift carrier's node — a plain BoxEl that declares NO transform of its own, so nothing else ever stomps the
    // LocalTransform this component writes (the same reconciler rule LyricsView.WriteCascade documents: LocalTransform
    // is re-asserted on a re-render only for the declared-static → declared-identity transition). The oversize +
    // re-centring lives on its PARENT as a DECLARED, viewport-bound transform, so a window resize re-centres the
    // backdrop for free even when no ticker is running (setting off / reduced motion).
    NodeHandle _driftNode;
    NodeHandle _root;                     // the surface root — the node the shield and the focus re-park park focus on
    long _driftOriginQpc;                 // QPC origin (Stopwatch.GetTimestamp — never TickCount64, which quantises to ~15.6 ms)
    Action? _driftTick;                   // the interval callback, allocated once (never per render)
    IReadSignal<Size2>? _viewport;        // Peeked by the tick — never subscribed from it

    /// <summary>The surface's mount/unmount terminals, read by <c>WaveeShell</c> where it authors the Flow.Show
    /// branch. A fade always; the slight scale-in only when the OS is not asking for reduced motion (read as a VALUE at
    /// the point of consumption — never a hook branch).</summary>
    internal static EnterExit EnterTerminal => Motion.ReducedMotion
        ? new EnterExit(Opacity: 0f, Active: true)
        : new EnterExit(Sx: 1.03f, Sy: 1.03f, Opacity: 0f, Active: true);

    /// <inheritdoc cref="EnterTerminal"/>
    internal static EnterExit ExitTerminal => Motion.ReducedMotion
        ? new EnterExit(Opacity: 0f, Active: true)
        : new EnterExit(Sx: 1.02f, Sy: 1.02f, Opacity: 0f, Active: true);

    Action DriftTickAction => _driftTick ??= DriftTick;

    public override Element Render()
    {
        var ui = UseContext(ShellUi.Slot);
        var b = UseContext(PlaybackBridge.Slot);
        var svc = UseContext(Services.Slot);
        var hooks = UseContext(InputHooks.Current);
        var vpSig = UseContextSignal(Viewport.Size);
        _viewport = vpSig;

        // ── the stage's ONE reflow flag ──────────────────────────────────────────────────────────────────────────────
        // A COARSE band signal, resolved in an effect (the PlayerBar idiom): the surface re-renders on a wide⇄compact
        // FLIP, never on a resize pixel, and the hysteresis in StageLayout.Resolve means dragging the window edge across
        // the boundary flips it exactly once per crossing instead of thrashing a mounted LyricsView.
        var stage = UseSignal(StageLayout.Seed(vpSig.Peek().Width, ColumnAvailH(vpSig.Peek().Height)));
        var stageSeeded = UseRef(vpSig.Peek().Width > 0f);
        UseSignalEffect(() =>
        {
            var prev = stage.Peek();
            var vp = vpSig.Value;
            var next = StageLayout.Resolve(vp.Width, ColumnAvailH(vp.Height), stageSeeded.Value ? prev : null);
            if (vp.Width > 0f) stageSeeded.Value = true;
            if (!next.Equals(prev)) stage.Value = next;
        });

        // The appearance epoch is what makes the Settings toggle apply LIVE to an already-open surface (the
        // DisableColorWashes / DisableMarquee idiom): the writer bumps it, this re-renders, the interval re-gates.
        _ = AppearancePrefs.Epoch.Value;
        bool animated = svc?.Settings.Get(WaveeSettings.LyricsAnimatedBackdrop) ?? true;
        // Reduced motion is a VALUE the PAL republishes on WM_SETTINGCHANGE — read where it is consumed, exactly like
        // LyricsView's three consumption points. It vetoes the drift independently of the app setting.
        bool drift = animated && !Motion.ReducedMotion;

        // The secondary-line toggle's state. Both reads are SUBSCRIPTIONS, for the same reasons the rail header
        // documents: Available so the button appears when a document with a translation/romanization lands, Epoch so a
        // write from here, from the rail, or from the Settings picker re-reads the mode on the same frame.
        int secondaryAvailable = LyricsPrefs.Available.Value;
        _ = LyricsPrefs.Epoch.Value;
        int secondary = LyricsPrefs.Clamp(svc?.Settings.Get(WaveeSettings.LyricsSecondaryLine) ?? LyricsPrefs.None);

        var track = b?.CurrentTrack.Value;
        string art = track?.Image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) ?? "" : "";
        string? blurHash = track?.Image?.BlurHash;

        // Escape routes to the FOCUSED node and bubbles up its ancestors, so the surface takes focus once at mount.
        // The root stays focusable (and does NOT set AllowFocusOnInteraction=false) so a click on the surface's own
        // background lands focus back here rather than clearing it — Escape keeps working after any interaction.
        UseLayoutEffect(() =>
        {
            if (!Context.HostNode.IsNull) hooks.FocusNode?.Invoke(Context.HostNode, false);
        }, DepKey.Empty);

        // A drift turned OFF mid-flight (settings flip, or the OS reduced-motion preference arriving) must not strand
        // the carrier at its last offset — put it back on the exact centre the declared parent transform establishes.
        UseEffect(() => { if (!drift) ResetDrift(); }, DepKey.From(drift));

        // OFF ⇒ no ticker at all (not a ticking no-op): a still backdrop must cost literally nothing per frame.
        UseInterval(DriftTickAction, DriftIntervalMs, enabled: drift && art.Length > 0);

        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            // …and it DECLARES ITS PARTICIPATION. Grow alone is not enough: FlexShrink defaults to 0 (Columns.cs, the
            // Yoga-style default) and MirrorParticipation copies that onto the component's anchor, so a subtree that
            // measures WIDER than the window is arranged at that width and simply hangs off the edge. This surface
            // measures exactly that way — the backdrop's oversize frame declares an explicit viewport x 1.30 Width, and
            // MeasureZStack reports a bounded layer's own size as the stack's, so 1.30x propagated all the way up: the
            // root was arranged 1534x952 inside a 1180x760 window. The pane region inherited the surplus (1062 wide
            // where 708 was available), which put the reading column's first glyph at x=717 instead of 584, pushed the
            // End-justified pivot past the right edge, and took the close button with it.
            Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            // Full-bleed POSITIONER: only the body band below is hittable, so the window caption strip and the docked
            // player bar keep receiving input through this layer.
            HitTestPassThrough = true,
            Focusable = true,
            OnBoundsChanged = RectProbe("root"),
            OnRealized = h => _root = h,
            OnKeyDown = e =>
            {
                if (e.Handled || e.KeyCode != Keys.Escape) return;
                e.Handled = true;
                Close(ui);
            },
            // NEVER LEAVE FOCUS NULL WHILE THE SURFACE IS UP. Escape routes to the FOCUSED node
            // (InputDispatcher.OnKey), and an unhandled Escape CLEARS focus — after which the dispatcher's whole
            // focused-routing block is skipped and Escape is not accelerator-eligible, so a second Escape does
            // literally nothing. Any path that drops focus to nothing therefore disarms the keyboard exit until the
            // user happens to click something focusable again. This re-parks focus on the surface in exactly that
            // case.
            // It fires only on a real subtree BOUNDARY crossing (GotFocus/LostFocus are routed through ancestors), and
            // the engine explicitly tolerates a LostFocus handler re-moving focus. Focus moving to another REAL node —
            // the player bar's play button, the caption search field — leaves GetFocus non-null and is a NO-OP here:
            // this must not fight the live chrome bands for focus, only refuse the null.
            OnFocusChanged = got =>
            {
                if (got || _root.IsNull) return;
                if ((hooks.GetFocus?.Invoke() ?? default).IsNull) hooks.FocusNode?.Invoke(_root, false);
            },
            Children =
            [
                new BoxEl { Height = TitleBar.ExpandedHeight, Shrink = 0f, HitTestPassThrough = true },
                new BoxEl
                {
                    OnBoundsChanged = RectProbe("body"),
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                    // The opaque floor under the (possibly missing / still-decoding) cover: the surface must never let
                    // the page it covers show through. Bound so a live re-theme re-fires it in place.
                    Fill = Prop.Of(() => StageInk.Floor),
                    Children =
                    [
                        Backdrop(vpSig, stage, art, blurHash),
                        Shield(hooks),
                        new BoxEl
                        {
                            OnBoundsChanged = RectProbe("content"),
                            Grow = 1f, Direction = 1, MinHeight = 0f, MinWidth = 0f,
                            Children =
                            [
                                TopBar(track, ui, svc?.Settings, secondary, secondaryAvailable, stage.Value.Wide),
                                StageBody(stage),
                            ],
                        },
                    ],
                },
                new BoxEl { Height = WaveeSize.PlayerBarH, Shrink = 0f, HitTestPassThrough = true },
            ],
        };
    }

    static void Close(ShellUi? ui)
    {
        if (ui is not null) ui.ImmersiveLyrics.Value = false;
    }

    // ── FG_STAGE_RECTS=1: the stage's ARRANGED geometry, by name ─────────────────────────────────────────────────────
    // The engine has no per-node key store (Reconciler.DebugKeyOf only knows keep-alive/relative anchors), so a generic
    // layout dump cannot name what it prints. This does the naming from the app side, where the names exist: one
    // Element.OnBoundsChanged per region, edge-triggered by the engine, printing the rect the region ACTUALLY got.
    // It is what turns "the lyrics are clipped" into "the pane region is N wide and the column inside it is M".
    static readonly bool s_stageRects = Diag.EnvFlag("FG_STAGE_RECTS");
    internal static bool StageRectsOn => s_stageRects;

    /// <inheritdoc cref="s_stageRects"/>
    internal static Action<RectF>? RectProbe(string name) =>
        s_stageRects ? r => WaveeLog.Instance.Info("stage", $"rect {name} = {r.X:0.##},{r.Y:0.##} {r.W:0.##}x{r.H:0.##}") : null;

    /// <summary>The surface's HIT SHIELD — one childless, full-bleed layer whose only job is to be the hit target
    /// anywhere the stage's own content is not.</summary>
    ///
    /// <remarks>
    /// <para><b>THE DEFECT IT FIXES: the stage was transparent to CLICKS.</b> <c>InputDispatcher.Hit</c> is
    /// handler-GATED — a node self-hits only if its <c>HandlerMask</c> intersects the `hitAnywhere` set. This surface's
    /// root is <c>HitTestPassThrough</c> with no click handler, and the body band carries only a <c>Fill</c> and a
    /// <c>ZStack</c>, so neither was a hit target. <c>Hit</c> keeps the LAST non-null child across the shell's ZStack,
    /// so when this layer contributed null the hit resolved from the CONTENT CARD UNDERNEATH survived. A click on the
    /// stage's scrim could therefore activate a control on the hidden page — a track row under the pointer would start
    /// a different song — and focus either moved into that page or was cleared outright, which is what made Escape
    /// stop closing the surface.</para>
    ///
    /// <para><b>It must stay CHILDLESS.</b> Same contract, for the same mechanism, as
    /// <c>StageIdentity.ContextShield</c>: a container that owns a pointer handler becomes the hover/press target for
    /// every gap in its subtree, and <c>AnimScheduler.SetHover</c> then drives that state into every descendant
    /// carrying a reveal or scale affordance — which is every <see cref="StageChrome"/> button. A childless layer's
    /// cascade reaches exactly nothing.</para>
    ///
    /// <para><b>It must carry NO <c>Fill</c> and NO <c>Gradient</c>.</b> The scrim system is complete in two full-bleed
    /// paint layers (<see cref="StageChrome.Scrim"/> + <see cref="StageChrome.ColumnShade"/>); a third painted layer
    /// here would be exactly the per-region veil patchwork that was removed. The shield is input-only.</para>
    ///
    /// <para>Ordering: it sits AFTER the backdrop and BEFORE the content, but that is only for readability — since
    /// <c>Hit</c> keeps the last matching child, every real control still wins over it regardless.</para>
    /// </remarks>
    const string ShieldKey = "stage:shield";

    /// <inheritdoc cref="ShieldKey"/>
    Element Shield(InputHooks hooks) => new BoxEl
    {
        Key = ShieldKey,
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
        // OnClick is what sets ClickBit and so makes this a hit target at all — the absorbing IS the fix. Parking
        // focus back on the root is the second half: it re-arms the surface's own Escape handler after any click on
        // dead space. (NearestFocusable would walk shield → body → root anyway, since the root is Focusable; the
        // explicit call states the intent rather than relying on that walk.)
        OnClick = () => { if (!_root.IsNull) hooks.FocusNode?.Invoke(_root, false); },
    };

    // ── the two regions ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE tree, one reflow flag (StageLayout.Wide): the row direction is the only thing the band changes here, and the
    // two children keep their KEYS across the flip so neither the identity column nor the mounted LyricsView/queue is
    // ever remounted by a resize.
    //
    // GRADIENT-GLYPH BUDGET (the lyrics pane). Since Wave A's settled-split fast path, only the line whose wipe is
    // mid-sweep emits a gradient glyph run at all (Split >= 1 and Split <= 0 both fall into the cheap plain-glyph
    // batch). Even at a 1044-DIP viewport the stage shows ~20 rows and exactly ONE of them is wiping, so a worst-case
    // ~40 gradient glyphs sits two orders of magnitude inside MaxGradGlyphs = 1024 — no cap change is warranted.
    static Element StageBody(IReadSignal<StageLayout> stage)
    {
        var L = stage.Value;
        return new BoxEl
        {
            Direction = (byte)(L.Wide ? 0 : 1),
            Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Stretch,
            // The air between the two regions, as the band's own Gap — the ONE place it is spent, accounted for by
            // FlexLayout in both measure and arrange. The compact shape stacks the two vertically and wants none.
            Gap = L.Wide ? StageLayout.RegionGapW : 0f,
            Children =
            [
                // ── THE IDENTITY'S PARTICIPATION LIVES HERE, not inside the component ─────────────────────────────────
                // The GROW LEAK, and why this wrapper is not ceremony. StageIdentity's wide column declares Grow = 1 so
                // it can fill its host and spend the free space vertically (Justify). A component's element is mounted
                // under an ANCHOR whose layout the reconciler MIRRORS from it (Reconciler.MirrorParticipation), and the
                // scene default for that anchor is a COLUMN — so inside the anchor Grow = 1 means "fill the height",
                // which is what the column wants. But the anchor is ALSO the flex item in THIS band, and when the band
                // is a ROW (wide) the very same FlexGrow reads as a HORIZONTAL claim: the identity took its 352 + 120
                // basis and then half of the row's free space on top, because a declared Width is a flex BASIS, not a
                // cap (FlexLayout.ClampMain clamps to Min/Max only). The pane region got whatever was left, which is
                // narrower than StagePanes' own arithmetic assumed — the whole of the "lyrics clip mid-word" report.
                // The wrapper resolves the ambiguity by OWNING the horizontal participation (Grow 0, the authored
                // width, no shrink) and being a COLUMN itself, so the anchor's mirrored Grow can only ever be vertical.
                new BoxEl
                {
                    Key = "stage:identity",
                    OnBoundsChanged = RectProbe("identity"),
                    Direction = 1, MinHeight = 0f, MinWidth = 0f,
                    Grow = 0f, Shrink = 0f,
                    // Compact claims no column at all (StageLayout.CompactStage.LayoutWidth is 0) — the header row is
                    // full-bleed and auto-height, so the wrapper must not author a width there.
                    Width = L.Wide ? L.LayoutWidth : float.NaN,
                    Children = [Embed.Comp(() => new StageIdentity(stage)) with { Key = "stage:identity-comp" }],
                },
                new BoxEl
                {
                    Key = "stage:panes",
                    OnBoundsChanged = RectProbe("panes"),
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                    Children = [Embed.Comp(() => new StagePanes())],
                },
            ],
        };
    }

    // ── minimal top chrome: the way out ──────────────────────────────────────────────────────────────────────────────
    // The identity moved into the stage's left column, so this band is now only the surface's OWN controls, pushed to
    // the right edge under the caption veil. The secondary-line toggle sits immediately left of the close button — the
    // same relative position it takes in the rail header — and is shown only when a second line is actually available
    // AND the lyrics pane is the one on screen (a translation toggle over a queue is chrome for a pane you cannot see).
    static Element TopBar(Track? track, ShellUi? ui, IAppSettings? settings, int secondary, int secondaryAvailable,
                          bool wide)
    {
        bool lyricsPane = StagePane.Current.Value == StagePane.Lyrics;
        var accent = StageChrome.AccentFor(track);
        var kids = new System.Collections.Generic.List<Element>(3)
        {
            new BoxEl { Grow = 1f, MinWidth = 0f, HitTestVisible = false },
        };
        if (secondaryAvailable != 0 && lyricsPane)
            // `active` = a second line is actually on screen (not merely "the mode is non-zero") — see the same
            // note in RightRail.LyricsHeaderKids.
            kids.Add(GlyphButton(Icons.Globe, LyricsPrefs.Tooltip(secondary),
                () => LyricsPrefs.Set(settings, LyricsPrefs.Next(secondary, secondaryAvailable)), accent,
                active: (secondaryAvailable & LyricsPrefs.BitFor(secondary)) != 0));
        kids.Add(CloseButton(() => Close(ui), accent));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.S, Shrink = 0f,
            Height = StageChrome.TopBandFor(wide),
            // 16 top / 16 right — the reference composition's inset for the collapse affordance.
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.M),
            // NO veil of its own. The scrim's top deepening already resolves under this band, across a feather many
            // times its height — a boxed gradient here is exactly the dark band across the top that this wave removed.
            Children = kids.ToArray(),
        };
    }

    // The SECONDARY control in this band — the translation / romanization toggle. A 40-DIP StageChrome.ScrimFab: the
    // on-media scrim plate at rest plus the hairline ring, the recipe MediaCard's cover FABs use, because this band
    // sits over whatever the cover happens to be and a plateless glyph on bright art is invisible. The latched arm
    // keeps the accent glyph the rail header's toggle speaks.
    static Element GlyphButton(string glyph, string tip, Action onClick, ColorF accent, bool active = false) =>
        ToolTip.Wrap(StageChrome.ScrimFab(glyph, onClick, RightRail.HeaderGlyph, accent, latched: active), tip);

    // THE WAY OUT — and it is deliberately NOT the same shape as the button beside it.
    //
    // It was a 40-DIP ScrimFab too, matched to the toggle, and it could not be found: the scrim's own top deepening is
    // already 76% black on every cover (StageLayout.ScrimTopA), so a 55%-black scrim plate on top of it has no edge at
    // all. Matching the secondary control was the mistake — the exit outranks it. StageChrome.ExitFab is a 44-DIP disc
    // whose ground is made of INK rather than of scrim, with a card shadow (the one separation channel that survives
    // an inverted ink ladder), and the prototype's collapse CHEVRON rather than the two-rect BackToWindow glyph, which
    // is a stronger read at this size.
    //
    // The tooltip names the KEYBOARD half. Escape is the fast way out and nothing else in the product teaches it.
    static Element CloseButton(Action onClick, ColorF accent) =>
        ToolTip.Wrap(StageChrome.ExitFab(Icons.ChevronDown, onClick), Loc.Get(Strings.Player.CloseLyricsHint));

    // ── the backdrop stack ───────────────────────────────────────────────────────────────────────────────────────────
    // The backdrop fills the BODY band, not the whole window — the caption strip and the player bar are left to the
    // shell — so every geometry below is derived from the body size, which is the viewport less those two fixed rows.
    static float BodyW(float vpW) => MathF.Max(1f, vpW);
    static float BodyH(float vpH) => MathF.Max(1f, vpH - TitleBar.ExpandedHeight - WaveeSize.PlayerBarH);

    /// <summary>The height the identity column actually gets: the body band less the surface's own top band. ONE
    /// definition, passed into <see cref="StageLayout.Resolve"/>, so the allocator's height ladder and the tree that
    /// realizes it cannot disagree about how much room there is — the same reason the width ladder takes the viewport
    /// width rather than re-deriving it.</summary>
    // The WIDE band height, unconditionally — and that is correct rather than a shortcut: the height ladder only ever
    // runs on the wide path (Resolve returns CompactStage from the WIDTH test before it looks at height at all), so the
    // compact band's smaller height can never be an input to it. Reading the live layout here would make the two
    // mutually recursive for no gain.
    static float ColumnAvailH(float vpH) => MathF.Max(0f, BodyH(vpH) - StageChrome.TopBandH);

    Element Backdrop(IReadSignal<Size2> vpSig, IReadSignal<StageLayout> stage, string art, string? blurHash)
    {
        Element cover = art.Length > 0
            ? Ui.Image(art, ImageFit.Cover, aspect: float.NaN, decodePx: 512f, corners: 0f,
                placeholder: StageInk.ArtStandIn(art), blurHash: blurHash,
                transition: ImageTransition.Fade(220f)) with
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    BakedBlur = new BakedBlurSpec(BackdropSigmaDip, BackdropResolutionScale),
                }
            : new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                Fill = StageInk.ArtStandIn(null),
            };

        return new BoxEl
        {
            Grow = 1f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    // The OVERSIZE frame — a PAINT scale, never a layout size.
                    //
                    // It used to declare Width/Height = viewport x 1.30 plus a compensating translation. That is the
                    // same picture, but it states the overscale as GEOMETRY, and geometry propagates: MeasureZStack
                    // reports a bounded layer's own explicit size as the stack's size (it is never min'd back against
                    // the width it was offered), so 1.30x climbed the ZStacks into the surface root, which was then
                    // arranged 1534x952 inside a 1180x760 window because FlexShrink defaults to 0. Everything the user
                    // reported followed from that one number: the pane region got 1062 where 708 was available, so the
                    // reading column's first glyph landed at x=717 instead of 584 and the lyrics ran off the right
                    // edge; the End-justified pivot and the close button went with it; and the body band extended 192
                    // DIP below the window, which is what cut the output-device line off the bottom of the column.
                    //
                    // As a SCALE about the box's own centre it is what it always was conceptually — a compositor
                    // transform on a slot-sized layer — so it costs layout nothing on either axis and cannot escape
                    // the surface again. Still DECLARED (engine-owned, viewport-bound), so the static backdrop stays
                    // resize-correct with no ticker running, and the drift carrier below still declares none, so the
                    // two never fight over one LocalTransform.
                    ZStack = true, HitTestVisible = false,
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Transform = Prop.Of(() =>
                    {
                        float cx = BodyW(vpSig.Value.Width) * 0.5f, cy = BodyH(vpSig.Value.Height) * 0.5f;
                        return new Affine2D(Overscale, 0f, 0f, Overscale,
                                            cx * (1f - Overscale), cy * (1f - Overscale));
                    }),
                    Children =
                    [
                        new BoxEl
                        {
                            ZStack = true, HitTestVisible = false,
                            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                            OnRealized = h => { _driftNode = h; },
                            Children = [cover],
                        },
                    ],
                },
                // ── the scrim system: TWO full-bleed paint layers, and that is the whole of it ────────────────────────
                // One continuous vertical gradient over the entire body (deep top, flat middle, deep bottom), then one
                // left-anchored layer that deepens the ground under the identity column and feathers to exactly zero.
                // Both are theme-invariant. Every chrome region's own boxed veil is GONE — the two layers below are
                // what the caption cluster, the column and the pivot band now sit on.
                new BoxEl { Grow = 1f, HitTestVisible = false, Gradient = StageChrome.Scrim() },
                new BoxEl
                {
                    // A PAINT layer, not a layout one: it is inside the full-bleed backdrop stack, so its 612-DIP width
                    // costs the pane region nothing (the column BOX is still StageLayout.LayoutWidth). Bound rather than
                    // read at render time so the wide⇄compact flip re-solves it without re-rendering the surface; 0 in
                    // the compact shape, where there is no column to deepen under.
                    Width = Prop.Of(() => stage.Value.Wide ? StageLayout.ColumnShadeW : 0f),
                    AlignSelf = FlexAlign.Stretch,
                    Shrink = 0f, HitTestVisible = false,
                    Gradient = StageChrome.ColumnShade(),
                },
            ],
        };
    }

    // ── the drift ticker ─────────────────────────────────────────────────────────────────────────────────────────────
    // ZERO managed allocation per tick: Peek only (never .Value — a subscription here would re-render the surface at
    // 30 Hz), scalar float math into one stack Affine2D, one ref write into the paint column.
    void DriftTick()
    {
        var scene = Context.Scene;
        var h = _driftNode;
        if (scene is null || h.IsNull || !scene.IsLive(h) || _viewport is null) return;

        long qpc = Stopwatch.GetTimestamp();
        if (_driftOriginQpc == 0L) _driftOriginQpc = qpc;   // t = 0 on the first tick ⇒ the surface opens with no jump
        float t = (float)((qpc - _driftOriginQpc) / (double)Stopwatch.Frequency);

        var vp = _viewport.Peek();
        float vw = BodyW(vp.Width), vh = BodyH(vp.Height);
        float sinA = MathF.Sin(Tau * t / DriftPeriodASec);
        float sinB = MathF.Sin(Tau * t / DriftPeriodBSec);
        // Divided by Overscale because this carrier now sits UNDER the frame's 1.30x paint scale (the frame is
        // slot-sized; the overscale is a transform, not a Width). Every DIP written here is magnified by that scale on
        // the way to the screen, so the division is what keeps the felt drift byte-identical to the pre-fix geometry.
        float dx = DriftAmpFrac * vw * sinA / Overscale;
        float dy = DriftAmpFrac * vh * sinB / Overscale;
        // The scale wobble rides the SAME two sinusoids, so it inherits their incommensurability instead of adding a
        // third period that could beat against them. Range: exactly ±DriftScaleAmp.
        float s = 1f + DriftScaleAmp * 0.5f * (sinB - sinA);
        // Scale about the carrier's own centre, then translate: T(dx,dy) ∘ T(c) ∘ S(s) ∘ T(-c). The carrier's box is
        // the body slot now, not the oversized frame, so its centre is the slot's centre.
        float cx = vw * 0.5f, cy = vh * 0.5f;
        var next = new Affine2D(s, 0f, 0f, s, cx * (1f - s) + dx, cy * (1f - s) + dy);

        ref NodePaint p = ref scene.Paint(h);
        var cur = p.LocalTransform;
        if (MathF.Abs(cur.Dx - next.Dx) < DriftWriteEps &&
            MathF.Abs(cur.Dy - next.Dy) < DriftWriteEps &&
            MathF.Abs(cur.M11 - next.M11) < DriftScaleEps) return;
        p.LocalTransform = next;
        scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    void ResetDrift()
    {
        _driftOriginQpc = 0L;
        var scene = Context.Scene;
        var h = _driftNode;
        if (scene is null || h.IsNull || !scene.IsLive(h)) return;
        ref NodePaint p = ref scene.Paint(h);
        if (p.LocalTransform.IsIdentity) return;
        p.LocalTransform = Affine2D.Identity;
        scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }
}
