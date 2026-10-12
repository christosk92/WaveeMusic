// ── Themes/Classic/Structure/Views/Drawer/ClassicDrawer.cs — Classic's `Shell.NarrowDrawer` (themes plan 6.1, T4c) ──────────
//
// From `Shell.UI.cs`: `NarrowDrawer`'s picture, `DrawerScrim` and `DrawerPane`. In the Narrow and Tiny bands the sidebar is an
// OVERLAY over the page. The core host (`Shell.NarrowDrawer`, a [CoreHost]) decides: the band's overlay and pinned answers, the
// session's open flag, the single `KeyPreview` slot it chains while open (Escape closes the overlay; while editing Escape belongs to
// the pane first), the leaf-navigation close, and the mount decision. This template draws and animates.
//
//   not mounted   a zero-size placeholder. BUG E1: on a desktop-width window the overlay is unreachable, so mount NOTHING (no scrim and
//                 no second `Sidebar.DrawerPane()`, a whole second pane planning the sidebar on every invalidation). The decision is
//                 the band ALONE (`NarrowDrawerMount.ShouldMount`), never the open flag: the pane must stay mounted for the ENTIRE time
//                 the shell is narrow, closed included, or the first open would have nothing already-mounted for the slide to animate from.
//   mounted       a ZStack: the SCRIM (a dim that fades with the open state; a click closes; a pinned drawer (editing in a forced band)
//                 keeps a lighter, non-dismissing scrim that lets the page behind stay usable) under the PANE (a stock overlay
//                 pane: in-app acrylic + flyout elevation, sliding in on the pane spline). Open/close is compositor-only.
//
// The scrim and the pane are Components: each runs its own opacity/translate transition on its own root, seeded from the closed
// resting value on mount so the first open has something to slide from.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's narrow-window drawer.</summary>
internal static class ClassicDrawer
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellNarrowDrawer"/>.</summary>
    public static Element Template(NarrowDrawerProps props, in ViewContext ctx)
    {
        if (!props.Mounted)
            return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

        var parts = props.Parts;
        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestVisible = props.Open,
            Children =
            [
                Embed.Comp(parts, static () => new ClassicDrawerScrim()),
                new BoxEl
                {
                    Grow = 1f, Direction = 0, Justify = FlexJustify.Start, HitTestPassThrough = true,
                    Children = [Embed.Comp(parts, static () => new ClassicDrawerPane())],
                },
            ],
        };
    }
}

/// <summary>The drawer's dim.</summary>
internal sealed class ClassicDrawerScrim : Component
{
    bool _mounted;

    public override Element Render()
    {
        var parts = UseProps<NarrowDrawerParts>();
        // Pinned (editing in a forced band) keeps the pane open behind a non-dismissing 0.2 scrim that lets the page
        // behind it stay usable (Q16): no hit test, no click-to-close.
        bool pinned = parts.IsPinned();
        bool open = parts.IsOpen();
        float ms = Design.Reduced ? 0f : Design.Motion.Fast;
        float target = parts.RestingOpacity(open);
        UseTransition(AnimChannel.Opacity, _mounted ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(open));
        _mounted = true;
        return new BoxEl
        {
            Grow = 1f,
            Fill = pinned ? ClassicFrameBitsRules.DrawerPinnedScrim : ClassicFrameBitsRules.DrawerScrim,
            Opacity = pinned ? 1f : target,
            HitTestVisible = open && !pinned, OnClick = pinned ? null : parts.Close,
        };
    }
}

/// <summary>The drawer's pane.</summary>
internal sealed class ClassicDrawerPane : Component
{
    bool _mounted;

    public override Element Render()
    {
        var parts = UseProps<NarrowDrawerParts>();
        bool open = parts.IsOpen();
        var vp = UseContextSignal(Viewport.Size);
        float width = parts.WidthNow(vp.Peek().Width);
        float ms = Design.Reduced ? 0f : (open ? MotionTok.PaneOpen.DurationMs : MotionTok.PaneClose.DurationMs);
        float target = parts.RestingTranslateX(open, width);
        UseTransition(AnimChannel.TranslateX, _mounted ? (open ? -width : 0f) : target, target, ms, Easing.FluentPane, DepKey.From(open));
        _mounted = true;

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Grow = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
            // The resting position stays coupled to the live width: a closed pane can grow while resizing inside the
            // band, and a stale static translation would expose the added strip.
            Transform = Prop.Of(() => Affine2D.Translation(parts.RestingTranslateX(parts.IsOpen(), parts.Width(vp.Value.Width)), 0f)),
            Width = Prop.Of(() => parts.Width(vp.Value.Width)),
            // A stock OVERLAY PANE (NavigationView minimal mode): in-app acrylic + flyout elevation, not a dialog slab.
            Fill = ColorF.Transparent, Acrylic = Tok.AcrylicFlyout,
            BorderWidth = 1f, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
            Corners = ClassicFrameBitsRules.DrawerPaneCorners,
            Shadow = Elevation.Flyout, HitTestVisible = open,
            Children = [parts.PaneBody()],
        };
    }
}
