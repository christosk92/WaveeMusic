// ── Theming/Policies/ThemePolicies.cs — what a structure decides for the core (themes plan 5.5) ───────────────────────
//
// T1 declares the SHAPE the rest of the plan hangs on and the values a theme that decides nothing yet returns:
//   · `ThemePolicies`      the policy bundle a structure returns. T3 fills it (tiers, nav pane, panel, player bar, chrome,
//                          stage) and re-points the core consumers; until then it is empty and no core code reads it.
//   · `ShellCapabilities`  what the shell offers. `Unrestricted` is "every feature on", which is what Classic is today.
//   · `StyleMetrics`       the style's row heights and gutters. T2/T3 fill it from the role table; `Unset` (all zero)
//                          means "no metric published yet" and no consumer reads one in T1.
//   · `TierFacts`          the window-tier fact (T3b): `Theming/Policies/TierFacts.cs`, with `TierPolicy` beside it.
//   · `ThemeFacts`         the bundle `ThemeHost.Facts` publishes and `ThemeFactsContext` mirrors for templates.

using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming;

/// <summary>The policy bundle of the active structure (T3 adds the members of plan 5.5: tiers, nav pane, panel, player bar,
/// chrome, stage). Equality is by value, so an unchanged bundle republished is not a change.</summary>
public sealed record ThemePolicies
{
    /// <summary>The bundle of a structure that has no policies yet.</summary>
    public static ThemePolicies Empty { get; } = new();

    /// <summary>The structure's window tiers (T3b). A structure that declares none is Standard at every size.</summary>
    public TierPolicy Tiers { get; init; } = TierPolicy.Standard;
}

/// <summary>What the shell offers under the active structure.</summary>
/// <param name="Tabs">The tab strip.</param>
/// <param name="NavTree">The nested navigation tree in the nav pane.</param>
/// <param name="FriendsPanel">The friends panel.</param>
/// <param name="ForwardButton">A forward button next to back.</param>
/// <param name="HistoryMenu">The back/forward history menu.</param>
/// <param name="MaxBandPins">How many pins the nav band holds (<see cref="int.MaxValue"/> = unlimited).</param>
public sealed record ShellCapabilities(bool Tabs, bool NavTree, bool FriendsPanel, bool ForwardButton, bool HistoryMenu, int MaxBandPins)
{
    /// <summary>Every capability on and no pin limit: what the shell is today (Classic).</summary>
    public static ShellCapabilities Unrestricted { get; } = new(true, true, true, true, true, int.MaxValue);
}

/// <summary>The style's metrics: the row heights, the gutters and the control height a structure lays out with. A value
/// type, so a style hands them out without allocating.</summary>
public readonly record struct StyleMetrics(
    float RowSingle, float RowDouble, float RowWithArt, float ControlHeight,
    float PivotRow, float SubPivotRow, float SeekRow, float TransportRow, float SearchWidth,
    float Gutter, float Section, float MinPageColumn, float PanoramaCompactBelow)
{
    /// <summary>No metric published (every member 0). T2/T3 replace it with the Classic values taken from <c>Design</c>.</summary>
    public static StyleMetrics Unset => default;
}

/// <summary>What the active composition publishes for the core: policies, capabilities, metrics and tiers. Core models
/// read <see cref="ThemeHost.Facts"/> (a signal); templates read <see cref="ThemeFactsContext"/>.</summary>
public sealed record ThemeFacts(ThemePolicies Policies, ShellCapabilities Capabilities, StyleMetrics Metrics, TierFacts Tiers)
{
    /// <summary>The facts before any composition is installed.</summary>
    public static ThemeFacts Default { get; } = new(ThemePolicies.Empty, ShellCapabilities.Unrestricted, StyleMetrics.Unset, TierFacts.Unknown);
}

/// <summary>The context that carries <see cref="ThemeHost.Facts"/> to templates. The value is the SIGNAL, not a snapshot: the
/// provider sits at the root, which renders once per theme switch, so a snapshot would go stale on every live change.
/// A template reads <c>ctx.Hooks.UseContext(ThemeFactsContext.Signal).Value</c> (the read subscribes it).</summary>
public static class ThemeFactsContext
{
    /// <summary>The provided signal. Null only where no root provides it (a bare unit test of a view).</summary>
    public static readonly Context<IReadSignal<ThemeFacts>?> Signal = new(null);
}
