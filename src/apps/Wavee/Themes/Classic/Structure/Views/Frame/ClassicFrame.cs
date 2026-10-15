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
//     column = 14 zero-size chord boxes · [chrome row] · content region · [player dock]
//              the bracketed bands and the content region COLLAPSE (bound Visible, still mounted) under full-screen video OR the
//              fullscreen stage: out of layout, paint and hit-test, subtree inactive
//     content region = ZStack[ row(sidebar column · content card · rail gap · rail reservation) · sidebar seam ·
//                              rail overlay · rail seam · narrow drawer ]
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

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's window frame.</summary>
internal static class ClassicFrame
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellFrame"/>.</summary>
    public static Element Template(ShellFrameProps props, in ViewContext ctx)
    {
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
                ctx.View(ViewKeys.ShellTitleBar, props.TitleBar) with { Visible = Prop.Of(props.ChromeMounted) },
                ContentRegion(props, in ctx),
                ctx.View(ViewKeys.PlayerBar, props.PlayerBar) with { Visible = Prop.Of(props.ChromeMounted) },
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

        return ZStack(
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
            ctx.View(ViewKeys.ShellFps, props.Fps)) with { Grow = 1f };
    }

    private static Element ContentRegion(ShellFrameProps props, in ViewContext ctx) => ZStack(
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
                    Direction = 1, Shrink = 0f, ClipToBounds = true,
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
                    // The stock Win11 content region: flush, one corner, a left+top stroke, no shadow, no gutter.
                    Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
                    Children =
                    [
                        new BoxEl { Grow = 1f, Fill = Prop.Of(static () => R.Layer.FileArea), Corners = Design.Size.ContentPaneCorners },
                        new BoxEl
                        {
                            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                            Fill = ColorF.Transparent, Corners = Design.Size.ContentPaneCorners, ClipToBounds = true,
                            IsolateLayout = true, Animate = ClassicFrameRules.ContentCardAnim, RelativeTo = ClassicFrameRules.ContentRowMorphId,
                            Children = [props.ContentHost()],
                        },
                        ContentRegionStroke(),
                    ],
                },
                // The rail's 8-DIP breathing room exists only while the rail is inline.
                new BoxEl
                {
                    Shrink = 0f, HitTestVisible = false,
                    Width = Prop.Of(props.RailGapWidth),
                },
                // The rail RESERVATION: snaps 0 ↔ width at commit; its underlay is the docked band's ONE coat.
                new BoxEl
                {
                    Shrink = 0f,
                    Width = Prop.Of(props.RailReservedWidth),
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, HitTestPassThrough = true, ClipToBounds = true, ZStack = true,
                            Children = [new BoxEl { Margin = ClassicFrameRules.StrokeOverhang, Fill = Prop.Of(static () => R.Layer.FileArea), Corners = ClassicFrameRules.RailBandCorners }],
                        },
                    ],
                },
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
        // The rail overlay: final width always; the rail body owns the translate, the coats and the bodies.
        new BoxEl
        {
            Grow = 1f, Direction = 0, Justify = FlexJustify.End, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Shrink = 0f, ClipToBounds = true, ZStack = true, HitTestPassThrough = true,
                    Width = Prop.Of(() => props.RailWidth.Value),
                    Children =
                    [
                        // The FLOATING rail's backing band (the shell ground) — paint-only, never the deepest hit.
                        new BoxEl
                        {
                            Grow = 1f, HitTestPassThrough = true,
                            Fill = Prop.Of(() => props.RailFloats() ? R.Layer.FloatingChrome : ColorF.Transparent),
                        },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, HitTestPassThrough = true,
                            Corners = ClassicFrameRules.RailBandCorners, IsolateLayout = true,
                            Opacity = Prop.Of(() => props.RailFade.Value),
                            Children = [ctx.View(ViewKeys.ShellPanel, props.Panel)],
                        },
                    ],
                },
            ],
        },
        // The rail seam: exists only while the rail is open. It paints AFTER (above) the rail overlay: the rail no longer
        // moves during a drag, so a narrowing drag carries the guide into the rail's rect, and it must stay visible over
        // the rail's content. At rest the strip lies wholly left of the rail, so hit-testing is unchanged.
        new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Width = Prop.Of(props.RailSeamWidth),
            Transform = Prop.Of(() => Affine2D.Translation(props.RailSeamX(), 0f)),
            Children = [props.RailSeam()],
        },
        props.NarrowDrawer()) with
    {
        // The ONE region that yields when the window is shorter than the column; clipped so a settling page never
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

    /// <summary>The content card's hairline: the top-left card radius and a 1-DIP stroke on the left and top edges (the right and bottom ones
    /// overhang the clip). ALWAYS laid out with the card's own size, and its colour a bound brush, so a theme or light/dark switch only recolours
    /// it; the stroke box is keyed so a test can hold it to that.</summary>
    private static Element ContentRegionStroke() => new BoxEl
    {
        ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Key = "shell.content.stroke", Margin = ClassicFrameRules.StrokeOverhang, BorderWidth = 1f, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
                Corners = Design.Size.ContentPaneCorners,
            },
        ],
    };
}
