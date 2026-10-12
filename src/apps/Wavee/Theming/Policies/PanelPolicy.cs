// ── Theming/Policies/PanelPolicy.cs — what a structure decides about the right panel and the mini player (plan 5.5) ──
//
// The panel's state machine is core (`PanelHost`, `Shell.Ui.Rail*`): open, mode, width, floating or inline, the docked
// video. The structure supplies the bounds it lives in: how wide the panel may be, how much content it must leave to stay
// inline (below that it floats over the page), whether it spans the full window height, and the mini player's bounds.
// `Default` is the shell before themes (what Wavee Classic declares).

namespace Wavee.Theming;

/// <summary>The right panel's policy.</summary>
/// <param name="MinWidth">The narrowest the panel may be dragged to or restored at.</param>
/// <param name="MaxWidth">The widest the panel may be dragged to or restored at.</param>
/// <param name="InlineMin">The content width beside the nav pane and the panel that keeps the panel inline; below it the
/// panel floats over the page.</param>
/// <param name="FullHeight">The panel spans the window's full height (above the player bar and the chrome) instead of the
/// content row. Read by the panel view (T6b); the state machine does not depend on it.</param>
/// <param name="Pip">The in-window mini player's bounds.</param>
public sealed record PanelPolicy(float MinWidth, float MaxWidth, float InlineMin, bool FullHeight, Video.PipBounds Pip)
{
    /// <summary>The shell before themes: 200-500 DIP, inline while 480 DIP of content remains, content-row height, the
    /// 0.2.9 mini-player bounds (a rect restored from a 0.2.9 profile must land identically).</summary>
    public static PanelPolicy Default { get; } = new(Shell.RailMinW, Shell.RailMaxW, Shell.MinContentW, false, Video.PipBounds.Classic);

    /// <summary>The one clamp of a panel width.</summary>
    public float ClampWidth(float width)
    {
        float w = float.IsFinite(width) ? width : MinWidth;
        return w < MinWidth ? MinWidth : w > MaxWidth ? MaxWidth : w;
    }

    /// <summary>Everything wrong with the policy, empty when it can drive the panel.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!float.IsFinite(MinWidth) || MinWidth <= 0f) problems.Add($"Panel.MinWidth must be positive and finite (was {MinWidth})");
        if (!float.IsFinite(MaxWidth) || MaxWidth <= 0f) problems.Add($"Panel.MaxWidth must be positive and finite (was {MaxWidth})");
        if (!float.IsFinite(InlineMin) || InlineMin < 0f) problems.Add($"Panel.InlineMin must be zero or more and finite (was {InlineMin})");
        if (problems.Count == 0 && MinWidth > MaxWidth) problems.Add("Panel.MinWidth must not exceed MaxWidth");
        var pip = Pip;
        if (!float.IsFinite(pip.MinW) || pip.MinW <= 0f || !float.IsFinite(pip.MinH) || pip.MinH <= 0f)
            problems.Add("Panel.Pip.MinW and MinH must be positive and finite");
        if (!float.IsFinite(pip.ReserveBottom) || pip.ReserveBottom < 0f || !float.IsFinite(pip.ReserveTop) || pip.ReserveTop < 0f
            || !float.IsFinite(pip.Margin) || pip.Margin < 0f)
            problems.Add("Panel.Pip.ReserveBottom, ReserveTop and Margin must be zero or more and finite");
        return problems;
    }
}
