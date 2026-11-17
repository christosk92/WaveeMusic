// ── Themes/Classic/Structure/Rules/PlayerTransportLayout.cs — the numbers of Classic's shared transport pieces (leftovers P3) ──
//
// What `Shell.PlayerBar.UI.cs` / `Shell.PlayerBar.Chapters.UI.cs` held as literals beside the seek rail, the chapter ticks and their tip,
// the scrub readout, the time labels, the live slot and the bar's credits line, moved verbatim with their pictures
// (`Views\Player\ClassicTransport.cs`, `ClassicPlayerBar.Credits.cs`). They are data, not decisions (WVTH003 allows literals only here).

using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's seek rail, time labels and credits line.</summary>
internal static class ClassicTransportLayout
{
    // ── the seek rail ──

    /// <summary>The rail's hit box: WinUI's SliderHorizontalHeight.</summary>
    public const float SeekHitHeight = 32f;

    /// <summary>The thumb ring: <c>Slider.DefaultStyle.ThumbRingDiameter</c>.</summary>
    public const float SeekRingDiameter = 22f;

    /// <summary>The DVR rail's live-edge mark.</summary>
    public const float LiveEdgeWidth = 2f;

    /// <summary>The live line (nothing to rewind): its stroke and its corner.</summary>
    public const float LiveLineThickness = 2f, LiveLineCorner = 1f;

    /// <summary>The live line's breath: 0.55 ↔ 1 over 3 s, and the finite flat track that REPLACES the loop in place, so the frame
    /// loop quiesces while paused, inactive or under reduced motion.</summary>
    public static readonly Keyframe[] LiveLineBreathe = [new(0f, 0.55f), new(0.5f, 1f), new(1f, 0.55f)];

    /// <summary>The flat track (see <see cref="LiveLineBreathe"/>).</summary>
    public static readonly Keyframe[] LiveLineFlat = [new(0f, 1f), new(1f, 1f)];

    /// <summary>One breath; the flat track's length.</summary>
    public const float LiveLineBreatheMs = 3000f, LiveLineFlatMs = 1f;

    // ── the chapter ticks and their tip ──

    /// <summary>A chapter tick (and the tip's invisible anchor).</summary>
    public const float ChapterTickHeight = 8f;

    /// <summary>The tip's column.</summary>
    public const float ChapterTipGap = 3f, ChapterTipWidth = 260f, ChapterTipMaxWidth = 320f;

    /// <summary>The tip's padding.</summary>
    public static readonly Edges4 ChapterTipPadding = new(9f, 6f, 9f, 8f);

    /// <summary>The tip's lines: the time and the range, the title.</summary>
    public const float ChapterTipTimeSize = 12f, ChapterTipTitleSize = 13f;

    /// <summary>The tip's emphasis weight.</summary>
    public const int ChapterTipWeight = 600;

    /// <summary>The title's line cap.</summary>
    public const int ChapterTipTitleLines = 2;

    // ── the scrub readout ──

    /// <summary>The readout bubble's padding (the Slider's thumb tip).</summary>
    public static readonly Edges4 ReadoutPadding = new(8f, 3f, 8f, 5f);

    /// <summary>The readout's text size.</summary>
    public const float ReadoutSize = 15f;

    // ── the seek preview (the picture under the pointer) ──

    /// <summary>The picture's width; its height follows the video's aspect.</summary>
    public const float PreviewWidth = 224f;

    /// <summary>The picture to the time bubble under it, and the time to the chapter inside the bubble.</summary>
    public const float PreviewGap = 6f, PreviewCaptionGap = 6f;

    /// <summary>The bubble's digits (the readout's 15 would outweigh the picture: 13, semibold) and the chapter (12).</summary>
    public const float PreviewTimeSize = 13f, PreviewLabelSize = 12f;

    public const int PreviewTimeWeight = 600;

    /// <summary>The bubble's padding: the readout's, a little wider for a pill.</summary>
    public static readonly Edges4 PreviewBubblePadding = new(10f, 3f, 10f, 5f);

    /// <summary>The air the tip keeps: 10 above the rail's line it points at (clear of the thumb), and 12 to either side, so a picture the
    /// window clamps stops short of the glass.</summary>
    public static readonly Edges4 PreviewLift = new(12f, 0f, 12f, 10f);

    // ── the lyric tip: close to the rail, with a pointer ──

    /// <summary>The lyric tip keeps to the rail: 2 between the pointer's point and the tip's anchor (the point rests just over the thumb), and 12 of air to either side,
    /// so a card the window clamps stops short of the glass.</summary>
    public const float LyricTipGap = 2f;

    public static readonly Edges4 LyricTipSides = new(12f, 0f, 12f, 0f);

    /// <summary>The pointer: a 16-wide, 8-tall wedge on the card's foot that stays on the POINTER (the teaching tip's beak: a square of side
    /// 16 / sqrt 2 turned 45 degrees, its lower half showing), drawn in a 16 x 16 slot whose centre sits on the card's bottom edge. The band
    /// it shows through is the wedge's 8 plus 1 over the card's edge, so the edge does not strike through the wedge's base; the slot is
    /// raised 7 into it.</summary>
    public const float LyricBeakLong = 16f, LyricBeakShort = 8f, LyricBeakHalf = 8f, LyricBeakBand = 9f, LyricBeakRise = -7f;

    public const float LyricBeakFill = 11.3137f, LyricBeakFillInset = 2.3431f, LyricBeakTurn = 45f;

    public const int LyricBeakPoints = 3;

    public static readonly Point2 LyricBeakP0 = new(0f, 8f), LyricBeakP1 = new(8f, 16f), LyricBeakP2 = new(16f, 8f);

    /// <summary>How close to either end of the tip the pointer's centre may come: the side air (12), the card's corner (4) and half the pointer (8).</summary>
    public const float LyricBeakKeepClear = 24f;

    /// <summary>The pair arrives from 6 below and 4 % small while the host's tip fade runs.</summary>
    public static readonly EnterExit PreviewEnter = new(Dy: 6f, Sx: 0.96f, Sy: 0.96f, Active: true);

    // ── the time labels ──

    /// <summary>The track slot: wide enough for "-59:59", so 0:09 → 0:10 moves nothing (a reservation at rest, a FLOOR while live).</summary>
    public const float TimeSlotWidth = 44f;

    /// <summary>The clickable slot's (the remaining time: it toggles to the duration) hit target: the 40 DIP rule, reached with vertical slop
    /// around the one caption line, so the label's look and the row's height do not change.</summary>
    public const float TimeTargetH = 40f;

    /// <summary>The vertical slop that lifts a <paramref name="lineHeight"/> tall slot to <see cref="TimeTargetH"/>.</summary>
    public static float TimeSlop(float lineHeight) => MathF.Max(0f, (TimeTargetH - lineHeight) * 0.5f);

    /// <summary>The live slot, sized once for "GO LIVE −99:59", so the mark ↔ action swap moves nothing.</summary>
    public const float LiveSlotWidth = 104f;

    /// <summary>The GO LIVE text button.</summary>
    public const float GoLiveMinWidth = 44f, GoLiveHeight = 20f;

    /// <summary>The live slot's type: the GO LIVE action and the LIVE word-mark.</summary>
    public const float LiveTextSize = 9f, LiveTextLineHeight = 12f;

    /// <summary>The live slot's weight.</summary>
    public const int LiveTextWeight = 600;

    /// <summary>The static red dot.</summary>
    public const float LiveDotSize = 6f, LiveDotCorner = 3f;

    /// <summary>The LIVE word-mark's outlined badge.</summary>
    public const float LiveBadgeHeight = 14f, LiveBadgeCorner = 2f;

    // ── the bar's credits line ──

    /// <summary>A credit link and its ", " separator.</summary>
    public const float CreditSize = 12f;

    /// <summary>Between two credited names (and in the line's plain words: the credits marquee's restart key and tooltip).</summary>
    public const string CreditSeparator = ", ";

    /// <summary>The "+N" chip's weight.</summary>
    public const int CreditChipWeight = 600;
}
