using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One changelog bullet: an optional scope eyebrow, the sentence as ONE selectable span run, then the
/// reference chips and the contributor stack.
///
/// <para>The chips sit on their own wrapping row UNDER the sentence rather than inline in it: a span run cannot carry a
/// box, and a chip is a box (dot + mono number + state word + tooltip). Keeping them out of the flow also means a long
/// sentence wraps as prose instead of reflowing three chips every time the window changes width.</para></summary>
static class ChangelogItem
{
    public static Element Create(ReleaseItem item, IssueStateCache? states, Action<string> openUrl)
    {
        var tokens = MarkdownLite.Tokenize(item.Text);
        var spans = new List<TextSpan>(tokens.Length + 1);
        // The scope reads as part of the sentence ("PLAYER  Docked video — …"), which is what the prototype draws, and
        // as one paragraph it stays selectable/copyable with the text it labels.
        if (item.Scope is { Length: > 0 } scope)
            spans.Add(new TextSpan(scope.ToUpperInvariant() + "   ", Weight: 700, Color: Tok.TextTertiary, Size: 11f));
        spans.AddRange(ReleaseNotesText.ToSpans(tokens, openUrl));

        var refs = new List<Element>(item.Issues.Length + item.Prs.Length);
        foreach (var i in item.Issues) refs.Add(IssueChip.Create(i, states?.Lookup(i), openUrl));
        foreach (var p in item.Prs) refs.Add(IssueChip.Pr(p, openUrl));

        var column = new List<Element>(2)
        {
            RichTextBlock.Paragraph(spans.ToArray()) with
            {
                Size = 13f, Grow = 1f, MinWidth = 0f, MaxWidth = float.NaN, Wrap = TextWrap.Wrap,
            },
        };
        if (refs.Count > 0)
            column.Add(new BoxEl
            {
                Direction = 0, Gap = 6f, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = refs.ToArray(),
            });

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
            Padding = new Edges4(Spacing.M, 9f, Spacing.M, 9f),
            Children =
            [
                new BoxEl { Direction = 1, Gap = 6f, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = column.ToArray() },
                Avatars.Create(item.Contributors),
            ],
        };
    }
}
