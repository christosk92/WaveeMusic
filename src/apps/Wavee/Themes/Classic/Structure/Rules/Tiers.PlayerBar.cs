// ── Themes/Classic/Structure/Rules/Tiers.PlayerBar.cs — the player bar's six pressure tiers (T3b) ─────────────────────
//
// Moved verbatim from `Shell.PlayerBar` (`Shell\Shell.cs`): the identity-first player dock's tiers, Minimal below 440 up to
// Full from 1240, over the BAR's own measured width (not the window's). "Widening commits immediately; narrowing holds the
// current presentation through a 24-DIP dip, so a pointer resize cannot chatter structural controls at a threshold": each
// edge is crossed upward at its width and downward once the width is 24 below it. The tier index is `Shell.PlayerBarTier`
// (Minimal = 0 ... Full = 5).

using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

internal static partial class ClassicTiers
{
    /// <summary>Compact from this bar width.</summary>
    public const float PlayerCompactW = 440f;

    /// <summary>Medium from this bar width.</summary>
    public const float PlayerMediumW = 760f;

    /// <summary>Comfortable from this bar width.</summary>
    public const float PlayerComfortableW = 900f;

    /// <summary>Wide from this bar width.</summary>
    public const float PlayerWideW = 1100f;

    /// <summary>Full from this bar width.</summary>
    public const float PlayerFullW = 1240f;

    /// <summary>How far below an edge the bar's width must fall before the edge is crossed downward.</summary>
    public const float PlayerNarrowHysteresis = 24f;

    /// <summary>The player bar's ladder: five edges, six tiers.</summary>
    public static TierLadder PlayerBar { get; } = TierLadder.Of(
        new TierEdge(PlayerCompactW, NarrowHold: PlayerNarrowHysteresis),
        new TierEdge(PlayerMediumW, NarrowHold: PlayerNarrowHysteresis),
        new TierEdge(PlayerComfortableW, NarrowHold: PlayerNarrowHysteresis),
        new TierEdge(PlayerWideW, NarrowHold: PlayerNarrowHysteresis),
        new TierEdge(PlayerFullW, NarrowHold: PlayerNarrowHysteresis));
}
