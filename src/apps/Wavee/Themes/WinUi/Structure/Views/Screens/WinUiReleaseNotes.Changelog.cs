// ── Themes/WinUi/Structure/Views/Screens/WinUiReleaseNotes.Changelog.cs — the bridge from the tokenizer to span runs ───────────────────────
//
// What the viewer's body draws its sentences with: a sentence is one paragraph whose bold / code / link runs are real spans. What a link does
// is the caller's (`openUrl`: the browser, or nothing inside a card).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.WinUi.WinUiReleaseNotesRules;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiReleaseNotes
{
    /// <summary>The one bridge from the pure tokenizer to span runs: a sentence is ONE paragraph whose bold / code / link
    /// runs are real spans. <paramref name="openUrl"/> decides what a link does (the browser, or nothing inside a card).</summary>
    static TextSpan[] ToSpans(InlineToken[] tokens, Action<string> openUrl)
    {
        if (tokens.Length == 0) return [];
        var spans = new TextSpan[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            spans[i] = t.Kind switch
            {
                InlineKind.Bold => RichTextBlock.Bold(t.Text),
                // Monospace one step down (the WinUI inline-code convention); no plate — a span carries no box.
                InlineKind.Code => new TextSpan(t.Text, Weight: t.Bold ? (ushort)600 : (ushort)0, Size: K.CodeSpanSize, FontFamily: "Cascadia Code", Color: Tok.TextSecondary),
                InlineKind.Link or InlineKind.Url => SpanLink(t.Text, t.Target ?? t.Text, openUrl),
                InlineKind.Issue => SpanLink(t.Text, Links.IssueUrl(t.Repo, t.Number, pr: false), openUrl),
                InlineKind.Pr => SpanLink(t.Text, Links.IssueUrl(t.Repo, t.Number, pr: true), openUrl),
                InlineKind.Mention => SpanLink(t.Text, "https://github.com/" + (t.Target ?? t.Text.TrimStart('@')), openUrl),
                _ => RichTextBlock.Run(t.Text),
            };
        }
        return spans;
    }

    static TextSpan SpanLink(string text, string url, Action<string> open) => RichTextBlock.Hyperlink(text, () => open(url));
}
