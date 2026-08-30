using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>The Welcome page's own display cuts — deliberately NOT added to <see cref="WaveeType"/>'s ramp (its
/// documented weight policy is 400/600, with three sanctioned divergences already spoken for: 700 for the Artist*
/// identity trio, 350 SemiLight for <c>PivotLabel</c>/<c>NpvLyric</c>). The prototype's headline
/// (<c>font: 200 62px</c>) is off BOTH the type ramp and that policy — Weight 200 is not shippable at display sizes
/// on Segoe UI Variable Display in light theme (the strokes break up). These two cuts are the sanctioned
/// replacement, at Weight 300, sized to <see cref="SetupLayout.HeroEnterWidth"/>: <see cref="Display"/> for the
/// full (&gt;=700 DIP) plate, <see cref="Small"/> once the hero column drops and the content column narrows.</summary>
static class SetupType
{
    /// <summary>~64/60, the Welcome headline at full plate width. <see cref="LineStacking.BlockLineHeight"/> is
    /// load-bearing: the default <c>MaxHeight</c> takes max(explicit, font-natural), and at 64 px Segoe UI Variable
    /// Display's natural line box is ~87 DIP — so an explicit 70 was silently ignored and the two headline lines sat
    /// ~87 DIP apart. The Zune cut is deliberately TIGHTER than natural (the prototype uses line-height 0.94), and
    /// BlockLineHeight is the only strategy that lets an explicit value win.</summary>
    public static SpanTextEl Display(TextSpan[] spans) => new(spans)
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 64f,
        LineHeight = 60f,                       // 0.94x, matching the prototype
        LineStacking = LineStacking.BlockLineHeight,
        Weight = 300,
        CharSpacing = -28f,
        Wrap = TextWrap.Wrap,
        Color = Tok.TextPrimary,
    };

    /// <summary>~44/50, the same headline once the plate drops its hero column (below the 700-DIP breakpoint) — a
    /// narrower content column can no longer afford the full display cut's line length.</summary>
    public static SpanTextEl Small(TextSpan[] spans) => new(spans)
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 44f,
        LineHeight = 42f,                       // 0.94x, same reasoning as Display above
        LineStacking = LineStacking.BlockLineHeight,
        Weight = 300,
        CharSpacing = -16f,
        Wrap = TextWrap.Wrap,
        Color = Tok.TextPrimary,
    };
}
