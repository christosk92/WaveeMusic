// ── Themes/WinUi/Structure/Views/Detail/WinUiLikedChipBar.cs — WinUI's Liked chip rail (`Liked.ChipBar`; P6 playlist-load lane) ─────────────────────────
//
// The core's chip rail (`LikedChipBarProps`: "All" first, exclusive selection, the un-evidenced tail disabled, re-tapping the active chip clears it), drawn with the
// stock toggle chips. At Compact every chip is a 40 target (W-LikedSongs-Compact: All · Chill · Dance ... at 40, the owner's Compact rule); from Small up the stock 32.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

internal static class WinUiLikedChipBar
{
    static readonly TemplateParts s_compact = new()
    {
        [ToggleButton.PartRoot] = static e => e with { Shrink = 0f, Height = WinUiDetailRules.CompactTarget, MinHeight = WinUiDetailRules.CompactTarget, MinWidth = WinUiDetailRules.CompactTarget },
    };

    /// <summary>The registered template of <see cref="ViewKeys.LikedChipBar"/>.</summary>
    public static Element Template(LikedChipBarProps p, in ViewContext ctx)
    {
        if (p.Titles.Length == 0) return new BoxEl { Key = "chips:off", HitTestVisible = false };

        TemplateParts? parts = ctx.Tier().Window == WidthTier.Compact ? s_compact : null;
        var selectAll = p.SelectAll;
        var select = p.Select;
        string? selected = p.Selected;
        // A crossing to or from Compact resizes every chip (32 ↔ 40): each keeps its node and reflows to its new box (audit B9).
        LayoutTransition? arrange = Design.Reduced ? null : WinUiDiscoveryMotion.Reflow;
        var kids = new Element[p.Titles.Length + 1];
        kids[0] = Controls.Chip(Loc.Get(Strings.Detail.Filter.AllChip), selected is null, true, selectAll, parts) with { Key = "chip:all", Layout = arrange };
        for (int i = 0; i < p.Titles.Length; i++)
        {
            string tag = p.Titles[i];
            bool on = string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase);
            bool available = i < p.Evidenced;
            kids[i + 1] = Controls.Chip(tag, on, available, available ? () => select(on ? null : tag) : null, parts) with { Key = "chip:" + tag, Layout = arrange };
        }
        if (!p.Inline) return Controls.ChipRail(kids, p.ScrollKey) with { Key = "chips" };

        // INLINE: the same scrolling line inside the page bar, its end fading where more chips wait.
        return Ui.ScrollView(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(Controls.ChipRailInsetX, 0f, Controls.ChipRailInsetX, 0f),
            Children = kids,
        }, horizontal: true) with
        {
            Key = "chips", Grow = 1f, MinWidth = 0f, AutoEdgeFade = true, AutoEdgeFadeBand = WinUiDetailRules.ChipRailFade,
            SuppressScrollBar = true, ScrollKey = p.ScrollKey,
        };
    }
}
