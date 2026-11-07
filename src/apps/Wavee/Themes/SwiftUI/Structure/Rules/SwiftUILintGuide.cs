// ── Themes/SwiftUI/Structure/Rules/SwiftUILintGuide.cs — the SwiftUI spike's edges for the `--inspect` lint (SPIKE) ────────────────────
//
// The borrowed WinUI pages draw their title on the page inset (16 / 24 / 36 / 48 by window tier) from the detail column's left edge, and the
// stock SelectorBar / ListView paddings WinUI's guide allows are the same here.

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.SwiftUI;

/// <summary>The SwiftUI spike's lint guide.</summary>
public sealed class SwiftUILintGuide : IThemeLintGuide
{
    private const string Gutter = "gutter";
    private const float Tolerance = 1f;

    /// <inheritdoc />
    public IReadOnlyList<LintEdge> Edges(LintContext context)
    {
        float inset = SwiftUIFrameRules.InsetX(context.Window);
        RectF content = context.RectOf(SwiftUIFrameRules.ContentKey) ?? new RectF(0f, 0f, context.WindowW, context.WindowH);
        return
        [
            new LintEdge(Gutter, "page.title", "the page title", LintSide.Left, content.X + inset, Tolerance,
                Keys: [SwiftUIFrameRules.PageTitleKey], Column: true),
        ];
    }

    private static readonly LintPolicy[] s_policies =
    [
        new(LintIntent.Allow, "home:facet:words", "the facet words are a stock SelectorBar: each item pads its words in from the column", Rule: "align.inset"),
        new(LintIntent.ItemPlate, "hit:", "search hits are list rows: the row's hover plate starts on the column and pads the art in"),
        new(LintIntent.ItemPlate, "facets", "the search facets are a stock SelectorBar: each item pads its words in from the column"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<LintPolicy> Policies(LintContext context) => s_policies;
}
