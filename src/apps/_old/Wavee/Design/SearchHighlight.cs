using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The library-search accent pill: [before] [matched run on AccentSelectedTextBackground] [after].
/// Shared by the library rows and the Charts grid titles — one look.</summary>
static class SearchHighlight
{
    /// <param name="maxLines">How many lines the highlighted title may occupy. 1 (the default) keeps the single-line
    /// library-row look. &gt;1 lets the RUN ROW wrap — flex wrap, breaking BETWEEN runs, because a paragraph cannot
    /// carry the pill: <c>SpanTextEl</c> shapes runs into one flow but has no per-span background fill, so a real
    /// paragraph would have to trade the pill for an accent-coloured run. Wrapping the row keeps the one look the
    /// library rows and the Charts grid share.</param>
    public static Element Row(string text, int matchStart, int matchLen, float size, ushort weight, ColorF baseColor,
                              int maxLines = 1)
    {
        int lines = maxLines < 1 ? 1 : maxLines;
        bool wrap = lines > 1;
        if (matchLen <= 0 || matchStart < 0 || matchStart + matchLen > text.Length)
            return new TextEl(text)
            {
                Size = size, Weight = weight, Color = baseColor, MinWidth = 0f,
                Wrap = wrap ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines,
                Trim = TextTrim.CharacterEllipsis,
            };

        // A wrapping row lets the UNMATCHED runs break internally too, so a long tail ("herlands") is not forced to
        // ellipsise just because it could not fit beside the pill. Grow stays off when wrapping: a grown run would eat
        // the whole first line and push the pill down on its own.
        Element Seg(string s, bool grow) => new TextEl(s)
        {
            Size = size, Weight = weight, Color = baseColor, Grow = wrap ? 0f : (grow ? 1f : 0f), MinWidth = 0f,
            Wrap = wrap ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines, Trim = TextTrim.CharacterEllipsis,
        };

        var kids = new List<Element>(3);
        if (matchStart > 0) kids.Add(Seg(text.Substring(0, matchStart), false));
        kids.Add(new BoxEl
        {
            Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.AccentSelectedTextBackground,
            Padding = new Edges4(3f, 1f, 3f, 1f),
            Children =
            [
                new TextEl(text.Substring(matchStart, matchLen))
                {
                    Size = size, Weight = weight, Color = Tok.TextOnAccentSelectedText,
                    MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
            ],
        });
        int after = matchStart + matchLen;
        // The trailing spacer exists only to push a single-line row's runs left; a wrapping row must not carry one, or
        // flex hands it the rest of line one and the following runs break early.
        if (after < text.Length) kids.Add(Seg(text.Substring(after), true));
        else if (!wrap) kids.Add(new BoxEl { Grow = 1f });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            // ClipToBounds + Basis 0 belong to the single-line row: on a wrapping one they clip the second line away
            // and collapse the row's basis before wrap can be measured. The MaxHeight cap is what bounds the wrap
            // instead — the run row has no MaxLines of its own, the way a single TextEl does.
            Wrap = wrap, Grow = wrap ? 0f : 1f, Basis = wrap ? float.NaN : 0f, ClipToBounds = !wrap,
            MaxHeight = wrap ? lines * LineBoxFor(size) : float.NaN,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The line box a run of <paramref name="size"/> occupies — the engine's own type ladder
    /// (<c>Ui.BodyStrong</c> 14/20, <c>Ui.Caption</c> 12/16), so the wrap cap matches what the shaper will lay.
    /// Anything off the ladder falls back to the WinUI-standard 1.43 ratio.</summary>
    static float LineBoxFor(float size) => size switch
    {
        14f => 20f,
        12f => 16f,
        _ => MathF.Ceiling(size * 1.43f),
    };
}
