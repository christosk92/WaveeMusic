// ── Entities/Detail.Insights.cs ────────────────────────────────────────────────────────────────────────────────────
// THE INSIGHTS SHEET — the host for the facts bento in EVERY arm (approved prototype "Insights Sheet").
//
// Role: UI
// Owner: M
// Wave: 4.5
// Spec: the approved HTML prototype; the arm/width/open rules are Detail.InsightsSheet (Detail.cs §3b).
//
// ── WHY THIS EXISTS ──────────────────────────────────────────────────────────────────────────────────────────────────
//
// A playlist and Liked Songs carry a facts bento (cards, charts, lenses). It used to be a row of the two-column rail and, in
// the vertical (hero/stacked) arm, a page footer nobody scrolled to. Now it is reached ONLY through the Insights toggle in
// the table's command bar and shown in a right-anchored sheet laid OVER the content, in every layout: the rail no longer
// renders it inline. The sheet is dismissed by Escape, the scrim, the close button, the toggle again, or navigating away. It is
// CLOSED on arrival and never opened by anything but the toggle's own click (width, route and data never open it), and it is an
// overlay, so opening it moves neither the rail nor the list.
//
// ENTRY POINTS, ONE CONTROL: the table's command bar (§6) carries it in EVERY arm — between the search and "…", in a slot
// reserved by kind (InsightsSheet.ToggleSlotReserved) that fades in when the facts settle. In the vertical arm the pinned
// 56-DIP context band (§7) carries it for the whole of the rest of the page once the hero COLLAPSES: a sheet meant to be
// read ALONGSIDE the list cannot be opened from chrome that scrolls away. The two-column rail no longer carries it.
// In the vertical arm the command bar's toggle and the band's word are never both legible
// (the band crossfades in exactly as the hero fades out) and never both interactive (input crosses with the pin, not with
// the paint). §7 carries the whole argument.
//
// ── WHAT IS ENGINE, AND WHAT IS OURS ─────────────────────────────────────────────────────────────────────────────────
//
// The overlay itself is the engine's `SplitView` in Overlay × Right: it owns the WinUI slide (350 ms open / 120 ms
// close on the 0.1,0.9 0.2,1.0 spline — reduced motion snaps it, because a LayoutTransition's position channel is
// KeepFade), the light-dismiss layer, Escape, the host-resize dismiss and the whole FOCUS contract (save the focused
// node, move focus into the pane, trap Tab inside it, restore on close). We add only what a SplitView has no opinion
// about: the SCRIM and its edge shadow (drawn inside the content slot, below the control's own dismiss layer), the
// pane's surface styling through `TemplateParts`, the header row, the scrollable body and the (deliberately inert)
// left-edge grip.
//
// The pane is composed in the SHARED FRAME (Detail.UI.cs), not per page: every facts-bearing page declares the SAME
// `FrameSlots.LikedFacts` builder, and the frame wraps BOTH arms in the overlay.
//
// NOT WIRED, ON PURPOSE: the grip is a visual only. An edge-swipe to open/close is undecided and the right edge is
// contested by the transcript rail.
//
// NAME NOTE: inside `Detail`, the nested `Text`/`Skeleton`/`Config` classes shadow `Ui.Text` and friends — text runs
// here are `new TextEl(...)` / `Ui.*`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Detail
{
    // ══ 1. THE TOGGLE (frame-owned state; the hero's toolbar row renders the button) ══════════════════════════════════

    /// <summary>The sheet's open state plus the verbs that move it — ONE instance per mounted frame, handed to the
    /// vertical arm through <see cref="VerticalSpec"/> so the toolbar button and the pane cannot disagree. Not part of
    /// any equality: it is a stable per-host object, never data.</summary>
    public sealed class InsightsToggle
    {
        public readonly Signal<bool> Open = new(false);

        /// <summary>The node a close hands focus back to: the control the reader last USED, not the last one realized.
        /// There are two entry points (the hero's toolbar button and the pinned band's word — §6/§7), only one of which
        /// owns input at any scroll position, so "the last one realized" would routinely hand focus to the one that is
        /// off screen. Each captures its own handle at realize; the click records which of the two moved the sheet.
        /// <para>It matters because the control's own saved-focus restore can have nothing to restore (the sheet was
        /// opened by a keyboard shortcut, or the opener was re-realized while the sheet was open).</para></summary>
        internal readonly NodeHandle[] ButtonNode = [NodeHandle.Null];
        readonly NodeHandle[] _heroNode = [NodeHandle.Null], _bandNode = [NodeHandle.Null];

        public InsightsToggle()
        {
            Toggle = () => { ButtonNode[0] = _heroNode[0]; Open.Value = !Open.Peek(); };
            ToggleFromBand = () => { ButtonNode[0] = _bandNode[0]; Open.Value = !Open.Peek(); };
            Close = () => { if (Open.Peek()) Open.Value = false; };
            // Seed the focus target with whichever entry point realizes first, so a sheet opened before either has been
            // clicked (a route restore) still has somewhere to hand focus back to.
            CaptureButton = h => { _heroNode[0] = h; if (ButtonNode[0].IsNull) ButtonNode[0] = h; };
            CaptureBandButton = h => { _bandNode[0] = h; if (ButtonNode[0].IsNull) ButtonNode[0] = h; };
        }

        /// <summary>The HERO toolbar button's click: open if closed, close if open.</summary>
        public readonly Action Toggle;
        /// <summary>The pinned BAND word's click — the same verb, recording the other opener for the focus return.</summary>
        public readonly Action ToggleFromBand;
        /// <summary>Close from anywhere (the × , a route change, leaving the vertical arm). Value-gated.</summary>
        public readonly Action Close;
        internal readonly Action<NodeHandle> CaptureButton;
        internal readonly Action<NodeHandle> CaptureBandButton;
    }

    // ══ 2. THE GLYPH AND THE TOKENS ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bar-chart mark, drawn like the facts' own pill glyphs (a stroked 24-unit path) rather than a font
    /// codepoint, so it sits in the same visual family as the bento it opens.</summary>
    static readonly PathData s_insightsGlyph =
        PathDataParser.Parse("M4 20h16M7.5 20v-7M12 20V6M16.5 20v-4", PathContentEpoch.Mint(), FillRule.NonZero);

    const float SheetHeaderPadY = Spacing.M;          // 12
    const float SheetPadX = Spacing.L;                // 16
    const float SheetBodyPadTop = Spacing.M;          // 12
    const float SheetBodyPadBottom = Spacing.XL;      // 20
    const float SheetGripW = 4f, SheetGripH = 36f;
    const float SheetToggleEdge = 32f, SheetToggleGlyph = 15f;
    const float SheetCloseEdge = 28f, SheetCloseGlyph = 11f;
    /// <summary>The scrim's cast under the sheet's left edge — the prototype's <c>-18px 0 48px</c>, as a gradient band
    /// (a real drop shadow on the pane would be clipped by the control's own pane clip).</summary>
    const float SheetShadowW = 28f;

    static readonly EasingSpec SheetSpline = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1.0f);

    // ══ 3. THE FRAME SEAM ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Wrap a track page (either arm) in the sheet's overlay. ALWAYS composed for a track page — open or not,
    /// facts or not — so the page's own tree shape never changes when the facts arrive or the sheet opens (the list keeps
    /// its scroll position, the hero keeps its measured height and the rail does not move).
    /// <para><paramref name="facts"/> null ⇒ the pane renders nothing and the toggle can never open it.</para></summary>
    public static Element InsightsOverlay(Element page, InsightsToggle toggle, Func<float, Element>? facts,
                                          float pageWidth, string routeKey, Action? onClosed)
    {
        float paneW = InsightsSheet.WidthFor(pageWidth);
        var open = toggle.Open;
        return SplitView.Create(
            pane: SheetPane(toggle, facts, paneW, routeKey),
            content: new BoxEl
            {
                Key = "insights:content", ZStack = true, Grow = 1f,
                Children = [page, SheetScrim(open, paneW)],
            },
            paneWidth: paneW,
            isPaneOpen: open,
            displayMode: SplitViewDisplayMode.Overlay,
            panePlacement: SplitViewPanePlacement.Right,
            onPaneClosed: onClosed,
            parts: s_sheetParts);
    }

    /// <summary>The pane surface: FROSTED GLASS over the page, and no shadow — the cast lives in the scrim layer,
    /// because the control clips its pane to the pane rect.
    ///
    /// <para><b>Why not a layer token.</b> This part used to author <c>Tok.FillLayerAlt</c>, which is WinUI's
    /// <c>LayerFillColorAltBrush</c>: opaque white in light theme but <c>#FFFFFF @ 0x0D</c> (5%) in DARK
    /// (<c>PaletteBuilder.cs</c>) — a veil, not a surface. The control DOES paint what this part returns; the fill it
    /// was handed simply had almost no coverage, so in dark theme the page read straight through the open sheet (the
    /// hero title, the list's Date added / Time lanes and the rows all legible over the bento's cards).</para>
    ///
    /// <para><b>The recipe is the engine's frosted-flyout plate</b>, authored exactly as <c>OverlayHost.FlyoutSurface</c>
    /// does it: the acrylic SPEC on the node, and the SAME spec's <see cref="AcrylicSpec.Fallback"/> as the node's own
    /// fill UNDERNEATH it. Both, always — where the acrylic layer runs it composites opaquely and the fill under it
    /// costs nothing; where it cannot run (<c>Materials.AcrylicEnabled</c> off for transparency/energy-saver, a
    /// non-primary swapchain, a video hole) the fallback is the only thing between the reader and floating text. The
    /// fill is a BOUND prop so a live theme flip re-reads it; the spec is read when the part is applied, which happens
    /// on every frame render (the frame re-renders on a theme flip).</para></summary>
    static readonly TemplateParts s_sheetParts = BuildSheetParts();

    static TemplateParts BuildSheetParts()
    {
        var parts = new TemplateParts();
        parts[SplitView.PartPane] = static b => b with
        {
            Fill = Prop.Of(static () => Tok.AcrylicFlyout.Fallback),
            Acrylic = Tok.AcrylicFlyout,
            ClipToBounds = true,
        };
        return parts;
    }

    // ══ 4. THE SCRIM ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The dim over the content plus the sheet's left-edge cast. Both are PAINT only — the control's own
    /// light-dismiss layer sits above this and takes the click — and both are mounted only while the sheet is open, so
    /// a closed sheet costs the page nothing.</summary>
    // NOTE: no Key on the Show's content root — that root is a single-child slot whose Key is inert (and reported by
    // ReuseGuard.KeyIgnoredInSingleChildSlot); the two layers inside it carry the keys instead.
    static Element SheetScrim(IReadSignal<bool> open, float paneW) => Flow.Show(() => open.Value, new BoxEl
    {
        ZStack = true, Grow = 1f, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Key = "insights:scrim", Grow = 1f, HitTestVisible = false,
                Fill = Prop.Of(static () => Tok.FillSmoke),
                Animate = new LayoutTransition(TransitionChannels.Opacity,
                    TransitionDynamics.Tween(160f, SheetSpline),
                    Enter: new EnterExit(Opacity: 0f, Active: true),
                    Exit: new EnterExit(Opacity: 0f, Active: true)),
            },
            // The cast: a band immediately left of the sheet, sliding in WITH it so the shadow never arrives early.
            new BoxEl
            {
                Key = "insights:cast", Direction = 0, Grow = 1f, Justify = FlexJustify.End, HitTestVisible = false,
                Margin = new Edges4(0f, 0f, paneW, 0f),
                Animate = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                    TransitionDynamics.Tween(350f, SheetSpline),
                    Enter: new EnterExit(Dx: paneW, Opacity: 0f, Active: true),
                    Exit: new EnterExit(Dx: paneW, Opacity: 0f, Active: true),
                    ExitDynamics: TransitionDynamics.Tween(120f, SheetSpline)),
                Children =
                [
                    new BoxEl
                    {
                        Width = SheetShadowW, Shrink = 0f, HitTestVisible = false,
                        Gradient = GradientRight(new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 0)),
                                                 new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 0x3D))),
                    },
                ],
            },
        ],
    });

    // ══ 5. THE PANE ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The sheet: a left rule, the inert grip, the header row (title + ×) and the scrolling body that hosts the
    /// SHARED bento at the sheet's own content width. The bento is not redesigned here — it is the same
    /// <c>User.FactsPanel</c> the rail composes, at a different measure.</summary>
    static Element SheetPane(InsightsToggle toggle, Func<float, Element>? facts, float paneW, string routeKey)
    {
        float contentW = MathF.Max(0f, paneW - SheetPadX * 2f);
        Element body = facts is null ? new BoxEl() : facts(contentW);

        var column = new BoxEl
        {
            Key = "insights:column", Direction = 1, Grow = 1f, MinHeight = 0f,
            Children =
            [
                SheetHeader(toggle),
                ScrollView(new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Padding = new Edges4(SheetPadX, SheetBodyPadTop, SheetPadX, SheetBodyPadBottom),
                    Children = [body],
                }) with { Key = "insights:body:" + routeKey, Grow = 1f, Shrink = 1f, MinHeight = 0f },
            ],
        };

        return new BoxEl
        {
            Key = "insights:pane", ZStack = true, Grow = 1f,
            Children = [column, SheetLeftEdge()],
        };
    }

    /// <summary>The 1-DIP left rule and the grip that rides it. The grip is DRAWN, never wired: an edge gesture is
    /// undecided and the right edge is contested by the transcript rail.</summary>
    static Element SheetLeftEdge() => new BoxEl
    {
        Key = "insights:edge", Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch, HitTestVisible = false,
        Children =
        [
            new BoxEl { Width = 1f, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeCardDefault) },
            new BoxEl
            {
                Width = SheetGripW + Spacing.XS, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center,
                Children =
                [
                    new BoxEl
                    {
                        Width = SheetGripW, Height = SheetGripH, Corners = CornerRadius4.All(SheetGripW / 2f),
                        Margin = new Edges4(Spacing.XXS, 0f, 0f, 0f), Opacity = 0.5f,
                        Fill = Prop.Of(static () => Tok.TextTertiary),
                    },
                ],
            },
        ],
    };

    static Element SheetHeader(InsightsToggle toggle) => new BoxEl
    {
        Key = "insights:header", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f,
        Padding = new Edges4(SheetPadX, SheetHeaderPadY, Spacing.S, SheetHeaderPadY),
        BorderWidth = 0f,
        Children =
        [
            Ui.BodyStrong(Loc.Get(Strings.Detail.LikedFacts.SheetTitle)) with
            {
                Size = 16f, LineHeight = 22f, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary,
            },
            ToolTip.Wrap(SheetCloseButton(toggle), Loc.Get(Strings.Detail.LikedFacts.SheetClose)) with { Key = "insights:close-tip" },
        ],
    };

    static Element SheetCloseButton(InsightsToggle toggle) => new BoxEl
    {
        Key = "insights:close",
        Width = SheetCloseEdge, Height = SheetCloseEdge, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        HoverFill = Prop.Of(static () => Tok.FillSubtleSecondary),
        PressedFill = Prop.Of(static () => Tok.FillSubtleTertiary),
        HoverDurationMs = MotionTok.ControlFaster.DurationMs, PressDurationMs = MotionTok.ControlFaster.DurationMs,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        OnClick = toggle.Close,
        Children = [Icon(Icons.ChromeClose, SheetCloseGlyph, Tok.TextSecondary)],
    };

    // ══ 6. THE COMMAND BAR'S TOGGLE (the PRE-STUCK half — see §7 for the other) ═══════════════════════════════════════

    // The toggle is a member of the table's COMMAND BAR (Track.Table.Chrome.cs BuildToolbar), in EVERY arm: [Sort] · [Tune] ─
    // search · Insights · ⋯. The vertical arm's toolbar row is therefore just the bar (it used to wrap the bar and trail the
    // toggle after "…"); the two-column arm's bar carries it where the rail's CTA group used to. The fit reserves its 32 DIP
    // (CommandBarLayout.InsightsWidth) whenever the kind reserves the slot. While the selection surface replaces the bar the slot
    // trails that surface, so an open sheet keeps its toggle (and the focus-return target).

    static readonly MotionTokenDef s_toggleFade =
        MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>The toggle's RESERVED slot: the button in a keyed box that is always present (so its width is in the layout from
    /// the first frame), at opacity 0 and inert until the facts have settled <paramref name="live"/>, then faded in. A late fact
    /// therefore never adds a button, moves a neighbour or changes a wrap.</summary>
    public static Element InsightsToggleSlot(InsightsToggle toggle, bool live, string key) => new BoxEl
    {
        Key = key, Direction = 0, Shrink = 0f, Opacity = live ? 1f : 0f, HitTestVisible = live, IsEnabled = live,
        Transition = s_toggleFade, Children = [InsightsToggleButton(toggle)],
    };

    /// <summary>The vertical arm's toolbar button — reachable while the hero is expanded, which is precisely the range
    /// the pinned band cannot serve (there it is transparent and owns no input). Its tint is BOUND to the open signal
    /// (a compositor-only repaint), so opening and closing the sheet never re-renders the hero that hosts it.</summary>
    public static Element InsightsToggleButton(InsightsToggle toggle)
    {
        var open = toggle.Open;
        var button = new BoxEl
        {
            Key = "insights:toggle",
            Width = SheetToggleEdge, Height = SheetToggleEdge, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Fill = Prop.Of(() => open.Value ? Tok.AccentTextPrimary with { A = 0.11f } : ColorF.Transparent),
            HoverFill = Prop.Of(() => open.Value ? Tok.AccentTextPrimary with { A = 0.17f } : Tok.FillSubtleSecondary),
            PressedFill = Prop.Of(() => open.Value ? Tok.AccentTextPrimary with { A = 0.08f } : Tok.FillSubtleTertiary),
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, PressDurationMs = MotionTok.ControlFaster.DurationMs,
            // ToggleButton, not Button: the pressed/unpressed state IS the control's meaning (the prototype's
            // aria-pressed), and the accent plate is the same state in paint.
            Role = AutomationRole.ToggleButton, Focusable = true, Cursor = CursorId.Hand,
            OnClick = toggle.Toggle, OnRealized = toggle.CaptureButton,
            Children =
            [
                new PathEl
                {
                    Geometry = s_insightsGlyph, Width = SheetToggleGlyph, Height = SheetToggleGlyph,
                    ViewBoxW = 24f, ViewBoxH = 24f, Shrink = 0f,
                    StrokeColor = Tok.TextSecondary, Stroke = new StrokeStyle(2.2f, LineCap.Round, LineJoin.Round),
                },
            ],
        };
        return ToolTip.Wrap(button, Loc.Get(Strings.Detail.LikedFacts.SheetOpen)) with { Key = "insights:toggle-tip" };
    }

    // ══ 7. THE PINNED BAND'S TOGGLE ══════════════════════════════════════════════════════════════════════════════════
    //
    // ── WHY THE TOGGLE IS IN TWO PLACES, AND WHY THAT IS NOT DUPLICATION ────────────────────────────────────────────
    //
    // The hero COLLAPSES. Its toolbar row is legible only while the reader is at the top of the page, and the sheet
    // exists to be consulted WHILE READING THE LIST — so a toggle that lives only up there is gone exactly when it is
    // wanted. The vertical arm's other chrome is the pinned 56-DIP context band, whose right cluster restates the
    // page's verbs as plateless words: Find · Filter · Play. Every one of those three is ALREADY a restatement of
    // something the hero carries (the command bar's search glyph, its funnel, the hero's Play pill) — the band is not a
    // second set of controls, it is the SAME controls after the hero has scrolled away. Insights joins that cluster on
    // exactly those terms, which is why the hero button STAYS:
    //
    //   · the two are never both legible. The band's opacity ramps 0 → 1 over the LAST 44 DIP of the collapse
    //     (VerticalLayout.CompactRevealStart) while the expanded hero fades 1 → 0 over the last 96 (ExpandedFadeStart);
    //     at rest the band is fully transparent, and once stuck the hero is gone. What overlaps is a crossfade, which
    //     is the handoff, not two buttons competing for the same glance;
    //   · INPUT is exclusive by construction and follows the pin, not the paint: the band takes hits only once its
    //     chrome is stuck (the chrome sticky's engaged edge → `TableHost._compactInteractive` → the band's HitTestVisible)
    //     and the hero's presentation stops taking them at the same edge. InsightsSheet.BandToggleTakesInput /
    //     HeroToggleTakesInput state that invariant, and DetailInsightsSheetTests pins it: at every scroll position EXACTLY ONE
    //     of the two is reachable. Dropping the hero one would therefore leave the whole pre-stuck range — the top of the page, where a
    //     reader most naturally reaches for the facts — with no way to open the sheet at all.
    //
    // Position in the cluster: Find · Filter · Insights · Play. The three verbs that shape WHAT YOU SEE sit together
    // and the one primary verb stays terminal.

    /// <summary>The pinned band's entry point: the sheet's toggle as a plateless WORD in the Find · Filter · Play
    /// cluster, latched to accent ink while the sheet is open (<c>Controls.TextAction</c>'s own <c>toggledOn</c> arm —
    /// accent in that band always means "the primary verb" or "this is on").
    /// <para>Its own component, so that opening or closing the sheet re-renders ONE word: the band is built by the
    /// table's <c>HeroPartsFor</c>, and reading the open signal out there would re-render the table host — and with it
    /// the list. The toggle instance is mount-stable per frame host (a propless factory is safe here: the field cannot
    /// go stale, because the slot is reserved by kind and stays mounted — the facts only fade it in, through <paramref name="live"/>).</para></summary>
    public static Element InsightsBandAction(InsightsToggle toggle, bool live)
        => new BoxEl
        {
            // The slot is reserved by kind: present (so the cluster and the search field's claim are final) but invisible and inert
            // until the facts settle, then faded in.
            Key = "band:insights", Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
            Opacity = live ? 1f : 0f, HitTestVisible = live, IsEnabled = live, Transition = s_toggleFade,
            Children = [Embed.Comp(() => new InsightsBandWord(toggle)) with { Key = "band:insights-word" }],
        };

    sealed class InsightsBandWord(InsightsToggle toggle) : Component
    {
        public override Element Render()
        {
            var t = toggle;
            // The ONLY signal this render reads — so this is the whole cost of a toggle click.
            bool open = t.Open.Value;
            return Controls.TextAction(Loc.Get(Strings.Detail.LikedFacts.SheetOpen), t.ToggleFromBand, toggledOn: open,
                                       height: BandLayout.ItemHeight, padX: BandLayout.ActionPadX) with
            {
                // ToggleButton, not Button: pressed/unpressed IS the control's meaning, exactly as in the hero (§6).
                Role = AutomationRole.ToggleButton,
                OnRealized = t.CaptureBandButton,
            };
        }
    }

    /// <summary>What the band's search field must leave for the cluster once the toggle joins it — the label widths the
    /// band estimates, routed through the one pure rule so the field narrows instead of shoving the words off the
    /// band's right edge (<see cref="InsightsSheet.BandActionsWidth"/>).</summary>
    public static float BandActionsClaim(string find, string filter, string play, bool showsToggle)
        => InsightsSheet.BandActionsWidth(
            BandLayout.EstimateLabelWidth(find, BandLayout.ActionPadX),
            BandLayout.EstimateLabelWidth(filter, BandLayout.ActionPadX),
            BandLayout.EstimateLabelWidth(play, BandLayout.ActionPadX),
            BandLayout.EstimateLabelWidth(Loc.Get(Strings.Detail.LikedFacts.SheetOpen), BandLayout.ActionPadX),
            showsToggle);
}
