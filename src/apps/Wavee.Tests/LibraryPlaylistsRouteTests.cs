// ── Wavee.Tests/LibraryPlaylistsRouteTests.cs — the library's playlists route (themes P5 Z7) ─────────────────────────────────────────
//
// `RouteKind.LibraryPlaylists` is the library's playlists list, the page a structure without a nav tree reaches from the band's row 2. It is
// an ordinary exact route: its key round-trips, its row is its own (never "Your Library"), it is known without developer mode, it claims no
// material, it is a library kind of the history families, the band lights "library" for it, and its page is installed by the lazy group.

using FluentGpu.Localization;
using Xunit;

namespace Wavee.Tests;

// Shell.Parse interns route args into the process-wide Entities.Strings table, which other suites reset: serial with them.
[Collection(EntitiesCollection.Name)]
public class LibraryPlaylistsRouteTests
{
    [Fact]
    public void The_route_round_trips_through_its_key()
    {
        var route = new Shell.Route(Shell.RouteKind.LibraryPlaylists);
        Assert.Equal("playlists", Shell.NameOf(route));
        var parsed = Shell.Parse("playlists");
        Assert.Equal(Shell.RouteKind.LibraryPlaylists, parsed.Kind);
        Assert.Equal("playlists", Shell.NameOf(parsed));
        Assert.Null(Shell.ArgOf(parsed));
        Assert.True(Shell.IsKnown(parsed, developerMode: false));
    }

    [Fact]
    public void The_row_is_its_own_and_the_kind_sits_with_the_library()
    {
        ref readonly var row = ref Shell.Row(Shell.RouteKind.LibraryPlaylists);
        Assert.Equal(Shell.RouteKind.LibraryPlaylists, row.Kind);
        Assert.Equal("playlists", row.Key);
        Assert.False(row.IsPrefix);
        Assert.False(row.DeveloperOnly);
        Assert.False(row.ClaimsMaterial);
        var (title, glyph) = Shell.Dest(new Shell.Route(Shell.RouteKind.LibraryPlaylists));
        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.NotEqual(Loc.Get(Strings.Nav.YourLibrary), title);
        Assert.False(string.IsNullOrEmpty(glyph));
        Assert.Equal(Shell.BandRules.Library, Shell.BandRules.IndexOf(Shell.RouteKind.LibraryPlaylists));
    }

    [Fact]
    public void The_second_view_of_the_library_row_is_the_playlists_route()
    {
        var views = Shell.PageChromeChannel.RouteViews(new Shell.Route(Shell.RouteKind.Liked));
        Assert.Equal("liked", views[0].Id);
        Assert.Equal("playlists", views[1].Id);
        Assert.False(views[1].Current);
        var onPlaylists = Shell.PageChromeChannel.RouteViews(new Shell.Route(Shell.RouteKind.LibraryPlaylists));
        Assert.Equal("playlists", Assert.Single(onPlaylists, v => v.Current).Id);
    }

    [Fact]
    public void The_lazy_group_installs_the_page()
    {
        var snapshot = Shell.SnapshotPagesForTests();
        try
        {
            Shell.RestorePagesForTests(new Shell.PageFactory?[snapshot.Length]);
            Assert.NotNull(Shell.PageFor(new Shell.Route(Shell.RouteKind.LibraryPlaylists)));
        }
        finally
        {
            Shell.RestorePagesForTests(snapshot);
        }
    }
}
