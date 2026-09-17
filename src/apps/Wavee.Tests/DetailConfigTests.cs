// ── Wavee.Tests/DetailConfigTests.cs — the per-kind configuration matrix, its resolution, and the sort keys ────────
//
// NEW in 0.3 (0.2.9 had no test for `DetailConfig` — the literals were read by the shell and never pinned). ch 03 §8's
// matrix, ROW FOR ROW: one fact per knob, each asserting all six columns (Playlist · Album/EP · Single · Compilation ·
// Liked · Show). Then `Config.For` = 0.2.9's `DetailPage.ResolveConfig`, including the rule the source comment got wrong
// (EP → Album; there is no "≤ 2 tracks" rule). Then `Detail.SortKeys`, whose −1 sentinel is what separates "never
// chosen" from "column 0".
//
// The sort-key facts parse an `EntityUri` (which interns), so the class joins EntitiesCollection.

using FluentGpu.Controls;
using Xunit;
using Config = Wavee.Detail.Config;
using SortKeys = Wavee.Detail.SortKeys;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class DetailConfigTests
{
    /// <summary>One matrix row: the knob's value on each of the six configs, in ch 03 §8's column order.</summary>
    static void Row<T>(Func<Config, T> knob, T playlist, T album, T single, T compilation, T liked, T show)
    {
        Assert.Equal(playlist, knob(Config.Playlist));
        Assert.Equal(album, knob(Config.Album));
        Assert.Equal(single, knob(Config.Single));
        Assert.Equal(compilation, knob(Config.Compilation));
        Assert.Equal(liked, knob(Config.Liked));
        Assert.Equal(show, knob(Config.Show));
    }

    // ── ch 03 §8, row for row ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Kind() => Row(c => c.Kind,
        DetailKind.Playlist, DetailKind.Album, DetailKind.Album, DetailKind.Album, DetailKind.Liked, DetailKind.Show);

    [Fact]
    public void TwoColumn() => Row(c => c.TwoColumn, true, true, true, true, true, true);

    [Fact]
    public void RailWidth() => Row(c => c.RailWidth, 240f, 280f, 280f, 280f, 240f, 280f);

    [Fact]
    public void RailWidth_ReadsTheDesignTokens() => Row(c => c.RailWidth,
        Design.Size.RailPlaylist, Design.Size.RailAlbum, Design.Size.RailAlbum, Design.Size.RailAlbum,
        Design.Size.RailPlaylist, Design.Size.RailAlbum);

    [Fact]
    public void Badges() => Row(c => c.Badges,
        BadgeStyle.OwnerRow, BadgeStyle.TypeYear, BadgeStyle.TypeYear, BadgeStyle.TypeYear, BadgeStyle.None, BadgeStyle.TypeYear);

    [Fact]
    public void ShowArtThumb() => Row(c => c.ShowArtThumb, true, false, false, false, true, false);

    [Fact]
    public void ShowAlbumColumn() => Row(c => c.ShowAlbumColumn, true, false, false, false, true, false);

    [Fact]
    public void Selection() => Row(c => c.Selection,
        ItemsSelectionMode.Extended, ItemsSelectionMode.Extended, ItemsSelectionMode.None,
        ItemsSelectionMode.Extended, ItemsSelectionMode.Extended, ItemsSelectionMode.None);

    [Fact]
    public void HasTrailing() => Row(c => c.HasTrailing, false, true, true, true, false, false);

    [Fact]
    public void Heart() => Row(c => c.Heart,
        HeartMode.Follow, HeartMode.Save, HeartMode.Save, HeartMode.Save, HeartMode.None, HeartMode.Follow);

    [Fact]
    public void ShowPlays() => Row(c => c.ShowPlays, false, true, true, true, false, false);

    [Fact]
    public void ShowTrackArtist() => Row(c => c.ShowTrackArtist, true, false, false, true, true, false);

    [Fact]
    public void Content() => Row(c => c.Content,
        DetailContent.Tracks, DetailContent.Tracks, DetailContent.Tracks, DetailContent.Tracks, DetailContent.Tracks,
        DetailContent.Episodes);

    [Fact]
    public void Recommendations() => Row(c => c.Recommendations, true, false, false, false, false, false);

    [Fact]
    public void ShowTempo() => Row(c => c.ShowTempo, true, false, false, false, true, false);

    [Fact]
    public void ShowVersions() => Row(c => c.ShowVersions, true, true, true, true, true, false);

    [Fact]
    public void PlaysColumnOptIn() => Row(c => c.PlaysColumnOptIn, true, false, false, false, true, false);

    [Fact]
    public void RailScope_PerKind() => Row(c => c.RailScope,
        RailScope.Playlist, RailScope.Album, RailScope.Album, RailScope.Album, RailScope.Liked, RailScope.Show);

    [Fact]
    public void RailResizable() => Row(c => c.RailResizable, true, true, true, true, true, true);

    /// <summary>A single / compilation is the album literal with exactly ONE knob changed.</summary>
    [Fact]
    public void SingleAndCompilation_AreTheAlbumWithOneKnob()
    {
        Assert.Equal(Config.Album with { Selection = ItemsSelectionMode.None }, Config.Single);
        Assert.Equal(Config.Album with { ShowTrackArtist = true }, Config.Compilation);
        Assert.NotEqual(Config.Album, Config.Single);
        Assert.NotEqual(Config.Album, Config.Compilation);
    }

    // ── Config.For (0.2.9 DetailPage.ResolveConfig) ──────────────────────────────────────────────────────────────────

    /// <summary>Playlist / Liked / Show are fixed by route, whatever release kind is passed.</summary>
    [Theory]
    [InlineData(AlbumKind.Single)]
    [InlineData(AlbumKind.EP)]
    [InlineData(AlbumKind.Album)]
    [InlineData(AlbumKind.Compilation)]
    public void For_FixedRouteKinds_IgnoreTheReleaseKind(AlbumKind releaseKind)
    {
        Assert.Equal(Config.Playlist, Config.For(DetailKind.Playlist, releaseKind));
        Assert.Equal(Config.Liked, Config.For(DetailKind.Liked, releaseKind));
        Assert.Equal(Config.Show, Config.For(DetailKind.Show, releaseKind));
    }

    /// <summary>The album route branches on the release kind — and EP has no column of its own: it IS the Album config.</summary>
    [Fact]
    public void For_TheAlbumRoute_BranchesOnReleaseKind_AndEpIsAnAlbum()
    {
        Assert.Equal(Config.Single, Config.For(DetailKind.Album, AlbumKind.Single));
        Assert.Equal(Config.Compilation, Config.For(DetailKind.Album, AlbumKind.Compilation));
        Assert.Equal(Config.Album, Config.For(DetailKind.Album, AlbumKind.Album));
        Assert.Equal(Config.Album, Config.For(DetailKind.Album, AlbumKind.EP));
        Assert.Equal(DetailKind.Album, Config.For(DetailKind.Album, AlbumKind.EP).Kind);
    }

    /// <summary>The config knows its own rail pair: each literal's scope resolves to the persisted keys of that scope.</summary>
    [Fact]
    public void EveryConfig_OpensItsRailAtItsScopesDefault()
    {
        foreach (var cfg in new[] { Config.Playlist, Config.Album, Config.Single, Config.Compilation, Config.Liked, Config.Show })
            Assert.Equal(Detail.RailPolicy.DefaultWidthFor(cfg.RailScope), cfg.RailWidth);
    }

    // ── Detail.SortKeys (0.2.9 DetailShell.SortColKey / SortDescKey) ─────────────────────────────────────────────────

    [Fact]
    public void SortKeys_ArePerContext_WithTheNeverChosenSentinel()
    {
        TestScope.Fresh();
        var album = EntityUri.Parse("spotify:album:4m2880jivSbbyEGAKfITCa");
        var playlist = EntityUri.Parse("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M");

        var col = SortKeys.Column(album);
        Assert.Equal("detail.sort.col:" + album.Text, col.Name);
        Assert.Equal(-1, col.Default);                       // −1 = never chosen ⇒ the profile's default sort
        Assert.NotEqual(Platform.Keys.DetailSortCol(album.Text).Default, col.Default);   // column 0 is a REAL column

        var desc = SortKeys.Descending(album);
        Assert.Equal("detail.sort.desc:" + album.Text, desc.Name);
        Assert.False(desc.Default);

        Assert.NotEqual(SortKeys.Column(album).Name, SortKeys.Column(playlist).Name);
        Assert.NotEqual(SortKeys.Descending(album).Name, SortKeys.Descending(playlist).Name);
        // The persisted spelling is 0.2.9's, byte for byte — a rename is a user who lost every sort they chose.
        Assert.Equal(Platform.Keys.DetailSortCol(album.Text).Name, col.Name);
        Assert.Equal(Platform.Keys.DetailSortDesc(album.Text).Name, desc.Name);
    }
}
