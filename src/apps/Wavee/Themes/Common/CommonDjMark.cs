// ── Themes/Common/CommonDjMark.cs — the AI DJ's mark, as a vector ─────────────────────────────────────────────────────
//
// A disc in the DJ's blue with the green ring and its filled segment on it (CommonDjMarkRules.cs), the official client's round DJ
// button. Any size. Disabled, the disc takes the subtle fill and the mark the disabled ink, so the button reads as waiting.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using D = Wavee.Themes.Common.CommonDjMarkRules;

namespace Wavee.Themes.Common;

internal static class CommonDjMark
{
    /// <summary>The mark in a <paramref name="size"/>-DIP square.</summary>
    public static Element Create(DjChrome dj, float size)
    {
        ColorF disc = !dj.CanPick ? Tok.FillSubtleSecondary : ColorScience.ToColor(D.DiscArgb);
        ColorF mark = !dj.CanPick ? Tok.TextDisabled : ColorScience.ToColor(D.MarkArgb);
        return new BoxEl
        {
            Width = size, Height = size, Shrink = 0f, ZStack = true, Corners = CornerRadius4.All(size / 2f), Fill = disc,
            HitTestVisible = false,
            Children =
            [
                new PathEl
                {
                    Geometry = D.Ring, Width = size, Height = size, ViewBoxW = D.ViewBox, ViewBoxH = D.ViewBox, Shrink = 0f,
                    StrokeColor = mark, Stroke = new StrokeStyle(D.RingStroke, LineCap.Round, LineJoin.Round),
                },
                new PathEl
                {
                    Geometry = D.Segment, Width = size, Height = size, ViewBoxW = D.ViewBox, ViewBoxH = D.ViewBox, Shrink = 0f,
                    Fill = mark,
                },
            ],
        };
    }
}
