// ── Themes/Common/Rules/CommonLocalFilesRules.cs — the numbers of the Local Files list's own views (#240 UX S4, S5, S6) ────────────────
//
// Data, not decisions: `CommonLocalFiles.cs` reads them (WVTH003 allows literals only here, in `Rules\**`). Type, colours, radii and the spacing
// rhythm come from the active style's tokens and roles; these are the boards' own geometry (boards240: Onboarding 6, FirstScan 7, Changes 9,
// Missing 12; WinUI dark 1440x960).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

/// <summary>The constants of the Local Files list's empty state, scan line, pill, row tags and review.</summary>
internal static class CommonLocalFilesRules
{
    // ── keys (`find prefix=local-`) ──

    public const string EmptyKey = "local-empty:body", EmptyTitleKey = "local-empty:title", AddMusicKey = "local-empty:add-music",
        ChooseKey = "local-empty:choose", CaptionKey = "local-empty:caption", DropKey = "local-empty:drop";
    public const string ScanKey = "local-scan:line", ScanBarKey = "local-scan:bar";
    public const string PillKey = "local-pill";
    public const string TagKey = "local-tag";
    /// <summary>The file mark on a Spotify row a file in the music folders is linked to ("Your files and Spotify").</summary>
    public const string FileMarkKey = "linked-file";
    public const string ReviewKey = "local-review", ReviewRowKey = "local-review:row:", LocateKey = "local-review:locate:", RemoveKey = "local-review:remove:";

    // ── the empty state (board 6: a 600 column centred in the list, the 440 line, 24 to the buttons, 8 to the caption, 40 to the 152 drop zone) ──

    /// <summary>The column's width (the drop zone's), and the line's measure.</summary>
    public const float EmptyWidth = 600f, EmptyMeasure = 440f;

    /// <summary>The air above the column (the list's top), between the title and the line, above the buttons, above the caption and above the drop zone.</summary>
    public const float EmptyTop = 48f, EmptyLineGap = 8f, EmptyButtonsGap = 24f, EmptyCaptionGap = 8f, EmptyDropGap = 40f, EmptyBottom = 48f;

    /// <summary>The gap between the two buttons, and the drop zone's height, corner, dash and the gap between its lines.</summary>
    public const float EmptyButtonGap = 8f, DropHeight = 152f, DropRadius = 8f, DropBorder = 2f, DashOn = 6f, DashOff = 4f, DropLineGap = 6f;

    /// <summary>The drop zone's glyph.</summary>
    public const float DropGlyph = 24f;

    // ── "Find music on this PC" (#240, 0dm; no board: the WinUI settings card's plate, the empty column's width) ──

    public const string FindKey = "local-find:card", FindTitleKey = "local-find:title", FindRowsKey = "local-find:rows", FindRowKey = "local-find:row:",
        FindCountKey = "local-find:count:", FindAddKey = "local-find:add", FindChooseKey = "local-find:choose";

    /// <summary>The title's weight (the settings card's title), the plate's border.</summary>
    public const ushort FindTitleWeight = 600;
    public const float FindBorder = 1f;

    /// <summary>The title to the line, the line to the rows, between rows, between a row's lines, the rows to the buttons.</summary>
    public const float FindLineGap = 4f, FindRowsGap = 12f, FindRowGap = 8f, FindSubGap = 2f, FindButtonsGap = 16f;

    /// <summary>Where a row's count and path start: under the checkbox's label (its 20 box and 8 gap).</summary>
    public const float FindPathInset = 28f;

    // ── the scan line (board 7: 560 wide under the facts, the time left 16 after the words, the bar 6 under them) ──

    /// <summary>The widest the line and its bar run, the gap before the time left (and above it, where it wraps under the line: one gap both ways,
    /// so the board's 16 across gives way a little), the gap above the bar, and the line's top air.</summary>
    public const float ScanMax = 560f, ScanAsideGap = 12f, ScanBarGap = 6f, ScanTop = 4f;

    /// <summary>The narrowest the bar is drawn (a head narrower than this gives the bar its whole width).</summary>
    public const float ScanMinBar = 120f;

    // ── the pill (board 9: 32 tall, r16, 12 / 16 padding, the arrow 14, 8 to the words; centred, 16 under the list's pinned band) ──

    public const float PillHeight = 32f, PillPadLead = 12f, PillPadEnd = 16f, PillGap = 8f, PillGlyph = 14f, PillTop = 16f;

    /// <summary>How far the pill drops in as it enters (and rises as it leaves).</summary>
    public const float PillDrop = 8f;

    public const ushort PillWeight = 600;

    /// <summary>The pill's enter and exit: a short drop with a fade, one curve both ways.</summary>
    public static readonly EnterExit PillMotion = new(Dy: -PillDrop, Opacity: 0f, Active: true);

    /// <summary>The same under reduced motion: the fade alone.</summary>
    public static readonly EnterExit PillFade = new(Opacity: 0f, Active: true);

    public static readonly MotionTokenDef PillTiming = MotionTokenDef.Eased(PillMotionMs, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    public const float PillMotionMs = 220f;

    // ── the row tag (board 9: 11/16, 6 across, r4; "Moved" with its 10 arrow, 4 to the word) ──

    public const float TagSize = 11f, TagLine = 16f, TagPadX = 6f, TagRadius = 4f, TagGlyph = 10f, TagGap = 4f;

    public const ushort TagWeight = 600;

    // ── the review (board 12: the list's rows 76 tall, 12 across, the 40 tile, 12 between; the list's plate r6) ──

    /// <summary>The gap between a review row's Locate… and Remove.</summary>
    public const float ReviewVerbGap = 8f;

    /// <summary>The list's top air, a row's least height, its padding across, the tile, the gap between its parts, the plate's corner, the note's and
    /// the footer's top air.</summary>
    public const float ReviewListTop = 16f, ReviewRow = 76f, ReviewPadX = 12f, ReviewTile = 40f, ReviewGap = 12f, ReviewRadius = 6f, ReviewNoteTop = 12f,
        ReviewFooterTop = 16f, ReviewTileGlyph = 18f;

    /// <summary>The tallest the list grows before it scrolls inside the dialog.</summary>
    public const float ReviewListMax = 320f;
}
