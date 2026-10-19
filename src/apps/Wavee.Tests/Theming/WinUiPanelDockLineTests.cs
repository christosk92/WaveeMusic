// ── Wavee.Tests/Theming/WinUiPanelDockLineTests.cs — WinUI's dock line: nav + 340 + 480 (P6 W4a) ───────────────────────────────────
//
// `PanelTierRule.PlacementAt`: the panel docks where the page keeps its 480 floor beside the nav pane and the panel; narrower, it floats at the
// same width; a floating panel docks again only past the floor plus the 24 hold (so a resize never chatters at the line).

using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class WinUiPanelDockLineTests
{
    static PanelTierRule Standard => WinUiPlayerTiers.Panel.Tiers[2];

    [Fact]
    public void Docks_at_the_line_and_floats_below_it()
    {
        // The 48 rail: 48 + 340 + 480 = 868 (the board's Small line); the 280 pane: 1100 (Standard).
        Assert.Equal(PanelPlacement.Dock, Standard.PlacementAt(868f, 48f, 340f, dockedNow: true));
        Assert.Equal(PanelPlacement.Float, Standard.PlacementAt(867f, 48f, 340f, dockedNow: true));
        Assert.Equal(PanelPlacement.Dock, Standard.PlacementAt(1100f, 280f, 340f, dockedNow: true));
        Assert.Equal(PanelPlacement.Float, Standard.PlacementAt(1099f, 280f, 340f, dockedNow: true));
    }

    [Fact]
    public void A_floating_panel_docks_again_only_past_the_hold()
    {
        Assert.Equal(PanelPlacement.Float, Standard.PlacementAt(1100f, 280f, 340f, dockedNow: false));
        Assert.Equal(PanelPlacement.Float, Standard.PlacementAt(1123f, 280f, 340f, dockedNow: false));
        Assert.Equal(PanelPlacement.Dock, Standard.PlacementAt(1124f, 280f, 340f, dockedNow: false));
    }

    [Fact]
    public void The_dock_line_follows_a_resized_panel()
    {
        Assert.Equal(PanelPlacement.Float, Standard.PlacementAt(1100f, 280f, 480f, dockedNow: true));
        Assert.Equal(PanelPlacement.Dock, Standard.PlacementAt(1240f, 280f, 480f, dockedNow: true));
    }

    [Fact]
    public void Compact_is_a_sheet_and_Huge_keeps_two_panes()
    {
        Assert.Equal(PanelPlacement.Sheet, WinUiPlayerTiers.Panel.Tiers[0].PlacementAt(2000f, 0f, 340f, dockedNow: true));
        Assert.Equal(2, WinUiPlayerTiers.Panel.Tiers[3].MaxDocked);
    }
}
