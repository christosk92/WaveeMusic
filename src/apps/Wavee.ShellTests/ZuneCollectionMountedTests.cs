// ── Wavee.ShellTests/ZuneCollectionMountedTests.cs — Zune's collection, mounted under Zune (themes plan 6.3; P5 Z7) ──────────────────────────
//
// The app mounted under Zune (`ZuneHost`) at 1440 x 960 (Standard), against the fake dataset. This part holds the library; Recents and the profile are the partial files
// beside it (`ZuneCollectionMountedTests.Recents.cs`, `.Profile.cs`).
//
//   registered   Zune's own registration draws every library key (zune.library.*), and a Classic mount of the same keys is untouched
//   albums       the real shell draws Zune's library page (its head and the navigator column on the frame's gutter), never Classic's toolbar; the page fits the content
//   artists      the page draws the artists' list column at the tier's width
//   row 2        the band's row 2 carries the library kinds with "playlists" second; a click on it navigates to the playlists route, which lists the playlists and folders
//   playlists    the same route works under Classic (no link to it, but navigating there draws the list)
//   compact      a 600-wide window folds the columns into the breadcrumb drill-in
//
// Anything that loads waits on the wall clock (`UntilBackground`), never a frame budget. Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneCollectionMountedTests(ShellHost host)
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

    static void GoLibrary(Shell.HeadlessShell shell, Shell.RouteKind kind)
    {
        Shell.GoTo(new Shell.Route(kind));
        shell.RunFrames(10);
    }

    static void GoHome(Shell.HeadlessShell shell)
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        shell.RunFrames(10);
    }

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_draws_every_library_key()
    {
        var builder = new FluentGpu.Theming.ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        var ids = new[]
        {
            ViewKeys.LibraryPage.Id, ViewKeys.LibraryAlbumPane.Id, ViewKeys.LibraryShowPane.Id, ViewKeys.LibraryArtistReader.Id, ViewKeys.LibrarySavedEpisodes.Id,
        };
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string template = registry.TemplateIdOf(id) ?? "";
            Assert.True(template.StartsWith("zune.", StringComparison.Ordinal), $"{id} is drawn by '{template}'");
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        // Classic's own registration of the page is untouched.
        Assert.Equal("classic.library.page", Wavee.Themes.Classic.ClassicTheme.Create().Registry.TemplateIdOf(ViewKeys.LibraryPage.Id));
    }

    // ── the library page ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Albums_draws_zunes_page_with_its_head_and_the_navigator_on_the_gutter()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoLibrary(shell, Shell.RouteKind.LibraryAlbums);
                UntilBackground(shell, () => !shell.Find(ZuneLibraryRules.PageKey).IsNull && !shell.Find(ZuneLibraryRules.HeadKey).IsNull
                    && !shell.Find(ZuneLibraryRules.NavKey).IsNull, "Zune's library page, its head and its navigator");
                Assert.Equal("zune.library.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.LibraryPage.Id));
                // Classic's toolbar is not drawn: the sort and view are the page's words.
                Assert.True(shell.Find("lib:toolbar").IsNull, "Classic's library toolbar is not drawn under Zune");

                // The page stands inside the content column, and the head and the navigator inside the page.
                shell.RunFrames(60);   // the page's entrance slide (Shell.PageTransition) settles before the page's rect is read
                var scene = shell.Host.Scene;
                var page = scene.AbsoluteRect(Node(shell, ZuneLibraryRules.PageKey));
                var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
                Assert.True(page.X >= content.X - 0.5f && page.X + page.W <= content.X + content.W + 0.5f, $"the page fits the content ({page} in {content})");
                var nav = scene.AbsoluteRect(Node(shell, ZuneLibraryRules.NavKey));
                Assert.True(nav.X >= page.X - 0.5f && nav.X + nav.W <= page.X + page.W + 0.5f, $"the navigator fits the page ({nav} in {page})");
                Assert.True(nav.W > 0f, "the navigator has width");
                var head = scene.AbsoluteRect(Node(shell, ZuneLibraryRules.HeadKey));
                Assert.True(head.Y + head.H <= nav.Y + 0.5f, $"the head stands over the navigator ({head} over {nav})");
                // Standard's record: the wide arm, not the drill-in.
                Assert.True(shell.Find(ZuneLibraryRules.CollapsedKey).IsNull, "the columns are not folded at Standard");
            }
            finally { GoHome(shell); }
        }, W, H);

    [Fact]
    public void Artists_draws_the_list_column_at_the_tiers_width()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoLibrary(shell, Shell.RouteKind.LibraryArtists);
                UntilBackground(shell, () => !shell.Find(ZuneLibraryRules.NavKey).IsNull, "Zune's artists list column");
                var nav = shell.Host.Scene.AbsoluteRect(Node(shell, ZuneLibraryRules.NavKey));
                Assert.True(MathF.Abs(nav.W - ZuneLibraryTiers.For(WidthTier.Standard, single: false).ReaderNavW) < 1f,
                    $"the artists' list is the record's width ({nav.W})");
            }
            finally { GoHome(shell); }
        }, W, H);

    [Fact]
    public void A_narrow_window_folds_the_columns_into_the_drill_in()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoLibrary(shell, Shell.RouteKind.LibraryAlbums);
                UntilBackground(shell, () => !shell.Find(ZuneLibraryRules.CollapsedKey).IsNull, "the collapsed drill-in at 600 wide");
                Assert.True(shell.Find(ZuneLibraryRules.WideKey).IsNull, "the wide arm is gone");
                shell.RunFrames(60);   // the page's entrance slide (Shell.PageTransition) settles before the page's rect is read
                var scene = shell.Host.Scene;
                var page = scene.AbsoluteRect(Node(shell, ZuneLibraryRules.PageKey));
                var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
                Assert.True(page.X + page.W <= content.X + content.W + 0.5f, $"no horizontal overflow ({page} in {content})");
            }
            finally { GoHome(shell); }
        }, 600f, 800f);

    // ── the playlists route ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bands_row_two_carries_playlists_and_navigating_to_it_lists_the_playlists()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoLibrary(shell, Shell.RouteKind.LibraryAlbums);
                UntilBackground(shell, () => Chrome() is { Views.Count: > 1 } && !shell.Find(ZuneBandRules.Row2Key).IsNull, "the library's kinds in row 2");
                var chrome = Chrome()!;
                Assert.Equal(new[] { "liked", "playlists", "albums", "artists", "podcasts", "audiobooks" }, chrome.Views.Select(v => v.Id).ToArray());
                Assert.Equal("albums", chrome.CurrentView?.Id);
                Until(shell, () => shell.FindPrefix(ZuneBandRules.ViewKeyPrefix).Count == Chrome()!.Views.Count, "every kind drawn in row 2");

                // A click on "playlists" navigates to the route and lights it.
                Click(shell, Node(shell, ZuneBandRules.ViewKeyPrefix + "playlists"));
                UntilBackground(shell, () => Shell.Current.Peek() is { Kind: Shell.RouteKind.LibraryPlaylists }, "the click navigated to the playlists route");
                UntilBackground(shell, () => Chrome()?.CurrentView?.Id == "playlists", "playlists is lit in row 2");

                // The page lists the user's playlists and folders (the fake dataset's rootlist).
                UntilBackground(shell, () => !shell.Find(User.LibraryPlaylistsRules.ColumnKey).IsNull, "the playlists page");
                UntilBackground(shell, () => shell.FindPrefix(User.LibraryPlaylistsRules.RowPrefix).Count > 0, "the playlists and folders listed");
                Assert.Equal(Shell.BandRules.Library, Shell.BandRules.IndexOf(Shell.Current.Peek().Kind));

                var scene = shell.Host.Scene;
                var column = scene.AbsoluteRect(Node(shell, User.LibraryPlaylistsRules.ColumnKey));
                var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
                Assert.True(column.X >= content.X - 0.5f && column.X + column.W <= content.X + content.W + 0.5f, $"the column fits the page ({column} in {content})");
            }
            finally { GoHome(shell); }
        }, W, H);

    [Fact]
    public void The_playlists_route_works_under_classic_too()
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoLibrary(shell, Shell.RouteKind.LibraryPlaylists);
                UntilBackground(shell, () => !shell.Find(User.LibraryPlaylistsRules.ColumnKey).IsNull, "the playlists page under Classic");
                UntilBackground(shell, () => shell.FindPrefix(User.LibraryPlaylistsRules.RowPrefix).Count > 0, "the playlists listed under Classic");
            }
            finally { GoHome(shell); }
        }, W, H);
}
