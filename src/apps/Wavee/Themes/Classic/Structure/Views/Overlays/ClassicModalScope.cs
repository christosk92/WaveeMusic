// ── Themes/Classic/Structure/Views/Overlays/ClassicModalScope.cs — the keyboard half of a Classic in-frame overlay ──────────────────
//
// The engine's overlays (a dialog, a menu, a flyout card, the context sheet) push their own focus trap. The frame's own overlays (the Compact
// drawer and the panel's bottom sheet) are laid out in the frame, so they take the same contract here, while they are open:
//
//   · focus moves into the surface (its first tab stop, without the focus ring: the overlay was opened, not tabbed to), so the keys go to it:
//     PageDown and the arrows scroll the surface's own list, never the page under it;
//   · Tab and Shift+Tab cycle inside the surface (a dispatcher focus scope rooted at it);
//   · on close the scope is removed and focus goes back where it was, if that node still lives.
//
// The pointer half (the scrim, the wheel) is the surface's own: `BlocksBackgroundScroll` on the scrim and the surface keeps the wheel off the
// page behind it.

using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace Wavee.Themes.Classic;

/// <summary>One in-frame overlay's focus trap (see the file header). Owned by the overlay's component; <see cref="Update"/> runs in a layout
/// effect once the surface is laid out.</summary>
internal sealed class ClassicModalScope
{
    InputHooks? _hooks;
    NodeHandle _root, _saved;
    bool _pushed;

    /// <summary>Is the trap up.</summary>
    public bool Active => _pushed;

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

/// <summary>The open/close motion of an in-frame overlay: a reversal starts where the surface is drawn.</summary>
internal static class ClassicOverlayMotion
{
    /// <summary>Where <paramref name="channel"/> of the component's rendered root is now: the running track's value, else
    /// <paramref name="rest"/> (the previous state's resting value), so a quick re-open mid-close continues instead of jumping.</summary>
    public static float From(RenderContext ctx, FluentGpu.Animation.AnimChannel channel, float rest)
        => ctx.Anim is { } anim && !ctx.HostNode.IsNull && anim.TryGetTrackValue(ctx.HostNode, channel, out float v) ? v : rest;
}
