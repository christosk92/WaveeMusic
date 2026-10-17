// ── Themes/Zune/Structure/Views/Frame/ZuneFrame.cs — Zune's `Shell.Frame` (themes plan 6.3; P5 Z2a) ───────────────────────────────────
//
// Boards Z-Frame-Home, Z-Frame-Panel and Z-Frame-Narrow. The core frame root keeps every hook, effect and state machine and hands this
// template one props record (`ShellFrameProps`); the template lays the window out and binds, it decides nothing.
//
//   THE FRAME, TOP DOWN (one column the window's height):
//
//     title bar (48, 56 at Huge 4K)                                         `shell.titlebar`      Shell.TitleBar
//     body = ZStack[ row[ page column · panel reservation · second reservation ] · narrow drawer · panel layer ]
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
//   transition); FLOATING it is 340 wide, flush right, from under the band (the frame record's band height) down to the play bar, the page
//   keeping its width; a SHEET is the body's whole box. At Huge the second pane (the queue) docks right of it with its own reservation.
//   There is no seam: Zune's panel has no drag handle (Z-Frame-Panel), and no gap: it meets the page.
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
        var column = new BoxEl
        {
            // Window-tall, bound: the body yields instead of shoving the play bar off the bottom.
            Direction = 1, Grow = 1f, Height = Prop.Of(() => props.Viewport.Value.Height),
            OnKeyDown = props.OnKeyDown,
            Children =
            [
                .. props.Chords,
                ctx.View(ViewKeys.ShellTitleBar, props.TitleBar) with { Key = ZuneFrameLayout.TitleBarKey, Visible = Prop.Of(props.ChromeMounted) },
                Body(props, in ctx, tiers),
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

    private static readonly Func<float> s_zero = static () => 0f;

    /// <summary>The body: the page row with the reservations, the narrow drawer's core host, and the panel layer over the row. The ONE region
    /// that yields when the window is shorter than the column; it collapses (bound Visible) under full-screen video and the fullscreen stage.</summary>
    private static Element Body(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers) => new BoxEl
    {
        ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
        Visible = Prop.Of(props.ChromeMounted),
        OnRealized = props.OnContentRegionRealized,
        OnBoundsChanged = props.OnContentRegionBounds,
        Children =
        [
            new BoxEl
            {
                // The row never moves on a dock: the page column FLIPs relative to it.
                MorphId = ZuneFrameRules.ContentRowMorphId, Direction = 0, Grow = 1f, ClipToBounds = true,
                Children =
                [
                    // Zune has no sidebar: the core's sidebar-column probe still gets a node, zero wide.
                    new BoxEl { Width = 0f, Shrink = 0f, HitTestVisible = false, OnRealized = props.OnSidebarColumnRealized },
                    PageColumn(props, in ctx),
                    Reservation(props.RailReservedWidth),
                    Reservation(props.SecondReservedWidth ?? s_zero),
                ],
            },
            props.NarrowDrawer(),
            PanelLayer(props, in ctx, tiers),
        ],
    };

    /// <summary>The page column (`shell.content.card`): the band slot over the page. Flat: the ground shows through (no card, no stroke).</summary>
    private static Element PageColumn(ShellFrameProps props, in ViewContext ctx) => new BoxEl
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

    /// <summary>The panels over the page row: the sheet's scrim, the primary panel's host and the second pane's host.</summary>
    private static Element PanelLayer(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers)
    {
        Func<PanelPlacement> placement = props.PanelPlacement ?? s_dock;
        var kids = new List<Element>(3)
        {
            Embed.Comp(props.Panel, static () => new ZunePanelScrim()),
            PrimaryHost(props, in ctx, tiers, placement),
        };
        if (props.SecondPanel is { } second) kids.Add(SecondHost(props, second, in ctx));
        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestPassThrough = true,
            Children = kids.ToArray(),
        };
    }

    private static readonly Func<PanelPlacement> s_dock = static () => PanelPlacement.Dock;

    /// <summary>The primary panel's host, placed by bound spacers: docked flush right at the body's full height (left of the second pane when
    /// it is open), floating 340 wide from under the band, a sheet the body's whole box. Its width is the placement's; it FLIPs when a
    /// crossing moves it. The panel's own template runs the open/close slide.</summary>
    private static Element PrimaryHost(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, Func<PanelPlacement> placement)
    {
        float Top() => placement() == PanelPlacement.Float ? ZuneTiers.FrameOf(tiers()).BandH : 0f;
        float Right() => placement() == PanelPlacement.Dock
            ? (props.SecondGapWidth?.Invoke() ?? 0f) + (props.SecondReservedWidth?.Invoke() ?? 0f)
            : 0f;
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
                new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(Top) },
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
                            Animate = ZuneFrameRules.PanelHostAnim,
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
                        new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(Right) },
                    ],
                },
            ],
        };
    }

    /// <summary>The second docked pane's host (Huge): the queue, flush right at the body's full height. Its panel body owns its slide.</summary>
    private static Element SecondHost(ShellFrameProps props, PanelProps second, in ViewContext ctx) => new BoxEl
    {
        Direction = 0, Grow = 1f, Justify = FlexJustify.End, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Key = ZuneFrameRules.SecondPanelHostKey,
                Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true, IsolateLayout = true,
                Width = Prop.Of(props.SecondPanelWidth ?? s_zero),
                Children = [ctx.View(ViewKeys.ShellPanel, second)],
            },
        ],
    };
}
