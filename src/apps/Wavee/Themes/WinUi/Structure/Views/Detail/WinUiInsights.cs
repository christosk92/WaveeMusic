// ── Themes/WinUi/Structure/Views/Detail/WinUiInsights.cs — WinUI's insights sheet and its entry points (themes plan 6.2; P6 W6a, from Classic's tuned I1) ──
//
// From `Entities\Detail.Insights.cs`: THE INSIGHTS SHEET, the host of the facts bento on every facts page (tuned Classic I1, board C-Liked): the
// engine `SplitView` in Overlay × Right, its frosted 320-DIP pane, the cast under its left edge, the header row with the ×, the scrolling body that
// hosts the page's own bento, and the entry points — the list bar's glyph button and the vertical arm's pinned-band word.
//
// WHERE IT SITS. The vertical arm's page is wrapped in it (the sheet covers the page, hero included; the hero bar and the pinned band carry the
// toggle). The two-column arm lays it over the LIST column below the list bar (the frame), so the bar's Insights button stays reachable
// and reads pressed while the sheet is open, as the board draws it.
//
// WHAT STAYS CORE (`Entities\Detail.Insights.cs`): the toggle object (`InsightsToggle`: the open signal and the verbs that move it, one instance
// per mounted frame), the arm / open rules (`InsightsSheet`: `ShowsToggle`, `FactsSettled`, `BandActionsWidth`), the facts latch and the focus
// return (the frame host), and the band's width claim (`Detail.BandActionsClaim`, called by the table's chrome).
//
// ── WHAT IS ENGINE, AND WHAT IS OURS ───────────────────────────────────────────────────────────────────────────────────────────
//
// The overlay itself is the engine's `SplitView` in Overlay × Right: it owns the WinUI slide (350 ms open / 120 ms close on the 0.1,0.9 0.2,1.0
// spline — reduced motion snaps it, because a LayoutTransition's position channel is KeepFade), the light-dismiss layer (a click on the list closes
// it), Escape, the host-resize dismiss and the whole FOCUS contract (save the focused node, move focus into the pane, trap Tab inside it, restore
// on close). We add only what a SplitView has no opinion about: the cast under the pane's left edge (drawn inside the content slot, below the
// control's own dismiss layer; the board has no dimming scrim), the pane's surface styling through `TemplateParts`, the header row and the
// scrollable body.
//
// NAME NOTE: `Text`, `Skeleton` and `Config` are nested `Detail` classes; text runs here are `new TextEl(...)` / `Ui.*`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using BandLayout = Wavee.Detail.BandLayout;
using InsightsToggle = Wavee.Detail.InsightsToggle;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's insights sheet (<see cref="ViewKeys.DetailInsights"/>) and its entry points (<see cref="ViewKeys.DetailInsightsToggle"/>).</summary>
internal static class WinUiInsights
{
    /// <summary>The registered template of <see cref="ViewKeys.DetailInsights"/>: what it covers (the vertical arm's page, or the two-column arm's
    /// pass-through layer over the list), wrapped in the sheet's overlay. ALWAYS composed for a track page (the host decides), open or not.</summary>
    public static Element SheetTemplate(DetailInsightsProps p, in ViewContext ctx)
        => p.Docked
            ? Docked(p.Toggle, p.Facts, p.RouteKey)
            : Sheet(p.Page ?? new BoxEl(), p.Toggle, p.Facts, p.PageWidth, p.RouteKey, p.OnClosed);

    /// <summary>The registered template of <see cref="ViewKeys.DetailInsightsToggle"/>: the list bar's glyph button, or the pinned band's word.
    /// Each is its own component, so opening or closing the sheet re-renders ONE button.</summary>
    public static Element ToggleTemplate(DetailInsightsToggleProps p, in ViewContext ctx)
    {
        var toggle = p.Toggle;
        // The bar's commands take the tier's target edge (40 at Compact, the brief's rule); a crossing resizes the one button and keeps its node, its
        // focus and its hover (audit B9: never a key built from the edge).
        float edge = MathF.Max(SheetToggleEdge, WinUiDetailTiers.Of(ctx.Tier()).Target);
        return p.Form == DetailInsightsToggleForm.BandWord
            ? Embed.Comp(() => new InsightsBandWord(toggle))
            : ToolTip.Wrap(Embed.Comp(new BarProps(toggle, edge), static () => new InsightsBarButton()) with { Key = "insights:toggle:bar" },
                Loc.Get(Strings.Detail.LikedFacts.SheetOpen)) with { Key = "insights:toggle-tip" };
    }

    // ══ 2. THE GLYPH AND THE TOKENS ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bar-chart mark, drawn like the facts' own pill glyphs (a stroked 24-unit path) rather than a font
    /// codepoint, so it sits in the same visual family as the bento it opens.</summary>
    static readonly PathData s_insightsGlyph =
        PathDataParser.Parse("M4 20h16M7.5 20v-7M12 20V6M16.5 20v-4", PathContentEpoch.Mint(), FillRule.NonZero);

    static float SheetPad => WinUiDetailRules.SheetPad;      // 16
    static float SheetGap => WinUiDetailRules.SheetGap;      // 12
    const float SheetToggleEdge = WinUiDetailRules.SheetToggleEdge, SheetToggleGlyph = WinUiDetailRules.SheetToggleGlyph;
    const float SheetCloseEdge = WinUiDetailRules.SheetCloseEdge, SheetCloseGlyph = WinUiDetailRules.SheetCloseGlyph;
    const float SheetShadowW = WinUiDetailRules.SheetShadowW;

    static readonly EasingSpec SheetSpline = WinUiDetailRules.SheetSpline;

    // ══ 3. THE FRAME SEAM ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Wrap <paramref name="page"/> in the sheet's overlay. ALWAYS composed for a track page — open or not, facts or not — so the
    /// tree under it never changes when the facts arrive or the sheet opens (the list keeps its scroll position and the hero its height).
    /// <para><paramref name="facts"/> null ⇒ the pane renders nothing and the toggle can never open it.</para></summary>
    static Element Sheet(Element page, InsightsToggle toggle, Func<float, Element>? facts,
                         float pageWidth, string routeKey, Action? onClosed)
    {
        float paneW = WinUiDetailRules.SheetWidthFor(pageWidth);
        var open = toggle.Open;
        return SplitView.Create(
            pane: SheetPane(toggle, facts, paneW, routeKey),
            content: new BoxEl
            {
                Key = "insights:content", ZStack = true, Grow = 1f,
                Children = [page, SheetCast(open, paneW)],
            },
            paneWidth: paneW,
            isPaneOpen: open,
            displayMode: SplitViewDisplayMode.Overlay,
            panePlacement: SplitViewPanePlacement.Right,
            onPaneClosed: onClosed,
            parts: s_sheetParts);
    }

    /// <summary>The pane surface: FROSTED GLASS over the list, and no shadow — the cast lives in the content layer, because the control clips
    /// its pane to the pane rect.
    ///
    /// <para><b>Why not a layer token.</b> <c>Tok.FillLayerAlt</c> is WinUI's <c>LayerFillColorAltBrush</c>: opaque white in light theme but
    /// <c>#FFFFFF @ 0x0D</c> (5%) in DARK — a veil, not a surface, and the list read straight through the open sheet.</para>
    ///
    /// <para><b>The recipe is the engine's frosted-flyout plate</b>, authored exactly as <c>OverlayHost.FlyoutSurface</c> does it: the acrylic
    /// SPEC on the node, and the SAME spec's <see cref="AcrylicSpec.Fallback"/> as the node's own fill UNDERNEATH it, so where the acrylic layer
    /// cannot run (transparency or energy saver off, a non-primary swapchain, a video hole) the fallback is still an opaque surface. The fill is
    /// a BOUND prop so a live theme flip re-reads it.</para></summary>
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

    // ══ 3b. THE DOCKED PANE (Huge, board C-Liked-Huge) ════════════════════════════════════════════════════════════════

    /// <summary>The pane DOCKED beside the list (the Huge record, C-Liked-Huge): a 360 column with its left hairline on the layer fill, the same header
    /// and body as the sheet. Its slot's width is bound to the open state (0 closed), and the slot eases it with the pane's own dynamics, so the
    /// list yields its width as the pane opens instead of jumping; the column inside keeps its full width while the slot reveals it, so nothing in
    /// the pane reflows. There is no light dismiss and no cast: a docked pane is a column of the page, closed by its × or the bar's toggle.</summary>
    static Element Docked(InsightsToggle toggle, Func<float, Element>? facts, string routeKey)
    {
        var open = toggle.Open;
        float w = WinUiDetailRules.DockedInsightsWidth;
        return new BoxEl
        {
            Key = "insights:dock", Direction = 1, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
            // A page whose facts have not settled yet docks nothing, even while the pane rests open (contract 1.21: a Huge dock rests open).
            Width = facts is null ? 0f : Prop.Of(() => open.Value ? w : 0f),
            Animate = WinUiDetailRules.DockReveal,
            Children =
            [
                new BoxEl
                {
                    Key = "insights:dock-column", Direction = 1, Grow = 1f, Width = w, Shrink = 0f, MinHeight = 0f,
                    Fill = Prop.Of(static () => Tok.FillLayerDefault),
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Grow = 1f, MinHeight = 0f,
                            Children =
                            [
                                new BoxEl { Width = 1f, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                                new BoxEl
                                {
                                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                                    Padding = WinUiDetailRules.DockPadding,
                                    Children = [SheetPane(toggle, facts, w - 1f - WinUiDetailRules.DockPadding.Left - WinUiDetailRules.DockPadding.Right, routeKey)],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    // ══ 3c. THE INSIGHTS IN THE ASIDE (Huge, C-Playlist-Huge / C-PlaylistFollowed-Huge; contract 1.21) ═════════════════

    /// <summary>The insights as a section of the docked aside: the same header (title + ×) over the shared bento at the aside's content
    /// <paramref name="width"/>, shown while the toggle is open (the list bar's button reads pressed) and faded in and out with it. No scroller of its own: the
    /// aside scrolls.</summary>
    public static Element AsideSection(InsightsToggle toggle, Func<float, Element> facts, float width, float gap = WinUiDetailRules.AsideTitleGap)
    {
        var open = toggle.Open;
        return new BoxEl
        {
            Key = "aside:insights", Direction = 1, MinWidth = 0f,
            Children =
            [
                Flow.Show(() => open.Value, new BoxEl
                {
                    Direction = 1, Gap = gap, MinWidth = 0f,
                    Enter = WinUiDetailRules.ArmFade, Exit = WinUiDetailRules.ArmFade, Transition = WinUiDetailRules.ArmFadeMotion,
                    Children = [SheetHeader(toggle), facts(width)],
                }),
            ],
        };
    }

    // ══ 4. THE CAST ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The sheet's left-edge cast (the board's shadow; there is no dimming scrim). PAINT only — the control's own light-dismiss layer
    /// sits above it and takes the click — and mounted only while the sheet is open, so a closed sheet costs the page nothing.</summary>
    // NOTE: no Key on the Show's content root — that root is a single-child slot whose Key is inert (and reported by
    // ReuseGuard.KeyIgnoredInSingleChildSlot); the layer inside it carries the key instead.
    static Element SheetCast(IReadSignal<bool> open, float paneW) => Flow.Show(() => open.Value, new BoxEl
    {
        ZStack = true, Grow = 1f, HitTestVisible = false,
        Children =
        [
            // A band immediately left of the sheet, sliding in WITH it so the shadow never arrives early.
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
                        Gradient = GradientRight(new GradientStop(0f, WinUiDetailRules.SheetCastClear),
                                                 new GradientStop(1f, WinUiDetailRules.SheetCastEdge)),
                    },
                ],
            },
        ],
    });

    // ══ 5. THE PANE ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The sheet (board C-Liked): 16 in on every side, the header row (title + ×) and, 12 below it, the scrolling body that hosts the
    /// SHARED bento at the sheet's content width. The bento is not redesigned here — it is the same <c>User.FactsPanel</c> the rail used to
    /// compose.</summary>
    static Element SheetPane(InsightsToggle toggle, Func<float, Element>? facts, float paneW, string routeKey)
    {
        float contentW = MathF.Max(0f, paneW - SheetPad * 2f);
        Element body = facts is null ? new BoxEl() : facts(contentW);
        return new BoxEl
        {
            Key = "insights:column", Direction = 1, Grow = 1f, MinHeight = 0f, Gap = SheetGap,
            Padding = new Edges4(SheetPad, SheetPad, SheetPad, 0f),
            Children =
            [
                SheetHeader(toggle),
                ScrollView(new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, SheetPad),
                    Children = [body],
                }) with { Key = "insights:body:" + routeKey, Grow = 1f, Shrink = 1f, MinHeight = 0f },
            ],
        };
    }

    static Element SheetHeader(InsightsToggle toggle) => new BoxEl
    {
        Key = "insights:header", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
        Children =
        [
            Ui.BodyStrong(Loc.Get(Strings.Detail.LikedFacts.SheetTitle)) with
            {
                Size = WinUiDetailRules.SheetTitleSize, LineHeight = WinUiDetailRules.SheetTitleLine, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1,
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
        OnClick = toggle.Dismiss,
        Children = [Icon(Icons.ChromeClose, SheetCloseGlyph, Tok.TextSecondary)],
    };

    // ══ 6. THE LIST BAR'S TOGGLE ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The list bar's 32-DIP glyph button (the two-column arm's bar, and the vertical arm's hero bar while the hero is expanded — the
    /// range the pinned band cannot serve). Pressed, it takes the accent plate and accent ink (board C-Liked: <c>aria-pressed</c>). Its own
    /// component: the open signal is the only thing it reads, so a toggle never re-renders the bar that hosts it.</summary>
    sealed record BarProps(InsightsToggle Toggle, float Edge);

    sealed class InsightsBarButton : Component
    {
        public override Element Render()
        {
            var (t, edge) = UseProps<BarProps>();
            bool open = t.Open.Value;
            // The tier's edge eases on the button's own box, never on the component's root (whose size would land at once).
            return new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [new BoxEl
            {
                Key = "insights:toggle",
                Width = edge, Height = edge, Shrink = 0f, Layout = Design.Reduced ? null : WinUiDiscoveryMotion.Reflow,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Fill = open ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.11f }) : (Prop<ColorF>)ColorF.Transparent,
                HoverFill = open ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.17f }) : Prop.Of(static () => Tok.FillSubtleSecondary),
                PressedFill = open ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.08f }) : Prop.Of(static () => Tok.FillSubtleTertiary),
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, PressDurationMs = MotionTok.ControlFaster.DurationMs,
                // ToggleButton, not Button: the pressed/unpressed state IS the control's meaning (the board's aria-pressed), and the accent
                // plate is the same state in paint.
                Role = AutomationRole.ToggleButton, Focusable = true, Cursor = CursorId.Hand,
                OnClick = t.Toggle, OnRealized = t.CaptureButton,
                Children =
                [
                    new PathEl
                    {
                        Geometry = s_insightsGlyph, Width = SheetToggleGlyph, Height = SheetToggleGlyph,
                        ViewBoxW = 24f, ViewBoxH = 24f, Shrink = 0f,
                        StrokeColor = open ? Tok.AccentTextPrimary : Tok.TextSecondary,
                        Stroke = new StrokeStyle(2.2f, LineCap.Round, LineJoin.Round),
                    },
                ],
            }] };
        }
    }

    // ══ 7. THE PINNED BAND'S TOGGLE ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The pinned band's entry point: the sheet's toggle as a plateless WORD in the Find · Filter · Play cluster, latched to accent ink
    /// while the sheet is open (<c>Controls.TextAction</c>'s own <c>toggledOn</c> arm — accent in that band always means "the primary verb" or
    /// "this is on"). The argument for why the toggle lives in two places is <c>Detail.InsightsBandAction</c>'s.</summary>
    sealed class InsightsBandWord(InsightsToggle toggle) : Component
    {
        public override Element Render()
        {
            var t = toggle;
            // The ONLY signal this render reads — so this is the whole cost of a toggle click.
            bool open = t.Open.Value;
            return Controls.TextAction(Loc.Get(Strings.Detail.LikedFacts.SheetOpen), t.ToggleFromBand, toggledOn: open,
                                       padX: BandLayout.ActionPadX) with
            {
                // ToggleButton, not Button: pressed/unpressed IS the control's meaning, exactly as on the bar.
                Role = AutomationRole.ToggleButton,
                OnRealized = t.CaptureBandButton,
            };
        }
    }
}
