// ── Themes/Classic/Structure/Views/Frame/ClassicFrame.cs — Classic's `Shell.Frame` (themes plan 6.1, T4a) ──────────────────
//
// From `Shell.UI.cs`: the element tree the frame root built inline (the column, the layers, `ContentRegion`, the content
// region's stroke). The core frame root keeps every hook, effect and state machine and hands this template one props record;
// nothing here decides, it lays out and binds. Elements, keys, morph ids, transitions and bound channels are exactly what the
// shell built before the move, so Classic renders and animates as it always did.
//
//   THE FRAME, BOTTOM-UP.  Everything under `OverlayHost` is one ZStack:
//
//     tinted[ MaterialLayer · column ]  ·  Stage.Root  ·  Video.PipLayer  ·  Video.FullscreenLayer  ·  Overlays
//           ·  file-drop cue  ·  CommandPalette  ·  setup scrim  ·  drag preview  ·  fps overlay
//
//   The video layers sit BELOW the banner, toast and drop-cue layers: the video is a hole in the UI swapchain (a DestOut
//   erase over a DComp visual beneath it), so everything drawn BEFORE it is erased where it overlaps and everything drawn
//   AFTER it covers the picture. Those layers are pass-through, so the mini player still takes its drag.
//
//     column = 14 zero-size chord boxes · [chrome row] · body
//     body   = ZStack[ column(content region · [player dock]) · panel layer ]
//              the bracketed bands, the content region and the panel layer COLLAPSE (bound Visible, still mounted) under full-screen
//              video OR the fullscreen stage: out of layout, paint and hit-test, subtree inactive
//     content region = ZStack[ row(sidebar column · content card · rail gap · rail reservation · second gap · second reservation) ·
//                              sidebar seam · rail seam · narrow drawer ]
//     panel layer    = ZStack[ sheet scrim · primary panel host · second panel host ]: over the content region AND the player dock, so
//                      the Compact sheet can cover the mini bar (TC2); it provides the sheet's one drag (`ClassicSheetDrag`) to the scrim and
//                      the primary panel
//
// The surfaces that are not yet a key of their own (the overlays) arrive as element builders on the props (`ShellFrameProps`); T11 gives
// each its own key and replaces the builder with a `ctx.View` call here. The two video layers are builders for good: they are the core
// hosts of the stay-mounted presenter, the pop-out's owner, the host observer, the join fold and the fullscreen mode, and they mount the
// `Video.*` keys (T7c) themselves; the frame only places them. The title bar (T4b) is
// mounted as `Shell.TitleBar`, the sidebar pane (T5a) as `Shell.NavPane`, the player bar (T6a) as `Player.Bar`, the right panel
// (T6b) as `Shell.Panel` (its seam stays a builder: the splitter is behaviour the host wires and the template places) and the stage
// (T7a1) as `Stage.Root`; T4c did the same for
// the material, the file-drop cue, the palette, the cover scrim, the drag chip and the frame-rate overlay (each member of the props is now
// the child view's props record); the narrow drawer's core host (its props change with the band) is still a builder.
//
// PER WINDOW TIER (TC1, `ClassicTiers.Frame`): the content card is square and flush at Compact (no rail beside it: only its top
// hairline, boards C-Shell-Compact and C-Frame-Narrow) and the r8 card with its left and top hairline from Small up. The frame renders
// once, so the card reads the tier through BOUND channels (`ctx.TierReader()`): a crossing recolours and reshapes it without a
// re-render. The four top-level regions carry keys (`shell.titlebar`, `shell.nav.column`, `shell.content.card`, `shell.playerbar`) so
// the tier tests find them.
//
// THE PANELS PER WINDOW TIER (TC2, `ClassicPanelTiers`): the primary panel host is placed by bound spacers from the tier's record (a
// sheet 8 under the title bar down to the window's bottom edge at Compact, floating 340 wide 8 in from the right, the title bar and the
// player bar at Small, docked beside the page at Standard and Huge) and carries a layout transition, so a crossing that docks, floats or
// sheets it FLIPs the host on the crossing frame while the content card FLIPs with the reservation. At Huge the second pane (the queue) sits
// right of it with its own reservation; the primary host moves left by the second pane's width when it opens, animated the same way.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's window frame.</summary>
internal static class ClassicFrame
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellFrame"/>.</summary>
    public static Element Template(ShellFrameProps props, in ViewContext ctx)
    {
        var tiers = ctx.TierReader();
        var column = new BoxEl
        {
            // Window-tall, bound: the content region yields instead of shoving the docked bar off the bottom.
            Direction = 1, Grow = 1f, Height = Prop.Of(() => props.Viewport.Value.Height),
            OnKeyDown = props.OnKeyDown,
            Children =
            [
                .. props.Chords,
                // FULL-SCREEN VIDEO COLLAPSES THE CHROME, it does not unmount it: every layer left under the video
                // costs GPU, and the docked bar is what stacked a second transport under the video's own, but a
                // structural Flow.Show rebuilt the tab row and the whole player bar (seek rail, marquee, art, device
                // buttons) on every exit. A bound Visible takes each out of layout, paint, hit-test and focus and
                // pauses its subtree (UseInterval, UseIsActive), and the edge never re-renders the frame.
                ctx.View(ViewKeys.ShellTitleBar, props.TitleBar) with { Key = ClassicFrameLayout.TitleBarKey, Visible = Prop.Of(props.ChromeMounted) },
                new BoxEl
                {
                    // The body: the content region and the dock in a column, the panel layer over both (TC2). It is the ONE region that
                    // yields when the window is shorter than the column.
                    ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                            Children =
                            [
                                ContentRegion(props, in ctx, tiers),
                                ctx.View(ViewKeys.PlayerBar, props.PlayerBar) with { Key = ClassicFrameLayout.PlayerBarKey, Visible = Prop.Of(props.ChromeMounted) },
                            ],
                        },
                        PanelLayer(props, in ctx, tiers),
                    ],
                },
            ],
        };

        var tinted = new BoxEl
        {
            Grow = 1f, ZStack = true, Fill = ColorF.Transparent,
            // The shell-wide "drop a file to play it" target. The engine hands a drop to the DEEPEST accepting target,
            // so a row's own file target still wins; only drops on the rest of the window land here.
            DropTarget = props.FileDrop,
            Children = [ctx.View(ViewKeys.ShellMaterialLayer, props.Material), column],
        };

        // The cover leaves under every page ask whether the navigation pane is docked beside the content card (the Rich tone plane's light cap,
        // tuned item F2), so the frame provides the core's predicate to its whole subtree. A stable reference: providing it never re-renders a page.
        return Ctx.Provide(ClassicMaterial.PaneDockedSlot, props.SidebarPaneDocked, ZStack(
            tinted,
            // The immersive stage covers the content, the sidebar and the rail, and sits below the banner, the drop cue
            // and the toast lane. A predicate, not a render read; Direction 1 so an oversized measure is cross-clamped.
            Flow.Show(props.ImmersiveOpen, new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true,
                Enter = Stage.EnterTerminal, Exit = Stage.ExitTerminal,
                Children = [ctx.View(ViewKeys.StageRoot, props.StageRoot)],
            }),
            // The video layers come BEFORE the in-stack overlays: the hole erases whatever is drawn under it, so the
            // runtime banner, the toasts and the drop pill must be painted after it to stay over a floating or
            // fullscreen picture (every one of them is pass-through, so the mini player's drag still lands).
            props.VideoPipLayer(),
            props.VideoFullscreenLayer(),
            props.Overlays(),
            ctx.View(ViewKeys.ShellFileDrop, props.FileDropCue),
            ctx.View(ViewKeys.ShellCommandPalette, props.CommandPalette),
            ctx.View(ViewKeys.ShellCoverScrim, props.CoverScrim),
            ctx.View(ViewKeys.ShellDragChip, props.DragChip),
            ctx.View(ViewKeys.ShellFps, props.Fps)) with { Grow = 1f });
    }

    private static Element ContentRegion(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers)
    {
        LayoutTransition? reserve = ctx.Reduced ? null : ClassicFrameRules.PanelReserveAnim;
        return ZStack(
        new BoxEl
        {
            // The row never moves on a toggle: the content card FLIPs relative to THIS frame.
            MorphId = ClassicFrameRules.ContentRowMorphId, Direction = 0, Grow = 1f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    // THE W12 TRAP lives in FrameRules.SidebarPaneWidth (the core computes the width): a drag peek presents the
                    // pane expanded, so this clipped column widens with it.
                    Key = ClassicFrameLayout.NavColumnKey, Direction = 1, Shrink = 0f, ClipToBounds = true,
                    OnRealized = props.OnSidebarColumnRealized,
                    Width = Prop.Of(props.SidebarColumnWidth),
                    Animate = ClassicFrameRules.SidebarPaneAnim,
                    Children =
                    [
                        // The layout firewall for the whole sidebar, INSIDE the animating pane. Minimal presents the
                        // column at 0 DIP and the drawer owns the sidebar, so the docked pane unmounts: a mounted one
                        // would keep planning the full list and leave its rows reachable by Tab inside the clipped column.
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, IsolateLayout = true, ClipToBounds = true,
                            Opacity = Prop.Of(() => props.SidebarFade.Value),
                            Children = [Flow.Show(props.SidebarPaneDocked, ctx.View(ViewKeys.ShellNavPane, props.NavPane))],
                        },
                    ],
                },
                new BoxEl
                {
                    // The stock Win11 content region: flush, one corner, a left+top stroke, no shadow, no gutter. Square at Compact.
                    Key = ClassicFrameLayout.ContentCardKey,
                    Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
                    Children =
                    [
                        new BoxEl { Grow = 1f, Fill = Prop.Of(static () => R.Layer.FileArea), Corners = Prop.Of(() => ClassicTiers.FrameOf(tiers()).CardCorners) },
                        new BoxEl
                        {
                            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                            Fill = ColorF.Transparent, Corners = Prop.Of(() => ClassicTiers.FrameOf(tiers()).CardCorners), ClipToBounds = true,
                            IsolateLayout = true, Animate = ClassicFrameRules.ContentCardAnim, RelativeTo = ClassicFrameRules.ContentRowMorphId,
                            Children = [props.ContentHost()],
                        },
                        ContentRegionStroke(tiers),
                    ],
                },
                // The rail's 8-DIP breathing room exists only while the rail is inline. It and the reservation ease on the panel's slide,
                // through real layout, so the card's right edge rides the sliding panel's left edge every frame (open and close).
                new BoxEl
                {
                    Shrink = 0f, HitTestVisible = false,
                    Width = Prop.Of(props.RailGapWidth), Animate = reserve,
                },
                // The rail RESERVATION (0 while closed or floating): its underlay is the docked band's ONE coat.
                Reservation(props.RailReservedWidth, reserve),
                // The second docked pane's gap and reservation (Huge, D3): 0 unless the queue is open beside the panel.
                new BoxEl
                {
                    Shrink = 0f, HitTestVisible = false,
                    Width = Prop.Of(props.SecondGapWidth ?? s_zero), Animate = reserve,
                },
                Reservation(props.SecondReservedWidth ?? s_zero, reserve),
            ],
        },
        // The sidebar seam: a strip translated to the pane edge, entirely on the content side of it.
        new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Width = Prop.Of(props.SidebarSeamWidth),
            Transform = Prop.Of(() => Affine2D.Translation(props.SidebarPresentedWidth.Value, 0f)),
            Children = [props.SidebarSeam()],
        },
        // The rail seam: exists only while the rail is open and docked. The panel layer paints over the content region, so a narrowing drag
        // that carries the guide into the panel's rect draws it under the panel's content; at rest the strip lies wholly left of the panel.
        new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Width = Prop.Of(props.RailSeamWidth),
            Transform = Prop.Of(() => Affine2D.Translation(props.RailSeamX(), 0f)),
            Children = [props.RailSeam()],
        },
        props.NarrowDrawer()) with
    {
        // The region that yields (inside the body) when the window is shorter than the column; clipped so a settling page never
        // paints into the dock slot. The drag spotlight scrim is scoped to it (chrome and dock stay lit).
        // The bound Visible COLLAPSES the whole shell body while the fullscreen stage OR full-screen video is up: out of
        // layout, paint and hit-test, and its subtree goes inactive (UseInterval pauses, UseActivation fires its edges as on a KeepAlive
        // tab switch — every consumer reviewed benign, V-U37). It sits on THIS node, which carries no MorphId: the
        // content row's MorphId stays on an inner node with an unbound Visible (the BindContract rule guards the
        // tagged node itself).
        Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
        Visible = Prop.Of(props.ChromeMounted),
        OnRealized = props.OnContentRegionRealized,
        OnBoundsChanged = props.OnContentRegionBounds,
    };
    }

    private static readonly Func<float> s_zero = static () => 0f;

    /// <summary>A docked panel's row reservation: <paramref name="width"/> wide, its underlay the docked band's ONE coat. Its width eases through
    /// real layout on the panel's slide (<paramref name="motion"/>), so the content card beside it reflows with the panel.</summary>
    private static Element Reservation(Func<float> width, LayoutTransition? motion) => new BoxEl
    {
        Shrink = 0f,
        Width = Prop.Of(width), Animate = motion,
        Children =
        [
            new BoxEl
            {
                Grow = 1f, HitTestPassThrough = true, ClipToBounds = true, ZStack = true,
                Children = [new BoxEl { Margin = ClassicFrameRules.StrokeOverhang, Fill = Prop.Of(static () => R.Layer.FileArea), Corners = ClassicFrameRules.RailBandCorners }],
            },
        ],
    };

    // ── the panel layer (TC2) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The panels over the content region and the dock: the Compact sheet's scrim, the primary panel host and the second pane.</summary>
    private static Element PanelLayer(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers)
    {
        Func<PanelPlacement> placement = props.PanelPlacement ?? s_dock;
        // The sheet's drag moves the primary HOST (the arm under it runs the open/close slide): its clip moves with it (the arm's
        // slide happens inside that clip), so the sheet can rise the 8 DIP to the title bar's edge, its rubber band, before the body's clip.
        var drag = new ClassicSheetDrag { Reach = ClassicPanelTiers.Gap };
        var kids = new List<Element>(3)
        {
            Embed.Comp(props.Panel, static () => new ClassicPanelScrim()),
            PrimaryHost(props, in ctx, tiers, placement, drag),
        };
        if (props.SecondPanel is { } second) kids.Add(SecondHost(props, second, in ctx, tiers));
        // ONE sheet drag for the primary host's bottom sheet: its scrim's dim and its arm both move with it (`ClassicSheetDrag`).
        return Ctx.Provide(ClassicPanel.SheetDragSlot, drag, new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestPassThrough = true,
            Visible = Prop.Of(props.ChromeMounted),
            Children = kids.ToArray(),
        });
    }

    private static readonly Func<PanelPlacement> s_dock = static () => PanelPlacement.Dock;

    /// <summary>The primary panel's host: bound spacers place it by the tier's record, its width is the placement's (the window for a
    /// sheet, 340 floating, the committed width docked), and it FLIPs when a crossing moves it. The panel body owns the slide, the coats
    /// and the bodies.</summary>
    private static Element PrimaryHost(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, Func<PanelPlacement> placement, ClassicSheetDrag drag)
    {
        float Top() => placement() == PanelPlacement.Dock ? 0f : ClassicPanelTiers.Of(tiers()).Inset;
        float Bottom()
        {
            var t = tiers();
            float bar = ClassicPlayerTiers.BarOf(t).Height;
            return placement() switch
            {
                PanelPlacement.Sheet => 0f,
                PanelPlacement.Float => bar + ClassicPanelTiers.Of(t).Inset,
                _ => bar,
            };
        }
        float Right() => placement() switch
        {
            PanelPlacement.Float => ClassicPanelTiers.Of(tiers()).Inset,
            PanelPlacement.Dock => (props.SecondGapWidth?.Invoke() ?? 0f) + (props.SecondReservedWidth?.Invoke() ?? 0f),
            _ => 0f,
        };
        float WidthNow() => placement() switch
        {
            PanelPlacement.Sheet => props.Viewport.Value.Width,
            PanelPlacement.Float => ClassicPanelTiers.Of(tiers()).FloatWidth,
            _ => props.RailWidth.Value,
        };
        CornerRadius4 CornersNow() => placement() switch
        {
            PanelPlacement.Sheet => new CornerRadius4(ClassicPanelTiers.Corner, ClassicPanelTiers.Corner, 0f, 0f),
            PanelPlacement.Float => CornerRadius4.All(ClassicPanelTiers.Corner),
            _ => ClassicFrameRules.RailBandCorners,
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
                            Key = ClassicPanelTiers.HostKey,
                            Direction = 1, Shrink = 0f, ClipToBounds = true, ZStack = true, HitTestPassThrough = true,
                            OnRealized = h => drag.SheetNode = h,
                            Width = Prop.Of(WidthNow), Corners = Prop.Of(CornersNow),
                            Animate = ClassicFrameRules.PanelHostAnim,
                            Children =
                            [
                                // The FLOATING panel's backing band (the shell ground) — paint-only, never the deepest hit.
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

    /// <summary>The second docked pane's host (Huge, D3): the queue beside the primary panel, docked at the right edge above the dock. Its
    /// panel body owns its slide.</summary>
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
                        Key = ClassicPanelTiers.SecondHostKey,
                        Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true,
                        Corners = ClassicFrameRules.RailBandCorners, IsolateLayout = true,
                        Width = Prop.Of(props.SecondPanelWidth ?? s_zero),
                        Children = [ctx.View(ViewKeys.ShellPanel, second)],
                    },
                ],
            },
            new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(() => ClassicPlayerTiers.BarOf(tiers()).Height) },
        ],
    };

    /// <summary>The content card's hairline: the top-left card radius and a 1-DIP stroke on the left and top edges (the right and bottom ones
    /// overhang the clip). ALWAYS laid out with the card's own size, and its colour a bound brush, so a theme or light/dark switch only recolours
    /// it; the stroke box is keyed so a test can hold it to that. At Compact (a square card flush to the window edge) the ring is clear and the
    /// one-DIP top line alone draws the hairline, so no stroke runs down the window's left edge.</summary>
    private static Element ContentRegionStroke(Func<TierFacts> tiers) => new BoxEl
    {
        ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Key = "shell.content.stroke", Margin = ClassicFrameRules.StrokeOverhang, BorderWidth = ClassicFrameRules.StrokeWidth,
                BorderColor = Prop.Of(() => ClassicTiers.FrameOf(tiers()).SquareCard ? ColorF.Transparent : Tok.StrokeCardDefault),
                Corners = Prop.Of(() => ClassicTiers.FrameOf(tiers()).CardCorners),
            },
            new BoxEl
            {
                Key = "shell.content.stroke.top", Height = ClassicFrameRules.StrokeWidth,
                Fill = Prop.Of(() => ClassicTiers.FrameOf(tiers()).SquareCard ? Tok.StrokeCardDefault : ColorF.Transparent),
            },
        ],
    };
}
