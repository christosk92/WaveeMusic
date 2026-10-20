// ── Themes/Zune/Structure/Rules/ZuneLintGuide.cs — where Zune's edges belong, for the `--inspect` lint (agent-inspect plan, lane A3) ──
//
// The numbers are the frame's own (the T1 audit's A1 and A2): the title bar and the play bar are window chrome and keep the plain gutter
// (`ZuneTiers.Frame(window).Gutter`: 16 / 24 / 48 / 48 / 64); the band's first pivot and every page's content start on the page column,
// x = content.X + ColumnLead(content.W, gutter), and end on its mirror; from Small to Standard (where ColumnLead IS the gutter) the band's
// last row-2 action, the page's right content edge and the play bar's last glyph land on one x. The guide only DESCRIBES those edges as
// `LintEdge` data (core resolves them against the scene by key); it measures nothing and lays nothing out.
//
// THE POLICIES say what Zune means where a rule would guess (each reason names the board that draws it):
//   · ON HOME ONLY (the route `home`), the captions ellipsise on one line: pins, history rows, the shelves' cards and descriptions, the
//     tiles, the cluster rows and the chart captions are `nowrap` + `text-overflow: ellipsis` on Z-Home and Z-Home-Compact (`TrimOk`). A
//     near-miss (a box a hair too narrow for its words) and a hard clip are still reported: the lint checks both before any intent;
//   · the page title's x is the page column's ink line (`Column`): align.inset measures the page's leads against it;
//   · the panorama hangs left of the page column by its record's `PanoramaNudgeX` (the light face's side bearing, Z-Album), and the settings
//     panorama by `ZuneSettingsRules.PanoramaBleed` (Z-Settings) (`Hang`).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's edge expectations for the lint.</summary>
public sealed class ZuneLintGuide : IThemeLintGuide
{
    /// <summary>The lint rule names the edges report under.</summary>
    private const string Gutter = "gutter", RightEdge = "right-edge";

    /// <summary>How far an edge may be off: a DIP, the audit's measuring precision.</summary>
    private const float Tolerance = 1f;

    /// <summary>The pages whose keyed root IS the page column (<c>ZuneFrameRules.PageRow</c>): the column's box starts one gutter before its content.</summary>
    private static readonly string[] s_pageColumns =
    [
        ZuneHomeRules.ColumnKey, ZuneBrowseRules.DirectoryKey, ZuneProfileRules.ColumnKey, ZuneProfileRules.ListBodyKey,
        ZuneProfileRules.ListHeadKey, ZuneLibraryRules.PageKey, ZuneRecentsRules.PageKey,
    ];

    /// <summary>The panorama voice on its exact line, flush with the edge above it (Z-Album, Z-Liked, Z-Settings: the title's line box on the band's
    /// and the page's top, its ascenders inside): the detail panorama band and the settings title.</summary>
    private static readonly string[] s_topBleeds = [ZuneDetailRules.PanoramaKey, ZuneSettings.TitleKey];

    /// <inheritdoc />
    public IReadOnlyList<string> DesignedTopBleeds => s_topBleeds;

    /// <inheritdoc />
    public IReadOnlyList<LintEdge> Edges(LintContext context)
    {
        var edges = new List<LintEdge>(8);
        float gutter = ZuneTiers.Frame(context.Window).Gutter;
        RectF content = context.RectOf(ZuneFrameLayout.ContentKey) ?? new RectF(0f, 0f, context.WindowW, context.WindowH);
        float lead = ZuneFrameRules.ColumnLead(content.W, gutter);
        float contentLeft = content.X + lead;
        float contentRight = content.X + content.W - lead;

        // The title bar's lead (the wordmark; Back where the tier draws no wordmark) sits on the plain gutter.
        bool wordmark = ZuneFrameRules.TitleBar(context.Window).Wordmark;
        edges.Add(new LintEdge(Gutter, "titlebar.lead", wordmark ? "the title bar's wordmark" : "the title bar's Back button", LintSide.Left,
            gutter, Tolerance, Keys: [wordmark ? ZuneFrameRules.WordmarkKey : ZuneFrameRules.BackKey], Advisory: !wordmark));

        // The band's first pivot and the page's content share the page column's x.
        edges.Add(new LintEdge(Gutter, "band.first-pivot", "the band's first pivot", LintSide.Left,
            contentLeft, Tolerance, Prefixes: [ZuneBandRules.PivotKeyPrefix]));
        edges.Add(new LintEdge(Gutter, "page.column", "the page column's content", LintSide.Left,
            contentLeft - gutter, Tolerance, Keys: s_pageColumns));
        edges.Add(new LintEdge(Gutter, "page.title", "the page title", LintSide.Left,
            contentLeft, Tolerance, Keys: [ZuneFrameRules.PageTitleKey], Column: true));

        // Right edges agree from Small to Standard (the audit's A2); Compact's rows scroll to the window's edge, and from Huge up the page
        // column's mirror and the chrome's plain gutter part on purpose.
        if (context.Window is WidthTier.Small or WidthTier.Standard)
        {
            edges.Add(new LintEdge(RightEdge, "band.last-action", "the band's last row-2 action", LintSide.Right,
                contentRight, Tolerance, Prefixes: [ZuneBandRules.ActionKeyPrefix]));
            edges.Add(new LintEdge(RightEdge, "page.column", "the page column's right content edge", LintSide.Right,
                contentRight + gutter, Tolerance, Keys: s_pageColumns));
            edges.Add(new LintEdge(RightEdge, "playbar.last-glyph", "the play bar's last glyph", LintSide.Right,
                context.WindowW - gutter, Tolerance, UnderKey: ZuneFrameLayout.PlayerBarKey, GlyphsOnly: true, Advisory: true));
        }
        return edges;
    }

    /// <summary>Why Home's captions may ellipsise: the boards draw them so.</summary>
    private const string HomeCaptions = "Home captions ellipsise on one line (Z-Home, Z-Home-Compact: nowrap + text-overflow: ellipsis)";

    /// <summary>The route `status` names Home by: the Home policies hold there only (the same surfaces on Library, Search and Browse follow
    /// their own boards, and the lint's implicit class still counts a MaxLines + ellipsis caption in a repeated family as designed).</summary>
    private const string HomeRoute = "home";

    /// <inheritdoc />
    public IReadOnlyList<LintPolicy> Policies(LintContext context)
    {
        var policies = new List<LintPolicy>(8);
        if (string.Equals(context.Route, HomeRoute, StringComparison.Ordinal))
        {
            policies.Add(new(LintIntent.TrimOk, ZuneHomeRules.PinTilePrefix, "pin captions: " + HomeCaptions));
            policies.Add(new(LintIntent.TrimOk, "zune-history:", "history rows and tiles: " + HomeCaptions));
            policies.Add(new(LintIntent.TrimOk, ZuneListRules.ShelfBodyKey, "shelf card titles and descriptions: " + HomeCaptions));
            policies.Add(new(LintIntent.TrimOk, "zune-shelf-card:", "shelf cards: " + HomeCaptions));
            policies.Add(new(LintIntent.TrimOk, "zune-tile:", "tile titles (two lines, then the ellipsis): " + HomeCaptions));
            policies.Add(new(LintIntent.TrimOk, "zune-cluster-row:", "cluster rows: " + HomeCaptions));
            policies.Add(new(LintIntent.TrimOk, "zune-chart:", "chart captions: " + HomeCaptions));
        }
        policies.Add(new(LintIntent.Hang, ZuneDetailRules.PanoramaKey,
            "the panorama title hangs left of the column by the light face's side bearing (PanoramaNudgeX, Z-Album)",
            Value: ZuneDetailTiers.For(context.Window).PanoramaNudgeX));
        policies.Add(new(LintIntent.Hang, ZuneSettings.TitleKey,
            "the settings panorama bleeds left of the gutter (ZuneSettingsRules.PanoramaBleed: Z-Settings draws it at 40 over a 48 gutter)",
            Value: -MathF.Min(ZuneTiers.Frame(context.Window).Gutter, ZuneSettingsRules.PanoramaBleed)));
        return policies;
    }
}
