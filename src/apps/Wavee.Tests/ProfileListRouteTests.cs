// ── Wavee.Tests/ProfileListRouteTests.cs — the profile pages' routes and click-source plumbing (#161) ──────────────
//
// `user:<uri>` (RouteKind.User) and `people:<facet>:<uri>` (RouteKind.ProfileList, Following = 0 | Followers = 1):
// the table rows, the key codec, the deep-link intake, the one composer every click source uses (ProfileRoute /
// ProfileListRoute), and the surfaces that remember a visited profile. Pure: no engine, no page.

using FluentGpu.Localization;
using Xunit;

namespace Wavee.Tests;

public class ProfileListRouteTests
{
    const string Jane = "spotify:user:jane";
    const string Abc = "spotify:user:abc";

    [Fact]
    public void User_row_is_a_material_prefix_that_carries_the_display_name()
    {
        ref readonly var row = ref Shell.Row(Shell.RouteKind.User);
        Assert.Equal("user:", row.Key);
        Assert.True(row.IsPrefix);
        Assert.True(row.ClaimsMaterial);
        Assert.False(row.KeyedByArg);
        Assert.False(row.DeveloperOnly);
        Assert.Equal("Mira", Shell.Dest(Shell.Parse("user:" + Jane, "Mira")).Title);
    }

    [Fact]
    public void The_list_row_is_a_non_material_prefix_keyed_and_placed_by_its_arg()
    {
        ref readonly var row = ref Shell.Row(Shell.RouteKind.ProfileList);
        Assert.Equal("people:", row.Key);
        Assert.True(row.IsPrefix);
        Assert.False(row.ClaimsMaterial);
        Assert.True(row.KeyedByArg);
        Assert.True(row.PlaceByArg);
    }

    [Fact]
    public void A_bare_spotify_user_uri_opens_the_profile()
    {
        var v = Shell.DeepLink(Jane);
        Assert.Equal(Shell.DeepLinkKind.Open, v.Kind);
        Assert.Equal(Shell.RouteKind.User, v.Route.Kind);
        Assert.Equal("user:" + Jane, Shell.NameOf(v.Route));
    }

    [Fact]
    public void Open_route_user_composes_the_key()
    {
        var v = Shell.DeepLink("wavee://open?route=user&arg=" + Jane);
        Assert.Equal(Shell.DeepLinkKind.Open, v.Kind);
        Assert.Equal(Shell.RouteKind.User, v.Route.Kind);
        Assert.Equal(EntityUri.Parse(Jane), v.Route.Subject);
        // The bare word with nothing after it addresses no one.
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("wavee://open?route=user").Kind);
    }

    [Fact]
    public void Shell_For_a_user_uri_is_the_profile_route()
    {
        var r = Shell.For(EntityUri.Parse(Jane), "Jane");
        Assert.Equal(Shell.RouteKind.User, r.Kind);
        Assert.Equal(EntityUri.Parse(Jane), r.Subject);
        Assert.True(Shell.IsKnown(r));
    }

    [Fact]
    public void ProfileList_round_trips_its_key()
    {
        string key = "people:1:" + Abc;
        var r = Shell.Parse(key);
        Assert.Equal(Shell.RouteKind.ProfileList, r.Kind);
        Assert.Equal(EntityUri.Parse(Abc), r.Subject);
        Assert.Equal(key, Shell.NameOf(r));
        Assert.Null(Shell.ArgOf(r));
        Assert.True(Shell.IsKnown(r));
        Assert.True(ProfileListRoute.TryParse(r, out var facet, out var user));
        Assert.Equal(ProfileFacet.Followers, facet);
        Assert.Equal(EntityUri.Parse(Abc), user);
        Assert.Equal(key, ProfileListRoute.Key(ProfileFacet.Followers, Abc));
        Assert.Equal("people:0:" + Abc, ProfileListRoute.Key(ProfileFacet.Following, Abc));
    }

    [Theory]
    [InlineData("people:9:" + Abc)]   // not a facet digit
    [InlineData("people:2:" + Abc)]   // there is no Playlists facet (no source for more than ten public playlists)
    [InlineData("people:1:")]         // no user
    [InlineData("people:0:")]
    [InlineData("people:x")]
    [InlineData("people:1")]
    public void ProfileList_refuses_a_bad_facet_or_a_missing_user(string key)
    {
        var r = Shell.Parse(key);
        Assert.False(Shell.IsKnown(r));
        Assert.False(ProfileListRoute.TryParse(r, out _, out _));
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("wavee://open?route=" + key).Kind);
    }

    [Fact]
    public void ProfileList_facets_are_distinct_places_but_one_page()
    {
        var following = Shell.Parse("people:0:" + Abc);
        var followers = Shell.Parse("people:1:" + Abc);
        Assert.False(Shell.SameSlot(following, followers));                       // a facet is still a new PLACE (history, tabs)
        Assert.Equal(Shell.SlotKey(following), Shell.SlotKey(followers));         // but the same mounted PAGE: the pill slides
        Assert.NotEqual(Shell.SlotKey(following), Shell.SlotKey(Shell.Parse("people:0:spotify:user:other")));
        Assert.True(Shell.SameSlot(following, Shell.Parse("people:0:" + Abc)));
        Assert.Equal(Shell.SlotKey(following), Shell.SlotKey(Shell.Parse("people:0:" + Abc)));
    }

    [Fact]
    public void A_profile_list_tab_is_labelled_by_its_facet()
    {
        Assert.Equal(Loc.Get(Strings.Person.Pivot.Followers), Shell.Dest(Shell.Parse("people:1:" + Abc)).Title);
        Assert.Equal(Loc.Get(Strings.Person.Pivot.Following), Shell.Dest(Shell.Parse("people:0:" + Abc)).Title);
        Assert.NotEqual(ProfileListRoute.FacetLabelKey(ProfileFacet.Following), ProfileListRoute.FacetLabelKey(ProfileFacet.Followers));
    }

    [Fact]
    public void ProfileListRoute_For_equals_the_deep_link_route()
    {
        var composed = ProfileListRoute.For(EntityUri.Parse(Abc), ProfileFacet.Followers);
        var deep = Shell.DeepLink("wavee://open?route=people:1:" + Abc);
        Assert.Equal(Shell.DeepLinkKind.Open, deep.Kind);
        Assert.Equal(composed, deep.Route);
        Assert.True(ProfileListRoute.For(default(EntityUri), ProfileFacet.Following).IsNone);
    }

    [Fact]
    public void ProfileRoute_For_a_uri_routes_even_without_a_spotify_user_kind()
    {
        // --fake's account row is a bare id, not a spotify:user: uri: Shell.For's kind switch would refuse it, the
        // composer goes through Parse so the "Profile" menu row still opens something.
        var bare = EntityUri.Parse("wavee-listener");
        Assert.True(Shell.For(bare, "Wavee Listener").IsNone);
        var r = ProfileRoute.For(bare, "Wavee Listener");
        Assert.Equal(Shell.RouteKind.User, r.Kind);
        Assert.NotEqual(default(EntityUri), r.Subject);
        Assert.True(Shell.IsKnown(r));
        Assert.True(ProfileRoute.For(default(EntityUri)).IsNone);
    }

    [Fact]
    public void ProfileRoute_For_a_user_row_is_named_after_it()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Users.RowFor(new StagedId(s.Text(Jane)), Authority.Full, (uint)UserFields.Identity);
        row.Name = s.Text("Jane Doe");
        s.Users.Settle();
        TestScope.CommitAndPublish(s);
        var user = Entities.User(EntityUri.Parse(Jane));

        var r = ProfileRoute.For(user);
        Assert.Equal(Shell.RouteKind.User, r.Kind);
        Assert.Equal(EntityUri.Parse(Jane), r.Subject);
        Assert.Equal("Jane Doe", Shell.Dest(r).Title);
        Assert.Equal(ProfileRoute.For(EntityUri.Parse(Jane), "Jane Doe"), r);
        Assert.Equal(ProfileListRoute.For(EntityUri.Parse(Jane), ProfileFacet.Following), ProfileListRoute.For(user, ProfileFacet.Following));
        Assert.True(ProfileRoute.For(new User(Table.None)).IsNone);
    }

    [Fact]
    public void Profile_routes_are_Detail_surfaces()
    {
        Assert.Equal(Design.NavSurface.Detail, Shell.SurfaceOf(Shell.RouteKind.User));
        Assert.Equal(Design.NavSurface.Detail, Shell.SurfaceOf(Shell.RouteKind.ProfileList));
    }

    [Fact]
    public void Profile_routes_are_not_sidebar_pins_yet()
    {
        Assert.Null(SidebarPinId.FromRoute("user:" + Jane));
        Assert.Null(SidebarPinId.FromRoute("people:1:" + Abc));
    }

    [Fact]
    public void RecentSurfaces_includes_a_visited_profile()
    {
        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var entries = new List<Shell.HistoryEntry>
        {
            new(Shell.Parse("people:1:" + Abc), at),                   // a list is not a destination of its own
            new(Shell.Parse("user:" + Jane, "Jane Doe"), at.AddMinutes(1)),
        };
        var rows = Shell.History.RecentSurfaces(entries, 6);
        var row = Assert.Single(rows);
        Assert.Equal("user:" + Jane, row.Route);
        Assert.Equal("Jane Doe", row.Title);
    }

    [Fact]
    public void FromHistory_offers_a_visited_profile_as_a_User_item()
    {
        var entries = new List<Shell.HistoryEntry> { new(Shell.Parse("user:" + Jane, "Jane Doe"), DateTime.UtcNow) };
        var s = Shell.Omnibar.FromHistory(entries, "jane");
        var item = Assert.Single(s.Items);
        Assert.Equal(Shell.Omnibar.ItemKind.User, item.Kind);
        Assert.Equal("Jane Doe", item.Title);
        // ... and choosing it navigates back to the same profile route.
        Assert.Equal(Shell.RouteKind.User, Shell.Omnibar.RouteFor(item).Kind);
    }
}
