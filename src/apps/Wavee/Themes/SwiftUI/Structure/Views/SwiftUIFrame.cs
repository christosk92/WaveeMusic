// ── Themes/SwiftUI/Structure/Views/SwiftUIFrame.cs — the SwiftUI spike's `Shell.Frame` (SPIKE, not for release) ──────────────────────
//
// macOS 26's NavigationSplitView with Apple Music's floating player, around the borrowed WinUI views:
//
//     row( nav column · detail column )                                      the window's height, over the material and the unified ground
//       nav column    = the core's pane width + 2 x 8, holding the FLOATING GLASS SIDEBAR (acrylic + rim + shadow, 18 corners, 8 from
//                       the window and the detail column); 0 at Compact (the drawer owns the pane), easing on the core's width
//       detail column = title bar slot (48, the WinUI toolbar: back, forward, search, account)
//                       body = ZStack[ content region · the GLASS CAPSULE player (12 from the column's sides and foot, the page scrolling
//                                      under it) · the panel layer ]
//
// Over it, as in every theme: the fullscreen stage, the video layers, the overlays, the file-drop cue, the palette, the cover scrim, the drag
// chip and the frame-rate overlay. The panels dock beside the content above the capsule (their foot is the capsule's), float, or sheet.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using F = Wavee.Themes.SwiftUI.SwiftUIFrameRules;

namespace Wavee.Themes.SwiftUI;

/// <summary>The SwiftUI spike's window frame.</summary>
internal static class SwiftUIFrame
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellFrame"/>.</summary>
    public static Element Template(ShellFrameProps props, in ViewContext ctx)
    {
        var tiers = ctx.TierReader();
        var facts = TierContext.DrawnIn(ctx.Hooks.UseContext(ThemeFactsContext.Signal));
        bool reduced = ctx.Reduced;
        var dock = new SwiftUIDockWidth(props);

        var detail = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f,
            Children =
            [
                TitleBarSlot(props, in ctx, facts, reduced),
                new BoxEl
                {
                    // The body: the content region, the capsule over its foot, the panel layer over both. The ONE region that yields.
                    ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                    Children =
                    [
                        ContentRegion(props, in ctx, dock),
                        Capsule(props, in ctx),
                        PanelLayer(props, in ctx, tiers, reduced, dock),
                    ],
                },
            ],
        };

        var row = new BoxEl
        {
            Direction = 0, Grow = 1f, Height = Prop.Of(() => props.Viewport.Value.Height),
            OnKeyDown = props.OnKeyDown,
            Children = [.. props.Chords, Sidebar(props, in ctx), detail],
        };

        var ground = new BoxEl
        {
            Grow = 1f, ZStack = true, Fill = Prop.Of(static () => R.Layer.ShellGround),
            DropTarget = props.FileDrop,
            Children = [ctx.View(ViewKeys.ShellMaterialLayer, props.Material), row],
        };

        return ZStack(
            ground,
            Flow.Show(props.ImmersiveOpen, new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true,
                Enter = Stage.EnterTerminal, Exit = Stage.ExitTerminal,
                Children = [ctx.View(ViewKeys.StageRoot, props.StageRoot)],
            }),
            props.VideoPipLayer(),
            props.VideoFullscreenLayer(),
            props.Overlays(),
            ctx.View(ViewKeys.ShellFileDrop, props.FileDropCue),
            ctx.View(ViewKeys.ShellCommandPalette, props.CommandPalette),
            ctx.View(ViewKeys.ShellCoverScrim, props.CoverScrim),
            ctx.View(ViewKeys.ShellDragChip, props.DragChip),
            ctx.View(ViewKeys.ShellFps, props.Fps)) with { Grow = 1f };
    }

    // ── glass ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A Liquid Glass plate (the engine's acrylic: blur, luminosity, tint) with its resting wash, its specular rim and its shadow.</summary>
    private static BoxEl Glass(string key, CornerRadius4 corners) => new()
    {
        Key = key, Corners = corners, ClipToBounds = true,
        Acrylic = SwiftUITokens.Glass, Fill = SwiftUITokens.GlassWash,
        BorderBrush = SwiftUITokens.GlassRim, BorderWidth = F.RimWidth,
        Shadow = SwiftUITokens.GlassShadow,
    };

    // ── the sidebar ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The nav column: the core's pane width plus the glass's insets, easing with it; the floating glass sidebar inside.</summary>
    private static Element Sidebar(ShellFrameProps props, in ViewContext ctx) => new BoxEl
    {
        Key = F.NavColumnKey, Direction = 1, Shrink = 0f,
        OnRealized = props.OnSidebarColumnRealized,
        Width = Prop.Of(() => F.NavColumnW(props.SidebarColumnWidth())),
        Animate = ctx.Reduced ? null : F.NavColumnAnim,
        Visible = Prop.Of(props.ChromeMounted),
        Children =
        [
            Glass(F.SidebarGlassKey, CornerRadius4.All(F.SidebarRadius)) with
            {
                Direction = 1, Grow = 1f, Margin = F.SidebarMargin, Padding = new Edges4(0f, F.SidebarTopPad, 0f, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, IsolateLayout = true, ClipToBounds = true,
                        Opacity = Prop.Of(() => props.SidebarFade.Value),
                        Children = [Flow.Show(props.SidebarPaneDocked, ctx.View(ViewKeys.ShellNavPane, props.NavPane))],
                    },
                ],
            },
        ],
    };

    // ── the title bar ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Element TitleBarSlot(ShellFrameProps props, in ViewContext ctx, IReadSignal<ThemeFacts> facts, bool reduced) => new BoxEl
    {
        Key = F.TitleBarKey, Direction = 1, Justify = FlexJustify.End, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
        Height = Prop.Of(() => F.TitleBarH(facts.Value.Capabilities.ShowsTabStrip)),
        Animate = reduced ? null : F.TitleSlotAnim,
        Visible = Prop.Of(props.ChromeMounted),
        Children = [ctx.View(ViewKeys.ShellTitleBar, props.TitleBar)],
    };

    // ── the content region ───────────────────────────────────────────────────────────────────────────────────────────────

    private static Element ContentRegion(ShellFrameProps props, in ViewContext ctx, SwiftUIDockWidth dock)
    {
        var panes = props.DockedPanes ?? s_noPanes;
        LayoutTransition? reserve = ctx.Reduced ? null : F.PanelReserveAnim;
        var row = new List<Element>(5 + panes.Count)
        {
            ContentCard(props, in ctx),
            new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(props.RailGapWidth), Animate = reserve },
            Reservation(dock.Reserved, reserve),
        };
        if (props.DockedPanes is null)
        {
            row.Add(new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(props.SecondGapWidth ?? s_zero), Animate = reserve });
            row.Add(Reservation(props.SecondReservedWidth ?? s_zero, reserve));
        }
        foreach (var pane in panes) row.Add(Reservation(pane.ReservedWidth, reserve));
        return ZStack(
            new BoxEl { MorphId = F.ContentRowMorphId, Direction = 0, Grow = 1f, ClipToBounds = true, Children = row.ToArray() },
            new BoxEl
            {
                Direction = 1, ClipToBounds = true,
                Width = Prop.Of(props.RailSeamWidth),
                Visible = Prop.Of(() => props.RailSeamWidth() > 0f),
                Transform = Prop.Of(() => Affine2D.Translation(props.RailSeamX(), 0f)),
                Children = [props.RailSeam()],
            },
            props.NarrowDrawer()) with
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
            Visible = Prop.Of(props.ChromeMounted),
            OnRealized = props.OnContentRegionRealized,
            OnBoundsChanged = props.OnContentRegionBounds,
        };
    }

    /// <summary>The detail column's content: the unified ground (no card, no hairline: the window is one surface) and the clipped page host.</summary>
    private static Element ContentCard(ShellFrameProps props, in ViewContext ctx) => new BoxEl
    {
        Key = F.ContentKey,
        Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
        Children =
        [
            new BoxEl { Grow = 1f, Fill = Prop.Of(static () => R.Layer.FileArea) },
            new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Fill = ColorF.Transparent, ClipToBounds = true, IsolateLayout = true,
                Animate = ctx.Reduced ? null : F.ContentCardAnim, RelativeTo = F.ContentRowMorphId,
                Children = [props.ContentHost()],
            },
        ],
    };

    private static readonly Func<float> s_zero = static () => 0f;
    private static readonly IReadOnlyList<DockedPaneProps> s_noPanes = [];

    private static Element Reservation(Func<float> width, LayoutTransition? motion) => new BoxEl
    {
        Shrink = 0f, Width = Prop.Of(width), Animate = motion,
        Children = [new BoxEl { Grow = 1f, HitTestPassThrough = true, Fill = Prop.Of(static () => R.Layer.FileArea) }],
    };

    // ── the capsule player ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The play bar in a floating glass capsule over the foot of the detail column (Apple Music's mini player). Its slot hugs the bar (the
    /// bar eases its own 64 / 72 change); the layer around it passes every hit through, so the page under the glass stays live.</summary>
    private static Element Capsule(ShellFrameProps props, in ViewContext ctx) => new BoxEl
    {
        Direction = 1, Grow = 1f, Justify = FlexJustify.End, HitTestPassThrough = true,
        Visible = Prop.Of(props.ChromeMounted),
        Children =
        [
            Glass(F.CapsuleKey, CornerRadius4.All(Radii.Full)) with
            {
                Direction = 1, Shrink = 0f,
                Margin = F.CapsuleMargin, Padding = F.CapsulePadding,
                Children =
                [
                    new BoxEl
                    {
                        Key = F.PlayerBarKey, Direction = 1, Shrink = 0f,
                        Children = [ctx.View(ViewKeys.PlayerBar, props.PlayerBar)],
                    },
                ],
            },
        ],
    };

    // ── the panel layer ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Func<PanelPlacement> s_dock = static () => PanelPlacement.Dock;

    private static WidthTier WindowOf(Func<TierFacts> tiers)
    {
        var f = tiers();
        return f.Policy is null ? WidthTier.Standard : f.Window;
    }

    private static Element PanelLayer(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, bool reduced, SwiftUIDockWidth dock)
    {
        Func<PanelPlacement> placement = props.PanelPlacement ?? s_dock;
        var panes = props.DockedPanes ?? s_noPanes;
        var kids = new List<Element>(3 + panes.Count)
        {
            Embed.Comp(props.Panel, static () => new SwiftUIPanelScrim()),
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

    private static float RightOf(ShellFrameProps props, IReadOnlyList<DockedPaneProps> panes, DockedPaneProps? pane, SwiftUIDockWidth dock)
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

    private static Element PrimaryHost(ShellFrameProps props, in ViewContext ctx, Func<TierFacts> tiers, Func<PanelPlacement> placement,
        IReadOnlyList<DockedPaneProps> panes, bool reduced, SwiftUIDockWidth dock)
    {
        float Bottom() => placement() == PanelPlacement.Sheet ? 0f : F.PanelFoot(WindowOf(tiers));
        float Right() => placement() == PanelPlacement.Dock ? RightOf(props, panes, null, dock) : 0f;
        float WidthNow() => placement() switch
        {
            PanelPlacement.Sheet => props.Viewport.Value.Width,
            PanelPlacement.Float => F.PanelFloatW,
            _ => dock.Host(),
        };
        CornerRadius4 CornersNow() => placement() switch
        {
            PanelPlacement.Sheet => new CornerRadius4(Radii.Overlay, Radii.Overlay, 0f, 0f),
            _ => F.PaneBandCorners,
        };
        return new BoxEl
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
                            Key = F.PanelHostKey,
                            Direction = 1, Shrink = 0f, ClipToBounds = true, ZStack = true, HitTestPassThrough = true,
                            Width = Prop.Of(WidthNow), Corners = Prop.Of(CornersNow),
                            Animate = reduced ? null : F.PanelHostAnim,
                            Children =
                            [
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
                        Key = F.SecondHostKey,
                        Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true,
                        Corners = F.PaneBandCorners, IsolateLayout = true,
                        Width = Prop.Of(props.SecondPanelWidth ?? s_zero),
                        Children = [ctx.View(ViewKeys.ShellPanel, second)],
                    },
                ],
            },
            new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(() => F.PanelFoot(WindowOf(tiers))) },
        ],
    };

    private static Element PaneHost(ShellFrameProps props, DockedPaneProps pane, IReadOnlyList<DockedPaneProps> panes, in ViewContext ctx,
        Func<TierFacts> tiers, bool reduced, SwiftUIDockWidth dock) => new BoxEl
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
                        Key = F.PaneHostKey(pane.Index),
                        Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true, IsolateLayout = true,
                        Corners = F.PaneBandCorners,
                        Width = Prop.Of(pane.Width),
                        Animate = reduced ? null : F.PanelHostAnim,
                        Children = [ctx.View(ViewKeys.ShellPanel, pane.Panel)],
                    },
                    new BoxEl { Shrink = 0f, HitTestVisible = false, Width = Prop.Of(() => RightOf(props, panes, pane, dock)) },
                ],
            },
            new BoxEl { Shrink = 0f, HitTestVisible = false, Height = Prop.Of(() => F.PanelFoot(WindowOf(tiers))) },
        ],
    };
}

/// <summary>The docked panel's width (WinUI's rule): live while a seam drag runs, the committed width at rest, held while closed.</summary>
internal sealed class SwiftUIDockWidth(ShellFrameProps props)
{
    float _held;

    float Live() => (props.RailLiveWidth ?? props.RailWidth).Value;

    public float Reserved() => props.RailReservedWidth() > 0f ? Live() : 0f;

    public float Host()
    {
        float live = Live();
        if (props.RailReservedWidth() > 0f || props.RailFloats() || _held <= 0f) _held = live;
        return _held;
    }
}

/// <summary>The Compact bottom sheet's scrim.</summary>
internal sealed class SwiftUIPanelScrim : Component
{
    bool _mounted;

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool show = view.Open && view.Placement == PanelPlacement.Sheet;
        float ms = Design.Reduced ? 0f : SwiftUIFrameRules.SheetScrimFadeMs;
        float target = show ? 1f : 0f;
        UseTransition(AnimChannel.Opacity, _mounted ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(show));
        _mounted = true;
        return new BoxEl
        {
            Grow = 1f, Opacity = target, HitTestVisible = show, OnClick = show ? props.Close : null, TabStop = false,
            BlocksBackgroundScroll = show,
            Children = [new BoxEl { Grow = 1f, Fill = SwiftUIFrameRules.SheetScrim, HitTestVisible = false }],
        };
    }
}
