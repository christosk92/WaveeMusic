// ── Shell/Sidebar.Resize.cs ────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's free-range resize rules: two regimes, three rail detents, one pure rule class
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-free-resize-implementation.md §2-§4
//
// The window never changes the sidebar's width or regime on its own (apart from the degenerate last-resort band).
// Above the icon rail the width is free; close to the rail the pane enters icon-only mode with three snap detents.
// Everything here is engine-free — no signal, no clock, no Element — so Wavee.Tests drives every branch. `public`
// because Wavee.Tests is a ProjectReference with no InternalsVisibleTo.

namespace Wavee;

/// <summary>PERSISTED ints (Platform.Keys.SidebarRailDetent) — append only.</summary>
public enum SidebarRailDetent : byte { Compact = 0, Default = 1, Large = 2 }

/// <summary>Persisted as the existing per-design `sidebar.&lt;slug&gt;.collapsed` bool: Rail ⇔ true.</summary>
public enum SidebarRegime : byte { Expanded = 0, Rail = 1 }

/// <summary>The rail's whole geometry for one detent, derived from the thumb ladder — never a per-detent literal table.</summary>
public readonly record struct SidebarRailMetrics(
    float Tile, float Art, float Glyph, float Corner, float StripW, float Pitch, float DividerW)
{
    /// <summary>The 2-DIP accent ring's inset on each side (today's Box 40 → ArtEdge 36).</summary>
    public const float RingInset = 2f;

    public static float TileOf(SidebarRailDetent d) => d switch
    {
        SidebarRailDetent.Compact => Design.Size.Thumb32,   // = ControlH, the WinUI 32×32 hit floor
        SidebarRailDetent.Large => Design.Size.Thumb64,
        _ => Design.Size.Thumb40,                           // today's Rail.Box
    };

    public static SidebarRailMetrics For(SidebarRailDetent d) => Of(TileOf(d));

    public static SidebarRailMetrics Of(float tile)
    {
        const float RailEdge = 8f;                              // (56 − 40)/2 today; P2 deletes the detents with it
        return new(
            Tile: tile,
            Art: tile - 2f * RingInset,
            Glyph: tile >= Design.Size.Thumb64 ? 20f : 16f,       // ControlSize.Large.IconSize : the stock 16
            Corner: Sidebar.Cover.Radius(tile, circular: false),   // 6 ≤ 40, else 8
            StripW: tile + 2f * RailEdge,
            Pitch: tile + SidebarRailExtents.TileGap,
            DividerW: tile - 2f * RailEdge);
    }

    public static SidebarRailDetent Coerce(int stored) => (uint)stored <= 2 ? (SidebarRailDetent)stored : SidebarRailDetent.Default;
}

/// <summary>THE pure resize rules: live tracking during a drag, the settle on release, the window yield, the
/// keyboard/toggle verbs. No engine type, no signal, no clock — Wavee.Tests drives every branch.</summary>
public static class SidebarResizeRules
{
    public const float ExpandedMinW = SidebarPaneBounds.NavPaneMinW;             // 180 (#84)
    public const float ExpandedMaxW = SidebarPaneBounds.NavPaneMaxW;             // 460
    /// <summary>Today's seam ForcePush: how far past the expanded floor the pointer travels before the regime flips.</summary>
    public const float RegimePush = 64f;
    public const float RegimeHysteresis = SidebarPaneBounds.NavPaneHysteresisDip; // 24
    /// <summary>RootlistSlotResolver.DepthHysteresis — the same anti-flicker band, around each detent edge.</summary>
    public const float DetentHysteresis = 4f;
    public const float MinFade = 0.35f;                                          // SplitterMath's default floor
    /// <summary>A release faster than this toward a neighbouring detent picks that neighbour, not the nearest. Tuning value (no token exists).</summary>
    public const float FlickDipPerSec = 800f;
    public const float NudgeW = 8f;        // Spacing.S — the shell's RailGapW
    public const float NudgeLargeW = 40f;  // ChromePromotionHysteresisW

    public static float RailFloorW => SidebarRailMetrics.For(SidebarRailDetent.Compact).StripW;   // 48
    public static float RailCeilW  => SidebarRailMetrics.For(SidebarRailDetent.Large).StripW;     // 80
    public static float RailEnterW => ExpandedMinW - RegimePush;                                   // 116
    public static float ExpandedEnterW => RailEnterW + RegimeHysteresis;                           // 140

    /// <summary>The user's stored facts.</summary>
    public readonly record struct State(SidebarRegime Regime, SidebarRailDetent Detent, float ExpandedWidth);
    /// <summary>What the frame presents mid-gesture.</summary>
    public readonly record struct Live(SidebarRegime Regime, SidebarRailDetent Detent, float PresentedWidth, float Fade);
    /// <summary>The release verdict. <see cref="TargetWidth"/> is what the column animates to.</summary>
    public readonly record struct Settle(SidebarRegime Regime, SidebarRailDetent Detent, float ExpandedWidth, float TargetWidth);

    public static float StripOf(SidebarRailDetent d) => SidebarRailMetrics.For(d).StripW;

    /// <summary>Live tracking. <paramref name="raw"/> is the seam cell the splitter writes 1:1 (clamped by it to [RailFloorW, ExpandedMaxW]).</summary>
    public static Live Track(float raw, in State prev)
    {
        if (!float.IsFinite(raw)) return new(prev.Regime, prev.Detent, prev.Regime == SidebarRegime.Rail ? StripOf(prev.Detent) : prev.ExpandedWidth, 1f);
        var regime = prev.Regime switch
        {
            SidebarRegime.Expanded => raw < RailEnterW ? SidebarRegime.Rail : SidebarRegime.Expanded,
            _ => raw >= ExpandedEnterW ? SidebarRegime.Expanded : SidebarRegime.Rail,
        };
        if (regime == SidebarRegime.Expanded)
        {
            float presented = Math.Clamp(raw, ExpandedMinW, ExpandedMaxW);
            float fade = raw >= ExpandedMinW ? 1f : SplitterFade(ExpandedMinW - raw, RegimePush);
            return new(regime, prev.Detent, presented, fade);
        }
        float strip = Math.Clamp(raw, RailFloorW, RailCeilW);
        var detent = NearestDetent(strip, prev.Regime == SidebarRegime.Rail ? prev.Detent : SidebarRailDetent.Large);
        float railFade = raw <= RailCeilW ? 1f : SplitterFade(raw - RailCeilW, ExpandedEnterW - RailCeilW);
        return new(regime, detent, strip, railFade);
    }

    /// <summary>Nearest detent by midpoint, holding <paramref name="prev"/> inside ±DetentHysteresis of an edge.</summary>
    public static SidebarRailDetent NearestDetent(float w, SidebarRailDetent prev)
    {
        float lo = Mid(SidebarRailDetent.Compact, SidebarRailDetent.Default);   // 52
        float hi = Mid(SidebarRailDetent.Default, SidebarRailDetent.Large);     // 68
        var nominal = w < lo ? SidebarRailDetent.Compact : w < hi ? SidebarRailDetent.Default : SidebarRailDetent.Large;
        if (nominal == prev) return prev;
        float edge = prev == SidebarRailDetent.Compact || nominal == SidebarRailDetent.Compact ? lo : hi;
        return MathF.Abs(w - edge) < DetentHysteresis ? prev : nominal;
    }

    /// <summary>The release. Expanded commits the raw width (clamped); Rail snaps to a detent and leaves the expanded memory alone.</summary>
    public static Settle Resolve(float raw, in State prev, float velocityDipPerSec = 0f)
    {
        var live = Track(raw, in prev);
        if (live.Regime == SidebarRegime.Expanded)
            return new(live.Regime, prev.Detent, live.PresentedWidth, live.PresentedWidth);
        var detent = live.Detent;
        if (MathF.Abs(velocityDipPerSec) >= FlickDipPerSec)
        {
            int dir = velocityDipPerSec < 0f ? -1 : 1;
            detent = (SidebarRailDetent)Math.Clamp((int)detent + dir, 0, 2);
        }
        return new(SidebarRegime.Rail, detent, prev.ExpandedWidth, StripOf(detent));
    }

    /// <summary>The column width at rest: a rail is never yielded; an expanded pane yields to keep <paramref name="contentFloorW"/>
    /// for the page, never below the expanded floor, and never by writing the preference.</summary>
    public static float Present(in State s, float viewportW, float contentFloorW)
    {
        if (s.Regime == SidebarRegime.Rail) return StripOf(s.Detent);
        float preferred = Math.Clamp(s.ExpandedWidth, ExpandedMinW, ExpandedMaxW);
        if (viewportW <= 0f || !float.IsFinite(viewportW)) return preferred;
        float cap = MathF.Max(ExpandedMinW, viewportW - contentFloorW);
        return MathF.Min(preferred, cap);
    }

    /// <summary>The LAST-RESORT band (D1 option b): the window cannot hold the expanded floor AND the content floor.
    /// Enter below (floor + floor); leave at +ChromePromotionHysteresisW. A zero width never moves it.</summary>
    public static bool LastResort(float viewportW, bool current, float contentFloorW)
    {
        if (viewportW <= 0f) return current;
        float enter = ExpandedMinW + contentFloorW;          // 660 with MinContentW 480
        return current ? viewportW < enter + NudgeLargeW : viewportW < enter;
    }

    /// <summary>Keyboard: ←/→ one step. Expanded nudges the width; at the floor a left step enters the rail at Large;
    /// Rail steps detents; a right step past Large expands at the floor.</summary>
    public static Settle Step(in State s, int direction, bool large)
    {
        if (direction == 0) return new(s.Regime, s.Detent, s.ExpandedWidth, s.Regime == SidebarRegime.Rail ? StripOf(s.Detent) : s.ExpandedWidth);
        if (s.Regime == SidebarRegime.Expanded)
        {
            float next = s.ExpandedWidth + direction * (large ? NudgeLargeW : NudgeW);
            if (next < ExpandedMinW && s.ExpandedWidth <= ExpandedMinW)
                return new(SidebarRegime.Rail, SidebarRailDetent.Large, s.ExpandedWidth, RailCeilW);
            float w = Math.Clamp(next, ExpandedMinW, ExpandedMaxW);
            return new(SidebarRegime.Expanded, s.Detent, w, w);
        }
        int i = (int)s.Detent + direction;
        if (i > 2) return new(SidebarRegime.Expanded, s.Detent, MathF.Max(s.ExpandedWidth, ExpandedMinW), MathF.Max(s.ExpandedWidth, ExpandedMinW));
        var d = (SidebarRailDetent)Math.Max(i, 0);
        return new(SidebarRegime.Rail, d, s.ExpandedWidth, StripOf(d));
    }

    /// <summary>Hamburger / double-click / "&lt;": flip the regime, keeping both memories.</summary>
    public static Settle Toggle(in State s) => s.Regime == SidebarRegime.Rail
        ? new(SidebarRegime.Expanded, s.Detent, s.ExpandedWidth, Math.Clamp(s.ExpandedWidth, ExpandedMinW, ExpandedMaxW))
        : new(SidebarRegime.Rail, s.Detent, s.ExpandedWidth, StripOf(s.Detent));

    static float Mid(SidebarRailDetent a, SidebarRailDetent b) => 0.5f * (StripOf(a) + StripOf(b));

    /// <summary>SplitterMath.Fade restated (CORE must not reference FluentGpu.Controls): 1 at 0, MinFade at `over`.</summary>
    static float SplitterFade(float into, float over)
    {
        if (over <= 0f) return 1f;
        float t = Math.Clamp(into / over, 0f, 1f);
        return 1f - t * (1f - MinFade);
    }
}
