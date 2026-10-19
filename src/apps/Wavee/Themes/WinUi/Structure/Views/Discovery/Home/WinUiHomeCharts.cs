// ── Themes/WinUi/Structure/Views/Home/WinUiHomeCharts.cs — Classic's `Home.Charts` (themes plan 6.1, T10a1) ─────────────
//
// From `Home\Zones.UI.cs` §2.5: the Browse facet's "Charts" sub-block (RCA 2026-09-30). "Charts" opens the Charts CATEGORY page; "Top 50" /
// "Viral 50" open the actual playlists the core host picked from the Featured Charts section (`ChartTilesModel`), each on the category
// page until that section lands or when it carries no match. A chart tile is the browse tile with a glyph PLATE where the category art
// would be — the same 48 edge, so the two grids line up. Keyed by the tile's role, so a target landing re-skins the same card. The block is
// one static tree (the template and the skeleton proxy both build it).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee Classic's Charts block.</summary>
internal static class WinUiHomeCharts
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeCharts"/>.</summary>
    public static Element Template(HomeChartsProps props, in ViewContext ctx) => Tree(props.Model);

    /// <summary>The block's one static tree: the three tiles in one row (W-Home2: no header of its own — the Browse chapter's header covers it), 16 apart,
    /// as many columns as fit at 220.</summary>
    internal static Element Tree(ChartTilesModel m)
    {
        Element[] tiles =
        [
            ChartTile("charts", Loc.Get(Strings.Home.Charts), Loc.Get(Strings.Home.UpdatedDaily), Icons.Equalizer, ChartTileTarget.Fallback),
            ChartTile("top50", "Top 50", Loc.Get(Strings.Home.ChartsCaption.Top50Global), Icons.Globe, m.Top50),
            ChartTile("viral50", "Viral 50", Loc.Get(Strings.Home.ChartsCaption.Viral50Country), Icons.Star, m.Viral50),
        ];
        return new GridEl
        {
            MinColWidth = 220f, MaxColumns = 3,
            ColGap = WinUiHomeRules.CardGap, RowGap = WinUiHomeRules.CardGap, Children = tiles,
        };
    }

    /// <summary>A chart tile is the browse tile with a glyph PLATE where the category art would be — the same 48 edge, so
    /// the two grids line up. Keyed by the tile's role, so a target landing re-skins the same card. Every tile here is clickable, so they
    /// take `Controls.TileCardParts`: the one-line text and the hand cursor the shared rule gives an invokable surface.</summary>
    static Element ChartTile(string key, string title, string description, string glyph, ChartTileTarget target)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = title, Description = description, IsClickEnabled = true,
            HeaderIconElement = Controls.IconPlate(glyph, WinUiHomeRules.TileArt, Tok.FillControlDefault, Tok.TextSecondary),
            OnClick = () => HomeVerbs.OpenChart(target),
            Style = Controls.TileCardStyle, Parts = Controls.TileCardParts,
        }) with { Key = "chart-tile:" + key, Layout = WinUiDiscoveryMotion.Shove };
}
