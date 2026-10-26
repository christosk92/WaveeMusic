// ── Themes/Zune/Structure/Views/Detail/ZuneInsights.cs — Zune's insights sheet and its entry points (themes plan 6.3; P5 Z6a1) ────────────────
//
// No Zune board draws the insights; they are built from Zune's parts (Q18): the facts bento (the page's own, `Detail.FrameSlots.LikedFacts`,
// drawn in Zune's voice by `ZuneFacts`) in a flat, square pane on the floating-pane layer, its "insights" heading (26/32 light, lowercased) and a
// 40 close, sliding over the list from the right (the engine's `SplitView`: its slide, light dismiss, Escape and focus contract), or as a
// section of the docked aside where one docks. The bento takes the width of the column it lands in (never a width of its own: an aside that
// yields to a docked panel would clip it). The entry points: the list bar's bar-chart mark (accent while open) and the stuck band's word.
//
// What stays core (`Entities\Detail.Insights.cs`): the toggle (the open signal and its verbs), the arm and open rules, the facts latch and the
// focus return.

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
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using InsightsToggle = Wavee.Detail.InsightsToggle;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using F = Wavee.Themes.Zune.ZuneFactsRules;
using Z = Wavee.Themes.Zune.ZuneDetailRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's insights sheet (<see cref="ViewKeys.DetailInsights"/>) and its entry points (<see cref="ViewKeys.DetailInsightsToggle"/>).</summary>
internal static class ZuneInsights
{
    /// <summary>The registered template of <see cref="ViewKeys.DetailInsights"/>: what it covers wrapped in the sheet, or the docked column.
    /// At Compact the sheet covers the whole page, the window's width (the vertical arm measures no page width of its own).</summary>
    public static Element SheetTemplate(DetailInsightsProps p, in ViewContext ctx)
    {
        if (p.Docked) return Docked(p.Toggle, p.Facts, p.RouteKey);
        float paneW = ctx.Tier().Window == WidthTier.Compact
            ? Z.SheetCoverWidth(ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width)
            : Z.SheetWidthFor(p.PageWidth);
        return Sheet(p.Page ?? new BoxEl(), p.Toggle, p.Facts, paneW, p.RouteKey, p.OnClosed);
    }

    /// <summary>The registered template of <see cref="ViewKeys.DetailInsightsToggle"/>: the bar's glyph (the tier's target edge, a re-pushed prop: a
    /// crossing resizes the one button and keeps its node, its focus and its hover) or the band's word.</summary>
    public static Element ToggleTemplate(DetailInsightsToggleProps p, in ViewContext ctx)
    {
        var toggle = p.Toggle;
        if (p.Form == DetailInsightsToggleForm.BandWord) return Embed.Comp(() => new BandWord(toggle));
        float edge = MathF.Max(Z.ToggleEdge, ZuneDetailTiers.For(ctx.Tier().Window).Target);
        return Controls.Named(Detail.Skeleton.Derivable(Embed.Comp(new BarProps(toggle, edge), static () => new BarButton()) with { Key = "insights:toggle:bar" }),
            Loc.Get(Strings.Detail.LikedFacts.SheetOpen)) with { Key = "insights:toggle-tip" };
    }

    static readonly TemplateParts s_parts = BuildParts();

    static TemplateParts BuildParts()
    {
        var parts = new TemplateParts();
        parts[SplitView.PartPane] = static b => b with { Fill = Prop.Of(static () => R.Layer.FloatingPane), ClipToBounds = true };
        return parts;
    }

    /// <summary>The sheet over <paramref name="page"/>: ALWAYS composed for a track page (open or not), so the tree under it never changes.</summary>
    static Element Sheet(Element page, InsightsToggle toggle, Func<float, Element>? facts, float paneW, string routeKey, Action? onClosed)
    {
        return SplitView.Create(
            pane: Pane(toggle, facts, routeKey),
            content: new BoxEl { Key = "insights:content", ZStack = true, Grow = 1f, Children = [page] },
            paneWidth: paneW,
            isPaneOpen: toggle.Open,
            displayMode: SplitViewDisplayMode.Overlay,
            panePlacement: SplitViewPanePlacement.Right,
            onPaneClosed: onClosed,
            parts: s_parts);
    }

    /// <summary>The docked column (a page with no rail at a tier that docks the insights): its width follows the toggle and eases.</summary>
    static Element Docked(InsightsToggle toggle, Func<float, Element>? facts, string routeKey)
    {
        var open = toggle.Open;
        float w = Z.DockedInsightsW;
        return new BoxEl
        {
            Key = "insights:dock", Direction = 1, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
            Width = facts is null ? 0f : Prop.Of(() => open.Value ? w : 0f), Animate = Z.ColumnReflow,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Width = w, Shrink = 0f, MinHeight = 0f, Fill = Prop.Of(static () => R.Layer.FloatingPane),
                    Children = [Pane(toggle, facts, routeKey)],
                },
            ],
        };
    }

    /// <summary>The insights as a section of the docked aside: the heading and the close over the bento, shown while the toggle is open.</summary>
    public static Element AsideSection(InsightsToggle toggle, Func<float, Element> facts)
    {
        var open = toggle.Open;
        return new BoxEl
        {
            Key = "aside:insights", Direction = 1, MinWidth = 0f,
            Children =
            [
                Flow.Show(() => open.Value, new BoxEl
                {
                    Direction = 1, Gap = Z.SheetGap, MinWidth = 0f, Enter = Z.ArmFade, Exit = Z.ArmFade, Transition = Z.ArmFadeMotion,
                    Children = [Header(toggle), facts(0f)],
                }),
            ],
        };
    }

    /// <summary>The pane: the header row over the scrolling bento, which stretches to the pane's content width. The side padding sits INSIDE
    /// the scroller, so a target on the bento's edge (the first face, the first bar) keeps its hit slop instead of losing it to the clip.</summary>
    static Element Pane(InsightsToggle toggle, Func<float, Element>? facts, string routeKey)
    {
        return new BoxEl
        {
            Key = "insights:column", Direction = 1, Grow = 1f, MinHeight = 0f, Gap = Z.SheetGap,
            Padding = new Edges4(0f, Z.SheetPad, 0f, 0f),
            Children =
            [
                new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(Z.SheetPad, 0f, Z.SheetPad, 0f), Children = [Header(toggle)] },
                ScrollView(new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Padding = new Edges4(Z.SheetPad, 0f, Z.SheetPad, Z.SheetPad),
                    Children = [facts is null ? new BoxEl() : facts(0f)],
                }) with { Key = "insights:body:" + routeKey, Grow = 1f, Shrink = 1f, MinHeight = 0f },
            ],
        };
    }

    static Element Header(InsightsToggle toggle) => new BoxEl
    {
        Key = "insights:header", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinWidth = 0f,
        Children =
        [
            K.Heading(Loc.Get(Strings.Detail.LikedFacts.SheetTitle)) with { Grow = 1f },
            Controls.Named(new BoxEl
            {
                Key = "insights:close", Width = Z.SheetClose, Height = Z.SheetClose, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = toggle.Dismiss,
                Children = [Icon(Icons.ChromeClose, Z.SheetCloseGlyph, Tok.TextSecondary)],
            }, Loc.Get(Strings.Detail.LikedFacts.SheetClose)),
        ],
    };

    /// <summary>The bar-chart mark (three square bars on a baseline, a 24-unit stroked path in the theme's thin line), parsed once.</summary>
    static readonly PathData s_mark = PathDataParser.Parse("M4 20h16M7.5 20v-7M12 20V6M16.5 20v-4", PathContentEpoch.Mint(), FillRule.NonZero);

    /// <summary>The list bar's toggle: the bar-chart mark, the accent ink while open (a toggle button: pressed IS its meaning).</summary>
    sealed record BarProps(InsightsToggle Toggle, float Edge);

    sealed class BarButton : Component
    {
        public override Element Render()
        {
            var (toggle, edge) = UseProps<BarProps>();
            bool open = toggle.Open.Value;
            // The tier's edge (36, 40 at Compact and Huge 4K) eases on the button's own box; it is not the component's root, whose size would land at once.
            return new BoxEl
            {
                Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children = [Button(toggle, edge, open)],
            };
        }

        static BoxEl Button(InsightsToggle toggle, float edge, bool open) => new()
        {
            Key = "insights:toggle", Width = edge, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Layout = Design.Reduced ? null : Z.ToggleResize,
            HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
            Role = AutomationRole.ToggleButton, Focusable = true, Cursor = CursorId.Hand,
            OnClick = toggle.Toggle, OnRealized = toggle.CaptureButton,
            Children =
            [
                new PathEl
                {
                    Geometry = s_mark, Width = Z.ToggleGlyph, Height = Z.ToggleGlyph, ViewBoxW = F.GlyphViewBox, ViewBoxH = F.GlyphViewBox, Shrink = 0f,
                    StrokeColor = open ? Tok.AccentTextPrimary : Tok.TextSecondary,
                    Stroke = new StrokeStyle(F.ToggleStroke, LineCap.Square, LineJoin.Miter),
                },
            ],
        };
    }

    /// <summary>The stuck band's toggle: the lowercase word, the accent ink while open.</summary>
    sealed class BandWord(InsightsToggle toggle) : Component
    {
        public override Element Render()
        {
            bool open = toggle.Open.Value;
            return new BoxEl
            {
                Key = "insights:word", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = Detail.BandLayout.Height,
                Padding = new Edges4(Detail.BandLayout.ActionPadX, 0f, Detail.BandLayout.ActionPadX, 0f),
                Role = AutomationRole.ToggleButton, Focusable = true, Cursor = CursorId.Hand,
                OnClick = toggle.ToggleFromBand, OnRealized = toggle.CaptureBandButton,
                Children =
                [
                    R.Type.DenseMeta(Loc.Get(Strings.Detail.LikedFacts.SheetOpen)) with
                    {
                        Color = open ? Tok.AccentTextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1,
                    },
                ],
            };
        }
    }
}
