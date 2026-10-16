// ── Theming/Policies/PanelPolicy.cs — what a structure decides about the right panel and the mini player (plan 5.5, 3.2) ──
//
// The panel's state machine is core (`PanelHost`, `Shell.Ui.Rail*`): open, mode, width, docked, floating or a sheet, the docked
// video, the second docked pane. The structure supplies the bounds it lives in: how wide the panel may be, how it is placed in each
// WINDOW TIER (docked beside the page, floating over it or a bottom sheet; how many panels dock side by side; the least width a docked
// panel keeps in that tier), whether it spans the full window height, and the mini player's bounds.
//
// PER WINDOW TIER (plan 3.2: `PanelPolicy` is keyed by `WidthTier`; TC2). Docking follows the tier the window is in, never a fit test of
// pane + panel + content: the core reads `For(tier)` on the committed window tier, so a crossing that docks or undocks the panel lands on
// the crossing frame and animates (plan 3.3). A structure that declares no tier rules docks one panel in every tier.
//
// `Default` is the shell before themes: the bounds Wavee Classic declares, and no tier rules (one docked panel everywhere).

using System.Collections.Immutable;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>How the panel is placed in one window tier.</summary>
/// <param name="Placement">Docked, floating or a sheet.</param>
/// <param name="MaxDocked">How many panels dock side by side (1, or 2: the primary panel and the queue beside it). Only read where
/// <paramref name="Placement"/> is <see cref="PanelPlacement.Dock"/>.</param>
/// <param name="FloorW">The least width a docked panel keeps in this tier at <paramref name="FloorAt"/> and below (0: none, the user's
/// width alone).</param>
/// <param name="FloorAt">The window width up to which the floor is <paramref name="FloorW"/>.</param>
/// <param name="FloorMaxW">The floor at <paramref name="FloorMaxAt"/> and above; between the two widths it grows linearly (a fill rule).</param>
/// <param name="FloorMaxAt">The window width from which the floor is <paramref name="FloorMaxW"/> (at or below <paramref name="FloorAt"/>:
/// a flat floor).</param>
public readonly record struct PanelTierRule(
    PanelPlacement Placement, int MaxDocked = 1, float FloorW = 0f, float FloorAt = 0f, float FloorMaxW = 0f, float FloorMaxAt = 0f)
{
    /// <summary>One docked panel, no floor: the rule of a structure that declares none.</summary>
    public static readonly PanelTierRule Docked = new(PanelPlacement.Dock);

    /// <summary>The panel docks beside the page.</summary>
    public bool Docks => Placement == PanelPlacement.Dock;

    /// <summary>Two panels dock side by side.</summary>
    public bool DocksTwo => Docks && MaxDocked >= 2;

    /// <summary>The least width a docked panel keeps in a window <paramref name="viewportWidth"/> wide: 0 where the rule has no floor or
    /// the panel does not dock; the floor, growing linearly from <see cref="FloorAt"/> to <see cref="FloorMaxAt"/>, rounded to whole DIP.</summary>
    public float FloorFor(float viewportWidth)
    {
        if (!Docks || !(FloorW > 0f)) return 0f;
        if (!(FloorMaxAt > FloorAt) || !float.IsFinite(viewportWidth)) return FloorW;
        float t = Math.Clamp((viewportWidth - FloorAt) / (FloorMaxAt - FloorAt), 0f, 1f);
        return MathF.Round(FloorW + t * (FloorMaxW - FloorW));
    }
}

/// <summary>The right panel's policy.</summary>
/// <param name="MinWidth">The narrowest the panel may be dragged to or restored at.</param>
/// <param name="MaxWidth">The widest the panel may be dragged to or restored at.</param>
/// <param name="FullHeight">The panel spans the window's full height (above the player bar and the chrome) instead of the
/// content row. Read by the panel view (T6b); the state machine does not depend on it.</param>
/// <param name="Pip">The in-window mini player's bounds.</param>
public sealed record PanelPolicy(float MinWidth, float MaxWidth, bool FullHeight, Video.PipBounds Pip)
{
    /// <summary>The shell before themes: 200-500 DIP, content-row height, the 0.2.9 mini-player bounds (a rect restored from a 0.2.9
    /// profile must land identically), and no tier rules (one docked panel in every tier).</summary>
    public static PanelPolicy Default { get; } = new(Shell.RailMinW, Shell.RailMaxW, false, Video.PipBounds.Classic);

    /// <summary>The placement per window tier, Compact first (<see cref="WidthTier"/> order; Huge 4K reads the last rule given). Empty: one
    /// docked panel in every tier.</summary>
    public ImmutableArray<PanelTierRule> Tiers { get; init; } = ImmutableArray<PanelTierRule>.Empty;

    /// <summary>The rule of window tier <paramref name="tier"/>.</summary>
    public PanelTierRule For(WidthTier tier)
    {
        if (Tiers.IsDefaultOrEmpty) return PanelTierRule.Docked;
        return Tiers[Math.Clamp((int)tier, 0, Tiers.Length - 1)];
    }

    /// <summary>The rule of the tiers in <paramref name="facts"/>: Standard's where no frame resolved them.</summary>
    public PanelTierRule For(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The one clamp of a panel width.</summary>
    public float ClampWidth(float width)
    {
        float w = float.IsFinite(width) ? width : MinWidth;
        return w < MinWidth ? MinWidth : w > MaxWidth ? MaxWidth : w;
    }

    /// <summary>Value equality over the tier rules (an <see cref="ImmutableArray{T}"/> compares by reference otherwise).</summary>
    public bool Equals(PanelPolicy? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || MinWidth != other.MinWidth || MaxWidth != other.MaxWidth || FullHeight != other.FullHeight || !Pip.Equals(other.Pip))
            return false;
        bool emptyA = Tiers.IsDefaultOrEmpty, emptyB = other.Tiers.IsDefaultOrEmpty;
        if (emptyA || emptyB) return emptyA && emptyB;
        return Tiers.AsSpan().SequenceEqual(other.Tiers.AsSpan());
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MinWidth);
        hash.Add(MaxWidth);
        hash.Add(FullHeight);
        hash.Add(Pip);
        if (!Tiers.IsDefaultOrEmpty)
            foreach (var rule in Tiers) hash.Add(rule);
        return hash.ToHashCode();
    }

    /// <summary>Everything wrong with the policy, empty when it can drive the panel.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!float.IsFinite(MinWidth) || MinWidth <= 0f) problems.Add($"Panel.MinWidth must be positive and finite (was {MinWidth})");
        if (!float.IsFinite(MaxWidth) || MaxWidth <= 0f) problems.Add($"Panel.MaxWidth must be positive and finite (was {MaxWidth})");
        if (problems.Count == 0 && MinWidth > MaxWidth) problems.Add("Panel.MinWidth must not exceed MaxWidth");
        if (!Tiers.IsDefaultOrEmpty)
        {
            if (Tiers.Length > (int)WidthTier.Huge4K + 1) problems.Add($"Panel.Tiers holds {Tiers.Length} rules, at most {(int)WidthTier.Huge4K + 1} fit WidthTier");
            for (int i = 0; i < Tiers.Length; i++)
            {
                var r = Tiers[i];
                if (r.MaxDocked is < 1 or > 2) problems.Add($"Panel.Tiers[{i}].MaxDocked must be 1 or 2 (was {r.MaxDocked})");
                // A floor above MaxWidth is not an error: the width clamp brings it inside the bounds.
                if (!float.IsFinite(r.FloorW) || r.FloorW < 0f || !float.IsFinite(r.FloorMaxW) || r.FloorMaxW < 0f)
                    problems.Add($"Panel.Tiers[{i}]'s floor must be zero or more and finite");
            }
        }
        var pip = Pip;
        if (!float.IsFinite(pip.MinW) || pip.MinW <= 0f || !float.IsFinite(pip.MinH) || pip.MinH <= 0f)
            problems.Add("Panel.Pip.MinW and MinH must be positive and finite");
        if (!float.IsFinite(pip.ReserveBottom) || pip.ReserveBottom < 0f || !float.IsFinite(pip.ReserveTop) || pip.ReserveTop < 0f
            || !float.IsFinite(pip.Margin) || pip.Margin < 0f)
            problems.Add("Panel.Pip.ReserveBottom, ReserveTop and Margin must be zero or more and finite");
        return problems;
    }
}
