// ── Shell/Sidebar.Resize.cs ────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's pane modes and seam rules — one pure rule class each; engine-free
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P2.1
//
// The pane reads the window only: the window's width TIER picks the band (T3b: the structure's window ladder, with its
// hysteresis, decides where the bands begin; Classic's 528 / 568 and 660 / 700 live in `Themes\Classic\Structure\Rules\Tiers.cs`),
// the band picks the mode (Expanded, Compact rail, Minimal drawer), and the user's collapse decides only in the Wide band.
// Forced modes never write the user's collapse. Everything here is engine-free — no signal, no clock, no Element — so
// Wavee.Tests drives every branch. `public` because Wavee.Tests is a ProjectReference with no InternalsVisibleTo.
//
// THE NUMBERS ARE THE STRUCTURE'S (themes plan 5.5, T3). Every rule takes a trailing `NavPanePolicy? policy`: the content
// floor, the width bounds, what each band forces and whether the pane is resizable (the band EDGES are the window tiers, not
// the policy's). Null answers for the ACTIVE policy (`ShellPolicy.Now`), so a caller that does not care gets the structure's;
// an effect or a render that must re-run when the policy changes passes `ShellPolicy.Tracked.NavPane`. The consts below are
// the shell's baseline, which `NavPanePolicy.Default` (what Wavee Classic declares) is built from; no rule reads them.

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>What the pane presents (WinUI NavigationView's display modes): Expanded (inline, pushes content —
/// CompactInline), Compact (the 48-px rail, inline), Minimal (no rail; the toggle opens the overlay drawer).</summary>
public enum SidebarPaneMode : byte { Expanded = 0, Compact = 1, Minimal = 2 }

/// <summary>The window's width band, the sidebar's reading of the window <see cref="WidthTier"/>. Wide (Standard and up): the
/// user's collapse decides. Narrow (Small): a FORCED rail whose toggle opens the overlay pane. Tiny (Compact): no rail, the
/// drawer. Where each begins is the structure's window ladder (Classic: Narrow from 528, leaving Tiny at 568; Wide from 660,
/// leaving Narrow at 700). What each band forces is the policy's (<see cref="NavPanePolicy.NarrowMode"/>,
/// <see cref="NavPanePolicy.TinyMode"/>).</summary>
public enum SidebarWindowBand : byte { Wide = 0, Narrow = 1, Tiny = 2 }

/// <summary>THE MODE RULES (design V.1). The pane reads the viewport ONLY; the right rail reads the pane's presented width
/// (never the reverse), so opening the rail or docking a video never moves the sidebar. A forced mode never writes the
/// user's collapse (WinUI's <c>m_wasForceClosed</c>, NavigationView.cpp:1491, 1658-1670): when the window grows back the
/// user's own state returns. Edit mode presents Expanded whatever <c>userCollapsed</c> says.</summary>
public static class SidebarPaneModeRules
{
    /// <summary>The shell's baseline content floor (<c>Shell.MinContentW</c>).</summary>
    public const float ContentFloorW = 480f;

    /// <summary>The band of a window width tier: Compact is Tiny, Small is Narrow, Standard and up are Wide. The hysteresis is
    /// the window ladder's (a tier already carries it), so this is a pure mapping.</summary>
    public static SidebarWindowBand BandOf(WidthTier window) => window switch
    {
        WidthTier.Compact => SidebarWindowBand.Tiny,
        WidthTier.Small => SidebarWindowBand.Narrow,
        _ => SidebarWindowBand.Wide,
    };

    /// <summary>① The mode. What a band forces (Narrow ⇒ Compact and Tiny ⇒ Minimal in Classic) wins whatever the user or Edit
    /// mode wants (Edit then pins the OVERLAY open instead); a band that forces nothing ⇒ the user's collapse, unless
    /// editing.</summary>
    public static SidebarPaneMode Resolve(SidebarWindowBand band, bool userCollapsed, bool editing, NavPanePolicy? policy = null)
        => ShellPolicy.NavOr(policy).ForcedModeOf(band) switch
        {
            NavPaneMode.Compact => SidebarPaneMode.Compact,
            NavPaneMode.Overlay or NavPaneMode.None => SidebarPaneMode.Minimal,
            _ => editing || !userCollapsed ? SidebarPaneMode.Expanded : SidebarPaneMode.Compact,
        };

    /// <summary>② The inline column's width: Expanded clamps the preference to the policy's bounds (Classic [180, 460]; a
    /// fixed pane presents its default) and to the content floor; Compact is the 48 rail; Minimal is nothing.</summary>
    public static float PresentedWidth(SidebarPaneMode mode, float preferredW, float viewportW, NavPanePolicy? policy = null)
    {
        switch (mode)
        {
            case SidebarPaneMode.Expanded:
            {
                var p = ShellPolicy.NavOr(policy);
                float want = p.Resizable ? preferredW : p.DefaultWidth;
                return Math.Clamp(
                    float.IsFinite(viewportW) && viewportW > 0f ? MathF.Min(want, viewportW - p.ContentFloorW) : want,
                    p.MinWidth, p.MaxWidth);
            }
            case SidebarPaneMode.Compact:
                return SidebarRowGeometry.RailWidth;
            default:
                return 0f;
        }
    }

    /// <summary>The overlay pane's width (the drawer in Minimal, the pane opened over a forced rail in Narrow): the
    /// preference, leaving at least the 48-px rail's worth of page visible.</summary>
    public static float OverlayWidth(float preferredW, float viewportW, NavPanePolicy? policy = null)
        => MathF.Max(0f, MathF.Min(SidebarPaneBounds.Clamp(preferredW, policy), viewportW - SidebarRowGeometry.RailWidth));

    /// <summary>Does a toggle / seam / double-click write <c>sidebar.pane.userCollapsed</c>? Only in a band that forces nothing
    /// (Wide in Classic) and never while editing.</summary>
    public static bool WritesUserCollapsed(SidebarWindowBand band, bool editing, NavPanePolicy? policy = null)
        => !editing && ShellPolicy.NavOr(policy).ForcedModeOf(band) == NavPaneMode.Expanded;

    /// <summary>Is there an overlay pane to open (the toggle's job in Narrow and Tiny)? A band that forces nothing has none, and
    /// neither has a band whose pane is <see cref="NavPaneMode.None"/>.</summary>
    public static bool HasOverlay(SidebarWindowBand band, NavPanePolicy? policy = null)
        => ShellPolicy.NavOr(policy).ForcedModeOf(band) is NavPaneMode.Compact or NavPaneMode.Overlay;

    /// <summary>Does the title-bar toggle have anything to do? In a band that forces nothing it flips the user's collapse, in a
    /// band with an overlay it opens it, and in a band with no pane (<see cref="NavPaneMode.None"/>) or while editing it is
    /// disabled.</summary>
    public static bool CanTogglePane(SidebarWindowBand band, bool editing, NavPanePolicy? policy = null)
        => !editing && ShellPolicy.NavOr(policy).ForcedModeOf(band) != NavPaneMode.None;

    /// <summary>Edit mode in a band with an overlay pins the overlay open (no light dismiss, no leaf-invoke close).</summary>
    public static bool OverlayPinned(SidebarWindowBand band, bool editing, NavPanePolicy? policy = null)
        => editing && HasOverlay(band, policy);

    /// <summary>The seam exists only where the pane is docked and resizable (Wide in Classic; a forced rail and the drawer
    /// have no width to drag).</summary>
    public static bool SeamVisible(SidebarWindowBand band, NavPanePolicy? policy = null)
    {
        var p = ShellPolicy.NavOr(policy);
        return p.Resizable && p.ForcedModeOf(band) == NavPaneMode.Expanded;
    }

    /// <summary>A leaf navigation closes an OVERLAY pane (NavigationView.cpp:4840-4846), never the inline one, and never
    /// while editing.</summary>
    public static bool LeafInvokeClosesOverlay(bool editing) => !editing;

    /// <summary>③ The right rail fits inline beside the presented pane and the content floor.</summary>
    public static bool RailFits(float presentedW, float railW, float viewportW, NavPanePolicy? policy = null)
        => presentedW + railW + ShellPolicy.NavOr(policy).ContentFloorW <= viewportW;
}

/// <summary>THE SEAM RULES (design V.1 "Seam"): drag resizes the policy's [min, max] (Classic 180-460); below
/// <see cref="NavPanePolicy.RailEnterW"/> (Classic 116) the pane collapses to the rail (writes the user's collapse); from the
/// rail, past <see cref="NavPanePolicy.ExpandedEnterW"/> (140) it expands; while editing it resizes but never flips (clamped
/// at the minimum). Double-click / Enter toggles; ←/→ step 8 (Shift 40). No detents, no flick. A pane that is not resizable
/// only toggles. Like the mode rules, every method takes a trailing policy (null = the active one).</summary>
public static class SidebarResizeRules
{
    /// <summary>The shell's baseline bounds and approach; <c>NavPanePolicy.Default</c> is built from them.</summary>
    public const float ExpandedMinW = SidebarPaneBounds.NavPaneMinW;             // 180
    public const float ExpandedMaxW = SidebarPaneBounds.NavPaneMaxW;             // 460
    public const float RegimePush = 64f;
    public const float RegimeHysteresis = SidebarPaneBounds.NavPaneHysteresisDip; // 24
    public const float MinFade = 0.35f;
    public const float NudgeW = 8f;
    public const float NudgeLargeW = 40f;

    /// <summary>The user's stored facts.</summary>
    public readonly record struct State(bool UserCollapsed, float ExpandedWidth);
    /// <summary>What the frame presents mid-drag.</summary>
    public readonly record struct Live(bool Collapsed, float PresentedWidth, float Fade);
    /// <summary>The release verdict; <see cref="TargetWidth"/> is what the column animates to.</summary>
    public readonly record struct Settle(bool UserCollapsed, float ExpandedWidth, float TargetWidth);

    public static Live Track(float raw, in State prev, bool editing, NavPanePolicy? policy = null)
    {
        if (!float.IsFinite(raw))
            return new(prev.UserCollapsed, prev.UserCollapsed ? SidebarRowGeometry.RailWidth : prev.ExpandedWidth, 1f);
        var p = ShellPolicy.NavOr(policy);
        bool collapsed = !editing && (prev.UserCollapsed ? raw < p.ExpandedEnterW : raw < p.RailEnterW);
        if (collapsed) return new(true, SidebarRowGeometry.RailWidth, 1f);
        float presented = Math.Clamp(raw, p.MinWidth, p.MaxWidth);
        float fade = editing || raw >= p.MinWidth ? 1f : SplitterFade(p.MinWidth - raw, p.CollapseApproach);
        return new(false, presented, fade);
    }

    public static Settle Resolve(float raw, in State prev, bool editing, NavPanePolicy? policy = null)
    {
        var live = Track(raw, in prev, editing, policy);
        return live.Collapsed
            ? new(true, prev.ExpandedWidth, SidebarRowGeometry.RailWidth)
            : new(false, live.PresentedWidth, live.PresentedWidth);
    }

    public static Settle Step(in State s, int direction, bool large, bool editing, NavPanePolicy? policy = null)
    {
        var p = ShellPolicy.NavOr(policy);
        if (s.UserCollapsed)
        {
            if (direction <= 0 || editing) return new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);
            float back = Math.Clamp(s.ExpandedWidth, p.MinWidth, p.MaxWidth);
            return new(false, back, back);
        }
        // A pane that cannot be resized keeps its width: the keys have nothing to step.
        if (!p.Resizable) return new(false, s.ExpandedWidth, p.ExpandedWidthFor(s.ExpandedWidth));
        float next = s.ExpandedWidth + direction * (large ? NudgeLargeW : NudgeW);
        if (!editing && next < p.MinWidth && s.ExpandedWidth <= p.MinWidth)
            return new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);
        float w = Math.Clamp(next, p.MinWidth, p.MaxWidth);
        return new(false, w, w);
    }

    public static Settle Toggle(in State s, NavPanePolicy? policy = null)
    {
        var p = ShellPolicy.NavOr(policy);
        return s.UserCollapsed
            ? new(false, s.ExpandedWidth, p.ExpandedWidthFor(s.ExpandedWidth))
            : new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);
    }

    /// <summary>SplitterMath.Fade restated (CORE must not reference FluentGpu.Controls): 1 at 0, MinFade at <paramref name="over"/>.</summary>
    static float SplitterFade(float into, float over)
    {
        if (over <= 0f) return 1f;
        float t = Math.Clamp(into / over, 0f, 1f);
        return 1f - t * (1f - MinFade);
    }
}
