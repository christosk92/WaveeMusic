// ── Wavee.Tests/Theming/WinUiListsTests.cs — WinUI's lists: the table ladder, the policies, the density (P6 W3) ──────────────────────────
//
// Pure facts of the lists lane: the table-tier ladder (< 560 two-line, 560-859 drops Plays / Date / Album, from 860 every lane but Added-by, each
// edge held 24 on widening), the list policy per `artworkInRows` (ON the Modern two-line skin, OFF the Classic one-line skin), the detail table's
// record per window tier, the density the "Row size" menu names (published rung, the style's `rowDensity` id, never Classic's `density`), and
// WinUI's own list styles being what the registry holds.

using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class WinUiListsTests : IDisposable
{
    public WinUiListsTests() => ThemeHost.Reset();

    public void Dispose() => ThemeHost.Reset();

    [Theory]
    [InlineData(300f, 4)]
    [InlineData(559f, 4)]
    [InlineData(560f, 3)]
    [InlineData(700f, 3)]
    [InlineData(859f, 3)]
    [InlineData(860f, 1)]
    [InlineData(1600f, 1)]
    public void A_fresh_column_width_resolves_to_its_band(float width, int tier)
        => Assert.Equal(tier, WinUiListTiers.TierFor(width, previous: 0, initialized: false));

    [Fact]
    public void Narrowing_yields_at_once_and_widening_holds_24()
    {
        // Widening from the narrow band: 560 itself is not enough, 584 is; 860 needs 884.
        Assert.Equal(WinUiListTiers.TierNarrow, WinUiListTiers.TierFor(570f, WinUiListTiers.TierNarrow));
        Assert.Equal(WinUiListTiers.TierMid, WinUiListTiers.TierFor(584f, WinUiListTiers.TierNarrow));
        Assert.Equal(WinUiListTiers.TierMid, WinUiListTiers.TierFor(870f, WinUiListTiers.TierMid));
        Assert.Equal(WinUiListTiers.TierWide, WinUiListTiers.TierFor(884f, WinUiListTiers.TierMid));
        // Narrowing: the wide band is left below 860, the mid band below 560.
        Assert.Equal(WinUiListTiers.TierMid, WinUiListTiers.TierFor(859f, WinUiListTiers.TierWide));
        Assert.Equal(WinUiListTiers.TierWide, WinUiListTiers.TierFor(860f, WinUiListTiers.TierWide));
        Assert.Equal(WinUiListTiers.TierNarrow, WinUiListTiers.TierFor(559f, WinUiListTiers.TierMid));
    }

    [Fact]
    public void The_policy_names_the_table_ladder_and_the_lists_policy_follows_the_artwork_option()
    {
        Assert.True(WinUiTiers.Policy.TryGetLadder(DetailLadders.Table, out var ladder));
        Assert.Same(WinUiListTiers.Table, ladder);

        var on = WinUiPolicies.For(WinUiParameters.Default).Lists;
        Assert.True(on.TrackArtwork);
        Assert.Equal(TrackRowStyle.Modern, on.RowStyle);

        var off = WinUiPolicies.For(WinUiParameters.Default with { ArtworkInRows = false }).Lists;
        Assert.False(off.TrackArtwork);
        Assert.Equal(TrackRowStyle.Classic, off.RowStyle);
    }

    [Fact]
    public void The_detail_table_records_draw_the_compact_row_and_the_trailing_heart()
    {
        var tiers = WinUiPolicies.For(WinUiParameters.Default).DetailTiers;
        var compact = tiers.For(WidthTier.Compact).Table;
        Assert.True(compact.CompactRows);
        Assert.True(compact.KeepDuration);
        Assert.True(compact.HeartTrailing);
        Assert.False(compact.VersionsLane);
        Assert.Equal(56f, compact.ModernRow);
        Assert.Equal(40f, compact.Art);
        Assert.False(tiers.For(WidthTier.Small).Table.VersionsLane);
        // The time lane is the boards' 72 (80 at Huge), wide enough for "Unavailable"; the Compact row keeps the core's.
        Assert.Equal(72f, tiers.For(WidthTier.Standard).Table.DurationWidth);
        Assert.Equal(80f, tiers.For(WidthTier.Huge).Table.DurationWidth);
        Assert.Equal(0f, compact.DurationWidth);
        Assert.True(tiers.For(WidthTier.Standard).Table.VersionsLane);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
            Assert.True(tiers.For(tier).Table.HeartTrailing);
        // An album's one-line rows stand under the two-line row: 40 under 48.
        Assert.Equal(40f, tiers.For(WidthTier.Standard).Table.RowFor(classic: false, WinUiDensityOption.RowDouble(WinUiDensity.Standard)) - WinUiListRules.AlbumRowTrim);
        // Huge: the board's 72 rows with a 64 cover.
        Assert.Equal(72f, tiers.For(WidthTier.Huge).Table.RowFor(false, 48f));
        Assert.Equal(64f, tiers.For(WidthTier.Huge).Table.ArtFor(40f));
    }

    [Fact]
    public void The_density_menu_names_the_style_option_and_the_published_rung()
    {
        Assert.Equal("style.rowDensity", WinUiListRules.DensityQualifiedId);
        Assert.Equal(WinUiDensity.Compact, WinUiListRules.DensityOfRung(WinUiDensityOption.Rows(WinUiDensity.Compact).Rung));
        Assert.Equal(WinUiDensity.Standard, WinUiListRules.DensityOfRung(WinUiDensityOption.Rows(WinUiDensity.Standard).Rung));
        // The two-line row's cover: 40 in the 48, 32 in the compact 40.
        Assert.Equal(40f, WinUiDensityOption.Rows(WinUiDensity.Standard).ModernArt);
        Assert.Equal(32f, WinUiDensityOption.Rows(WinUiDensity.Compact).ModernArt);
    }

    [Fact]
    public void Winuis_list_styles_are_its_own_and_depth_blur_the_stage()
    {
        var registry = WinUiTheme.Create().Registry;
        Assert.True(registry.TryGetStyle(StyleKeys.TrackTable, out var table));
        Assert.Same(WinUiListStyles.TrackTable, table);
        Assert.True(registry.TryGetStyle(StyleKeys.EagerTrackRow, out var eager));
        Assert.Same(WinUiListStyles.EagerTrackRow, eager);
        Assert.True(registry.TryGetStyle(StyleKeys.EpisodeRow, out var episode));
        Assert.Same(WinUiListStyles.EpisodeRow, episode);
        Assert.True(registry.TryGetStyle(StyleKeys.Surface, out var surface));
        Assert.Same(WinUiListStyles.Surface, surface);
        Assert.True(registry.TryGetStyle(StyleKeys.LyricsLines, out var lyrics));
        Assert.Same(WinUiListStyles.LyricsLines, lyrics);
        Assert.True(lyrics!.StageDepthBlur);
        Assert.True(registry.TryGetStyle(StyleKeys.ArtistChart, out var chart));
        Assert.Same(WinUiListStyles.ArtistChart, chart);
        Assert.True(registry.TryGetStyle(StyleKeys.TrackDrawer, out var drawer));
        Assert.Same(WinUiListStyles.TrackDrawer, drawer);
    }
}
