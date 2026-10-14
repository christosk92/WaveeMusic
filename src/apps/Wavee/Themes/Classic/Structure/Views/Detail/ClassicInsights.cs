// ── Themes/Classic/Structure/Views/Detail/ClassicInsights.cs — Classic's insights sheet and its two entry points (themes plan 6.1, T9a) ──
//
// From `Entities\Detail.Insights.cs`: THE INSIGHTS SHEET, the vertical arm's host for the facts bento (approved prototype "Insights Sheet"):
// the engine `SplitView` in Overlay × Right, its frosted pane, the scrim and its left-edge cast, the header row with the ×, the scrolling body
// that hosts the page's own bento, and the two entry points — the hero toolbar's glyph button and the pinned band's plateless word.
//
// WHAT STAYS CORE (`Entities\Detail.Insights.cs`): the toggle object (`InsightsToggle`: the open signal and the verbs that move it, one instance
// per mounted frame), the arm / width / open rules (`InsightsSheet`: `ShowsToggle`, `WidthFor`, `FactsSettled`, `BandToggleTakesInput`,
// `HeroToggleTakesInput`, `BandActionsWidth`), the facts latch and the focus return (the frame host), and the band's width claim
// (`Detail.BandActionsClaim`, called by the table's chrome).
//
// ── WHAT IS ENGINE, AND WHAT IS OURS ───────────────────────────────────────────────────────────────────────────────────────────
//
// The overlay itself is the engine's `SplitView` in Overlay × Right: it owns the WinUI slide (350 ms open / 120 ms close on the 0.1,0.9 0.2,1.0
// spline — reduced motion snaps it, because a LayoutTransition's position channel is KeepFade), the light-dismiss layer, Escape, the host-resize
// dismiss and the whole FOCUS contract (save the focused node, move focus into the pane, trap Tab inside it, restore on close). We add only what
// a SplitView has no opinion about: the SCRIM and its edge shadow (drawn inside the content slot, below the control's own dismiss layer), the
// pane's surface styling through `TemplateParts`, the header row, the scrollable body and the (deliberately inert) left-edge grip.
//
// NOT WIRED, ON PURPOSE: the grip is a visual only. An edge-swipe to open/close is undecided and the right edge is contested by the transcript rail.
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
using InsightsSheet = Wavee.Detail.InsightsSheet;
using InsightsToggle = Wavee.Detail.InsightsToggle;

namespace Wavee.Themes.Classic;

/// <summary>Classic's insights sheet (<see cref="ViewKeys.DetailInsights"/>) and its entry points (<see cref="ViewKeys.DetailInsightsToggle"/>).</summary>
internal static class ClassicInsights
{
    /// <summary>The registered template of <see cref="ViewKeys.DetailInsights"/>: the vertical arm's page, wrapped in the sheet's overlay. ALWAYS
    /// composed for a track page's vertical arm (the host decides), open or not.</summary>
    public static Element SheetTemplate(DetailInsightsProps p, in ViewContext ctx)
        => Sheet(p.Page ?? new BoxEl(), p.Toggle, p.Facts, p.PageWidth, p.RouteKey, p.OnClosed);

    /// <summary>The registered template of <see cref="ViewKeys.DetailInsightsToggle"/>: the hero toolbar's button, or the pinned band's word (its
    /// own component, so that opening or closing the sheet re-renders ONE word).</summary>
    public static Element ToggleTemplate(DetailInsightsToggleProps p, in ViewContext ctx)
    {
        var toggle = p.Toggle;
        return p.Form == DetailInsightsToggleForm.BandWord
            ? Embed.Comp(() => new InsightsBandWord(toggle))
            : ToggleButton(toggle);
    }

    // ══ 2. THE GLYPH AND THE TOKENS ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bar-chart mark, drawn like the facts' own pill glyphs (a stroked 24-unit path) rather than a font
    /// codepoint, so it sits in the same visual family as the bento it opens.</summary>
    static readonly PathData s_insightsGlyph =
        PathDataParser.Parse("M4 20h16M7.5 20v-7M12 20V6M16.5 20v-4", PathContentEpoch.Mint(), FillRule.NonZero);

    static float SheetHeaderPadY => ClassicDetailLayout.SheetHeaderPadY;          // 12
    static float SheetPadX => ClassicDetailLayout.SheetPadX;                      // 16
    static float SheetBodyPadTop => ClassicDetailLayout.SheetBodyPadTop;          // 12
    static float SheetBodyPadBottom => ClassicDetailLayout.SheetBodyPadBottom;    // 20
    const float SheetGripW = ClassicDetailLayout.SheetGripW, SheetGripH = ClassicDetailLayout.SheetGripH;
    const float SheetToggleEdge = ClassicDetailLayout.SheetToggleEdge, SheetToggleGlyph = ClassicDetailLayout.SheetToggleGlyph;
    const float SheetCloseEdge = ClassicDetailLayout.SheetCloseEdge, SheetCloseGlyph = ClassicDetailLayout.SheetCloseGlyph;
    /// <summary>The scrim's cast under the sheet's left edge — the prototype's <c>-18px 0 48px</c>, as a gradient band
    /// (a real drop shadow on the pane would be clipped by the control's own pane clip).</summary>
    const float SheetShadowW = ClassicDetailLayout.SheetShadowW;

    static readonly EasingSpec SheetSpline = ClassicDetailLayout.SheetSpline;

    // ══ 3. THE FRAME SEAM ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Wrap the vertical arm's page in the sheet's overlay. ALWAYS composed for a track page's vertical arm —
    /// open or not, facts or not — so the page's own tree shape never changes when the facts arrive or the sheet opens
    /// (the list keeps its scroll position and the hero keeps its measured height).
    /// <para><paramref name="facts"/> null ⇒ the pane renders nothing and the toggle can never open it.</para></summary>
    static Element Sheet(Element page, InsightsToggle toggle, Func<float, Element>? facts,
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
                        Gradient = GradientRight(new GradientStop(0f, ClassicDetailLayout.SheetCastClear),
                                                 new GradientStop(1f, ClassicDetailLayout.SheetCastEdge)),
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
                        Margin = new Edges4(Spacing.XXS, 0f, 0f, 0f), Opacity = ClassicDetailLayout.SheetGripOpacity,
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

    /// <summary>The vertical arm's toolbar button — reachable while the hero is expanded, which is precisely the range
    /// the pinned band cannot serve (there it is transparent and owns no input). Its tint is BOUND to the open signal
    /// (a compositor-only repaint), so opening and closing the sheet never re-renders the hero that hosts it.</summary>
    static Element ToggleButton(InsightsToggle toggle)
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
                // ToggleButton, not Button: pressed/unpressed IS the control's meaning, exactly as in the hero (§6).
                Role = AutomationRole.ToggleButton,
                OnRealized = t.CaptureBandButton,
            };
        }
    }
}
