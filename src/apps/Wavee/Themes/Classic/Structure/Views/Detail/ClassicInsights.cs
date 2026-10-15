// ── Themes/Classic/Structure/Views/Detail/ClassicInsights.cs — Classic's insights sheet and its entry points (themes plan 6.1, T9a; tuned I1) ──
//
// From `Entities\Detail.Insights.cs`: THE INSIGHTS SHEET, the host of the facts bento on every facts page (tuned Classic I1, board C-Liked): the
// engine `SplitView` in Overlay × Right, its frosted 320-DIP pane, the cast under its left edge, the header row with the ×, the scrolling body that
// hosts the page's own bento, and the entry points — the list bar's glyph button and the vertical arm's pinned-band word.
//
// WHERE IT SITS. The vertical arm's page is wrapped in it (the sheet covers the page, hero included; the hero bar and the pinned band carry the
// toggle). The two-column arm lays it over the LIST column below the list bar (`ClassicDetailFrame`), so the bar's Insights button stays reachable
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
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using BandLayout = Wavee.Detail.BandLayout;
using InsightsToggle = Wavee.Detail.InsightsToggle;

namespace Wavee.Themes.Classic;

/// <summary>Classic's insights sheet (<see cref="ViewKeys.DetailInsights"/>) and its entry points (<see cref="ViewKeys.DetailInsightsToggle"/>).</summary>
internal static class ClassicInsights
{
    /// <summary>The registered template of <see cref="ViewKeys.DetailInsights"/>: what it covers (the vertical arm's page, or the two-column arm's
    /// pass-through layer over the list), wrapped in the sheet's overlay. ALWAYS composed for a track page (the host decides), open or not.</summary>
    public static Element SheetTemplate(DetailInsightsProps p, in ViewContext ctx)
        => Sheet(p.Page ?? new BoxEl(), p.Toggle, p.Facts, p.PageWidth, p.RouteKey, p.OnClosed);

    /// <summary>The registered template of <see cref="ViewKeys.DetailInsightsToggle"/>: the list bar's glyph button, or the pinned band's word.
    /// Each is its own component, so opening or closing the sheet re-renders ONE button.</summary>
    public static Element ToggleTemplate(DetailInsightsToggleProps p, in ViewContext ctx)
    {
        var toggle = p.Toggle;
        return p.Form == DetailInsightsToggleForm.BandWord
            ? Embed.Comp(() => new InsightsBandWord(toggle))
            : ToolTip.Wrap(Embed.Comp(() => new InsightsBarButton(toggle)), Loc.Get(Strings.Detail.LikedFacts.SheetOpen)) with { Key = "insights:toggle-tip" };
    }

    // ══ 2. THE GLYPH AND THE TOKENS ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bar-chart mark, drawn like the facts' own pill glyphs (a stroked 24-unit path) rather than a font
    /// codepoint, so it sits in the same visual family as the bento it opens.</summary>
    static readonly PathData s_insightsGlyph =
        PathDataParser.Parse("M4 20h16M7.5 20v-7M12 20V6M16.5 20v-4", PathContentEpoch.Mint(), FillRule.NonZero);

    static float SheetPad => ClassicDetailLayout.SheetPad;      // 16
    static float SheetGap => ClassicDetailLayout.SheetGap;      // 12
    const float SheetToggleEdge = ClassicDetailLayout.SheetToggleEdge, SheetToggleGlyph = ClassicDetailLayout.SheetToggleGlyph;
    const float SheetCloseEdge = ClassicDetailLayout.SheetCloseEdge, SheetCloseGlyph = ClassicDetailLayout.SheetCloseGlyph;
    const float SheetShadowW = ClassicDetailLayout.SheetShadowW;

    static readonly EasingSpec SheetSpline = ClassicDetailLayout.SheetSpline;

    // ══ 3. THE FRAME SEAM ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Wrap <paramref name="page"/> in the sheet's overlay. ALWAYS composed for a track page — open or not, facts or not — so the
    /// tree under it never changes when the facts arrive or the sheet opens (the list keeps its scroll position and the hero its height).
    /// <para><paramref name="facts"/> null ⇒ the pane renders nothing and the toggle can never open it.</para></summary>
    static Element Sheet(Element page, InsightsToggle toggle, Func<float, Element>? facts,
                         float pageWidth, string routeKey, Action? onClosed)
    {
        float paneW = ClassicDetailLayout.SheetWidthFor(pageWidth);
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
                        Gradient = GradientRight(new GradientStop(0f, ClassicDetailLayout.SheetCastClear),
                                                 new GradientStop(1f, ClassicDetailLayout.SheetCastEdge)),
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
                Size = ClassicDetailLayout.SheetTitleSize, LineHeight = ClassicDetailLayout.SheetTitleLine, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1,
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

    // ══ 6. THE LIST BAR'S TOGGLE ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The list bar's 32-DIP glyph button (the two-column arm's bar, and the vertical arm's hero bar while the hero is expanded — the
    /// range the pinned band cannot serve). Pressed, it takes the accent plate and accent ink (board C-Liked: <c>aria-pressed</c>). Its own
    /// component: the open signal is the only thing it reads, so a toggle never re-renders the bar that hosts it.</summary>
    sealed class InsightsBarButton(InsightsToggle toggle) : Component
    {
        public override Element Render()
        {
            var t = toggle;
            bool open = t.Open.Value;
            return new BoxEl
            {
                Key = "insights:toggle",
                Width = SheetToggleEdge, Height = SheetToggleEdge, Shrink = 0f,
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
            };
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
