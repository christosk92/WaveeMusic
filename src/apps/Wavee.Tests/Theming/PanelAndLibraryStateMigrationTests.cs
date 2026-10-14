// ── Wavee.Tests/Theming/PanelAndLibraryStateMigrationTests.cs — M12/M13: panel sizes and library columns became Classic's theme state ──
//
// `shell.rail.width` and `shell.rail.docked-video.height` are copied by the "PanelWidth" / "PanelDockedVideoHeight" bootstrap steps to
// `theme.classic.state.panelWidth` / `.dockedVideoHeight`; `library.<kind>.leftw` / `.midw` by "LibraryColumnWidths" to
// `theme.classic.state.library.<kind>.leftw` / `.midw`. An existing user keeps their panel, docked video and column widths, and
// the state is per theme (another theme's scope starts at the key's default).

using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class PanelAndLibraryStateMigrationTests
{
    static readonly string[] Kinds = ["albums", "artists", "podcasts", "audiobooks"];
    static readonly string[] StepNames = ["PanelWidth", "PanelDockedVideoHeight", "LibraryColumnWidths"];

    static ThemeBootstrapStep Step(string name) => Assert.Single(ThemeBootstrap.Steps, s => s.Name == name);

    static SnapshotSettings Run(SnapshotSettings s)
    {
        ThemeBootstrap.Run(s, StepNames.Select(Step).ToArray());
        return s;
    }

    static SettingKey<float> Legacy(string name) => new(name, 0f);

    [Fact]
    public void The_steps_read_the_legacy_keys_and_write_the_state_keys_the_readers_use()
    {
        Assert.Equal(new[] { "shell.rail.width" }, Step("PanelWidth").ReadsLegacyKeys);
        Assert.Equal(new[] { ThemeState.SettingName(ClassicTheme.Id, Shell.PanelSizeKeys.Width.Id) }, Step("PanelWidth").WritesKeys);
        Assert.Equal(new[] { "shell.rail.docked-video.height" }, Step("PanelDockedVideoHeight").ReadsLegacyKeys);
        Assert.Equal(new[] { ThemeState.SettingName(ClassicTheme.Id, Shell.PanelSizeKeys.DockedVideoHeight.Id) }, Step("PanelDockedVideoHeight").WritesKeys);

        var library = Step("LibraryColumnWidths");
        Assert.Equal(8, library.ReadsLegacyKeys.Length);
        Assert.Equal(8, library.WritesKeys.Length);
        foreach (string kind in Kinds)
        {
            Assert.Contains("library." + kind + ".leftw", library.ReadsLegacyKeys);
            Assert.Contains("library." + kind + ".midw", library.ReadsLegacyKeys);
            Assert.Contains(ThemeState.SettingName(ClassicTheme.Id, LibraryViewPrefs.LeftWKey(kind).Id), library.WritesKeys);
            Assert.Contains(ThemeState.SettingName(ClassicTheme.Id, LibraryViewPrefs.MidWKey(kind).Id), library.WritesKeys);
        }
        Assert.Null(ThemeBootstrap.Disjointness(ThemeBootstrap.Steps));
    }

    [Fact]
    public void A_stored_panel_width_and_docked_height_carry_over_and_the_legacy_keys_stay()
    {
        var s = new SnapshotSettings();
        s.Set(Legacy("shell.rail.width"), 420f);
        s.Set(Legacy("shell.rail.docked-video.height"), 250f);
        Run(s);
        Assert.Equal(420f, ThemeState.Get(Shell.PanelSizeKeys.Width, s, ClassicTheme.Id));
        Assert.Equal(250f, ThemeState.Get(Shell.PanelSizeKeys.DockedVideoHeight, s, ClassicTheme.Id));
        Assert.Equal(420f, s.Get(Legacy("shell.rail.width")));
    }

    [Fact]
    public void A_panel_width_outside_the_bounds_is_clamped_on_read()
    {
        var s = new SnapshotSettings();
        s.Set(Legacy("shell.rail.width"), 5000f);
        Run(s);
        Assert.Equal(Shell.RailMaxW, ThemeState.Get(Shell.PanelSizeKeys.Width, s, ClassicTheme.Id));
    }

    [Fact]
    public void Every_kinds_columns_carry_over_and_an_untouched_kind_keeps_its_default()
    {
        var s = new SnapshotSettings();
        s.Set(Legacy("library.albums.leftw"), 400f);
        s.Set(Legacy("library.albums.midw"), 500f);
        s.Set(Legacy("library.artists.leftw"), 300f);
        Run(s);
        Assert.Equal(400f, ThemeState.Get(LibraryViewPrefs.LeftWKey("albums"), s, ClassicTheme.Id));
        Assert.Equal(500f, ThemeState.Get(LibraryViewPrefs.MidWKey("albums"), s, ClassicTheme.Id));
        Assert.Equal(300f, ThemeState.Get(LibraryViewPrefs.LeftWKey("artists"), s, ClassicTheme.Id));
        Assert.Equal(440f, ThemeState.Get(LibraryViewPrefs.MidWKey("artists"), s, ClassicTheme.Id));
        Assert.Equal(340f, ThemeState.Get(LibraryViewPrefs.LeftWKey("podcasts"), s, ClassicTheme.Id));
        Assert.False(s.Has("theme.classic.state.library.podcasts.leftw"));
    }

    [Fact]
    public void Nothing_stored_writes_nothing_and_a_state_value_already_set_is_never_overwritten()
    {
        var empty = Run(new SnapshotSettings());
        Assert.False(empty.Has("theme.classic.state.panelWidth"));
        Assert.False(empty.Has("theme.classic.state.dockedVideoHeight"));

        var s = new SnapshotSettings();
        s.Set(Legacy("shell.rail.width"), 420f);
        ThemeState.Set(Shell.PanelSizeKeys.Width, 260f, s, ClassicTheme.Id);
        Run(s);
        Assert.Equal(260f, ThemeState.Get(Shell.PanelSizeKeys.Width, s, ClassicTheme.Id));
    }

    [Fact]
    public void The_state_is_per_theme_so_another_themes_scope_starts_at_the_default()
    {
        var s = new SnapshotSettings();
        ThemeState.Set(Shell.PanelSizeKeys.Width, 420f, s, ClassicTheme.Id);
        Assert.Equal(Shell.RailDefaultW, ThemeState.Get(Shell.PanelSizeKeys.Width, s, "other"));
    }
}
