// ── Themes/WinUi/Structure/Views/Frame/WinUiFrame.cs — WinUI's `Shell.Frame` (themes plan 6.2; P6 W2a) ──────────────────────────────────
//
// Boards Shell-Top, W-Shell-Compact / Small / Huge / Short, W-Narrow and W-Tiers. The core frame root keeps every hook, effect and state
// machine and hands this template one props record (`ShellFrameProps`); the template lays the window out and binds, it decides nothing.
//
//   THE FRAME, TOP DOWN (one column the window's height, over the window material):
//
//     title bar slot (88 = the 40 tab strip over the 48 toolbar; 48 with tabs off)          `shell.titlebar`       Shell.TitleBar
//     body = ZStack[ column( content region · play bar ) · panel layer ]
//            content region = ZStack[ row( nav column · content card · panel gap · panel reservation · docked panes' reservations )
//                                     · the nav seam · the panel seam · the narrow drawer ]
//            content card   = the content layer (#282828 dark / #f9f9f9 light), r8 on its top left with a 1-px left+top hairline from
//                             Small up, square and flush at Compact (LeftMinimal: no rail)                   `shell.content.card`
//            play bar       = Player.Bar (Mini 64 at Compact, 72 elsewhere)                                   `shell.playerbar`
//
//   Under the column: the material (Shell.Material). Over it, as in every theme: the fullscreen stage, the two video layers, the overlays, the
//   file-drop cue, the command palette, the setup wizard's cover scrim, the drag chip and the frame-rate overlay.
//
//   THE NAV COLUMN (NavigationView's pane: the 280 pane, the 48 rail, nothing at Compact): its width is the core's
//   (`SidebarColumnWidth`: the mode the policy, the tier and the user pick) and EASES over 167 ms (`WinUiFrameRules.NavColumnAnim`), the
//   content card FLIPping with it on the same dynamics, so a mode change (the option, the tier, the hamburger) never snaps the page.
//
//   THE TITLE BAR SLOT eases its height 88 <-> 48 when the `tabs` option flips (`ShellCapabilities.ShowsTabStrip`), anchored at its foot: the
//   toolbar row stays glued to the page and the page follows the slot frame by frame (`WinUiFrameRules.TitleSlotAnim`).
//
//   THE PANELS (W-Shell-Small, W-Shell-Huge; W4a owns the panel's picture). The panel layer lies over the content region AND the play bar
//   (the Compact sheet covers the mini bar) and places the primary host by bound spacers from its placement: DOCKED flush right beside the
//   card above the play bar, its width the committed width and the row reserving it; FLOATING 340 wide over the card's right edge (acrylic,
//   the page keeps its width); a SHEET the body's whole box. The second docked pane (Classic's arrangement) or the docked panes of the core's
//   arrangement (`DockedPanes`, Huge's two panes) each get their own host and reservation.
//
//   THE CHROME COLLAPSES under full-screen video and the fullscreen stage (bound Visible, still mounted), as every theme's does.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's window frame.</summary>
internal static class WinUiFrame
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellFrame"/>.</summary>
    public static Element Template(ShellFrameProps props, in ViewContext ctx)
    {
        var tiers = ctx.TierReader();
        var facts = TierContext.DrawnIn(ctx.Hooks.UseContext(ThemeFactsContext.Signal));
        bool reduced = ctx.Reduced;
        var dock = new DockWidth(props);
        var column = new BoxEl
        {
            // Window-tall, bound: the body yields instead of shoving the play bar off the bottom.
            Direction = 1, Grow = 1f, Height = Prop.Of(() => props.Viewport.Value.Height),
            OnKeyDown = props.OnKeyDown,
            Children =
            [
                .. props.Chords,
                TitleBarSlot(props, in ctx, tiers, facts, reduced),
                new BoxEl
                {
                    // The body: the content region over the play bar, the panel layer over both. The ONE region that yields.
                    ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                            Children =
                            [
                                ContentRegion(props, in ctx, tiers, dock),
                                new BoxEl
                                {
                                    // The bar's slot is the box the page above yields to. It hugs the bar: the bar's own root eases its 64 <-> 72 change at the
                                    // 641 crossing through real layout, and the view's anchor follows that live height, so the page above yields frame by frame.
                                    Key = WinUiFrameLayout.PlayerBarKey, Direction = 1, Shrink = 0f, Visible = Prop.Of(props.ChromeMounted),
                                    Children = [ctx.View(ViewKeys.PlayerBar, props.PlayerBar)],
                                },
                            ],
                        },
                        PanelLayer(props, in ctx, tiers, reduced, dock),
                    ],
                },
            ],
        };

        var ground = new BoxEl
        {
            Grow = 1f, ZStack = true, Fill = ColorF.Transparent,
            // The shell-wide "drop a file to play it" target (a row's own file target still wins: the deepest accepting target takes it).
            DropTarget = props.FileDrop,
            Children = [ctx.View(ViewKeys.ShellMaterialLayer, props.Material), column],
        };

        return ZStack(
            ground,
            // The immersive stage covers the whole frame and sits below the banner, the drop cue and the toasts.
            Flow.Show(props.ImmersiveOpen, new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true,
                Enter = Stage.EnterTerminal, Exit = Stage.ExitTerminal,
                Children = [ctx.View(ViewKeys.StageRoot, props.StageRoot)],
            }),
            // The video layers come BEFORE the in-stack overlays: the video is a hole erased through what is drawn under it.
            props.VideoPipLayer(),
            props.VideoFullscreenLayer(),
            props.Overlays(),
            ctx.View(ViewKeys.ShellFileDrop, props.FileDropCue),
            ctx.View(ViewKeys.ShellCommandPalette, props.CommandPalette),
            ctx.View(ViewKeys.ShellCoverScrim, props.CoverScrim),
            ctx.View(ViewKeys.ShellDragChip, props.DragChip),
            ctx.View(ViewKeys.ShellFps, props.Fps)) with { Grow = 1f };
    }

    /// <summary>The title bar's slot (`shell.titlebar`): 88 with the strip, 48 without, easing between them anchored at its foot (the engine bar
    /// inside sets its own height from the same facts, so the strip is uncovered or wiped above a toolbar that never moves against the page).</summary>
    private static Element TitleBarSlot(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, IReadSignal<ThemeFacts> facts, bool reduced) => new BoxEl
    {
        Key = WinUiFrameLayout.TitleBarKey, Direction = 1, Justify = FlexJustify.End, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
        Height = Prop.Of(() => WinUiTiers.FrameOf(tiers()).TitleBarH(facts.Value.Capabilities.ShowsTabStrip)),
        Animate = reduced ? null : WinUiFrameRules.TitleSlotAnim,
        Visible = Prop.Of(props.ChromeMounted),
        Children = [ctx.View(ViewKeys.ShellTitleBar, props.TitleBar)],
    };

    private static Element ContentRegion(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, DockWidth dock)
    {
        var panes = props.DockedPanes ?? s_noPanes;
        LayoutTransition? reserve = ctx.Reduced ? null : WinUiFrameRules.PanelReserveAnim;
        var row = new List<Element>(6 + panes.Count)
        {
            new BoxEl
            {
                // NavigationView's pane column: the core computes the width (280, the 48 rail, 0 at Compact); it eases over 167 ms.
                Key = WinUiFrameRules.NavColumnKey, Direction = 1, Shrink = 0f, ClipToBounds = true,
                OnRealized = props.OnSidebarColumnRealized,
                Width = Prop.Of(props.SidebarColumnWidth),
                Animate = ctx.Reduced ? null : WinUiFrameRules.NavColumnAnim,
                Children =
                [
                    // The layout firewall for the whole pane, inside the animating column. Minimal presents the column at 0 and the drawer
                    // owns the pane, so the docked pane unmounts (no rows reachable by Tab inside a clipped column).
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, IsolateLayout = true, ClipToBounds = true,
                        Opacity = Prop.Of(() => props.SidebarFade.Value),
                        Children = [Flow.Show(props.SidebarPaneDocked, ctx.View(ViewKeys.ShellNavPane, props.NavPane))],
                    },
                ],
            },
            ContentCard(props, in ctx, tiers),
            // The panel's breathing room exists only while the panel is docked inline. It and the reservation ease on the panel's slide, through
            // real layout, so the page's edge rides the sliding pane's edge every frame (open, close, a drag that closes it); a seam drag
            // (transitions suppressed) moves them with the pointer.
            new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(props.RailGapWidth), Animate = reserve },
            Reservation(dock.Reserved, reserve),
        };
        // Classic's one second pane (`SecondPanel`) is the first of the core's docked panes: a frame that draws the panes draws it once (P6 W4a:
        // Huge's two panes were reserved and mounted twice).
        if (props.DockedPanes is null)
        {
            row.Add(new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(props.SecondGapWidth ?? s_zero), Animate = reserve });
            row.Add(Reservation(props.SecondReservedWidth ?? s_zero, reserve));
        }
        foreach (var pane in panes) row.Add(Reservation(pane.ReservedWidth, reserve));
        return ZStack(
            // The row never moves on a toggle: the content card FLIPs relative to it.
            new BoxEl { MorphId = WinUiFrameRules.ContentRowMorphId, Direction = 0, Grow = 1f, ClipToBounds = true, Children = row.ToArray() },
            // The nav seam (a non-resizable pane presents it 0 wide) and the panel seam (the docked pane resizes 280-480).
            new BoxEl
            {
                Direction = 1, ClipToBounds = true,
                Width = Prop.Of(props.SidebarSeamWidth),
                Transform = Prop.Of(() => Affine2D.Translation(props.SidebarPresentedWidth.Value, 0f)),
                Children = [props.SidebarSeam()],
            },
            new BoxEl
            {
                Direction = 1, ClipToBounds = true,
                Width = Prop.Of(props.RailSeamWidth),
                // A closed (or undocked) panel presents the seam 0 wide: out of the tree then, so its 16-wide grip is no hit target nobody can see.
                Visible = Prop.Of(() => props.RailSeamWidth() > 0f),
                Transform = Prop.Of(() => Affine2D.Translation(props.RailSeamX(), 0f)),
                Children = [props.RailSeam()],
            },
            props.NarrowDrawer()) with
        {
            // Collapses (bound Visible, still mounted) under the fullscreen stage and full-screen video. No MorphId on this node.
            Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
            Visible = Prop.Of(props.ChromeMounted),
            OnRealized = props.OnContentRegionRealized,
            OnBoundsChanged = props.OnContentRegionBounds,
        };
    }

    /// <summary>The content layer: the card fill, the clipped page host (FLIPping with the pane) and the hairline. The tier's shape is BOUND,
    /// so a crossing reshapes it without a re-render.</summary>
    private static Element ContentCard(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers)
    {
        CornerRadius4 Corners() => WinUiFrameRules.CardCorners(WinUiTiers.FrameOf(tiers()).SquareCard);
        return new BoxEl
        {
            Key = WinUiFrameLayout.ContentKey,
            Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
            Children =
            [
                new BoxEl { Grow = 1f, Fill = Prop.Of(static () => R.Layer.FileArea), Corners = Prop.Of(Corners) },
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                    Fill = ColorF.Transparent, Corners = Prop.Of(Corners), ClipToBounds = true, IsolateLayout = true,
                    Animate = ctx.Reduced ? null : WinUiFrameRules.ContentCardAnim, RelativeTo = WinUiFrameRules.ContentRowMorphId,
                    Children = [props.ContentHost()],
                },
                Stroke(tiers, Corners),
            ],
        };
    }

    /// <summary>The card's hairline: a 1-px stroke on the left and top edges with the r8 arc (the right and bottom overhang the clip); at
    /// Compact (square, flush) only the top line, so no stroke runs down the window's edge.</summary>
    private static Element Stroke(Func<TierFacts> tiers, Func<CornerRadius4> corners) => new BoxEl
    {
        ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Key = "shell.content.stroke", Margin = WinUiFrameRules.StrokeOverhang, BorderWidth = WinUiFrameRules.StrokeWidth,
                BorderColor = Prop.Of(() => WinUiTiers.FrameOf(tiers()).SquareCard ? ColorF.Transparent : Tok.StrokeCardDefault),
                Corners = Prop.Of(corners),
            },
            new BoxEl
            {
                Key = "shell.content.stroke.top", Height = WinUiFrameRules.StrokeWidth,
                Fill = Prop.Of(() => WinUiTiers.FrameOf(tiers()).SquareCard ? Tok.StrokeCardDefault : ColorF.Transparent),
            },
        ],
    };

    private static readonly Func<float> s_zero = static () => 0f;
    private static readonly IReadOnlyList<DockedPaneProps> s_noPanes = [];

    /// <summary>A docked panel's row reservation (0 while closed or floating), its underlay the content layer so a slide never shows a hole.
    /// Its width eases through real layout on the panel's slide (<paramref name="motion"/>), so the content card beside it reflows with it.</summary>
    private static Element Reservation(Func<float> width, LayoutTransition? motion) => new BoxEl
    {
        Shrink = 0f,
        Width = Prop.Of(width), Animate = motion,
        Children =
        [
            new BoxEl
            {
                Grow = 1f, HitTestPassThrough = true, ClipToBounds = true, ZStack = true,
                Children = [new BoxEl { Margin = WinUiFrameRules.StrokeOverhang, Fill = Prop.Of(static () => R.Layer.FileArea), Corners = WinUiFrameRules.PaneBandCorners }],
            },
        ],
    };

    // ── the panel layer ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Func<PanelPlacement> s_dock = static () => PanelPlacement.Dock;

    /// <summary>The panels over the content region and the play bar: the sheet's scrim, the primary host, the second pane, the docked panes.</summary>
    private static Element PanelLayer(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, bool reduced, DockWidth dock)
    {
        Func<PanelPlacement> placement = props.PanelPlacement ?? s_dock;
        var panes = props.DockedPanes ?? s_noPanes;
        var kids = new List<Element>(3 + panes.Count)
        {
            Embed.Comp(props.Panel, static () => new WinUiPanelScrim()),
            PrimaryHost(props, in ctx, tiers, placement, panes, reduced, dock),
        };
        if (props.DockedPanes is null && props.SecondPanel is { } second) kids.Add(SecondHost(props, second, in ctx, tiers));
        foreach (var pane in panes) kids.Add(PaneHost(props, pane, panes, in ctx, tiers, reduced, dock));
        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestPassThrough = true,
            Visible = Prop.Of(props.ChromeMounted),
            Children = kids.ToArray(),
        };
    }

    /// <summary>What docks right of <paramref name="pane"/> (null: the primary panel): Classic's second pane, and the panes of the core's
    /// arrangement on that side (nearest the primary first).</summary>
    private static float RightOf(ShellFrameProps props, IReadOnlyList<DockedPaneProps> panes, DockedPaneProps? pane, DockWidth dock)
    {
        bool before = pane is not null && pane.Side() == PaneSide.Before;
        float right = pane is null
            ? (props.DockedPanes is null ? (props.SecondGapWidth?.Invoke() ?? 0f) + (props.SecondReservedWidth?.Invoke() ?? 0f) : 0f)
            : before ? props.RailGapWidth() + dock.Reserved() : 0f;
        foreach (var other in panes)
        {
            if (ReferenceEquals(other, pane)) continue;
            bool after = other.Side() == PaneSide.After;
            bool counts = pane is null ? after
                : before ? after || other.Index < pane.Index
                : after && other.Index > pane.Index;
            if (counts) right += other.ReservedWidth();
        }
        return right;
    }

    /// <summary>The primary panel's host, placed by bound spacers: docked flush right beside the card above the play bar (its committed width);
    /// floating 340 wide over the card's right edge from the toolbar's foot to the play bar (W-Shell-Small); a sheet the body's whole box
    /// (over the mini bar). It FLIPs when a crossing docks, floats or sheets it. The panel's own template runs the open/close slide.</summary>
    private static Element PrimaryHost(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, Func<PanelPlacement> placement,
        IReadOnlyList<DockedPaneProps> panes, bool reduced, DockWidth dock)
    {
        float Top() => placement() == PanelPlacement.Float ? WinUiFrameRules.PanelFloatInset : 0f;
        float Bottom() => placement() switch
        {
            PanelPlacement.Sheet => 0f,
            PanelPlacement.Float => WinUiTiers.FrameOf(tiers()).BarH + WinUiFrameRules.PanelFloatInset,
            _ => WinUiTiers.FrameOf(tiers()).BarH,
        };
        float Right() => placement() switch
        {
            PanelPlacement.Float => WinUiFrameRules.PanelFloatInset,
            PanelPlacement.Dock => RightOf(props, panes, null, dock),
            _ => 0f,
        };
        float WidthNow() => placement() switch
        {
            PanelPlacement.Sheet => props.Viewport.Value.Width,
            PanelPlacement.Float => WinUiFrameRules.PanelFloatW,
            _ => dock.Host(),
        };
        CornerRadius4 CornersNow() => placement() switch
        {
            PanelPlacement.Sheet => new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
            PanelPlacement.Float => new CornerRadius4(Radii.Card, 0f, 0f, Radii.Card),
            _ => WinUiFrameRules.PaneBandCorners,
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, HitTestPassThrough = true,
            Children =
            [
                new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(Top) },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.End, HitTestPassThrough = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = WinUiFrameRules.PanelHostKey,
                            Direction = 1, Shrink = 0f, ClipToBounds = true, ZStack = true, HitTestPassThrough = true,
                            Width = Prop.Of(WidthNow), Corners = Prop.Of(CornersNow),
                            Animate = reduced ? null : WinUiFrameRules.PanelHostAnim,
                            Children =
                            [
                                // The floating panel's backing (the shell ground) — paint-only, never the deepest hit.
                                new BoxEl
                                {
                                    Grow = 1f, HitTestPassThrough = true, Corners = Prop.Of(CornersNow),
                                    Fill = Prop.Of(() => props.RailFloats() ? R.Layer.FloatingChrome : ColorF.Transparent),
                                },
                                new BoxEl
                                {
                                    Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, HitTestPassThrough = true,
                                    Corners = Prop.Of(CornersNow), IsolateLayout = true,
                                    Opacity = Prop.Of(() => props.RailFade.Value),
                                    Children = [ctx.View(ViewKeys.ShellPanel, props.Panel)],
                                },
                            ],
                        },
                        new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(Right) },
                    ],
                },
                new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(Bottom) },
            ],
        };
    }

    /// <summary>The second docked pane's host (the queue beside the panel at Huge): docked at the right edge above the play bar.</summary>
    private static Element SecondHost(ShellFrameProps props, PanelProps second, in ViewContext ctx, Func<TierFacts> tiers) => new BoxEl
    {
        Direction = 1, Grow = 1f, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.End, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Key = WinUiFrameRules.SecondHostKey,
                        Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true,
                        Corners = WinUiFrameRules.PaneBandCorners, IsolateLayout = true,
                        Width = Prop.Of(props.SecondPanelWidth ?? s_zero),
                        Children = [ctx.View(ViewKeys.ShellPanel, second)],
                    },
                ],
            },
            new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(() => WinUiTiers.FrameOf(tiers()).BarH) },
        ],
    };

    /// <summary>A pane of the core's docked arrangement (Huge's second pane): flush right above the play bar, as wide as the pane, with a spacer
    /// after it as wide as what docks right of it. Keyed by its index, so a crossing that adds or drops another pane keeps its node.</summary>
    private static Element PaneHost(ShellFrameProps props, DockedPaneProps pane, IReadOnlyList<DockedPaneProps> panes, in ViewContext ctx,
        Func<TierFacts> tiers, bool reduced, DockWidth dock) => new BoxEl
    {
        Direction = 1, Grow = 1f, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.End, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Key = WinUiFrameRules.PaneHostKey(pane.Index),
                        Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true, IsolateLayout = true,
                        Corners = WinUiFrameRules.PaneBandCorners,
                        Width = Prop.Of(pane.Width),
                        Animate = reduced ? null : WinUiFrameRules.PanelHostAnim,
                        Children = [ctx.View(ViewKeys.ShellPanel, pane.Panel)],
                    },
                    new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(() => RightOf(props, panes, pane, dock)) },
                ],
            },
            new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(() => WinUiTiers.FrameOf(tiers()).BarH) },
        ],
    };
}

/// <summary>The docked panel's width, one source for the page's reservation and the panel's host. LIVE: the seam drag's width while a drag is
/// in flight (the pane and the page follow the pointer every frame, as WinUI's resizable panes do), the committed width at rest. While the panel
/// is closed the host holds the width it closed at, so a pane that a drag closed slides out at the width it had, the reservation easing from the
/// same width to 0 beside it.</summary>
internal sealed class DockWidth(ShellFrameProps props)
{
    float _held;

    float Live() => (props.RailLiveWidth ?? props.RailWidth).Value;

    /// <summary>The row's reservation: the live width while the panel is open and docked, else 0 (tracked).</summary>
    public float Reserved() => props.RailReservedWidth() > 0f ? Live() : 0f;

    /// <summary>The docked host's width: the live width while open, the width it closed at while closed (tracked).</summary>
    public float Host()
    {
        float live = Live();
        if (props.RailReservedWidth() > 0f || props.RailFloats() || _held <= 0f) _held = live;
        return _held;
    }
}

/// <summary>The Compact bottom sheet's scrim: it fades in while a sheet is open and closes the panel on a tap (W-Shell-Compact 3).</summary>
internal sealed class WinUiPanelScrim : Component
{
    bool _mounted;

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool show = view.Open && view.Placement == PanelPlacement.Sheet;
        float ms = Design.Reduced ? 0f : WinUiFrameRules.SheetScrimFadeMs;
        float target = show ? 1f : 0f;
        UseTransition(AnimChannel.Opacity, _mounted ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(show));
        _mounted = true;
        return new BoxEl
        {
            Grow = 1f, Opacity = target, HitTestVisible = show, OnClick = show ? props.Close : null, TabStop = false,
            BlocksBackgroundScroll = show,
            Children = [new BoxEl { Grow = 1f, Fill = WinUiFrameRules.SheetScrim, HitTestVisible = false }],
        };
    }
}
