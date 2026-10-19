// ── Themes/WinUi/Structure/Views/Drawer/WinUiDrawer.cs — WinUI's `Shell.NarrowDrawer` (P6 W2b) ──────────────────────────────────────
//
// NavigationView's overlay pane: below 641 (Minimal) and, at Small, over the 48 rail, the hamburger opens the 280 pane OVER the page as a light-dismiss
// overlay. The core host (`Shell.NarrowDrawer`, a [CoreHost]) decides: the band's overlay and pinned answers, the session's open flag, the Escape key preview, the
// leaf-navigation close and the mount decision. This template draws and animates: a light scrim (a click closes) under a stock overlay pane (acrylic, the
// flyout elevation) that slides in on the pane spline from the left edge. It never pushes the page: the drawer is a layer of the frame's content region.
//
//   not mounted   a zero-size placeholder (the desktop bands never reach it).
//   mounted       the scrim and the pane stay mounted for the whole time the shell is narrow, closed included, so the first open has something to slide
//                 from. Open/close is compositor-only (opacity and translate), and a reversal continues from where the surface is drawn.
//
// The pane fills the content region's height and is 280 wide (the policy's fixed width, clamped to the window less the rail). WinUI's Compact title bar
// carries the account in its toolbar (board `W-Narrow`), so unlike Classic's drawer there is no account row over the list.
//
// MODAL (open and not pinned): the scrim and the pane keep the wheel off the page behind, focus moves into the pane with Tab trapped there, and Escape
// closes (the core host's key preview).
//
// Derived from Classic's `ClassicDrawer`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's narrow-window drawer.</summary>
internal static class WinUiDrawer
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
                Embed.Comp(parts, static () => new WinUiDrawerScrim()),
                new BoxEl
                {
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Start, HitTestPassThrough = true,
                    Children = [Embed.Comp(props, static () => new WinUiDrawerPane())],
                },
            ],
        };
    }
}

/// <summary>The drawer's smoke.</summary>
internal sealed class WinUiDrawerScrim : Component
{
    bool _mounted;

    public override Element Render()
    {
        var parts = UseProps<NarrowDrawerParts>();
        // Pinned (editing in a forced band) keeps the pane open behind a non-dismissing scrim that lets the page behind it stay usable: no hit test, no click-to-close.
        bool pinned = parts.IsPinned();
        bool open = parts.IsOpen();
        float ms = Design.Reduced ? 0f : Design.Motion.Fast;
        float target = parts.RestingOpacity(open);
        UseTransition(AnimChannel.Opacity, _mounted ? WinUiDrawerMotion.From(Context, AnimChannel.Opacity, 1f - target) : target, target, ms, Easing.Linear, DepKey.From(open));
        _mounted = true;
        bool modal = open && !pinned;
        return new BoxEl
        {
            Grow = 1f,
            Fill = pinned ? WinUiNavDrawerRules.PinnedScrim : WinUiNavDrawerRules.Scrim,
            Opacity = pinned ? 1f : target,
            HitTestVisible = modal, OnClick = pinned ? null : parts.Close, TabStop = false,
            // The page is a sibling under the drawer, never an ancestor: without this the wheel over the dim found it.
            BlocksBackgroundScroll = modal,
        };
    }
}

/// <summary>The drawer's pane.</summary>
internal sealed class WinUiDrawerPane : Component
{
    bool _mounted;
    readonly WinUiDrawerScope _scope = new();

    public override Element Render()
    {
        var props = UseProps<NarrowDrawerProps>();
        var parts = props.Parts;
        bool open = parts.IsOpen();
        bool modal = open && !parts.IsPinned();
        var hooks = UseContext(InputHooks.Current);
        var vp = UseContextSignal(Viewport.Size);
        float width = parts.WidthNow(vp.Peek().Width);
        float ms = Design.Reduced ? 0f : (open ? MotionTok.PaneOpen.DurationMs : MotionTok.PaneClose.DurationMs);
        float target = parts.RestingTranslateX(open, width);
        UseTransition(AnimChannel.TranslateX, _mounted ? WinUiDrawerMotion.From(Context, AnimChannel.TranslateX, open ? -width : 0f) : target, target, ms,
            Easing.FluentPane, DepKey.From(open));
        _mounted = true;
        // Focus into the open pane, Tab trapped there; back where it was on close (a pinned pane leaves the page usable: no trap).
        UseLayoutEffect(() => _scope.Update(hooks, Context.Scene, Context.HostNode, modal), DepKey.From(modal));
        UseEffect(() => (Action?)(() => _scope.Lower(Context.Scene)), DepKey.Empty);

        return new BoxEl
        {
            // A column child that GROWS to the region's height: the anchor mirrors the grow, a stretch would not reach.
            Key = "drawer-pane", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
            // The resting position stays coupled to the live width: a closed pane can grow while resizing inside the band, and a stale static
            // translation would expose the added strip.
            Transform = Prop.Of(() => Affine2D.Translation(parts.RestingTranslateX(parts.IsOpen(), parts.Width(vp.Value.Width)), 0f)),
            Width = Prop.Of(() => parts.Width(vp.Value.Width)),
            // A stock OVERLAY PANE (NavigationView's minimal and compact overlay): in-app acrylic + flyout elevation.
            Fill = ColorF.Transparent, Acrylic = Tok.AcrylicFlyout,
            BorderWidth = WinUiNavDrawerRules.PaneBorderWidth, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
            Corners = WinUiNavDrawerRules.PaneCorners,
            Shadow = Elevation.Flyout, HitTestVisible = open,
            // Over the page, not inside it: the wheel over the pane's own rows scrolls the nav list (an ancestor), never the page.
            BlocksBackgroundScroll = true,
            Children = [parts.PaneBody()],
        };
    }
}

/// <summary>One in-frame overlay's focus trap: the keyboard half of a modal layer laid out in the frame. While open, focus moves into the surface (its first
/// tab stop, without the focus ring), Tab and Shift+Tab cycle inside it, and on close the scope is removed and focus goes back where it was.</summary>
internal sealed class WinUiDrawerScope
{
    InputHooks? _hooks;
    NodeHandle _root, _saved;
    bool _pushed;

    /// <summary>Raise the trap on <paramref name="root"/> while <paramref name="open"/>, lower it when not.</summary>
    public void Update(InputHooks hooks, SceneStore? scene, NodeHandle root, bool open)
    {
        if (open && !_pushed)
        {
            if (root.IsNull || scene is null || !scene.IsLive(root)) return;
            _hooks = hooks;
            _root = root;
            _pushed = true;
            _saved = hooks.GetFocus?.Invoke() ?? default;
            hooks.PushFocusScope?.Invoke(root);
            var first = hooks.FirstFocusableIn?.Invoke(root) ?? default;
            if (!first.IsNull) hooks.FocusNode?.Invoke(first, false);
        }
        else if (!open && _pushed) Lower(scene);
    }

    /// <summary>Lower the trap (the close edge, or the owner unmounting while open).</summary>
    public void Lower(SceneStore? scene)
    {
        if (!_pushed || _hooks is not { } hooks) return;
        _pushed = false;
        hooks.PopFocusScope?.Invoke(_root);
        if (!_saved.IsNull && scene is not null && scene.IsLive(_saved)) hooks.RestoreFocus?.Invoke(_saved);
        _saved = default;
        _root = default;
    }
}

/// <summary>The open/close motion of the drawer: a reversal starts where the surface is drawn.</summary>
internal static class WinUiDrawerMotion
{
    /// <summary>Where <paramref name="channel"/> of the component's rendered root is now: the running track's value, else <paramref name="rest"/> (the previous
    /// state's resting value), so a quick re-open mid-close continues instead of jumping.</summary>
    public static float From(RenderContext ctx, AnimChannel channel, float rest)
        => ctx.Anim is { } anim && !ctx.HostNode.IsNull && anim.TryGetTrackValue(ctx.HostNode, channel, out float v) ? v : rest;
}
