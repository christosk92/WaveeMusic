// ── Wavee.ShellTests/ZuneListsMountedTests.cs — Zune's lists, mounted under Zune (themes plan 6.3; P5 Z3) ───────────────────────────
//
// The app mounted under Zune (`ZuneHost`): what only a mounted tree can say about Zune's list styles.
//
//   registered   every list key resolves to Zune's style, not Classic's stand-in (the registry holds Zune's instances)
//   album        an album page draws Zune's track table: Zune's header ("zune.list.header") and Zune's row skins ("zune.row.drop")
//   playlist     so does a playlist page
//   density      the rows stand at 44 / 52 / 60 as the style's density moves, and switching it throws nothing (the track chrome's "Row size"
//                reads the PUBLISHED rung, never the style's option as Classic's `RowDensity`)
//   playing      the playing row carries the accent bars ("zune.row.now") on exactly one row
//
// Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneListsMountedTests(ShellHost host)
{
    static readonly Shell.Route s_album = Shell.For(EntityUri.Parse("spotify:album:al2"), "Album");
    static readonly Shell.Route s_playlist = Shell.For(EntityUri.Parse("spotify:playlist:pl1"), "Playlist");

    /// <summary>al2's first row (the pool run starts at 26): the track the playing-row fact plays.</summary>
    const string AlbumFirstTrack = "spotify:track:tr26";

    const string HeaderKey = "zune.list.header", DropKey = "zune.row.drop", NowKey = "zune.row.now";

    static void Open(Shell.HeadlessShell shell, Shell.Route route, string what)
    {
        Shell.GoTo(route);
        // A wall-clock wait, not a frame budget (as ArmSwapPlaceTests): a playlist this process never revalidated opens RevalidateThenPaint and
        // HOLDS its rows (the shimmer) for up to ListFreshness.BlockingBudgetMs (1500 ms of the Stopwatch clock); 240 headless frames run in a
        // fraction of that, so a fact run alone (no earlier fact warmed pl1) timed out.
        UntilBackground(shell, () => shell.FindPrefix("row").Count(k => k.Key == "row") > 2 && shell.FindPrefix(DropKey).Count > 0, what);
        shell.RunFrames(40);
    }

    static float FirstRowHeight(Shell.HeadlessShell shell)
    {
        var scene = shell.Host.Scene;
        return shell.FindPrefix("row").Where(k => k.Key == "row")
            .Select(k => scene.Bounds(k.Node)).Where(b => b.H > 0f).OrderBy(b => b.Y).First().H;
    }

    static void ApplyDensity(Shell.HeadlessShell shell, ZuneDensity density)
    {
        var t = ThemeHost.Apply(new OptionChange(ZuneTheme.Id, ZuneListRules.DensityQualifiedId, density));
        for (int i = 0; i < 300 && !t.IsCompleted; i++) shell.RunFrames(1);
        Assert.True(t.IsCompletedSuccessfully, "the density change completed (" + t.Status + ")");
        for (int i = 0; i < 120 && shell.Host.FreezeState != FluentGpu.Hosting.FreezePhase.None; i++) shell.RunFrames(1);
        shell.RunFrames(30);
    }

    [Fact]
    public void Every_list_key_is_zunes_own()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            var registry = ThemeHost.Registry;
            Assert.True(registry.TryGetStyle(StyleKeys.TrackTable, out var table));
            Assert.Same(ZuneListStyles.TrackTable, table);
            Assert.True(registry.TryGetStyle(StyleKeys.EagerTrackRow, out var eager));
            Assert.Same(ZuneListStyles.EagerTrackRow, eager);
            Assert.True(registry.TryGetStyle(StyleKeys.EpisodeRow, out var episode));
            Assert.Same(ZuneListStyles.EpisodeRow, episode);
            Assert.True(registry.TryGetStyle(StyleKeys.Surface, out var surface));
            Assert.Same(ZuneListStyles.Surface, surface);
            Assert.True(registry.TryGetStyle(StyleKeys.LyricsLines, out var lyrics));
            Assert.Same(ZuneListStyles.LyricsLines, lyrics);
            Assert.True(registry.TryGetStyle(StyleKeys.ArtistChart, out var chart));
            Assert.Same(ZuneListStyles.ArtistChart, chart);
            Assert.True(registry.TryGetStyle(StyleKeys.TrackDrawer, out var drawer));
            Assert.Same(ZuneListStyles.TrackDrawer, drawer);
            // The policies the lists ride: one Modern skin, artwork on, Zune's detail table records.
            var facts = ThemeHost.Facts.Peek();
            Assert.Same(ZuneListRules.Lists, facts.Policies.Lists);
            Assert.Same(ZuneListRules.DetailTiers, facts.Policies.DetailTiers);
        }, 1440f, 960f);

    [Fact]
    public void An_album_page_draws_zunes_track_table()
        => ZuneHost.Mounted(host, shell =>
        {
            Open(shell, s_album, "the album's rows under Zune");
            Assert.NotEmpty(shell.FindPrefix(HeaderKey));
            Assert.NotEmpty(shell.FindPrefix(DropKey));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Num));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Title));
            // Zune draws no Classic pill or divider.
            Assert.Empty(shell.FindPrefix("row-divider"));
        }, 1440f, 960f);

    [Fact]
    public void A_playlist_page_draws_zunes_track_table()
        => ZuneHost.Mounted(host, shell =>
        {
            Open(shell, s_playlist, "the playlist's rows under Zune");
            Assert.NotEmpty(shell.FindPrefix(HeaderKey));
            Assert.NotEmpty(shell.FindPrefix(DropKey));
            Assert.NotEmpty(shell.FindPrefix(Track.CellKey.Title));
            Assert.Empty(shell.FindPrefix("row-divider"));
        }, 1440f, 960f);

    [Fact]
    public void The_rows_stand_at_the_densitys_height_and_switching_it_throws_nothing()
        => ZuneHost.Mounted(host, shell =>
        {
            Open(shell, s_playlist, "the playlist's rows under Zune");
            try
            {
                foreach (var density in new[] { ZuneDensity.Compact, ZuneDensity.Comfortable, ZuneDensity.Standard })
                {
                    ApplyDensity(shell, density);
                    UntilBackground(shell, () => shell.FindPrefix(DropKey).Count > 0, "the rows after the density change");
                    shell.RunFrames(20);
                    Assert.Equal((int)density, ThemeHost.Metrics.Peek().TrackRows.Rung);
                    float expected = ZuneListRules.RowFor(density);
                    Assert.InRange(FirstRowHeight(shell), expected - 1f, expected + 1f);
                }
            }
            finally { ApplyDensity(shell, ZuneDensity.Standard); }
        }, 1440f, 960f);

    [Fact]
    public void The_playing_row_carries_the_accent_bars_on_one_row()
        => ZuneHost.Mounted(host, shell =>
        {
            Play(AlbumFirstTrack);
            shell.RunFrames(10);
            Open(shell, s_album, "the album's rows under Zune");
            UntilBackground(shell, () => shell.FindPrefix(NowKey).Count > 0, "the playing row's bars");
            Assert.Single(shell.FindPrefix(NowKey));
        }, 1440f, 960f);
}
