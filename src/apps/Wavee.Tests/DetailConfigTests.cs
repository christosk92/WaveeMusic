// ── Wavee.Tests/DetailConfigTests.cs — the per-kind configuration matrix, its resolution, and the sort keys ────────
//
// NEW in 0.3 (0.2.9 had no test for `DetailConfig` — the literals were read by the shell and never pinned). ch 03 §8's
// matrix, ROW FOR ROW: one fact per knob, each asserting all six columns (Playlist · Album/EP · Single · Compilation ·
// Liked · Show). Then `Config.For` = 0.2.9's `DetailPage.ResolveConfig`, including the rule the source comment got wrong
// (EP → Album; there is no "≤ 2 tracks" rule). Then `Detail.SortKeys`, whose −1 sentinel is what separates "never
// chosen" from "column 0".
//
// The sort-key facts parse an `EntityUri` (which interns), so the class joins EntitiesCollection.
//
// Podcast rework wave P2 (§5.4): `Config.Episode` — the show's frame with three knobs turned, fixed by route, sharing
// the show's persisted rail pair — its eyebrow arm, and the `FrameSlots` contract the six appended podcast slots join:
// equality is PRESENCE (a slot appearing or disappearing is a new spec; a new builder body is not), and the twelve older
// slots keep their mask bits (the hash IS the mask).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Localization;
using Xunit;
using Config = Wavee.Detail.Config;
using FrameSlots = Wavee.Detail.FrameSlots;
using SortKeys = Wavee.Detail.SortKeys;
using Text = Wavee.Detail.Text;

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
        foreach (var cfg in new[] { Config.Playlist, Config.Album, Config.Single, Config.Compilation, Config.Liked, Config.Show, Config.Episode })
            Assert.Equal(Detail.RailPolicy.DefaultWidthFor(cfg.RailScope), cfg.RailWidth);
    }

    // ── Config.Episode (podcast rework §5.4) ─────────────────────────────────────────────────────────────────────────

    /// <summary>An episode is the SHOW's frame with exactly three knobs turned: its own kind, no heart in the fixed FAB
    /// group (its ♥ is a satellite), the show's rail scope. Everything else — episodes in the right column, the album-like
    /// 280 rail, the TypeYear badge style, no selection, no trailing body — is the show's.</summary>
    [Fact]
    public void Episode_IsTheShowWithThreeKnobs()
    {
        var episode = Config.Episode;
        Assert.Equal(Config.Show with { Kind = DetailKind.Episode, Heart = HeartMode.None, RailScope = RailScope.Show }, episode);
        Assert.NotEqual(Config.Show, episode);

        Assert.Equal(DetailKind.Episode, episode.Kind);
        Assert.Equal(HeartMode.None, episode.Heart);
        Assert.Equal(RailScope.Show, episode.RailScope);
        Assert.Equal(DetailContent.Episodes, episode.Content);
        Assert.Equal(BadgeStyle.TypeYear, episode.Badges);
        Assert.Equal(Design.Size.RailAlbum, episode.RailWidth);
        Assert.Equal(ItemsSelectionMode.None, episode.Selection);
        Assert.True(episode.TwoColumn);
        Assert.True(episode.RailResizable);
        Assert.False(episode.HasTrailing);
        Assert.False(episode.ShowPlays);
        Assert.False(episode.ShowTrackArtist);
    }

    /// <summary>The episode route is fixed like the show's: no release kind reaches it.</summary>
    [Theory]
    [InlineData(AlbumKind.Single)]
    [InlineData(AlbumKind.EP)]
    [InlineData(AlbumKind.Album)]
    [InlineData(AlbumKind.Compilation)]
    public void For_TheEpisodeRoute_IsFixed(AlbumKind releaseKind)
        => Assert.Equal(Config.Episode, Config.For(DetailKind.Episode, releaseKind));

    /// <summary>The podcast family persists ONE rail: an episode page reads and writes the show's width/collapsed pair,
    /// and "Keep left-rail same size" still folds it into Uniform like every other surface.</summary>
    [Fact]
    public void Episode_SharesTheShowsRailPair()
    {
        var scope = Detail.RailPolicy.ScopeFor(Config.Episode.RailScope, uniform: false);
        Assert.Equal(RailScope.Show, scope);
        Assert.Equal(Detail.RailPolicy.KeysFor(RailScope.Show), Detail.RailPolicy.KeysFor(scope));
        Assert.Equal(Platform.Keys.DetailShowRailWidth, Detail.RailPolicy.KeysFor(scope).Width);
        Assert.Equal(Platform.Keys.DetailShowRailCollapsed, Detail.RailPolicy.KeysFor(scope).Collapsed);
        Assert.Equal(RailScope.Uniform, Detail.RailPolicy.ScopeFor(Config.Episode.RailScope, uniform: true));
    }

    /// <summary>An episode's TypeYear eyebrow is "Episode" (a show's is "Podcast"), with the year when one is known; any
    /// other badge style forwards to the kind-free rule unchanged.</summary>
    [Fact]
    public void Eyebrow_AnEpisodeIsAnEpisode()
    {
        Assert.Equal(Loc.Get(Strings.Nav.Episode),
            Text.Eyebrow(DetailKind.Episode, BadgeStyle.TypeYear, AlbumKind.Album, 0, false, true, true));
        Assert.StartsWith(Loc.Get(Strings.Nav.Episode),
            Text.Eyebrow(DetailKind.Episode, BadgeStyle.TypeYear, AlbumKind.Album, 2026, false, true, true));
        Assert.Contains("2026", Text.Eyebrow(DetailKind.Episode, BadgeStyle.TypeYear, AlbumKind.Album, 2026, false, true, true));
        Assert.NotEqual(Text.Eyebrow(DetailKind.Show, BadgeStyle.TypeYear, AlbumKind.Album, 0, false, true, true),
            Text.Eyebrow(DetailKind.Episode, BadgeStyle.TypeYear, AlbumKind.Album, 0, false, true, true));
        Assert.Equal(Text.Eyebrow(BadgeStyle.None, AlbumKind.Album, 0, false, true, true),
            Text.Eyebrow(DetailKind.Episode, BadgeStyle.None, AlbumKind.Album, 0, false, true, true));
    }

    // ── FrameSlots: PRESENCE equality, appended bits (podcast rework §5.4) ─────────────────────────────────────────────

    static Element Box() => new BoxEl();

    /// <summary>The eighteen slots one at a time, in declaration order — the twelve older ones, then the six podcast seams.</summary>
    static FrameSlots[] EachSlotAlone() =>
    [
        new() { Cover = _ => Box() },
        new() { CompactCover = _ => Box() },
        new() { Title = (_, _) => Box() },
        new() { Attribution = _ => Box() },
        new() { Description = _ => Box() },
        new() { Pulse = () => Box() },
        new() { Chart = () => Box() },
        new() { PreRelease = () => Box() },
        new() { ReleasePanel = _ => Box() },
        new() { LikedFacts = _ => Box() },
        new() { Trailing = () => Box() },
        new() { Episodes = _ => Box() },
        new() { Badges = () => Box() },
        new() { Rating = _ => Box() },
        new() { Ledger = _ => Box() },
        new() { Primary = _ => Box() },
        new() { Satellites = () => Array.Empty<Element>() },
        new() { Topics = _ => Box() },
    ];

    /// <summary>Each new slot's PRESENCE changes equality — a slot appearing is a new spec the frame renders.</summary>
    [Fact]
    public void FrameSlots_ANewSlotsPresence_ChangesEquality()
    {
        var none = new FrameSlots();
        foreach (var one in EachSlotAlone()[12..])
        {
            Assert.NotEqual(none, one);
            Assert.NotEqual(none.GetHashCode(), one.GetHashCode());
        }
    }

    /// <summary>…and its BODY does not: a re-push with a different builder (fresh closures, other output) is equal, so a
    /// page re-rendering with the same slot set costs the frame nothing — values reach a slot body through its signals.</summary>
    [Fact]
    public void FrameSlots_ANewSlotsBody_DoesNotChangeEquality()
    {
        var pairs = new (FrameSlots A, FrameSlots B)[]
        {
            (new() { Badges = () => new BoxEl() }, new() { Badges = () => new BoxEl { Width = 1f } }),
            (new() { Rating = _ => new BoxEl() }, new() { Rating = w => new BoxEl { Width = w } }),
            (new() { Ledger = _ => new BoxEl() }, new() { Ledger = w => new BoxEl { Height = w } }),
            (new() { Primary = _ => new BoxEl() }, new() { Primary = accent => new BoxEl { Fill = accent } }),
            (new() { Satellites = () => Array.Empty<Element>() }, new() { Satellites = () => [new BoxEl(), new BoxEl()] }),
            (new() { Topics = _ => new BoxEl() }, new() { Topics = w => new BoxEl { Width = w } }),
        };
        foreach (var (a, b) in pairs)
        {
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        // A full podcast set against the same set with every body swapped and an older slot added: only the added slot counts.
        var podcast = new FrameSlots { Badges = () => Box(), Rating = _ => Box(), Ledger = _ => Box(), Primary = _ => Box(),
                                       Satellites = () => Array.Empty<Element>(), Topics = _ => Box(), Episodes = _ => Box() };
        var swapped = podcast with { Badges = () => new BoxEl { Width = 2f }, Topics = _ => new BoxEl { Width = 3f } };
        Assert.Equal(podcast, swapped);
        Assert.NotEqual(podcast, swapped with { Attribution = _ => Box() });
        Assert.NotEqual(podcast, swapped with { Ledger = null });
    }

    /// <summary>The six are APPENDED: the twelve older slots keep bits 1…2048 (the hash is the presence mask), the new ones
    /// take 4096…131072, and all eighteen are distinct.</summary>
    [Fact]
    public void FrameSlots_TheSixPodcastSlotsAreAppendedBits()
    {
        var each = EachSlotAlone();
        Assert.Equal(18, each.Length);
        for (int i = 0; i < each.Length; i++)
        {
            Assert.Equal(1 << i, each[i].GetHashCode());
            for (int j = i + 1; j < each.Length; j++) Assert.NotEqual(each[i], each[j]);
        }
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
