// ── Themes/Classic/Structure/Rules/ClassicSheetRules.cs — the numbers of Classic's bottom sheet (themes plan 3.6, TC9) ──────────────────
//
// `C-ContextMenu-Compact` ("Compact song menu, bottom sheet") and `C-Flyouts-Compact` ("Compact filter sheet"): a card laid over the lower edge of
// the window under a 40 % scrim that starts below the 48-DIP chrome row, rounded 8 on top, a 32 x 4 grabber, then the identity header (56), the
// four-column quick-action strip (64 tall cells) and 44-DIP rows. Every target is at least 40. The sheet exists only where the structure's policy says
// context menus are sheets (Compact today); these are the geometry of the one picture, not a per-tier choice. WVTH003 allows literals only here.

using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of the bottom sheet (public: the mounted tests read them).</summary>
public static class ClassicSheetRules
{
    /// <summary>The 48-DIP chrome row the scrim starts below (the title bar stays reachable under a sheet).</summary>
    public const float ChromeRow = 48f;

    /// <summary>The scrim over the page: black at 40 %.</summary>
    public static ColorF Scrim => ColorF.FromRgba(0, 0, 0, 102);

    /// <summary>The card's top corners.</summary>
    public const float Radius = 8f;

    /// <summary>The card's padding either side and under the last row.</summary>
    public const float SidePad = 8f, BottomPad = 12f;

    /// <summary>The grabber's zone (its height), the pill and its corner.</summary>
    public const float GrabberZone = 16f, GrabberW = 32f, GrabberH = 4f, GrabberRadius = 2f;

    /// <summary>The grabber's ink: white at 30 %.</summary>
    public static ColorF Grabber => ColorF.FromRgba(255, 255, 255, 77);

    /// <summary>The identity header: its height, side padding and gap, the tile's box, and the title's and subtitle's type.</summary>
    public const float HeaderHeight = 56f, HeaderPadX = 8f, HeaderGap = 12f, HeaderTile = 40f;

    /// <summary>The header's title (14 over 20, semi-bold) and subtitle (12 over 16).</summary>
    public const float TitleSize = 14f, TitleLine = 20f, SubtitleSize = 12f, SubtitleLine = 16f;

    /// <summary>The quick-action strip: the gap between cells, its padding, a cell's height, corner and gap, the icon and the label.</summary>
    public const float StripGap = 4f, StripCell = 64f, StripCellRadius = 4f, StripCellGap = 6f, StripIcon = 18f, StripLabel = 12f;

    /// <summary>The strip's padding (4 above, 8 below).</summary>
    public static readonly Edges4 StripPadding = new(0f, 4f, 0f, 8f);

    /// <summary>A row: its height, side padding, gap, the icon, the label, the trailing chevron and check.</summary>
    public const float RowHeight = 44f, RowPadX = 12f, RowGap = 12f, RowIcon = 16f, RowText = 14f, RowTrail = 12f, RowRadius = 4f;

    /// <summary>The divider: its margin and thickness.</summary>
    public static readonly Edges4 DividerMargin = new(8f, 4f, 8f, 4f);

    /// <summary>The entry slide (up from below).</summary>
    public const float EnterDy = 48f;

    // ── the drag (ONE behaviour for every bottom sheet: the Compact panel sheet and the context sheet; `ClassicSheetDrag`) ───────────

    /// <summary>A release dismisses once the sheet has travelled this share of its height, or <see cref="DismissTravel"/>, whichever
    /// is less (a tall sheet does not ask for a 300-DIP pull).</summary>
    public const float DismissShare = 0.3f, DismissTravel = 160f;

    /// <summary>A downward fling of at least this speed (DIP per second) dismisses whatever the travel, once the sheet has moved at least
    /// <see cref="FlingMinTravel"/> (a tap's jitter never flings).</summary>
    public const float FlingSpeed = 900f, FlingMinTravel = 12f;

    /// <summary>Dragged up past its rest the sheet rubber-bands: it gives at most its reach (<see cref="RubberReach"/> where nothing clips it
    /// sooner), stiffening as it goes.</summary>
    public const float RubberReach = 48f, RubberStiffness = 0.55f;

    /// <summary>The release velocity is the pointer's travel over the last <see cref="VelocityWindowS"/> seconds; a pointer that has not
    /// moved for <see cref="StillAfterS"/> seconds before the release has none.</summary>
    public const double VelocityWindowS = 0.1, StillAfterS = 0.06;

    /// <summary>The spring a sheet released short of the threshold settles back with (damped enough not to wobble).</summary>
    public const float SettleResponseS = 0.32f, SettleDamping = 0.9f;

    /// <summary>A drag-dismissed context sheet slides the rest of the way out in this time (the panel sheet runs its own 300 ms slide).</summary>
    public const float DismissMs = 220f;

    /// <summary>The sheet's presented offset for a raw drag offset: 1:1 below the rest, rubber-banded above it up to <paramref name="reach"/>.</summary>
    public static float Rubber(float raw, float reach)
    {
        if (raw >= 0f) return raw;
        float r = MathF.Max(1f, reach);
        return -r * (1f - 1f / (1f - raw * RubberStiffness / r));
    }

    /// <summary>Does a release at <paramref name="offset"/> (DIP below the rest), moving at <paramref name="velocity"/> (DIP per second,
    /// positive downward), dismiss a sheet <paramref name="height"/> tall?</summary>
    public static bool Dismisses(float offset, float velocity, float height)
    {
        if (offset <= 0f) return false;
        if (velocity <= -FlingSpeed) return false;                                  // flicked back up: it stays
        if (velocity >= FlingSpeed && offset >= FlingMinTravel) return true;
        return offset >= MathF.Min(DismissTravel, MathF.Max(1f, height) * DismissShare);
    }

    /// <summary>The scrim's share of its resting opacity with the sheet <paramref name="offset"/> DIP below its rest.</summary>
    public static float ScrimShare(float offset, float height) => Math.Clamp(1f - offset / MathF.Max(1f, height), 0f, 1f);
}
