// ── Themes/Common/Rules/CommonMixLessonRules.cs — the Mix lesson's page: the fixed numbers every voice shares (#48 Mix) ───────────
//
// Each theme's voice (`MixLessonVoice`, `MixTeachLook`, built in its Rules) brings its type, controls, plates and gutters; these are the
// page's own small measures: the gaps in a row, the check and badge sizes, the selection pill, the progress bar.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.Common;

public static class CommonMixLessonRules
{
    /// <summary>The hint colour when the accent is too close to the lesson's own (a pink or magenta accent, Zune's default): an electric cyan,
    /// which no song, layer or accent of the lesson uses.</summary>
    public const uint AltHint = 0xFF00D8FF;
    /// <summary>How close two hues may be (degrees) before the hint steps aside for the accent.</summary>
    public const float HintClearance = 40f;

    /// <summary>The hint colour (the target, its guide, the guide's marker words) for <paramref name="hue"/>: that data hue, unless the theme's
    /// accent sits next to it, then <see cref="AltHint"/>. A hint must never look like the accent ("you are here" / "do this").</summary>
    public static ColorF Hint(uint hue)
    {
        ColorF c = R.Ink.DataDot(hue);
        var (ha, sa, _) = Tok.AccentDefault.ToHsv();
        var (hc, _, _) = c.ToHsv();
        float d = MathF.Abs(ha - hc) % 360f;
        if (d > 180f) d = 360f - d;
        return sa > 0.25f && d < HintClearance ? R.Ink.DataDot(AltHint) : c;
    }

    /// <summary>The editor's head: the gap between its parts, the pair line's gap, a song's swatch and its corner.</summary>
    public const float HeadGap = 10f, PairGap = 6f, Swatch = 8f, SwatchCorner = 2f;
    /// <summary>The status chip: its dot, its glyph while playing, the gap after them.</summary>
    public const float ChipDot = 8f, ChipGlyph = 12f, ChipGap = 8f;
    /// <summary>A colour change eases over this long (a task's check, the chip's dot).</summary>
    public const float BrushMs = 167f;
    /// <summary>The guide: the room under its last step, the eyebrow's glyph and gap, the gap between buttons and before the foot's hint.</summary>
    public const float GuideFoot = 48f, EyebrowGlyph = 16f, EyebrowGap = 10f, CtaGap = 8f, FootGap = 12f;
    /// <summary>The progress bar's height.</summary>
    public const float ProgressHeight = 3f;
    /// <summary>The tasks: the list's padding and the gap between rows, a row's padding, the gap after the check and between its lines, a row's corner.</summary>
    public const float TaskCardPad = 4f, TaskGap = 2f, TaskPadX = 12f, TaskPadY = 10f, TaskInnerGap = 12f, TextGap = 4f, TaskRowCorner = 4f;
    /// <summary>A check's top inset (on the first line), its glyph, a drag task's ring inside it.</summary>
    public const float CheckTop = 0f, CheckGlyph = 10f, DragDot = 8f, DragRing = 1.5f;
    /// <summary>The current task's selection pill (WinUI's list pill).</summary>
    public const float PillWidth = 3f, PillHeight = 16f;
    /// <summary>The comparison's controls under its words.</summary>
    public const float CompareTop = 6f;
    /// <summary>The recap: a row's gap and padding, the name column, the number badge (its size, top inset, numeral).</summary>
    public const float RecapGap = 10f, RecapPadY = 8f, RecapName = 64f, Badge = 18f, BadgeTop = 1f, BadgeText = 11f;
    /// <summary>A done line rises into place by this much.</summary>
    public const float Rise = 8f;
    /// <summary>The page's gutter when a tier steps it: its width eases through real layout (the house's 280 ms reflow), so the page's edges
    /// glide instead of stepping.</summary>
    public static readonly FluentGpu.Foundation.LayoutTransition GutterReflow = new(FluentGpu.Foundation.TransitionChannels.Size,
        FluentGpu.Foundation.TransitionDynamics.Tween(GutterMs, FluentGpu.Foundation.Easing.SmoothOut), Size: FluentGpu.Foundation.SizeMode.Reflow,
        Anchor: FluentGpu.Foundation.SizeAnchor.Leading, Axes: FluentGpu.Foundation.SizeAxes.Width);
    public const float GutterMs = 280f;
    /// <summary>A bold term's weight.</summary>
    public const ushort StrongWeight = 600;
}
