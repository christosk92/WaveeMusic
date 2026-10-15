// ── Themes/Classic/Structure/Rules/Tiers.Library.cs — the library page's collapse ladder (themes plan 3.6; T10c1) ──────────────────────────────
//
// `LibraryLayoutBreakpoints` from `Entities\User.cs` (ch 15 §8, verbatim), moved out of core (plan 3.6: each move PR moves its own view's ladders into
// `Rules\Tiers.<Family>.cs`). Only Classic's library page reads it: below `CollapseBelow` the master-detail columns become a breadcrumb drill-in, with a hysteresis so a window
// sitting on the boundary does not flip-flop. The view answers it from the page's own measured width and writes the host's collapse signal, which the selection, the crumbs and the
// persisted pick read; no view compares a width with a number (WVTH013).
//
// Public, because `LibraryLayoutBreakpointTests` pins the ladder.

namespace Wavee.Themes.Classic;

/// <summary>Classic's library collapse ladder.</summary>
public static class ClassicLibraryTiers
{
    /// <summary>Below this width the columns collapse (~NavigationView.CompactModeThresholdWidth).</summary>
    public const float CollapseBelow = 640f;

    /// <summary>The width it takes past <see cref="CollapseBelow"/> to un-collapse.</summary>
    public const float Hysteresis = 24f;

    /// <summary>Collapse when narrow; un-collapse only once comfortably wide. An unmeasured width (≤ 0) keeps the answer.</summary>
    public static bool Collapsed(float w, bool wasCollapsed)
    {
        if (w <= 0f) return wasCollapsed;
        return wasCollapsed ? w < CollapseBelow + Hysteresis : w < CollapseBelow;
    }

    /// <summary>The artist reader's STACKED verb row (Y3) folds shuffle and ↗ into one ⋯ menu when its measured lane is narrower than the whole
    /// row (<see cref="Artist.ReaderShape.ActionsW"/>, 316), so the row never clips (the Small board's "More: Shuffle, Open artist page").
    /// An unmeasured lane (≤ 0) folds: the first frame never overflows.</summary>
    public static bool FoldVerbs(float laneWidth) => laneWidth < Artist.ReaderShape.ActionsW;
}
