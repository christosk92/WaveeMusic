using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee.Components;

// DetailRail (Wavee.FadeUp / .Shove) is visible here unqualified: Wavee.Components nests under the enclosing
// namespace Wavee, so a `using Wavee;` would be a redundant import (CS8019, an error under TreatWarningsAsErrors).

/// <summary>The ONE 18px/800-value-over-11px-caption stat tile — replaces four hand copies
/// (<c>DetailTrailing.CompactStatTile</c>, <c>ModulePage.FactTile</c>, <c>PreReleaseCountdown.UnitTile</c> and
/// <c>TrackFactsStrip</c>'s own reflow copy) that had drifted onto diverging motion specs.</summary>
public static class StatTile
{
    /// <summary>The 18 px/800 value over an 11 px caption on FillCardSecondary — the ONE stat tile (it replaces four hand copies).
    /// The PARENT authors the width (grid column / equal flex share); the tile never measures to its text: the value box and
    /// its TextEl carry MinWidth = 0 so a long value ELLIPSIZES (or, with wrapValue, wraps to two lines) inside the tile
    /// instead of running past its border — today's `MaxLines=1 + CharacterEllipsis` never engaged because the ZStack and
    /// the TextEl kept MinWidth = auto (their own text width). Value swaps cross-fade in place (MotionRecipes.TextSwap) inside
    /// a ClipToBounds box whose size is the tile's, so a swap can never change measurement. Layout motion is Position only.</summary>
    public static Element Create(string key, string value, string caption, bool wrapValue = false, LayoutTransition? layout = null, Element? trailing = null)
    {
        Element valueRun = new TextEl(value)
        {
            Size = 18f, Weight = 800, Color = Tok.TextPrimary, MinWidth = 0f,
            MaxLines = wrapValue ? 2 : 1, Trim = TextTrim.CharacterEllipsis,
            Wrap = wrapValue ? TextWrap.Wrap : TextWrap.NoWrap,
        };
        Element valueBox = new BoxEl
        {
            // ZStack = true (not the Ui.ZStack helper): the helper's signature has no room for MinWidth, and the value
            // box must carry it — an unconstrained ZStack measures to the WIDER of the outgoing/incoming runs mid-swap,
            // which is exactly the re-thrash R3 in the plan calls out.
            ZStack = true, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Key = "v:" + value,
                    Animate = MotionRecipes.TextSwap,
                    Children = [valueRun],
                },
            ],
        };
        Element captionRun = new TextEl(caption) { Size = 11f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

        return new BoxEl
        {
            Key = "fact:" + key, Enter = DetailRail.FadeUp, Layout = layout ?? DetailRail.Shove,
            Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, ClipToBounds = true,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = trailing is null ? [valueBox, captionRun] : [valueBox, captionRun, trailing],
        };
    }
}
