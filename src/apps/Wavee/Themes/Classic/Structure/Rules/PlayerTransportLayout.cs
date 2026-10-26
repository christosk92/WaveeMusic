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

    // ── the time labels ──

    /// <summary>The track slot: wide enough for "-59:59", so 0:09 → 0:10 moves nothing (a reservation at rest, a FLOOR while live).</summary>
    public const float TimeSlotWidth = 44f;

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
