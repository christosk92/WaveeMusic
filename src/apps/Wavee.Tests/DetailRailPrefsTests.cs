// ── Wavee.Tests/DetailRailPrefsTests.cs — the detail rails' persisted pairs, as a typed pref (C16, X39) ───────────────
//
// `DetailRailPrefs` is the host's door to the ten `Platform.Keys.Detail*Rail*` keys: the reads that seed a rail cell,
// the commit a grip release makes, the re-open verb and Settings' reset. The width RULES (clamps, caps) are
// `Detail.RailPolicy`'s and pinned in DetailRailPolicyTests; these facts pin the store traffic: which keys, how many
// epoch bumps, and that one scope never writes another's pair.
//
// `Platform.Settings` is process state, so the class joins the platform collection and installs its own store.

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class DetailRailPrefsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public DetailRailPrefsTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    static readonly RailScope[] s_four = [RailScope.Album, RailScope.Playlist, RailScope.Liked, RailScope.Show];
    static readonly RailScope[] s_all = [RailScope.Album, RailScope.Playlist, RailScope.Liked, RailScope.Show, RailScope.Uniform];

    [Fact]
    public void A_scope_nobody_dragged_reads_its_authored_default_and_is_not_collapsed()
    {
        foreach (var scope in s_all)
        {
            Assert.Equal(Detail.RailPolicy.DefaultWidthFor(scope), DetailRailPrefs.StoredWidth(scope));
            Assert.False(DetailRailPrefs.Collapsed(scope));
        }
    }

    [Fact]
    public void A_hand_edited_width_reads_back_inside_the_live_bounds()
    {
        _store.Set(Platform.Keys.DetailAlbumRailWidth, 10f);
        _store.Set(Platform.Keys.DetailPlaylistRailWidth, 9999f);
        Assert.Equal(Detail.RailPolicy.MinWidth, DetailRailPrefs.StoredWidth(RailScope.Album));
        Assert.Equal(Detail.RailPolicy.MaxWidth, DetailRailPrefs.StoredWidth(RailScope.Playlist));
    }

    [Fact]
    public void A_commit_writes_the_scopes_pair_and_bumps_the_layout_epoch_once()
    {
        int before = Prefs.DetailHero.Epoch.Peek();
        DetailRailPrefs.Commit(RailScope.Liked, 301f, collapsed: true);

        Assert.Equal(before + 1, Prefs.DetailHero.Epoch.Peek());
        Assert.Equal(301f, _store.Get(Platform.Keys.DetailLikedRailWidth));
        Assert.True(_store.Get(Platform.Keys.DetailLikedRailCollapsed));
        Assert.Equal(301f, DetailRailPrefs.StoredWidth(RailScope.Liked));
        Assert.True(DetailRailPrefs.Collapsed(RailScope.Liked));
    }

    [Fact]
    public void A_commit_never_touches_another_scopes_pair()
    {
        DetailRailPrefs.Commit(RailScope.Album, 333f, collapsed: true);

        foreach (var other in new[] { RailScope.Playlist, RailScope.Liked, RailScope.Show, RailScope.Uniform })
        {
            Assert.Equal(Detail.RailPolicy.DefaultWidthFor(other), DetailRailPrefs.StoredWidth(other));
            Assert.False(DetailRailPrefs.Collapsed(other));
        }
        Assert.False(_store.WasWritten(Platform.Keys.DetailUniformRailWidth));
        Assert.False(_store.WasWritten(Platform.Keys.DetailShowRailWidth));
    }

    [Fact]
    public void Collapsing_and_reopening_persist_the_flag_with_one_bump_each()
    {
        int before = Prefs.DetailHero.Epoch.Peek();
        DetailRailPrefs.SetCollapsed(RailScope.Show, true);
        Assert.True(DetailRailPrefs.Collapsed(RailScope.Show));
        Assert.Equal(before + 1, Prefs.DetailHero.Epoch.Peek());

        DetailRailPrefs.SetCollapsed(RailScope.Show, false);
        Assert.False(DetailRailPrefs.Collapsed(RailScope.Show));
        Assert.Equal(before + 2, Prefs.DetailHero.Epoch.Peek());
        Assert.False(_store.WasWritten(Platform.Keys.DetailShowRailWidth));          // the flag never writes the width
    }

    [Fact]
    public void The_reset_gate_is_closed_until_one_of_the_four_pairs_moves()
    {
        Assert.False(DetailRailPrefs.HasCustomized());
        DetailRailPrefs.Commit(RailScope.Playlist, 260f, collapsed: false);
        Assert.True(DetailRailPrefs.HasCustomized());
    }

    [Fact]
    public void A_collapsed_flag_alone_opens_the_reset_gate()
    {
        DetailRailPrefs.SetCollapsed(RailScope.Album, true);
        Assert.True(DetailRailPrefs.HasCustomized());
    }

    [Fact]
    public void The_uniform_pair_never_opens_the_reset_gate()
    {
        DetailRailPrefs.Commit(RailScope.Uniform, 400f, collapsed: true);
        Assert.False(DetailRailPrefs.HasCustomized());
    }

    [Fact]
    public void Reset_restores_the_four_pairs_keeps_the_uniform_pair_and_bumps_once()
    {
        foreach (var scope in s_four) DetailRailPrefs.Commit(scope, 400f, collapsed: true);
        DetailRailPrefs.Commit(RailScope.Uniform, 410f, collapsed: true);
        int before = Prefs.DetailHero.Epoch.Peek();

        DetailRailPrefs.ResetAll();

        Assert.Equal(before + 1, Prefs.DetailHero.Epoch.Peek());
        foreach (var scope in s_four)
        {
            Assert.Equal(Detail.RailPolicy.DefaultWidthFor(scope), DetailRailPrefs.StoredWidth(scope));
            Assert.False(DetailRailPrefs.Collapsed(scope));
        }
        Assert.Equal(410f, DetailRailPrefs.StoredWidth(RailScope.Uniform));
        Assert.True(DetailRailPrefs.Collapsed(RailScope.Uniform));
        Assert.False(DetailRailPrefs.HasCustomized());
    }
}
