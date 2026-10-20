// ── Theming/Policies/TouchTargetPolicy.cs — the hit floor the touch tier gives every click target ────────────────────────────────────────
//
// Owner rule: Compact targets are >= 40 DIP. Many targets are drawn smaller on purpose (a 28 glyph button, a 32 pill, a 24 tree "+"): the
// engine's hit floor (`InputDispatcher.MinHitSize`, wired through `InputHooks.SetMinHitSize`) lets each take hits out to the floor, centred
// on its box, without moving layout or paint; a clipping ancestor cuts it and a real hit always wins over it. The shell root writes the floor
// whenever the window tier changes. Only Compact has one: from Small up the mouse targets keep their drawn boxes.

using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>The hit floor per window tier.</summary>
public static class TouchTargetPolicy
{
    /// <summary>The Compact touch floor (DIP): the owner's "Compact targets >= 40".</summary>
    public const float CompactFloor = 40f;

    /// <summary>The hit floor a window of <paramref name="window"/> gives its click targets (0 = none).</summary>
    public static float FloorFor(WidthTier window) => window == WidthTier.Compact ? CompactFloor : 0f;

    /// <summary>The gap between a pager's chevrons and its pips at a <paramref name="window"/>: <paramref name="rest"/>, and 8 at Compact, where a 28 chevron
    /// on the page's edge (a scroll bar's lane beyond it) can only take the floor's width from that gap.</summary>
    public static float PagerGap(WidthTier window, float rest) => window == WidthTier.Compact ? MathF.Max(rest, CompactPagerGap) : rest;

    /// <summary>The Compact gap between a pager's chevrons and its pips.</summary>
    public const float CompactPagerGap = 8f;

    /// <summary>The floor's share of a <paramref name="box"/>-wide control, all on ONE side (<paramref name="left"/> or right): for a control whose other side
    /// cannot take it (a pager's chevron beside its pips, whose real hit wins there; a control on its page's edge, where the page's clip cuts it).</summary>
    public static FluentGpu.Foundation.Edges4 OneSide(float box, bool left)
    {
        float reach = MathF.Max(0f, CompactFloor - box);
        return left ? new(reach, 0f, 0f, 0f) : new(0f, 0f, reach, 0f);
    }
}
