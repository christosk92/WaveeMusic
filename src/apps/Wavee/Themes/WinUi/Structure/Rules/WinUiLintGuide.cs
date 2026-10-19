// ── Themes/WinUi/Structure/Rules/WinUiLintGuide.cs — where WinUI's edges belong, for the `--inspect` lint (P6 W0) ────────────────────
//
// The numbers are the frame record's (`WinUiTiers.Frame(window)`): every page's content starts at the content card's left edge plus the
// tier's inset (16 / 24 / 36 / 48) and ends at its mirror. The guide only DESCRIBES those edges as `LintEdge` data (core resolves them
// against the scene by key); it measures nothing and lays nothing out. An edge whose key no node carries is skipped, so the guide is inert
// over the Classic stand-in and starts checking as the family lanes key their WinUI views (`WinUiFrameLayout.PageTitleKey`).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's edge expectations for the lint.</summary>
public sealed class WinUiLintGuide : IThemeLintGuide
{
    /// <summary>The lint rule names the edges report under.</summary>
    private const string Gutter = "gutter";

    /// <summary>How far an edge may be off: a DIP, the audit's measuring precision.</summary>
    private const float Tolerance = 1f;

    /// <inheritdoc />
    public IReadOnlyList<LintEdge> Edges(LintContext context)
    {
        float inset = WinUiTiers.Frame(context.Window).InsetX;
        RectF content = context.RectOf(WinUiFrameLayout.ContentKey) ?? new RectF(0f, 0f, context.WindowW, context.WindowH);

        // The page title sits on the content inset (W-Tiers "Content inset": 16 / 24 / 36 / 48 left and right).
        return
        [
            new LintEdge(Gutter, "page.title", "the page title", LintSide.Left, content.X + inset, Tolerance,
                Keys: [WinUiFrameLayout.PageTitleKey]),
        ];
    }
}
