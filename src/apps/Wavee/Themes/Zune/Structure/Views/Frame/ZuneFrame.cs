// ── Themes/Zune/Structure/Views/Frame/ZuneFrame.cs — Zune's `Shell.Frame` (themes plan 6.3; P5 Z2a) ───────────────────────────────────
//
// Boards Z-Frame-Home, Z-Frame-Panel and Z-Frame-Narrow. The core frame root keeps every hook, effect and state machine and hands this
// template one props record (`ShellFrameProps`); the template lays the window out and binds, it decides nothing.
//
//   THE FRAME, TOP DOWN (one column the window's height):
//
//     title bar (48, 56 at Huge 4K)                                         `shell.titlebar`      Shell.TitleBar
//     body = ZStack[ row[ page column · panel reservation · one reservation per docked pane ] · narrow drawer · panel layer ]
//            page column = [ band slot (Shell.NavPane, Z2b) · page ]        `shell.content.card`
//     play bar                                                              `shell.playerbar`     Player.Bar
//
//   Under the column: the material (Shell.Material, Z2b). Over it, as in every theme: the fullscreen stage, the two video layers, the
//   overlays, the file-drop cue, the command palette, the setup wizard's cover scrim, the drag chip and the frame-rate overlay.
//
//   THE BAND SPANS THE PAGE COLUMN ONLY (Z-Frame-Panel): the band slot sits above the page inside the page column, so a docked panel runs the
//   body's full height, from under the title bar down to the play bar. The slot hugs what the band draws: Z2b's band sets its own height
//   (84, collapsing to 52 on scroll) and animates it; until the band lands the slot holds whatever `Shell.NavPane` draws (Classic's stand-in
//   draws an empty box under a structure without a nav tree), and nothing below it depends on that height.
//
//   THE PANEL (Z-Frame-Panel inline, Z-Frame-Narrow floating). The panel layer lies over the page row and places the primary panel's host by
//   bound spacers from its placement: DOCKED it is flush right, the body's full height, as wide as the committed width, and the row reserves
//   that width (`RailReservedWidth`, 0 while closed or floating), so the page column narrows with no vertical jump (both FLIP on one 300 ms
//   transition); FLOATING it is 340 wide, flush right, from under the band as it stands now (the band slot's measured foot, ZT1) down to the play bar, the page
//   keeping its width; a SHEET is the body's whole box. There is no seam: Zune's panel has no drag handle (Z-Frame-Panel), and no gap: it
//   meets the page.
//
//   THE DOCKED PANES (ZT2, Z-Frame-Huge, Z-Frame-Huge4K). Where the tier docks more, each pane of the core's arrangement
//   (`ShellFrameProps.DockedPanes`) has its own host, keyed by its index (never by tier), with its own row reservation: Huge docks the queue
//   left of the primary (queue | lyrics · friends, 720), Huge 4K adds friends right of it (queue | lyrics | friends, 1080). Every host sits
//   flush right with a spacer after it as wide as what docks right of it (no gaps). On a crossing every move is a SIZE channel (ZT1's
//   lesson): a host eases its width (`ZunePanelTiers.HostMotion`), its spacer eases through real layout (`ShiftMotion`) and the host rides
//   it, and the floating panel's top spacer eases its height (`TopMotion`); the page column's own FLIP eases its right edge with them.
//
//   THE CHROME COLLAPSES under full-screen video and the fullscreen stage (bound Visible, still mounted), as Classic's does.
//
// THE PAGE CHROME CONTEXT. The title bar's trail and Z2b's band read the current page's chrome; the frame provides the core's signal
// (`NavPaneProps.Band.Chrome`) to its whole subtree as `ZuneFrameContext.PageChrome`, so neither reaches into the shell.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>What Zune's frame provides to the views it mounts.</summary>
public static class ZuneFrameContext
{
    /// <summary>The current page's chrome (title, trail, row 2): the core's signal, provided by the frame. Null outside the frame (a view
    /// mounted alone) or where the core hands no band data.</summary>
    public static readonly Context<IReadSignal<PageChrome?>?> PageChrome = new(null);
}

/// <summary>Zune's window frame.</summary>
internal static class ZuneFrame
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellFrame"/>.</summary>
    public static Element Template(ShellFrameProps props, in ViewContext ctx)
    {
        var tiers = ctx.TierReader();
        // The band slot's laid-out height (what the band draws now: 84 or 52, easing between them), for the floating panel's top.
        var bandH = ctx.Hooks.UseSignal(float.NaN);
        bool reduced = ctx.Reduced;
        var column = new BoxEl
        {
            // Window-tall, bound: the body yields instead of shoving the play bar off the bottom.
            Direction = 1, Grow = 1f, Height = Prop.Of(() => props.Viewport.Value.Height),
            OnKeyDown = props.OnKeyDown,
            Children =
            [
                .. props.Chords,
                TitleBarSlot(props, in ctx, tiers, reduced),
                Body(props, in ctx, tiers, bandH),
                ctx.View(ViewKeys.PlayerBar, props.PlayerBar) with { Key = ZuneFrameLayout.PlayerBarKey, Visible = Prop.Of(props.ChromeMounted) },
            ],
        };

        var ground = new BoxEl
        {
            Grow = 1f, ZStack = true, Fill = ColorF.Transparent,
            // The shell-wide "drop a file to play it" target (a row's own file target still wins: the deepest accepting target takes it).
            DropTarget = props.FileDrop,
            Children = [ctx.View(ViewKeys.ShellMaterialLayer, props.Material), column],
        };

        return Ctx.Provide(ZuneFrameContext.PageChrome, props.NavPane.Band?.Chrome, ZStack(
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
            ctx.View(ViewKeys.ShellFps, props.Fps)) with { Grow = 1f });
    }

    /// <summary>The title bar's slot (`shell.titlebar`): the record's height (48, 56 at Huge 4K), easing across the 3200 crossing so the band
    /// and the page under it follow frame by frame (ZT1). The engine bar inside sets its own height from the same record.</summary>
    private static Element TitleBarSlot(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, bool reduced) => new BoxEl
    {
        Key = ZuneFrameLayout.TitleBarKey, Direction = 1, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
        Height = Prop.Of(() => ZuneTiers.FrameOf(tiers()).TitleBarH),
        Animate = reduced ? null : ZuneFrameTiers.TitleBarHeightMotion,
        Visible = Prop.Of(props.ChromeMounted),
        Children = [ctx.View(ViewKeys.ShellTitleBar, props.TitleBar)],
    };

    /// <summary>The body: the page row with the reservations, the narrow drawer's core host, and the panel layer over the row. The ONE region
    /// that yields when the window is shorter than the column; it collapses (bound Visible) under full-screen video and the fullscreen stage.</summary>
    private static Element Body(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, Signal<float> bandH)
    {
        var panes = props.DockedPanes ?? s_noPanes;
        var row = new List<Element>(3 + panes.Count)
        {
            // Zune has no sidebar: the core's sidebar-column probe still gets a node, zero wide.
            new BoxEl { Width = 0f, Shrink = 0f, HitTestVisible = false, OnRealized = props.OnSidebarColumnRealized },
            PageColumn(props, in ctx, bandH),
            Reservation(props.RailReservedWidth),
        };
        foreach (var pane in panes) row.Add(Reservation(pane.ReservedWidth));
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
            Visible = Prop.Of(props.ChromeMounted),
            OnRealized = props.OnContentRegionRealized,
            OnBoundsChanged = props.OnContentRegionBounds,
            Children =
            [
                // The row never moves on a dock: the page column FLIPs relative to it.
                new BoxEl { MorphId = ZuneFrameRules.ContentRowMorphId, Direction = 0, Grow = 1f, ClipToBounds = true, Children = row.ToArray() },
                props.NarrowDrawer(),
                PanelLayer(props, in ctx, tiers, bandH, panes),
            ],
        };
    }

    private static readonly IReadOnlyList<DockedPaneProps> s_noPanes = [];

    /// <summary>The page column (`shell.content.card`): the band slot over the page. Flat: the ground shows through (no card, no stroke).</summary>
    private static Element PageColumn(ShellFrameProps props, in ViewContext ctx, Signal<float> bandH) => new BoxEl
    {
        Key = ZuneFrameLayout.ContentKey,
        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
        Animate = ZuneFrameRules.PageColumnAnim, RelativeTo = ZuneFrameRules.ContentRowMorphId,
        Children =
        [
            // The band (Z2b): it hugs what `Shell.NavPane` draws and never shrinks under the page.
            new BoxEl
            {
                Key = ZuneFrameRules.BandSlotKey, Direction = 1, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
                // The slot eases its own height (a reflow, anchored at the top and clipped at the foot): the band inside changes height at once, and a
                // slot that merely hugged an animating child snapped to the child's target (the page jumped by the row's 32). The page follows frame by frame.
                Animate = Design.Reduced ? null : ZuneBandRules.HeightMotion,
                // The floating panel hangs from the band's foot as it is NOW (ZT1): the slot's own height, 84 or 52 or easing between.
                OnBoundsChanged = r => { if (bandH.Peek() != r.H) bandH.Value = r.H; },
                Children = [ctx.View(ViewKeys.ShellNavPane, props.NavPane)],
            },
            new BoxEl
            {
                Key = ZuneFrameRules.PageClipKey,
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f, ClipToBounds = true, IsolateLayout = true,
                Children = [props.ContentHost()],
            },
        ],
    };

    /// <summary>A docked panel's row reservation: <paramref name="width"/> wide (0 while closed or floating), painted the panel's ground so the
    /// slide-out never shows a hole.</summary>
    private static Element Reservation(Func<float> width) => new BoxEl
    {
        Shrink = 0f, HitTestPassThrough = true,
        Width = Prop.Of(width),
        Fill = Prop.Of(static () => R.Layer.FloatingPane),
    };

    // ── the panel layer ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The panels over the page row: the sheet's scrim, the primary panel's host and one host per docked pane.</summary>
    private static Element PanelLayer(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, Signal<float> bandH,
        IReadOnlyList<DockedPaneProps> panes)
    {
        Func<PanelPlacement> placement = props.PanelPlacement ?? s_dock;
        bool reduced = ctx.Reduced;
        var kids = new List<Element>(2 + panes.Count)
        {
            Embed.Comp(props.Panel, static () => new ZunePanelScrim()),
            PrimaryHost(props, in ctx, tiers, placement, bandH, panes, reduced),
        };
        foreach (var pane in panes) kids.Add(PaneHost(props, pane, panes, in ctx, reduced));
        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestPassThrough = true,
            Children = kids.ToArray(),
        };
    }

    private static readonly Func<PanelPlacement> s_dock = static () => PanelPlacement.Dock;

    /// <summary>What docks right of <paramref name="pane"/> (null: the primary panel), read live: right of the primary, every open pane on its
    /// right (<see cref="PaneSide.After"/>); right of a pane after it, the open panes after it farther out (a higher index); right of a pane
    /// before it, the primary's reservation, every open pane after it, and the open panes before it nearer the primary (a lower index). The
    /// panes on each side sit nearest the primary first.</summary>
    private static float RightOf(ShellFrameProps props, IReadOnlyList<DockedPaneProps> panes, DockedPaneProps? pane)
    {
        bool before = pane is not null && pane.Side() == PaneSide.Before;
        float right = before ? props.RailReservedWidth() : 0f;
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

    /// <summary>The primary panel's host, placed by bound spacers: docked flush right at the body's full height (left of the panes docked after
    /// it), floating 340 wide from under the band as the band stands now (its slot's measured height: 84, 52 collapsed or without a row 2,
    /// 112 / 72 at Huge 4K; the record's full height until the slot is first laid out), a sheet the body's whole box. Its width is the
    /// placement's (the committed width docked: 340, the 360 floor where panes dock beside it). A crossing moves it by size channels only: its
    /// width eases, and the spacers above it and right of it ease through real layout. The panel's own template runs the open/close slide.</summary>
    private static Element PrimaryHost(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, Func<PanelPlacement> placement,
        Signal<float> bandH, IReadOnlyList<DockedPaneProps> panes, bool reduced)
    {
        float Top()
        {
            if (placement() != PanelPlacement.Float) return 0f;
            float measured = bandH.Value;
            return float.IsFinite(measured) ? measured : ZuneTiers.FrameOf(tiers()).BandH;
        }
        float Right() => placement() == PanelPlacement.Dock ? RightOf(props, panes, null) : 0f;
        float WidthNow() => placement() switch
        {
            PanelPlacement.Sheet => props.Viewport.Value.Width,
            PanelPlacement.Float => ZuneFrameRules.PanelFloatW,
            _ => props.RailWidth.Value,
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, HitTestPassThrough = true,
            Children =
            [
                new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(Top), Animate = reduced ? null : ZunePanelTiers.TopMotion },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.End, HitTestPassThrough = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = ZuneFrameRules.PanelHostKey,
                            Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true,
                            Width = Prop.Of(WidthNow),
                            Animate = reduced ? null : ZunePanelTiers.HostMotion,
                            Children =
                            [
                                new BoxEl
                                {
                                    Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, HitTestPassThrough = true, IsolateLayout = true,
                                    Opacity = Prop.Of(() => props.RailFade.Value),
                                    Children = [ctx.View(ViewKeys.ShellPanel, props.Panel)],
                                },
                            ],
                        },
                        new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(Right), Animate = reduced ? null : ZunePanelTiers.ShiftMotion },
                    ],
                },
            ],
        };
    }

    /// <summary>A docked pane's host (Huge's queue; Huge 4K's queue and friends): flush right at the body's full height, as wide as the pane,
    /// with a spacer after it as wide as what docks right of it (both easing on a crossing). Keyed by the pane's index, so a crossing that adds
    /// or drops another pane keeps its node. Its panel body owns its slide.</summary>
    private static Element PaneHost(ShellFrameProps props, DockedPaneProps pane, IReadOnlyList<DockedPaneProps> panes, in ViewContext ctx,
        bool reduced) => new BoxEl
    {
        Direction = 0, Grow = 1f, Justify = FlexJustify.End, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Key = ZunePanelTiers.PaneHostKey(pane.Index),
                Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true, IsolateLayout = true,
                Width = Prop.Of(pane.Width),
                Animate = reduced ? null : ZunePanelTiers.HostMotion,
                Children = [ctx.View(ViewKeys.ShellPanel, pane.Panel)],
            },
            new BoxEl
            {
                Shrink = 0f, HitTestVisible = false, Width = Prop.Of(() => RightOf(props, panes, pane)),
                Animate = reduced ? null : ZunePanelTiers.ShiftMotion,
            },
        ],
    };
}
