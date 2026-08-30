using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core.ReleaseNotes;

namespace Wavee;

/// <summary>The one bridge between the pure inline tokenizer (<see cref="MarkdownLite"/>) and the engine's span-run
/// text model (<see cref="TextSpan"/>): a release-note sentence becomes ONE selectable, wrapping paragraph whose
/// bold/code/link runs are real spans, not four stacked <c>TextEl</c>s.
///
/// <para>Issue and PR references inside the SENTENCE render as plain links here; the coloured state CHIPS are separate
/// elements after the paragraph (see <see cref="IssueChip"/>), because a chip carries a dot, a state word and a
/// tooltip — none of which a text run can express.</para></summary>
static partial class ReleaseNotesText
{
    /// <summary>Map inline tokens onto span runs. <paramref name="openUrl"/> is invoked on click for every link-ish
    /// run — the caller decides whether that means the browser or something else.</summary>
    public static TextSpan[] ToSpans(InlineToken[]? tokens, Action<string> openUrl)
    {
        if (tokens is null || tokens.Length == 0) return [];
        var spans = new List<TextSpan>(tokens.Length);
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            switch (t.Kind)
            {
                case InlineKind.Bold:
                    spans.Add(RichTextBlock.Bold(t.Text));
                    break;
                case InlineKind.Code:
                    // Monospace + one step down, the WinUI inline-code convention. No background: a span run carries
                    // no box, and a per-run plate would need its own element (which would break the flow).
                    spans.Add(new TextSpan(t.Text, Weight: t.Bold ? (ushort)600 : (ushort)0, Size: 12f, FontFamily: "Cascadia Code", Color: Tok.TextSecondary));
                    break;
                case InlineKind.Link:
                case InlineKind.Url:
                {
                    string target = t.Target ?? t.Text;
                    spans.Add(RichTextBlock.Hyperlink(t.Text, () => openUrl(target)));
                    break;
                }
                case InlineKind.Issue:
                    spans.Add(Ref(t, pr: false, openUrl));
                    break;
                case InlineKind.Pr:
                    spans.Add(Ref(t, pr: true, openUrl));
                    break;
                case InlineKind.Mention:
                {
                    string url = "https://github.com/" + (t.Target ?? t.Text.TrimStart('@'));
                    spans.Add(RichTextBlock.Hyperlink(t.Text, () => openUrl(url)));
                    break;
                }
                default:
                    spans.Add(RichTextBlock.Run(t.Text));
                    break;
            }
        }
        return spans.ToArray();
    }

    static TextSpan Ref(in InlineToken t, bool pr, Action<string> openUrl)
    {
        string url = IssueUrl(t.Repo, t.Number, pr);
        return RichTextBlock.Hyperlink(t.Text, () => openUrl(url));
    }
}
