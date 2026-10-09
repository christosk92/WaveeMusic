// ── Shell/Sidebar.Resize.cs ────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's pane modes and seam rules — one pure rule class each; engine-free
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P2.1
//
// The pane reads the viewport only: the window's width band picks the mode (Expanded, Compact rail, Minimal drawer),
// and the user's collapse decides only in the Wide band. Forced modes never write the user's collapse. Everything here
// is engine-free — no signal, no clock, no Element — so Wavee.Tests drives every branch. `public` because Wavee.Tests
// is a ProjectReference with no InternalsVisibleTo.

namespace Wavee;

/// <summary>What the pane presents (WinUI NavigationView's display modes): Expanded (inline, pushes content —
/// CompactInline), Compact (the 48-px rail, inline), Minimal (no rail; the toggle opens the overlay drawer).</summary>
public enum SidebarPaneMode : byte { Expanded = 0, Compact = 1, Minimal = 2 }

/// <summary>The window's width band, hysteretic. Wide: the user's collapse decides. Narrow (528 ≤ w &lt; 660, leave at 700):
/// a FORCED rail whose toggle opens the overlay pane. Tiny (w &lt; 528 = 48 + 480, leave at 568): no rail, the drawer.</summary>
public enum SidebarWindowBand : byte { Wide = 0, Narrow = 1, Tiny = 2 }

/// <summary>THE MODE RULES (design V.1). The pane reads the viewport ONLY; the right rail reads the pane's presented width
/// (never the reverse), so opening the rail or docking a video never moves the sidebar. A forced mode never writes the
/// user's collapse (WinUI's <c>m_wasForceClosed</c>, NavigationView.cpp:1491, 1658-1670): when the window grows back the
/// user's own state returns. Edit mode presents Expanded whatever <c>userCollapsed</c> says.</summary>
public static class SidebarPaneModeRules
{
    /// <summary>The content floor (<c>Shell.MinContentW</c>).</summary>
    public const float ContentFloorW = 480f;
    public const float NarrowEnterW = 660f;   // below: forced rail
    public const float WideEnterW = 700f;     // the Narrow → Wide edge (hysteresis 40)
    public const float TinyEnterW = 528f;     // below: no rail (48 + 480)
    public const float TinyLeaveW = 568f;     // the Tiny → Narrow edge (hysteresis 40)

    /// <summary>The next band from the viewport width and the current band. A non-positive or non-finite width (a
    /// minimized window) never moves it.</summary>
    public static SidebarWindowBand BandOf(float viewportW, SidebarWindowBand current)
    {
        if (!float.IsFinite(viewportW) || viewportW <= 0f) return current;
        return current switch
        {
            SidebarWindowBand.Wide => viewportW >= NarrowEnterW ? SidebarWindowBand.Wide
                : viewportW >= TinyEnterW ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
            SidebarWindowBand.Narrow => viewportW >= WideEnterW ? SidebarWindowBand.Wide
                : viewportW >= TinyEnterW ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
            _ => viewportW >= WideEnterW ? SidebarWindowBand.Wide
                : viewportW >= TinyLeaveW ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
        };
    }

    /// <summary>The first band of a launch (no history): asked from Wide, so only the entering thresholds apply (a fresh
    /// launch at 600 is Narrow, at 500 Tiny).</summary>
    public static SidebarWindowBand InitialBand(float viewportW) => BandOf(viewportW, SidebarWindowBand.Wide);

    /// <summary>① The mode. Narrow ⇒ Compact and Tiny ⇒ Minimal whatever the user or Edit mode wants (Edit then pins the
    /// OVERLAY open instead); Wide ⇒ the user's collapse, unless editing. <paramref name="paneHidden"/> is the Zune style:
    /// it presents no pane in any band (Minimal).</summary>
    public static SidebarPaneMode Resolve(SidebarWindowBand band, bool userCollapsed, bool editing, bool paneHidden = false)
        => paneHidden ? SidebarPaneMode.Minimal : band switch
        {
            SidebarWindowBand.Tiny => SidebarPaneMode.Minimal,
            SidebarWindowBand.Narrow => SidebarPaneMode.Compact,
            _ => editing || !userCollapsed ? SidebarPaneMode.Expanded : SidebarPaneMode.Compact,
        };

    /// <summary>② The inline column's width: Expanded clamps the preference to [180, 460] and to the content floor;
    /// Compact is the 48 rail; Minimal is nothing.</summary>
    public static float PresentedWidth(SidebarPaneMode mode, float preferredW, float viewportW) => mode switch
    {
        SidebarPaneMode.Expanded => Math.Clamp(
            float.IsFinite(viewportW) && viewportW > 0f ? MathF.Min(preferredW, viewportW - ContentFloorW) : preferredW,
            SidebarPaneBounds.NavPaneMinW, SidebarPaneBounds.NavPaneMaxW),
        SidebarPaneMode.Compact => SidebarRowGeometry.RailWidth,
        _ => 0f,
    };

    /// <summary>The overlay pane's width (the drawer in Minimal, the pane opened over a forced rail in Narrow): the
    /// preference, leaving at least the 48-px rail's worth of page visible.</summary>
    public static float OverlayWidth(float preferredW, float viewportW)
        => MathF.Max(0f, MathF.Min(SidebarPaneBounds.Clamp(preferredW), viewportW - SidebarRowGeometry.RailWidth));

    /// <summary>Does a toggle / seam / double-click write <c>sidebar.pane.userCollapsed</c>? Only in the Wide band and
    /// never while editing.</summary>
    public static bool WritesUserCollapsed(SidebarWindowBand band, bool editing) => band == SidebarWindowBand.Wide && !editing;

    /// <summary>Is there an overlay pane to open (the toggle's job in Narrow and Tiny)?</summary>
    public static bool HasOverlay(SidebarWindowBand band) => band != SidebarWindowBand.Wide;

    /// <summary>Edit mode outside the Wide band pins the overlay open (no light dismiss, no leaf-invoke close).</summary>
    public static bool OverlayPinned(SidebarWindowBand band, bool editing) => editing && band != SidebarWindowBand.Wide;

    /// <summary>The seam exists only in the Wide band (a forced rail and the drawer have no width to drag).</summary>
    public static bool SeamVisible(SidebarWindowBand band) => band == SidebarWindowBand.Wide;

    /// <summary>A leaf navigation closes an OVERLAY pane (NavigationView.cpp:4840-4846), never the inline one, and never
    /// while editing.</summary>
    public static bool LeafInvokeClosesOverlay(bool editing) => !editing;

    /// <summary>③ The right rail fits inline beside the presented pane and the content floor.</summary>
    public static bool RailFits(float presentedW, float railW, float viewportW)
        => presentedW + railW + ContentFloorW <= viewportW;
}

/// <summary>THE SEAM RULES (design V.1 "Seam"): drag resizes 180-460; below 116 the pane collapses to the rail (writes
/// the user's collapse); from the rail, past 140 it expands; while editing it resizes but never flips (clamped at 180).
/// Double-click / Enter toggles; ←/→ step 8 (Shift 40). No detents, no flick.</summary>
public static class SidebarResizeRules
{
    public const float ExpandedMinW = SidebarPaneBounds.NavPaneMinW;             // 180
    public const float ExpandedMaxW = SidebarPaneBounds.NavPaneMaxW;             // 460
    public const float RegimePush = 64f;
    public const float RegimeHysteresis = SidebarPaneBounds.NavPaneHysteresisDip; // 24
    public const float MinFade = 0.35f;
    public const float NudgeW = 8f;
    public const float NudgeLargeW = 40f;
    public static float RailEnterW => ExpandedMinW - RegimePush;                  // 116
    public static float ExpandedEnterW => RailEnterW + RegimeHysteresis;          // 140

    /// <summary>The user's stored facts.</summary>
    public readonly record struct State(bool UserCollapsed, float ExpandedWidth);
    /// <summary>What the frame presents mid-drag.</summary>
    public readonly record struct Live(bool Collapsed, float PresentedWidth, float Fade);
    /// <summary>The release verdict; <see cref="TargetWidth"/> is what the column animates to.</summary>
    public readonly record struct Settle(bool UserCollapsed, float ExpandedWidth, float TargetWidth);

    public static Live Track(float raw, in State prev, bool editing)
    {
        if (!float.IsFinite(raw))
            return new(prev.UserCollapsed, prev.UserCollapsed ? SidebarRowGeometry.RailWidth : prev.ExpandedWidth, 1f);
        bool collapsed = !editing && (prev.UserCollapsed ? raw < ExpandedEnterW : raw < RailEnterW);
        if (collapsed) return new(true, SidebarRowGeometry.RailWidth, 1f);
        float presented = Math.Clamp(raw, ExpandedMinW, ExpandedMaxW);
        float fade = editing || raw >= ExpandedMinW ? 1f : SplitterFade(ExpandedMinW - raw, RegimePush);
        return new(false, presented, fade);
    }

    public static Settle Resolve(float raw, in State prev, bool editing)
    {
        var live = Track(raw, in prev, editing);
        return live.Collapsed
            ? new(true, prev.ExpandedWidth, SidebarRowGeometry.RailWidth)
            : new(false, live.PresentedWidth, live.PresentedWidth);
    }

    public static Settle Step(in State s, int direction, bool large, bool editing)
    {
        if (s.UserCollapsed)
        {
            if (direction <= 0 || editing) return new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);
            float back = Math.Clamp(s.ExpandedWidth, ExpandedMinW, ExpandedMaxW);
            return new(false, back, back);
        }
        float next = s.ExpandedWidth + direction * (large ? NudgeLargeW : NudgeW);
        if (!editing && next < ExpandedMinW && s.ExpandedWidth <= ExpandedMinW)
            return new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);
        float w = Math.Clamp(next, ExpandedMinW, ExpandedMaxW);
        return new(false, w, w);
    }

    public static Settle Toggle(in State s) => s.UserCollapsed
        ? new(false, s.ExpandedWidth, Math.Clamp(s.ExpandedWidth, ExpandedMinW, ExpandedMaxW))
        : new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);

    /// <summary>SplitterMath.Fade restated (CORE must not reference FluentGpu.Controls): 1 at 0, MinFade at <paramref name="over"/>.</summary>
    static float SplitterFade(float into, float over)
    {
        if (over <= 0f) return 1f;
        float t = Math.Clamp(into / over, 0f, 1f);
        return 1f - t * (1f - MinFade);
    }
}
