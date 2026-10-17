// ── Wavee.ShellTests/ShellCapabilitiesMountedTests.cs — the shell capabilities and the band's data, mounted (P5 ZK) ────────────
//
// What only the real mounted shell can say about `ShellCapabilities` (Theming\Policies\ThemePolicies.cs) once a structure turns some
// off: the title bar loses its tab island and its Forward button, the sidebar pane is not mounted (and presents no width), the
// workspace collapses to the active tab on the switch and every parked tab comes back, in order, on the switch back (owner Q4), the
// new-tab verbs refuse, and the band's pins are capped by `MaxBandPins`. The theme is the test-only `ClassicBand` (Classic's views
// with Zune's capabilities), switched to with a real theme transaction; Classic is put back alone whatever happens.

using FluentGpu.Hosting;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ShellCapabilitiesMountedTests(ShellHost host)
{
    const float W = 1440f, H = 900f;

    void WithBand(Action<Shell.HeadlessShell> body)
    {
        host.Run(static () => ThemeHost.Install(ClassicTheme.Create(), ClassicBand.Create()));
        try { host.Mounted(body, W, H); }
        finally { host.Run(ClassicAlt.RestoreClassic); }
    }

    static void SwitchTo(Shell.HeadlessShell shell, string id)
    {
        var t = ThemeHost.Switch(id);
        for (int i = 0; i < 300 && !t.IsCompleted; i++) shell.RunFrames(1);
        Assert.True(t.IsCompletedSuccessfully, "the switch to '" + id + "' completed");
        Assert.Equal(ThemeTransactionOutcome.Committed, t.Result.Outcome);
        shell.RunFrames(30);
    }

    static bool PaneMounted(Shell.HeadlessShell shell)
        => !shell.Find("sidebar.classic").IsNull || !shell.Find("sidebar.library").IsNull;

    [Fact]
    public void Without_tabs_forward_and_a_nav_tree_the_title_bar_has_no_tab_island_or_forward_and_no_pane_mounts()
        => WithBand(shell =>
        {
            shell.RunFrames(30);
            // Classic first: the strip, Forward and the pane are all there at a Standard width.
            Assert.False(shell.Find("chrome-tab-strip").IsNull, "Classic draws its tab strip");
            Assert.True(Shell.ChromeLayout.Peek().ShowForward, "Classic seats Forward at 1440");
            Until(shell, () => PaneMounted(shell), "Classic's pane");

            SwitchTo(shell, ClassicBand.Id);

            var row = Shell.RowStateOf(Shell.ChromeLayout.Peek());
            Assert.True(row.TabsHidden, "no tab island at all");
            Assert.False(row.ShowNewTab, "no \"+\" slot");
            Assert.False(row.ShowForward, "the budget has no Forward");
            Assert.True(row.ShowBack, "Back stays");
            Assert.True(shell.Find("chrome-tab-strip").IsNull, "no tab strip");
            Assert.True(shell.Find("chrome-tab-count").IsNull, "no tab count");
            Assert.True(shell.Find("chrome-forward").IsNull, "no Forward button");

            Assert.False(PaneMounted(shell), "the sidebar pane does not mount");
            Assert.Equal(SidebarPaneMode.Minimal, Sidebar.Mode.Peek());
            Assert.Equal(0f, Sidebar.PresentedWidth.Peek());

            // The pane's toggle verbs refuse: nothing opens, nothing is presented.
            Sidebar.TogglePane();
            Sidebar.OpenPane();
            shell.RunFrames(10);
            Assert.False(PaneMounted(shell), "a toggle mounts no pane");
            Assert.False(Sidebar.OverlayOpen.Peek());

            // Back to Classic: everything returns.
            SwitchTo(shell, ClassicTheme.Id);
            Assert.False(shell.Find("chrome-tab-strip").IsNull, "the strip is back");
            Until(shell, () => PaneMounted(shell), "the pane back under Classic");
        });

    [Fact]
    public void The_switch_collapses_the_workspace_to_the_active_tab_and_the_switch_back_restores_every_tab()
        => WithBand(shell =>
        {
            shell.RunFrames(30);
            int[] mine = [];
            try
            {
                Shell.OpenTab(new Shell.Route(Shell.RouteKind.Recents));
                shell.RunFrames(10);
                int recents = Shell.Tabs.ActiveId;
                Shell.OpenTab(new Shell.Route(Shell.RouteKind.Browse));
                shell.RunFrames(10);
                int browse = Shell.Tabs.ActiveId;
                mine = [recents, browse];
                Shell.ActivateTabById(recents);
                shell.RunFrames(20);

                int[] before = Shell.Tabs.Tabs.Select(t => t.Id).ToArray();
                int active = Shell.Tabs.ActiveId;
                Assert.True(before.Length >= 3);
                Assert.Equal(Shell.RouteKind.Recents, Shell.Current.Peek().Kind);

                SwitchTo(shell, ClassicBand.Id);

                Assert.True(Shell.Tabs.Collapsed, "the workspace collapsed");
                Assert.Single(Shell.Tabs.Tabs);
                Assert.Equal(active, Shell.Tabs.ActiveId);
                Assert.Equal(before.Length - 1, Shell.Tabs.ParkedCount);
                Assert.Equal(Shell.RouteKind.Recents, Shell.Current.Peek().Kind);

                // The new-tab verbs refuse; History (its own tab under Classic) opens in place.
                FrameVerbs.Shared.NewTab();
                WorkspaceVerbs.Shared.OpenTab(new Shell.Route(Shell.RouteKind.Home));
                shell.RunFrames(10);
                Assert.Single(Shell.Tabs.Tabs);
                Assert.Equal(Shell.RouteKind.Recents, Shell.Current.Peek().Kind);
                Shell.GoTo(new Shell.Route(Shell.RouteKind.History));
                shell.RunFrames(20);
                Assert.Single(Shell.Tabs.Tabs);
                Assert.Equal(Shell.RouteKind.History, Shell.Current.Peek().Kind);
                Assert.Equal(active, Shell.Tabs.ActiveId);

                SwitchTo(shell, ClassicTheme.Id);

                Assert.False(Shell.Tabs.Collapsed, "the workspace expanded");
                Assert.Equal(before, Shell.Tabs.Tabs.Select(t => t.Id).ToArray());
                Assert.Equal(active, Shell.Tabs.ActiveId);
                Assert.Equal(Shell.RouteKind.History, Shell.Tabs.Active.Route.Kind);   // the active tab kept where it went
                Assert.Equal(Shell.RouteKind.Browse, Shell.Tabs.Tabs[Shell.Tabs.IndexOf(browse)].Route.Kind);
            }
            finally
            {
                if (Shell.Tabs.Collapsed) Shell.Tabs.Expand();   // a fact that failed while collapsed: the next mount would expand it too
                foreach (int id in mine) if (Shell.Tabs.IndexOf(id) >= 0 && Shell.Tabs.Count > 1) Shell.CloseTabById(id);
                shell.RunFrames(10);
            }
        });

    [Fact]
    public void The_band_caps_its_pins_at_six_and_carries_the_library_row_and_the_lit_destination()
        => WithBand(shell =>
        {
            shell.RunFrames(30);
            var pinned = new List<string>();
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    string uri = "spotify:album:zkband" + i;
                    string id = SidebarPinId.AlbumPrefix + uri;
                    if (Sidebar.IsPinned(id)) continue;
                    Assert.True(Sidebar.Model.Pin(id, SidebarEntryKind.Album, uri, "Band " + i), "pinned " + id);
                    pinned.Add(id);
                }
                shell.RunFrames(10);
                var band = Sidebar.DockedPaneProps.Band;
                Assert.NotNull(band);
                Assert.Same(band, Sidebar.DrawerPaneProps.Band);
                Assert.True(band!.Pins().Count >= 8, "Classic does not cap the pins");

                SwitchTo(shell, ClassicBand.Id);

                var pins = band.Pins();
                Assert.Equal(6, pins.Count);
                Assert.All(pins, p => Assert.NotNull(p.Go));
                Assert.Equal(4, band.Destinations().Count);
                Assert.Equal(new[] { "home", "browse", "library", "recents" }, band.Destinations().Select(d => d.Id).ToArray());

                // Row 2 of a library route, answered from the route, with the route lit; the band lights "library".
                Shell.GoTo(new Shell.Route(Shell.RouteKind.LibraryArtists));
                shell.RunFrames(30);
                var chrome = band.Chrome.Peek();
                Assert.NotNull(chrome);
                Assert.Equal(6, chrome!.Views.Count);   // liked, playlists (P5 Z7), albums, artists, podcasts, audiobooks
                Assert.Equal(Shell.NameOf(new Shell.Route(Shell.RouteKind.LibraryArtists)), chrome.CurrentView?.Id);
                Assert.Equal(Shell.BandRules.Library, band.Current());

                // Recents publishes its four pivots as row 2 (P5 Z7; Classic's band draws none of it).
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Recents));
                shell.RunFrames(30);
                Assert.Equal(4, band.Chrome.Peek()!.Views.Count);
                Assert.Equal(Shell.BandRules.Recents, band.Current());
            }
            finally
            {
                foreach (string id in pinned) Sidebar.Model.Unpin(id);
                shell.RunFrames(10);
            }
        });
}
