// ── Wavee.Tests/Theming/SidebarPaneStateMigrationTests.cs — M7b: the sidebar pane's width and collapse and the Library view are Classic's theme state ──
//
// `sidebar.pane.width` / `sidebar.pane.userCollapsed` (settings) and the Library's list/grid choice (the device file `sidebar.json`) are copied by the
// "SidebarPaneWidth", "SidebarPaneCollapsed" and "SidebarLibraryView" bootstrap steps to `theme.classic.state.paneWidth` / `.paneCollapsed` / `.libraryView`,
// so an existing user keeps the pane they left. The host that writes the state (`Sidebar`) is pinned too: where it writes, that the view is one undoable
// entry and part of "Reset layout", and that another theme's scope brings its own values.

using System.Text;
using Wavee.Theming;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class SidebarPaneStateMigrationTests : IDisposable
{
    static readonly SettingKey<float> LegacyWidth = new("sidebar.pane.width", 320f);
    static readonly SettingKey<bool> LegacyCollapsed = new("sidebar.pane.userCollapsed", false);
    static readonly SettingKey<float> Width = new("theme.classic.state.paneWidth", 320f);
    static readonly SettingKey<bool> Collapsed = new("theme.classic.state.paneCollapsed", false);
    static readonly SettingKey<int> View = new("theme.classic.state.libraryView", 0);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-m7b-" + Guid.NewGuid().ToString("N"));

    public SidebarPaneStateMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static ThemeBootstrapStep Step(string name) => Assert.Single(ThemeBootstrap.Steps, s => s.Name == name);

    void WriteDeviceFile(string json) => File.WriteAllText(Path.Combine(_dir, "sidebar.json"), json, new UTF8Encoding(false));

    const string GridFile = "{\"v\":3,\"layouts\":{\"classic\":{\"sections\":[{\"id\":\"pinned\"}]},\"library\":{\"sections\":[{\"id\":\"home\"},"
        + "{\"id\":\"library\",\"sort\":\"alphabetical\",\"descending\":true,\"view\":\"grid\",\"showLiked\":false}]}}}";

    [Fact]
    public void The_steps_read_the_legacy_keys_and_write_the_state_keys()
    {
        var width = Step("SidebarPaneWidth");
        Assert.Equal(new[] { "sidebar.pane.width" }, width.ReadsLegacyKeys);
        Assert.Equal(new[] { ThemeState.SettingName("classic", Sidebar.PaneWidthKey.Id) }, width.WritesKeys);
        var collapsed = Step("SidebarPaneCollapsed");
        Assert.Equal(new[] { "sidebar.pane.userCollapsed" }, collapsed.ReadsLegacyKeys);
        Assert.Equal(new[] { ThemeState.SettingName("classic", Sidebar.PaneCollapsedKey.Id) }, collapsed.WritesKeys);
        var view = Step("SidebarLibraryView");
        Assert.Empty(view.ReadsLegacyKeys);   // a file, not a setting
        Assert.Equal(new[] { ThemeState.SettingName("classic", Sidebar.LibraryViewKey.Id) }, view.WritesKeys);
        Assert.Null(ThemeBootstrap.Disjointness(ThemeBootstrap.Steps));
    }

    [Fact]
    public void A_stored_width_and_collapse_carry_over_and_the_legacy_keys_stay()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyWidth, 233f);
        s.Set(LegacyCollapsed, true);
        ThemeBootstrap.Run(s, [Step("SidebarPaneWidth"), Step("SidebarPaneCollapsed")]);

        Assert.Equal(233f, s.Get(Width));
        Assert.True(s.Get(Collapsed));
        Assert.Equal(233f, s.Get(LegacyWidth));
        Assert.True(s.Get(LegacyCollapsed));
        Assert.Equal(233f, ThemeState.Get(Sidebar.PaneWidthKey, s, "classic"));
        Assert.True(ThemeState.Get(Sidebar.PaneCollapsedKey, s, "classic"));
    }

    [Fact]
    public void A_user_who_never_moved_the_pane_stays_at_the_defaults()
    {
        var s = new SnapshotSettings();
        ThemeBootstrap.Run(s, [Step("SidebarPaneWidth"), Step("SidebarPaneCollapsed"), Step("SidebarLibraryView")]);

        Assert.False(s.Has("theme.classic.state.paneWidth"));
        Assert.False(s.Has("theme.classic.state.paneCollapsed"));
        Assert.Equal(SidebarPaneBounds.DefaultWidth, ThemeState.Get(Sidebar.PaneWidthKey, s, "classic"));
        Assert.False(ThemeState.Get(Sidebar.PaneCollapsedKey, s, "classic"));
    }

    [Fact]
    public void A_width_outside_the_pane_range_is_copied_and_clamped_on_read()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyWidth, 900f);
        ThemeBootstrap.Run(s, [Step("SidebarPaneWidth")]);

        Assert.Equal(900f, s.Get(Width));
        Assert.Equal(SidebarPaneBounds.NavPaneMaxW, ThemeState.Get(Sidebar.PaneWidthKey, s, "classic"));
    }

    [Fact]
    public void A_state_value_that_exists_is_not_overwritten()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyWidth, 233f);
        s.Set(Width, 410f);
        ThemeBootstrap.Run(s, [Step("SidebarPaneWidth")]);
        Assert.Equal(410f, s.Get(Width));
    }

    [Theory]
    [InlineData("grid", 1)]
    [InlineData("list", 0)]
    public void The_library_view_in_the_device_file_carries_over(string stored, int expected)
    {
        WriteDeviceFile(GridFile.Replace("grid", stored));
        var s = new SnapshotSettings();
        ThemeBootstrap.CopyLibraryView(s, _dir);

        Assert.Equal(expected, s.Get(View));
        Assert.True(s.Has("theme.classic.state.libraryView"));
        Assert.Equal((SidebarLibraryView)expected, Sidebar.ViewOf(ThemeState.Get(Sidebar.LibraryViewKey, s, "classic")));
    }

    [Fact]
    public void The_file_is_left_untouched()
    {
        WriteDeviceFile(GridFile);
        ThemeBootstrap.CopyLibraryView(new SnapshotSettings(), _dir);
        Assert.Equal(GridFile, File.ReadAllText(Path.Combine(_dir, "sidebar.json")));
    }

    [Theory]
    [InlineData("{\"v\":3}")]
    [InlineData("{\"v\":3,\"layouts\":{\"library\":{\"sections\":[{\"id\":\"library\"}]}}}")]
    [InlineData("{\"v\":3,\"layouts\":{\"library\":{\"sections\":[{\"id\":\"library\",\"view\":\"mosaic\"}]}}}")]
    [InlineData("{\"v\":3,\"layouts\":{\"classic\":{\"sections\":[{\"id\":\"library\",\"view\":\"grid\"}]}}}")]
    public void No_usable_view_in_the_file_leaves_the_default(string json)
    {
        WriteDeviceFile(json);
        var s = new SnapshotSettings();
        ThemeBootstrap.CopyLibraryView(s, _dir);
        Assert.False(s.Has("theme.classic.state.libraryView"));
    }

    [Fact]
    public void No_device_file_and_a_demo_copy_nothing()
    {
        var s = new SnapshotSettings();
        ThemeBootstrap.CopyLibraryView(s, _dir);
        ThemeBootstrap.CopyLibraryView(s, null);
        Assert.False(s.Has("theme.classic.state.libraryView"));
    }

    [Fact]
    public void An_unreadable_file_fails_the_step_so_it_runs_again_next_launch()
    {
        WriteDeviceFile("{ not json");
        var s = new SnapshotSettings();
        Assert.ThrowsAny<Exception>(() => ThemeBootstrap.CopyLibraryView(s, _dir));
        Assert.False(s.Has("theme.classic.state.libraryView"));
    }

    [Fact]
    public void A_view_already_in_the_state_is_not_overwritten()
    {
        WriteDeviceFile(GridFile);
        var s = new SnapshotSettings();
        s.Set(View, 0);
        ThemeBootstrap.CopyLibraryView(s, _dir);
        Assert.Equal(0, s.Get(View));
    }

    [Fact]
    public void The_state_keys_are_the_pane_ranges()
    {
        Assert.Equal((SidebarPaneBounds.NavPaneMinW, SidebarPaneBounds.NavPaneMaxW, SidebarPaneBounds.DefaultWidth),
            (Sidebar.PaneWidthKey.Min, Sidebar.PaneWidthKey.Max, Sidebar.PaneWidthKey.Default));
        Assert.Equal((0, 1), (Sidebar.LibraryViewKey.Min, Sidebar.LibraryViewKey.Max));
        Assert.Equal(SidebarLibraryView.List, Sidebar.ViewOf(7));   // an unknown stored int reads as the default
    }
}
