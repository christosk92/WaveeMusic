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
// THE PANE ARRANGEMENT (contract 1.29, P5 ZT2). Where a tier docks more than one pane, the rule says WHICH bodies dock beside the
// primary panel and in what order (`PanelTierRule.Panes`, a `PanelArrangement`): up to two docked panes, each a fixed body on one side of
// the primary, either INDEPENDENT (Classic Huge: the queue right of the primary, opened and closed on its own through the remembered
// "queue beside" flag) or LINKED (Zune Huge: queue | lyrics · friends; Zune Huge 4K: queue | lyrics | friends: the panes open and close
// with the panel as one, and the primary steps aside to the arrangement's fallback body when it would show a docked pane's body).
// `MaxDocked` caps the arrangement (a structure option such as Zune's `maxDockedPanes` lowers it); a docking rule with `MaxDocked` 2 and
// no arrangement docks the queue beside the primary, exactly as before the arrangement existed.
//
// `Default` is the shell before themes: the bounds Wavee Classic declares, and no tier rules (one docked panel everywhere).

using System.Collections.Immutable;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>One docked pane beside the primary panel: the body it always shows and the side of the primary it sits on.</summary>
/// <param name="Mode">The pane's one body.</param>
/// <param name="Side">Right of the primary (<see cref="PaneSide.After"/>, Classic's queue) or left of it (<see cref="PaneSide.Before"/>,
/// Zune's queue).</param>
public readonly record struct DockedPane(PanelMode Mode, PaneSide Side = PaneSide.After)
{
    /// <summary>The other bodies this pane offers as TABS beside <see cref="Mode"/>, its first tab (P6 WK; the WinUI board's Huge: Queue + Now playing |
    /// Lyrics + Friends, each docked pane its own tab group). Empty (the default): the pane shows <see cref="Mode"/> alone, as before. Only a LINKED
    /// arrangement may give a pane tabs. Appended (P6 WK).</summary>
    public PanelModeSet Tabs { get; init; }

    /// <summary>Every body the pane may show: <see cref="Mode"/> and its <see cref="Tabs"/>.</summary>
    public PanelModeSet Bodies => Tabs.With(Mode);

    /// <summary>The pane offers more than one body (a tab group of its own).</summary>
    public bool HasTabs => Bodies.Bits != default(PanelModeSet).With(Mode).Bits;

    /// <summary>The body the pane shows for the tab the user picked (<paramref name="chosen"/>, a <see cref="PanelMode"/> value, or -1 for none): the
    /// pick where the pane offers it, else <see cref="Mode"/>.</summary>
    public PanelMode BodyFor(int chosen) => chosen >= 0 && chosen < 8 && Bodies.Contains((PanelMode)chosen) ? (PanelMode)chosen : Mode;
}

/// <summary>The panes a window tier docks beside the primary panel (contract 1.29).</summary>
/// <param name="Second">The second pane (null: none).</param>
/// <param name="Third">The third pane (null: none; never without a second).</param>
/// <param name="Linked">The docked panes open and close with the primary panel as one (Zune), instead of on their own (Classic's queue,
/// kept in the remembered "queue beside" flag).</param>
/// <param name="Fallback">Where the panes are linked: the body the primary shows instead when its mode is a docked pane's (the body is never
/// on screen twice). Never a docked pane's own body.</param>
public readonly record struct PanelArrangement(DockedPane? Second = null, DockedPane? Third = null, bool Linked = false,
    PanelMode Fallback = PanelMode.Lyrics)
{
    /// <summary>Classic's arrangement (D3): the queue, independent, right of the primary. What a docking rule with two panes and no
    /// arrangement of its own docks.</summary>
    public static PanelArrangement QueueBeside { get; } = new(new DockedPane(PanelMode.Queue));

    /// <summary>How many panes dock beside the primary (0, 1 or 2).</summary>
    public int Count => Second is null ? 0 : Third is null ? 1 : 2;

    /// <summary>The pane at <paramref name="index"/> (0 the second pane, 1 the third), or null.</summary>
    public DockedPane? At(int index) => index switch { 0 => Second, 1 => Third, _ => null };

    /// <summary>A docked pane shows <paramref name="mode"/>.</summary>
    public bool Holds(PanelMode mode) => (Second is { } s && s.Bodies.Contains(mode)) || (Third is { } t && t.Bodies.Contains(mode));

    /// <summary>The index of the docked pane that offers <paramref name="mode"/> (0 the second pane, 1 the third), or -1 (P6 WK).</summary>
    public int IndexHolding(PanelMode mode) => Second is { } s && s.Bodies.Contains(mode) ? 0 : Third is { } t && t.Bodies.Contains(mode) ? 1 : -1;

    /// <summary>The bodies the docked panes show.</summary>
    public PanelModeSet Modes
    {
        get
        {
            var set = default(PanelModeSet);
            if (Second is { } s) set = new PanelModeSet((byte)(set.Bits | s.Bodies.Bits));
            if (Third is { } t) set = new PanelModeSet((byte)(set.Bits | t.Bodies.Bits));
            return set;
        }
    }

    /// <summary>The body the primary presents for its mode <paramref name="primary"/>: the fallback where the panes are linked and one of
    /// them already shows it, else the mode itself.</summary>
    public PanelMode Presented(PanelMode primary) => Linked && Holds(primary) ? Fallback : primary;

    /// <summary>The arrangement with at most <paramref name="count"/> docked panes (the nearest kept: the second before the third).</summary>
    public PanelArrangement Capped(int count) => count <= 0 ? default : count == 1 ? this with { Third = null } : this;
}

/// <summary>How the panel is placed in one window tier.</summary>
/// <param name="Placement">Docked, floating or a sheet.</param>
/// <param name="MaxDocked">How many panes dock side by side: 1, 2 or 3 (the primary panel and up to two panes of <see cref="PanelTierRule.Panes"/>).
/// Only read where <paramref name="Placement"/> is <see cref="PanelPlacement.Dock"/>.</param>
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

    /// <summary>The panes this tier may dock beside the primary, in their order (contract 1.29). Default: none declared, which docks
    /// Classic's queue beside the primary where <see cref="MaxDocked"/> is 2 or more.</summary>
    public PanelArrangement Panes { get; init; }

    /// <summary>The panel docks beside the page.</summary>
    public bool Docks => Placement == PanelPlacement.Dock;

    /// <summary>The panes that dock beside the primary in this tier: <see cref="Panes"/> (Classic's queue where none is declared), capped by
    /// <see cref="MaxDocked"/>; none where the tier does not dock or docks one.</summary>
    public PanelArrangement Arrangement
        => !Docks || MaxDocked < 2 ? default : (Panes.Count == 0 ? PanelArrangement.QueueBeside : Panes).Capped(MaxDocked - 1);

    /// <summary>Two panes (or more) dock side by side.</summary>
    public bool DocksTwo => Arrangement.Count >= 1;

    /// <summary>Three panes dock side by side.</summary>
    public bool DocksThree => Arrangement.Count >= 2;

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

    /// <summary>The arrangement of the widest tier that docks a pane at <paramref name="index"/> (a pane keeps its body, side and link while
    /// it slides out on a crossing to a tier without it); Classic's queue beside the primary where no tier docks one there.</summary>
    public PanelArrangement ArrangementAt(int index)
    {
        if (!Tiers.IsDefaultOrEmpty)
            for (int i = Tiers.Length - 1; i >= 0; i--)
                if (Tiers[i].Arrangement.At(index) is not null) return Tiers[i].Arrangement;
        return PanelArrangement.QueueBeside;
    }

    /// <summary>The docked pane at <paramref name="index"/> in <see cref="ArrangementAt"/>; Classic's queue where no tier docks one.</summary>
    public DockedPane PaneAt(int index) => ArrangementAt(index).At(index) ?? new DockedPane(PanelMode.Queue);

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
                int declared = r.Panes.Count == 0 ? 1 : r.Panes.Count;   // no arrangement: Classic's queue beside
                if (r.MaxDocked is < 1 or > 3) problems.Add($"Panel.Tiers[{i}].MaxDocked must be 1, 2 or 3 (was {r.MaxDocked})");
                else if (r.Docks && r.MaxDocked > 1 + declared)
                    problems.Add($"Panel.Tiers[{i}].MaxDocked is {r.MaxDocked} but its arrangement docks at most {1 + declared} panes");
                ValidatePanes(r.Panes, i, problems);
                // A floor above MaxWidth is not an error: the docked width honours it (Zune docks its 340 panel at 360 at Huge, ZT2).
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

    /// <summary>An arrangement's problems: a third pane needs a second, a body docks once, the video never docks beside (its one card lives
    /// in the primary), an independent pane is the queue (the one remembered "queue beside" flag holds it, so there is one), and a linked
    /// arrangement's fallback is not a docked pane's body.</summary>
    private static void ValidatePanes(PanelArrangement panes, int i, List<string> problems)
    {
        if (panes.Second is null && panes.Third is not null) problems.Add($"Panel.Tiers[{i}].Panes has a third pane but no second");
        if (panes.Second is { } a && panes.Third is { } b && (a.Bodies.Bits & b.Bodies.Bits) != 0)
            problems.Add($"Panel.Tiers[{i}].Panes docks a body twice ({a.Mode} | {b.Mode} and their tabs)");
        if (panes.Holds(PanelMode.Video)) problems.Add($"Panel.Tiers[{i}].Panes docks the video beside the primary (its card lives in the primary)");
        if (panes.Count == 0) return;
        if (!panes.Linked && (panes.Count > 1 || panes.Second is not { Mode: PanelMode.Queue, HasTabs: false }))
            problems.Add($"Panel.Tiers[{i}].Panes: an independent docked pane is the queue alone; link the panes to dock others");
        if (panes.Linked && panes.Holds(panes.Fallback))
            problems.Add($"Panel.Tiers[{i}].Panes' fallback {panes.Fallback} is a docked pane's body");
    }
}
