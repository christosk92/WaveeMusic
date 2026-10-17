// ── Wavee.ShellTests/ZuneDiscoveryMountedTests.cs — Zune's Home, Browse and Search, mounted under Zune (themes plan 6.3; P5 Z5) ────────────────
//
// The app mounted under Zune (`ZuneHost`) at 1440 x 960 (Standard), against the fake dataset:
//
//   registered   Zune's own registration draws every discovery key (zune.*), Home.FacetRow is Nothing, and Classic registers Nothing for the new
//                Home.Quickplay key
//   home         the real shell draws Zune's Home page (its column on the frame's gutter, the quickplay strip with the user's pins), never
//                Classic's facet row
//   row 2        the band's row 2 is Home's facets ("all" first and lit), the band stands at its two rows, and a click on a facet switches it
//                (the route's argument and the lit view follow)
//   browse       Zune's directory draws the bands, and a band switch in row 2 filters it to that band
//   search       Zune's results page draws the facet body and the band's row 2 is the facet strip with its counts
//
// Anything that loads waits on the wall clock (`UntilBackground`), never a frame budget. Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Themes.Common;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneDiscoveryMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;

    static void Click(Shell.HeadlessShell shell, NodeHandle node)
    {
        var r = shell.Host.Scene.AbsoluteRect(node);
        var p = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: 2000));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 2010));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 2060));
        shell.RunFrames(3);
    }

    static PageChrome? Chrome() => Sidebar.DockedPaneProps.Band!.Chrome.Peek();

    static float BandH(Shell.HeadlessShell shell) => shell.Host.Scene.AbsoluteRect(Node(shell, ZuneBandRules.BandKey)).H;

    static void GoHome(Shell.HeadlessShell shell)
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        shell.RunFrames(10);
    }

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_draws_every_discovery_key_and_nothing_for_the_facet_row()
    {
        var builder = new FluentGpu.Theming.ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        var ids = new List<string>
        {
            ViewKeys.HomePage.Id, ViewKeys.HomeQuickplay.Id, ViewKeys.HomeZoneRecentGrid.Id, ViewKeys.HomeDaylist.Id, ViewKeys.HomeDaylistClock.Id,
            ViewKeys.HomeCharts.Id, ViewKeys.HomeSection.Id, ViewKeys.BrowseDirectory.Id, ViewKeys.BrowseCharts.Id, ViewKeys.BrowseCategory.Id,
            ViewKeys.BrowseSection.Id, ViewKeys.SearchPage.Id,
        };
        foreach (var kind in Enum.GetValues<Wavee.HomeUi.ZoneKind>())
            if (HomeZoneKeys.TryKeyOf(kind, out var key)) ids.Add(key.Id);
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string template = registry.TemplateIdOf(id) ?? "";
            Assert.True(template.StartsWith("zune.", StringComparison.Ordinal), $"{id} is drawn by '{template}'");
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Equal(CommonNothing.IdFor(ViewKeys.HomeFacetRow.Id), registry.TemplateIdOf(ViewKeys.HomeFacetRow.Id));
        // Classic has no quickplay strip: it registers Nothing for the new key.
        Assert.Equal(CommonNothing.IdFor(ViewKeys.HomeQuickplay.Id), ClassicTheme.Create().Registry.TemplateIdOf(ViewKeys.HomeQuickplay.Id));
    }

    // ── Home ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Home_draws_zunes_page_with_the_quickplay_strip_and_the_users_pins()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            var pinned = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                string uri = "spotify:album:z5quick" + i;
                string id = SidebarPinId.AlbumPrefix + uri;
                if (Sidebar.IsPinned(id)) continue;
                Assert.True(Sidebar.Model.Pin(id, SidebarEntryKind.Album, uri, "Quick " + i), "pinned " + id);
                pinned.Add(id);
            }
            try
            {
                GoHome(shell);
                UntilBackground(shell, () => !shell.Find(ZuneHomeRules.ColumnKey).IsNull && !shell.Find(ZuneHomeRules.QuickplayKey).IsNull,
                    "Zune's home column and its quickplay strip");
                Assert.Equal("zune.home.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.HomePage.Id));
                Assert.Equal("zune.home.quickplay", ThemeHost.Registry.TemplateIdOf(ViewKeys.HomeQuickplay.Id));
                // Classic's page and facet row are not drawn: the facet words are the band's row 2.
                Assert.True(shell.Find("home:column").IsNull, "Classic's home column is not drawn under Zune");
                Assert.True(shell.Find("home:facet:band").IsNull, "Classic's facet row is not drawn under Zune");

                // The quickplay strip's pins group holds the user's pins (the tier's count at most).
                UntilBackground(shell, () => shell.FindPrefix(ZuneHomeRules.PinTilePrefix).Count > 0, "the pins in the quickplay strip");
                int pins = shell.FindPrefix(ZuneHomeRules.PinTilePrefix).Count;
                Assert.InRange(pins, 1, ZuneHomeTiers.For(WidthTier.Standard).QuickPins);
                Assert.False(shell.Find(ZuneHomeRules.QuickGroupPrefix + ZuneHomeRules.PinsGroup).IsNull, "the pins group");

                // The column stands on the frame's gutter, inside the window.
                var scene = shell.Host.Scene;
                var column = scene.AbsoluteRect(Node(shell, ZuneHomeRules.ColumnKey));
                var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
                Assert.True(column.X >= content.X - 0.5f && column.X + column.W <= content.X + content.W + 0.5f, $"the column fits the page ({column} in {content})");

                // The zones of the fake feed follow the strip.
                UntilBackground(shell, () => shell.FindPrefix(ZuneHomeRules.ZonePrefix).Count > 0, "the zone chapters of the fake feed");
            }
            finally
            {
                foreach (string id in pinned) Sidebar.Model.Unpin(id);
                shell.RunFrames(10);
            }
        }, W, H);

    [Fact]
    public void The_bands_row_two_is_homes_facets_with_the_current_lit_and_a_click_switches_the_facet()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoHome(shell);
                // The facet words land with the first model; until then the row is "all" alone.
                UntilBackground(shell, () => Chrome() is { Views.Count: > 1 } && !shell.Find(ZuneBandRules.Row2Key).IsNull,
                    "Home's facets in the band's row 2");
                var chrome = Chrome()!;
                Assert.Equal(Wavee.HomeUi.HomeHost.AllViewId, chrome.Views[0].Id);
                Assert.Equal(Wavee.HomeUi.HomeHost.AllViewId, chrome.CurrentView?.Id);
                Until(shell, () => shell.FindPrefix(ZuneBandRules.ViewKeyPrefix).Count == Chrome()!.Views.Count, "every facet drawn in row 2");
                Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + Wavee.HomeUi.HomeHost.AllViewId).IsNull, "the lit facet is drawn");
                var standard = ZuneBandRules.Band(WidthTier.Standard);
                Until(shell, () => MathF.Abs(BandH(shell) - standard.ExpandedH) < 0.5f, "the band at its two rows on Home");

                // A click on the podcasts facet switches it: the route's argument and the lit view follow.
                const string Podcasts = "podcasts-chip";
                Assert.Contains(chrome.Views, v => v.Id == Podcasts);
                Click(shell, Node(shell, ZuneBandRules.ViewKeyPrefix + Podcasts));
                UntilBackground(shell, () => Shell.Current.Peek() is { Kind: Shell.RouteKind.Home } r && Shell.ArgOf(r) == Podcasts,
                    "the click navigated to the podcasts facet");
                UntilBackground(shell, () => Chrome()?.CurrentView?.Id == Podcasts, "the podcasts facet is lit in row 2");
                Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + Podcasts).IsNull, "the lit facet is drawn");
                Assert.True(MathF.Abs(BandH(shell) - standard.ExpandedH) < 0.5f, "the band keeps its two rows through the switch");

                // And back through row 2: "all".
                Click(shell, Node(shell, ZuneBandRules.ViewKeyPrefix + Wavee.HomeUi.HomeHost.AllViewId));
                UntilBackground(shell, () => Chrome()?.CurrentView?.Id == Wavee.HomeUi.HomeHost.AllViewId, "all is lit again");
            }
            finally { GoHome(shell); }
        }, W, H);

    // ── Browse ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Browse_draws_zunes_directory_and_a_band_switch_filters_it()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));
                UntilBackground(shell, () => !shell.Find(ZuneBrowseRules.DirectoryKey).IsNull && shell.FindPrefix(ZuneBrowseRules.BandPrefix).Count > 1,
                    "Zune's Browse directory and its bands");
                Assert.Equal("zune.browse.directory", ThemeHost.Registry.TemplateIdOf(ViewKeys.BrowseDirectory.Id));
                Assert.Equal(BrowseBands.Top, Chrome()?.CurrentView?.Id);
                Assert.False(shell.Find(ZuneBrowseRules.BandPrefix + "genres:band").IsNull, "the genres band on the whole directory");

                Click(shell, Node(shell, ZuneBandRules.ViewKeyPrefix + BrowseBands.Genres));
                UntilBackground(shell, () => Chrome()?.CurrentView?.Id == BrowseBands.Genres, "genres is lit in row 2");
                UntilBackground(shell, () => shell.Find(ZuneBrowseRules.BandPrefix + "charts+foryou:band").IsNull
                    && shell.Find(ZuneBrowseRules.BandPrefix + "mood:band").IsNull, "the directory filtered to the genres");
                Assert.False(shell.Find(ZuneBrowseRules.BandPrefix + "genres:band").IsNull, "the genres band stays");
            }
            finally { GoHome(shell); }
        }, W, H);

    // ── Search ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Search_draws_zunes_page_and_the_bands_row_two_is_the_facet_strip_with_counts()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                Shell.GoTo(Shell.Parse("search", Entities.FakeSearchQuery));
                UntilBackground(shell, () => !shell.Find(ZuneSearchRules.PageKey).IsNull && Chrome() is { Views.Count: > 1 },
                    "Zune's search page and its facets in row 2");
                Assert.Equal("zune.search.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.SearchPage.Id));
                var chrome = Chrome()!;
                Assert.Equal("all", chrome.Views[0].Id);
                Assert.Equal("all", chrome.CurrentView?.Id);
                Assert.Null(chrome.Views[0].Count);                                      // All wears no count
                Assert.Contains(chrome.Views, v => v.Count is > 0);                    // the facets wear theirs
                Until(shell, () => shell.FindPrefix(ZuneBandRules.ViewKeyPrefix).Count == Chrome()!.Views.Count, "every facet drawn in row 2");
            }
            finally { GoHome(shell); }
        }, W, H);
}
