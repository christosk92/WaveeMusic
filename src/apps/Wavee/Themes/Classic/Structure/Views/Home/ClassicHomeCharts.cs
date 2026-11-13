// ── Themes/Classic/Structure/Views/Home/ClassicHomeCharts.cs — Classic's `Home.Charts` (themes plan 6.1, T10a1) ─────────────
//
// From `Home\Zones.UI.cs` §2.5: the Browse facet's "Charts" sub-block (RCA 2026-09-30). "Charts" opens the Charts CATEGORY page; "Top 50" /
// "Viral 50" open the actual playlists the core host picked from the Featured Charts section (`ChartTilesModel`), each on the category
// page until that section lands or when it carries no match. A chart tile is the browse tile with a glyph PLATE where the category art
// would be — the same 48 edge, so the two grids line up. Keyed by the tile's role, so a target landing re-skins the same card. The block is
// one static tree (the template and the skeleton proxy both build it).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Charts block.</summary>
internal static class ClassicHomeCharts
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeCharts"/>.</summary>
    public static Element Template(HomeChartsProps props, in ViewContext ctx) => Tree(props.Model, ClassicHomeTiers.Of(ctx.Tier()).HeaderSubtitles, ctx.Hooks);

    /// <summary>The block's one static tree. <paramref name="subtitle"/> false drops the header's subtitle (Compact, <c>C-Home-Compact</c>).
    /// <paramref name="hooks"/> is the template's (null for the skeleton proxy): a tile that shows what it showed is handed back as the same
    /// element (<see cref="ItemMemo"/>).</summary>
    internal static Element Tree(ChartTilesModel m, bool subtitle, RenderContext? hooks = null)
    {
        Element[] tiles =
        [
            ItemMemo.Of(hooks, new ChartTileKey("charts", Loc.Get(Strings.Home.Charts), Loc.Get(Strings.Home.UpdatedDaily), Icons.Equalizer,
                                                ChartTileTarget.Fallback), s_chartTile),
            ItemMemo.Of(hooks, new ChartTileKey("top50", "Top 50", Loc.Get(Strings.Home.ChartsCaption.Top50Global), Icons.Globe, m.Top50), s_chartTile),
            ItemMemo.Of(hooks, new ChartTileKey("viral50", "Viral 50", Loc.Get(Strings.Home.ChartsCaption.Viral50Country), Icons.Star, m.Viral50),
                        s_chartTile),
        ];
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Stretch,
            Children =
            [
                Ui.SectionHeader(Loc.Get(Strings.Home.Charts), subtitle ? Loc.Get(Strings.Home.UpdatedDaily) : null) with { Margin = default },
                new GridEl
                {
                    MinColWidth = ClassicHomeRules.TilesMinCol, MaxColumns = ClassicHomeRules.ChartsMaxCols,
                    ColGap = Spacing.M, RowGap = Spacing.M, Children = tiles,
                },
            ],
        };
    }

    /// <summary>A chart tile is the browse tile with a glyph PLATE where the category art would be — the same 48 edge, so
    /// the two grids line up. Keyed by the tile's role, so a target landing re-skins the same card. Every tile here is clickable, so they
    /// take `Controls.TileCardParts`: the one-line text and the hand cursor the shared rule gives an invokable surface.</summary>
    static Element ChartTile(in ChartTileKey k)
    {
        var target = k.Target;
        return SettingsCard.Create(new SettingsCard.Options
        {
            Header = k.Title, Description = k.Description, IsClickEnabled = true,
            HeaderIconElement = Controls.IconPlate(k.Glyph, ClassicHomeRules.TileArt, Tok.FillControlDefault, Tok.TextSecondary),
            OnClick = () => HomeVerbs.OpenChart(target),
            Style = Controls.TileCardStyle, Parts = Controls.TileCardParts,
        }) with { Key = "chart-tile:" + k.Key };
    }

    /// <summary>Everything a chart tile shows (its <see cref="ItemMemo"/> key).</summary>
    readonly record struct ChartTileKey(string Key, string Title, string Description, string Glyph, ChartTileTarget Target);

    static readonly Func<ChartTileKey, Element> s_chartTile = static k => ChartTile(in k);
}
