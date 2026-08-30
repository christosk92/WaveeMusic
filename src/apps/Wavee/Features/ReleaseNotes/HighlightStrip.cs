using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The three-up highlight row under the hero, with its eyebrow.
///
/// <para>Three is a cap, not a layout: two highlights render as two equal cards and one renders full width, because a
/// release with one headline feature should not be padded out with empty boxes. Renders NOTHING (a zero-height,
/// hit-invisible box) when the document has no highlights at all.</para></summary>
static class HighlightStrip
{
    public const int Max = 3;

    public static Element Create(IReadOnlyList<HighlightItem>? highlights, Action<string, string?>? nav)
    {
        if (highlights is null || highlights.Count == 0)
            return new BoxEl { Height = 0f, HitTestVisible = false };

        int n = highlights.Count > Max ? Max : highlights.Count;
        var cards = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            var item = highlights[i];
            cards.Add(HighlightCard.Create(item, nav)
                with { Key = "hl:" + item.Doc.Version + ":" + (item.Highlight.Id is { Length: > 0 } id ? id : i.ToString()) });
        }

        return new BoxEl
        {
            Direction = 1, Gap = 6f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                WaveeType.Eyebrow(Loc.Get(Strings.WhatsNew.Highlights)) with { Color = Tok.TextTertiary },
                new BoxEl
                {
                    Direction = 0, Gap = 10f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Children = cards.ToArray(),
                },
            ],
        };
    }
}
