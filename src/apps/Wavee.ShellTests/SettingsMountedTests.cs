// ── Wavee.ShellTests/SettingsMountedTests.cs — the Settings page as Classic's registered view, mounted (T11b) ─────────────────────────────────────
//
// What only a mounted page can say: the page mounts through its key (the core host publishes the props, Classic's template draws), every tab body sits in its own
// keyed scroll lane (so each keeps its offset), the deep link opens the tab it names, and the three surfaces the setup wizard shares with the page mount through their own
// keys. The Appearance tab's picker, options and legacy rows are `ThemeOptionsMountedTests`'; the Storage, Privacy & diagnostics and About tabs read the disk, the crash
// reports and the process on mount, so they are left to the live app.

using FluentGpu.Foundation;
using Wavee;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class SettingsMountedTests(ShellHost host)
{
    [Theory]
    [InlineData(Settings.Tab.General)]
    [InlineData(Settings.Tab.Appearance)]
    [InlineData(Settings.Tab.Playback)]
    [InlineData(Settings.Tab.Notifications)]
    public void A_tab_opens_in_its_own_keyed_scroll_lane(Settings.Tab tab)
        => host.Mounted(shell =>
        {
            Settings.Open(tab);
            shell.RunFrames(60);

            string slug = Settings.TabSlugs[(int)tab];
            Node(shell, "settings:scroll:" + slug);
            // The other tabs' lanes are not mounted once the cross-fade settles: only the lane is keyed per tab.
            foreach (string other in Settings.TabSlugs)
                if (other != slug) Assert.True(shell.Find("settings:scroll:" + other).IsNull, "the '" + other + "' lane is mounted beside '" + slug + "'");
        });

    // Classic tuning T2 (Q38): a tab switch is the page cross-fade. The arriving lane declares the page's neutral swap and fades in.
    [Fact]
    public void A_tab_switch_cross_fades_the_lane_like_a_page()
        => host.Mounted(shell =>
        {
            Settings.Open(Settings.Tab.General);
            shell.RunFrames(60);

            Settings.Open(Settings.Tab.Appearance);
            shell.RunFrames(1);
            var lane = Node(shell, "settings:tab:appearance");
            Assert.True(shell.Host.Animation.TryGetTransition(lane, out LayoutTransition swap), "the tab lane declares no transition");
            Assert.Equal(Design.Nav.RecipeFor(Design.NavTransitionKind.Neutral), swap);
            Assert.True(shell.Host.Animation.HasTracks(lane), "the arriving tab snapped in instead of fading");
            shell.RunFrames(60);
        });

    // Classic tuning T1 + F4: the head is the 40/52 page title alone (no glyph, no meta line), and the first section header has no top margin:
    // the tab lane's own 16 is the whole gap above it.
    [Fact]
    public void The_head_is_the_page_title_alone_and_the_first_section_sits_on_the_lanes_top()
        => host.Mounted(shell =>
        {
            Settings.Open(Settings.Tab.General);
            shell.RunFrames(60);

            var scene = shell.Host.Scene;
            var title = scene.PresentedAbsoluteRect(Node(shell, "settings:title"));
            Assert.True(title.H >= 52f, $"the title is {title.H} tall: not the 40/52 page title");

            var lane = Node(shell, "settings:scroll:general");
            var laneBox = scene.PresentedAbsoluteRect(lane);
            // The first section header sits 16 under the lane's top edge (the lane's own padding), not 16 + 32.
            float first = shell.FindPrefix("settings:section:").Min(s => scene.PresentedAbsoluteRect(s.Node).Y);
            Assert.Equal(16f, first - laneBox.Y, 0.5f);
        });

    [Fact]
    public void A_deep_link_opens_the_tab_its_slug_names_and_a_bad_slug_lands_on_general()
        => host.Mounted(shell =>
        {
            Settings.Open(Settings.TabFromSlug("playback"));
            shell.RunFrames(60);
            Node(shell, "settings:scroll:playback");

            Settings.Open(Settings.TabFromSlug("no-such-tab"));
            shell.RunFrames(60);
            Node(shell, "settings:scroll:general");
        });

    [Fact]
    public void The_theme_picker_mounts_through_its_key()
        => ViewHarness.With(host, ViewKeys.SettingsThemePicker, SettingsThemePickerProps.Instance, harness =>
        {
            Assert.True(harness.Contains("settings:theme:picker"));
            Assert.True(harness.Contains("settings:theme:card:classic"));
        });

    [Fact]
    public void The_options_mount_as_the_pages_expander_or_the_wizards_plate_rows()
    {
        ViewHarness.With(host, ViewKeys.SettingsThemeOptions, SettingsThemeOptionsProps.Page, harness =>
            Assert.True(harness.Contains("settings:theme:options")));
        ViewHarness.With(host, ViewKeys.SettingsThemeOptions, SettingsThemeOptionsProps.WizardPlate, harness =>
        {
            Assert.True(harness.Contains("settings:theme:wizard-options"));
            // The wizard shows Classic's generated options (the sidebar layout cards first) and nothing it still binds to a legacy key.
            Assert.True(harness.Contains("opt:structure.sidebar"));
            Assert.False(harness.Contains("legacy:trackListStyle"));
            Assert.False(harness.Contains("legacy:sidebarReset"));
        });
    }

    [Fact]
    public void The_sidebar_layout_option_draws_the_two_preview_cards_and_the_density_its_combo()
        => ViewHarness.With(host, ViewKeys.SettingsThemeOptions, SettingsThemeOptionsProps.Page, harness =>
        {
            Assert.True(harness.Contains("opt:structure.sidebar"));
            Assert.True(harness.Contains("classic"));
            Assert.True(harness.Contains("library"));
            Assert.True(harness.Contains("opt:structure.libraryDensity"));
            Assert.True(harness.Contains("legacy:sidebarReset"));   // the three sidebar actions stay legacy rows, below the options
        });
}
