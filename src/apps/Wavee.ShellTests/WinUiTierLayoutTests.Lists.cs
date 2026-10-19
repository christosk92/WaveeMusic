// ── Wavee.ShellTests/WinUiTierLayoutTests.Lists.cs — WinUI's track table mounted, per tier (themes plan 3.5; P6 W3) ──────────────────────
//
// The lists lane's fixtures: an album page and a playlist page under WinUI, asserted on records and keyed nodes, never on pixels.
//
//   registered   the app resolves WinUI's own list styles (not Classic's stand-in) and WinUI's list policy
//   rows         a playlist's rows stand at the density's 48 with WinUI's header ("winui.list.header") and the full-width plate (the drop line key); an
//                album's numbered rows at 40 (48 less the one-line trim)
//   lanes        the heart stands in its own trailing lane; Album and Date show from 860 of list column (1440 window) and not below it (800 window); Added-by
//                never (no WinUI board draws it); Small has no versions chevron lane
//   density      the style's `rowDensity` (the id the "Row size" menu requests) re-sizes the rows live: 40 compact, 48 default
//   rows (C)     below 641 the Compact row form: no column header, 56-tall rows, the time and a 40 "..." on every row

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class WinUiTierLayoutTests
{
    static readonly Shell.Route s_listsAlbum = Shell.For(EntityUri.Parse("spotify:album:al2"), "Album");
    static readonly Shell.Route s_listsPlaylist = Shell.For(EntityUri.Parse("spotify:playlist:pl1"), "Playlist");

    const string ListHeaderKey = "winui.list.header", ListDropKey = "winui.row.drop";

    static void OpenList(Shell.HeadlessShell shell, Shell.Route route, string what)
    {
        Shell.GoTo(route);
        UntilBackground(shell, () => shell.FindPrefix("row").Count(k => k.Key == "row") > 2 && shell.FindPrefix(ListDropKey).Count > 0, what);
        shell.RunFrames(40);
    }

    static float FirstListRowHeight(Shell.HeadlessShell shell)
    {
        var scene = shell.Host.Scene;
        return shell.FindPrefix("row").Where(k => k.Key == "row")
            .Select(k => scene.Bounds(k.Node)).Where(b => b.H > 0f).OrderBy(b => b.Y).First().H;
    }

    [Fact]
    public void Every_list_key_is_winuis_own()
        => Mounted(shell =>
        {
            shell.RunFrames(10);
            var registry = ThemeHost.Registry;
            Assert.True(registry.TryGetStyle(Wavee.Theming.Contracts.StyleKeys.TrackTable, out var table));
            Assert.Same(WinUiListStyles.TrackTable, table);
            Assert.True(registry.TryGetStyle(Wavee.Theming.Contracts.StyleKeys.LyricsLines, out var lyrics));
            Assert.Same(WinUiListStyles.LyricsLines, lyrics);
            var lists = ThemeHost.Facts.Peek().Policies.Lists;
            Assert.Equal(TrackRowStyle.Modern, lists.RowStyle);
            Assert.True(lists.TrackArtwork);
            Assert.Same(WinUiListRules.DetailTiers, ThemeHost.Facts.Peek().Policies.DetailTiers);
        }, 1440f, 960f);

    [Fact]
    public void A_playlist_draws_two_line_rows_at_48_with_the_trailing_heart_and_no_added_by()
        => Mounted(shell =>
        {
            OpenList(shell, s_listsPlaylist, "the playlist's rows under WinUI");
            Assert.NotEmpty(shell.FindPrefix(ListHeaderKey));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Art));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.HeartTrailing));
            Assert.DoesNotContain(shell.FindPrefix(Track.CellKey.Heart), k => k.Key == Track.CellKey.Heart);
            Assert.Empty(shell.FindPrefix(Track.CellKey.By));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Album));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Date));
            Assert.InRange(FirstListRowHeight(shell), 47f, 49f);
        }, 1440f, 960f);

    [Fact]
    public void An_albums_numbered_rows_are_one_line_at_40()
        => Mounted(shell =>
        {
            OpenList(shell, s_listsAlbum, "the album's rows under WinUI");
            Assert.Empty(shell.FindPrefix(Track.CellKey.Art));
            Assert.InRange(FirstListRowHeight(shell), 39f, 41f);
        }, 1440f, 960f);

    [Fact]
    public void Album_and_date_leave_below_the_860_column_and_the_small_band_has_no_chevron_lane()
        => Mounted(shell =>
        {
            OpenList(shell, s_listsPlaylist, "the playlist's rows under WinUI at Small");
            Assert.Empty(shell.FindPrefix(Track.CellKey.Album));
            Assert.Empty(shell.FindPrefix(Track.CellKey.Date));
            Assert.Empty(shell.FindPrefix(Track.CellKey.Plays));
            Assert.Empty(shell.FindPrefix(Track.CellKey.Expand));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.HeartTrailing));
        }, 800f, 600f);

    [Fact]
    public void The_compact_band_draws_the_compact_row_with_no_header()
        => Mounted(shell =>
        {
            OpenList(shell, s_listsPlaylist, "the playlist's rows under WinUI at Compact");
            Assert.InRange(FirstListRowHeight(shell), 55f, 57f);
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Duration));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.More));
        }, 400f, 860f);

    [Fact]
    public void The_row_size_request_resizes_the_rows_and_throws_nothing()
        => Mounted(shell =>
        {
            OpenList(shell, s_listsPlaylist, "the playlist's rows under WinUI");
            try
            {
                foreach (var density in new[] { WinUiDensity.Compact, WinUiDensity.Standard })
                {
                    var t = ThemeHost.Apply(new OptionChange(WinUiTheme.Id, WinUiListRules.DensityQualifiedId, density));
                    for (int i = 0; i < 300 && !t.IsCompleted; i++) shell.RunFrames(1);
                    Assert.True(t.IsCompletedSuccessfully, "the density change completed (" + t.Status + ")");
                    for (int i = 0; i < 120 && shell.Host.FreezeState != FluentGpu.Hosting.FreezePhase.None; i++) shell.RunFrames(1);
                    shell.RunFrames(30);
                    UntilBackground(shell, () => shell.FindPrefix(ListDropKey).Count > 0, "the rows after the density change");
                    shell.RunFrames(20);
                    Assert.Equal(WinUiDensityOption.Rows(density).Rung, ThemeHost.Metrics.Peek().TrackRows.Rung);
                    float expected = WinUiDensityOption.RowDouble(density);
                    Assert.InRange(FirstListRowHeight(shell), expected - 1f, expected + 1f);
                }
            }
            finally
            {
                _ = ThemeHost.Apply(new OptionChange(WinUiTheme.Id, WinUiListRules.DensityQualifiedId, WinUiDensity.Standard));
                shell.RunFrames(60);
            }
        }, 1440f, 960f);
}
