// ── Wavee.Tests/DetailRailPrefsTests.cs — the detail rails' remembered sizes, as theme state (C16, X39; M8) ───────────
//
// `DetailRailPrefs` is the host's door to the ten `theme.<id>.state.rail.<scope>.width|collapsed` theme-state keys: the reads that seed a rail cell,
// the commit a grip release makes, the re-open verb and the structure's reset. The width RULES (clamps, caps) are
// `Detail.RailPolicy`'s and pinned in DetailRailPolicyTests; these facts pin the store traffic: which keys, how many
// epoch bumps, and that one scope never writes another's pair.
//
// `Platform.Settings` is process state, so the class joins the platform collection and installs its own store.

using Wavee.Theming;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class DetailRailPrefsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public DetailRailPrefsTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    /// <summary>The stored key of a scope's width / collapsed state (the active theme's scope: Classic).</summary>
    static SettingKey<float> Width(string scope) => new(ThemeState.SettingName(ThemeCatalog.FallbackId, "rail." + scope + ".width"), 0f);

    static SettingKey<bool> Collapsed(string scope) => new(ThemeState.SettingName(ThemeCatalog.FallbackId, "rail." + scope + ".collapsed"), false);

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
        _store.Set(Width("album"), 10f);
        _store.Set(Width("playlist"), 9999f);
        Assert.Equal(Detail.RailPolicy.MinWidth, DetailRailPrefs.StoredWidth(RailScope.Album));
        Assert.Equal(Detail.RailPolicy.MaxWidth, DetailRailPrefs.StoredWidth(RailScope.Playlist));
    }

    [Fact]
    public void A_commit_writes_the_scopes_pair_and_bumps_the_rail_epoch_once()
    {
        int before = DetailRailPrefs.Epoch.Peek();
        DetailRailPrefs.Commit(RailScope.Liked, 301f, collapsed: true);

        Assert.Equal(before + 1, DetailRailPrefs.Epoch.Peek());
        Assert.Equal(301f, _store.Get(Width("liked")));
        Assert.True(_store.Get(Collapsed("liked")));
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
        Assert.False(_store.WasWritten(Width("uniform")));
        Assert.False(_store.WasWritten(Width("show")));
    }

    [Fact]
    public void Collapsing_and_reopening_persist_the_flag_with_one_bump_each()
    {
        int before = DetailRailPrefs.Epoch.Peek();
        DetailRailPrefs.SetCollapsed(RailScope.Show, true);
        Assert.True(DetailRailPrefs.Collapsed(RailScope.Show));
        Assert.Equal(before + 1, DetailRailPrefs.Epoch.Peek());

        DetailRailPrefs.SetCollapsed(RailScope.Show, false);
        Assert.False(DetailRailPrefs.Collapsed(RailScope.Show));
        Assert.Equal(before + 2, DetailRailPrefs.Epoch.Peek());
        Assert.False(_store.WasWritten(Width("show")));          // the flag never writes the width
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
        int before = DetailRailPrefs.Epoch.Peek();

        DetailRailPrefs.ResetAll();

        Assert.Equal(before + 1, DetailRailPrefs.Epoch.Peek());
        foreach (var scope in s_four)
        {
            Assert.Equal(Detail.RailPolicy.DefaultWidthFor(scope), DetailRailPrefs.StoredWidth(scope));
            Assert.False(DetailRailPrefs.Collapsed(scope));
        }
        Assert.Equal(410f, DetailRailPrefs.StoredWidth(RailScope.Uniform));
        Assert.True(DetailRailPrefs.Collapsed(RailScope.Uniform));
        Assert.False(DetailRailPrefs.HasCustomized());
    }

    [Fact]
    public void The_sizes_are_the_active_themes_state_so_nothing_is_written_to_a_core_key()
    {
        DetailRailPrefs.Commit(RailScope.Album, 333f, collapsed: true);

        Assert.Equal(333f, _store.Get(Width("album")));
        Assert.True(_store.Get(Collapsed("album")));
        Assert.Equal(2, _store.WrittenCount);   // the pair, and nothing else
        Assert.True(_store.Has("theme.classic.state.rail.album.width"));
        Assert.True(_store.Has("theme.classic.state.rail.album.collapsed"));
    }

    [Fact]
    public void The_clear_verb_is_enabled_only_while_a_size_is_customised_and_runs_the_reset()
    {
        DetailRailPrefs.RegisterVerbs();
        try
        {
            Assert.True(ThemeVerbs.Has(DetailVerbIds.ClearRememberedSizes));
            Assert.False(ThemeVerbs.IsEnabled(DetailVerbIds.ClearRememberedSizes));

            DetailRailPrefs.Commit(RailScope.Liked, 301f, collapsed: false);
            Assert.True(ThemeVerbs.IsEnabled(DetailVerbIds.ClearRememberedSizes));
            int before = DetailRailPrefs.Epoch.Peek();

            Assert.True(ThemeVerbs.Run(DetailVerbIds.ClearRememberedSizes));

            Assert.Equal(before + 1, DetailRailPrefs.Epoch.Peek());
            Assert.Equal(Detail.RailPolicy.DefaultWidthFor(RailScope.Liked), DetailRailPrefs.StoredWidth(RailScope.Liked));
            Assert.False(ThemeVerbs.IsEnabled(DetailVerbIds.ClearRememberedSizes));
        }
        finally
        {
            ThemeVerbs.Unregister(DetailVerbIds.ClearRememberedSizes);
        }
    }
}
