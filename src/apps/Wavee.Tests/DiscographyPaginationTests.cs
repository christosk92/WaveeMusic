// ── Wavee.Tests/DiscographyPaginationTests.cs — the discography's route, facet filter and paging (ch 08 §8) ─────────
//
// 0.2.9's `DiscographyPaginationTests.cs` held 13 facts over four seams. In 0.3 three of those seams are gone, so the
// facts map like this:
//
//   1  MapArtist_Fixture_CarriesPerFacetTotals          → KEPT as The_overview_states_each_facets_total (the decoder
//                                                          stages 18 / 46 / 2 on the three edges, not on a record).
//   2  Aggregate_RoutesToOwningSource_AndEmptyForUnowned → KEPT as A_facet_page_answers_only_its_own_artist (routing by
//                                                          owner is the planner's; the fact that survives is that a page
//                                                          lands on the named artist and nobody else).
//   3  Dim_Probe_ReturnsEmptyWindow_WithTotal            → ADAPTED as The_first_page_is_the_total_probe: there is no
//                                                          limit-0 probe — the overview's first page STATES the total, so
//                                                          the grid sizes the whole facet (shimmer-up-to-N) with no extra ask.
//   4  KindMatches_SinglesFacet_IncludesSingleAndEp       → KEPT verbatim over ArtistCatalog.KindMatches.
//   5  Dim_SinglesFacet_SurfacesSinglesAndEps_NotAlbums   → ADAPTED as A_singles_page_keeps_eps_and_coerces_a_contradiction.
//   6  Probe_TotalIsInMemoryFilteredCount                → DIED: StoreLibrarySource's in-memory slice is gone; the total
//                                                          is the server's, carried by the edge (fact 3 covers it).
//   7  OffsetWindow_SlicesInMemory                       → ADAPTED as A_later_page_lands_at_its_offset_and_completes.
//   8  SinglesFacet_SurfacesSinglesAndEps_NotAlbums (store) → DIED with StoreLibrarySource (fact 5 is the one filter).
//   9-13 VirtualCollectionSeedTests (5 facts)             → DIED: `VirtualCollection<T>` and its provisional seed are
//                                                          deleted (ch 08 §1.2). The edge's Total/State replace the seed;
//                                                          `EdgeTable.ReplacePage`'s own paging facts live in EdgesTests.
//
// Plus the route helpers 0.2.9 kept untested (DiscographyRoute.Make/Parse), now pinned.
//
// W2-A5 (scroll-feel defects, 2026-09-16): the grid subscribes to its facet's TOTAL alone and every realized cell
// follows its OWN row. The cell's decision is a value (DiscoCellStamp) a memo gates on, and the cell's props gate on
// data — DiscoCellRulesTests / DiscoCellStampTests / DiscoCellPropsTests pin exactly when each of those moves.

using System.Text;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DiscographyRouteTests
{
    [Theory]
    [InlineData(DiscoFacet.Albums, "disco:0:spotify:artist:04gDigrS5kc9YWfZHwBETP")]
    [InlineData(DiscoFacet.Singles, "disco:1:spotify:artist:04gDigrS5kc9YWfZHwBETP")]
    [InlineData(DiscoFacet.Compilations, "disco:2:spotify:artist:04gDigrS5kc9YWfZHwBETP")]
    public void The_key_is_one_digit_then_the_whole_uri_and_it_round_trips(DiscoFacet facet, string key)
    {
        Assert.Equal(key, DiscoRoute.Key(facet, "spotify:artist:04gDigrS5kc9YWfZHwBETP"));
        Assert.True(DiscoRoute.TryParseKey(key, out var parsed, out var uri));
        Assert.Equal(facet, parsed);
        Assert.Equal("spotify:artist:04gDigrS5kc9YWfZHwBETP", uri.ToString());
    }

    [Theory]
    [InlineData("disco:")]
    [InlineData("disco:1")]
    [InlineData("disco:1:")]
    [InlineData("disco:9:spotify:artist:x")]
    [InlineData("disco:x:spotify:artist:x")]
    [InlineData("album:1:spotify:artist:x")]
    public void A_key_without_a_facet_digit_and_a_uri_is_not_a_route(string key)
        => Assert.False(DiscoRoute.TryParseKey(key, out _, out _));

    [Fact]
    public void KindMatches_SinglesFacet_IncludesSingleAndEp_ExcludesAlbum()
    {
        Assert.True(ArtistCatalog.KindMatches(AlbumKind.Single, DiscoFacet.Singles));
        Assert.True(ArtistCatalog.KindMatches(AlbumKind.EP, DiscoFacet.Singles));
        Assert.False(ArtistCatalog.KindMatches(AlbumKind.Album, DiscoFacet.Singles));
        Assert.False(ArtistCatalog.KindMatches(AlbumKind.Compilation, DiscoFacet.Singles));

        Assert.True(ArtistCatalog.KindMatches(AlbumKind.Album, DiscoFacet.Albums));
        Assert.True(ArtistCatalog.KindMatches(AlbumKind.Compilation, DiscoFacet.Compilations));
    }

    [Theory]
    [InlineData("SINGLE", 1, AlbumKind.Single)]
    [InlineData("SINGLE", 4, AlbumKind.EP)]
    [InlineData("single", 3, AlbumKind.Single)]
    [InlineData("EP", 2, AlbumKind.EP)]
    [InlineData("COMPILATION", 20, AlbumKind.Compilation)]
    [InlineData("ALBUM", 12, AlbumKind.Album)]
    [InlineData("", 12, AlbumKind.Album)]
    public void The_wire_type_word_maps_like_the_0_2_9_mapper(string type, int tracks, AlbumKind expected)
        => Assert.Equal(expected, ArtistCatalog.KindOf(Encoding.UTF8.GetBytes(type), tracks));
}

[Collection(EntitiesCollection.Name)]
public class DiscographyPaginationTests
{
    const string ArtistUri = "spotify:artist:04gDigrS5kc9YWfZHwBETP";

    static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotify", name));
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    static Artist ArtistOf(string uri = ArtistUri) => Entities.Artist(EntityUri.Parse(uri.AsSpan()));

    static string Release(string uri, string type, int tracks, int year)
        => "{\"releases\":{\"items\":[{\"uri\":\"" + uri + "\",\"name\":\"N\",\"type\":\"" + type + "\",\"tracks\":{\"totalCount\":"
           + tracks + "},\"date\":{\"year\":" + year + ",\"month\":3,\"day\":4,\"precision\":\"DAY\"}}]}}";

    static string FacetPage(string facet, int total, params string[] items)
        => "{\"data\":{\"artistUnion\":{\"discography\":{\"" + facet + "\":{\"totalCount\":" + total + ",\"items\":["
           + string.Join(",", items) + "]}}}}}";

    [Fact]
    public void The_overview_states_each_facets_total()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistPage(Fixture("artist-maroon5.json"), Utf8(ArtistUri), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf();
        Assert.Equal(18, Artist.FacetTotal(artist, DiscoFacet.Albums));
        Assert.Equal(46, Artist.FacetTotal(artist, DiscoFacet.Singles));
        Assert.Equal(2, Artist.FacetTotal(artist, DiscoFacet.Compilations));
        // …while the overview carries only the first window — the total is not the slice.
        Assert.Equal(10, Entities.Current.Edges.ArtistAlbums.Count(artist.Slot));
        Assert.Equal(10, Entities.Current.Edges.ArtistSingles.Count(artist.Slot));
    }

    [Fact]
    public void The_first_page_is_the_total_probe()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistPage(Fixture("artist-maroon5.json"), Utf8(ArtistUri), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf();
        var albums = Entities.Current.Edges.ArtistAlbums;
        Assert.Equal(EdgeState.Partial, albums.State(artist.Slot));        // more pages to come …
        Assert.True(Artist.HasFacet(artist, DiscoFacet.Albums));            // … and the section is present at its total
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.ArtistCompilations.State(artist.Slot));   // 2 of 2
    }

    [Fact]
    public void A_later_page_lands_at_its_offset_and_completes()
    {
        TestScope.Fresh();
        var first = Staging.Rent();
        Spotify.Decode.DiscographyFacet(Utf8(FacetPage("albums", 3,
            Release("spotify:album:p1", "ALBUM", 10, 2020), Release("spotify:album:p2", "ALBUM", 10, 2019))),
            Utf8(ArtistUri), DiscoFacet.Albums, 0, first);
        TestScope.CommitAndPublish(first);

        var artist = ArtistOf();
        var edge = Entities.Current.Edges.ArtistAlbums;
        Assert.Equal(EdgeState.Partial, edge.State(artist.Slot));
        Assert.Equal(3, edge.Total(artist.Slot));

        var second = Staging.Rent();
        Spotify.Decode.DiscographyFacet(Utf8(FacetPage("albums", 3, Release("spotify:album:p3", "ALBUM", 10, 2018))),
            Utf8(ArtistUri), DiscoFacet.Albums, 2, second);
        TestScope.CommitAndPublish(second);

        Assert.Equal(EdgeState.Complete, edge.State(artist.Slot));
        Assert.Equal(Entities.Current.Albums.Slot("spotify:album:p3".AsSpan()), edge.Targets(artist.Slot)[2]);
    }

    [Fact]
    public void A_singles_page_keeps_eps_and_coerces_a_contradiction()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.DiscographyFacet(Utf8(FacetPage("singles", 3,
            Release("spotify:album:s1", "SINGLE", 1, 2021), Release("spotify:album:s2", "SINGLE", 6, 2020),
            Release("spotify:album:s3", "ALBUM", 2, 2019))), Utf8(ArtistUri), DiscoFacet.Singles, 0, s);
        TestScope.CommitAndPublish(s);

        Assert.Equal(AlbumKind.Single, Entities.Album(EntityUri.Parse("spotify:album:s1".AsSpan())).Kind);
        Assert.Equal(AlbumKind.EP, Entities.Album(EntityUri.Parse("spotify:album:s2".AsSpan())).Kind);
        // The server listed it under Singles: the facet wins, so the facet's count and its cards agree.
        Assert.Equal(AlbumKind.Single, Entities.Album(EntityUri.Parse("spotify:album:s3".AsSpan())).Kind);
    }

    [Fact]
    public void A_facet_page_answers_only_its_own_artist()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.DiscographyFacet(Utf8(FacetPage("albums", 1, Release("spotify:album:mine", "ALBUM", 9, 2020))),
            Utf8(ArtistUri), DiscoFacet.Albums, 0, s);
        TestScope.CommitAndPublish(s);

        Assert.Equal(1, Entities.Current.Edges.ArtistAlbums.Count(ArtistOf().Slot));
        Assert.Equal(EdgeState.Unknown, Entities.Current.Edges.ArtistAlbums.State(ArtistOf("spotify:artist:someone-else").Slot));
        Assert.Equal(EdgeState.Unknown, Entities.Current.Edges.ArtistSingles.State(ArtistOf().Slot));   // one facet per answer
    }

    [Fact]
    public void An_empty_facet_is_complete_and_absent_not_partial_forever()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.DiscographyFacet(Utf8(FacetPage("compilations", 0)), Utf8(ArtistUri), DiscoFacet.Compilations, 0, s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf();
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.ArtistCompilations.State(artist.Slot));
        Assert.False(Artist.HasFacet(artist, DiscoFacet.Compilations));
    }

    [Fact]
    public void A_parsed_disco_route_names_the_facet_and_the_artist()
    {
        TestScope.Fresh();
        var route = Shell.Parse("disco:1:" + ArtistUri);
        Assert.Equal(Shell.RouteKind.Discography, route.Kind);
        Assert.True(DiscoRoute.TryParse(route, out var facet, out var artist));
        Assert.Equal(DiscoFacet.Singles, facet);
        Assert.Equal(ArtistUri, artist.Text);

        var back = DiscoRoute.For(ArtistOf(), DiscoFacet.Compilations);
        Assert.Equal("disco:2:" + ArtistUri, Shell.NameOf(back));
    }
}

/// <summary>W2-A5: which album a grid position paints, pure over the edge's state and its targets. The edge decides the
/// grid's EXTENT (its total); this rule decides, per cell, whether there is a row to paint at all.</summary>
public class DiscoCellRulesTests
{
    [Fact]
    public void An_unanswered_facet_paints_no_card_whatever_the_edge_lists()
        => Assert.Equal(Table.None, DiscoCellRules.TargetAt(EdgeState.Unknown, [5, 6, 7], 0));

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_position_inside_what_landed_names_its_album(EdgeState state)
        => Assert.Equal(6, DiscoCellRules.TargetAt(state, [5, 6, 7], 1));

    [Fact]
    public void A_position_past_what_landed_is_a_placeholder_for_the_page_still_to_come()
    {
        Assert.Equal(Table.None, DiscoCellRules.TargetAt(EdgeState.Partial, [5, 6, 7], 3));
        Assert.Equal(Table.None, DiscoCellRules.TargetAt(EdgeState.Partial, [5, 6, 7], -1));
        Assert.Equal(Table.None, DiscoCellRules.TargetAt(EdgeState.Partial, default, 0));
    }

    [Fact]
    public void A_none_target_inside_the_list_is_a_placeholder()
        => Assert.Equal(Table.None, DiscoCellRules.TargetAt(EdgeState.Complete, [5, Table.None, 7], 1));
}

/// <summary>W2-A5: the cell's stamp is the value its memo gates on, so these facts ARE the cell's re-render rule — it
/// moves when the row gains its card group or is written while it paints a card, and stays equal for a write to a row
/// that is still shimmering (an Identity-only landing) or to any other row.</summary>
[Collection(EntitiesCollection.Name)]
public class DiscoCellStampTests
{
    static int Stage(string uri, AlbumFields groups, string title)
    {
        var s = Staging.Rent();
        ref var row = ref s.Albums.RowFor(s.Text(uri), Authority.Full, (uint)groups);
        row.Title = s.Text(title);
        TestScope.CommitAndPublish(s);
        return Entities.Current.Albums.Slot(uri.AsSpan());
    }

    [Fact]
    public void None_and_an_unallocated_slot_are_the_placeholder()
    {
        TestScope.Fresh();
        var albums = Entities.Current.Albums;
        Assert.Equal(DiscoCellStamp.Placeholder, DiscoCellRules.StampOf(albums, Table.None));
        Assert.Equal(DiscoCellStamp.Placeholder, DiscoCellRules.StampOf(albums, albums.Count + 10));
        Assert.False(DiscoCellStamp.Placeholder.Ready);
    }

    [Fact]
    public void A_row_without_its_card_group_shimmers_and_a_further_thin_write_leaves_the_stamp_equal()
    {
        TestScope.Fresh();
        int slot = Stage("spotify:album:stamp-thin", AlbumFields.Identity, "Half a card");
        var albums = Entities.Current.Albums;
        var thin = DiscoCellRules.StampOf(albums, slot);
        Assert.Equal(new DiscoCellStamp(slot, 0, false), thin);   // names the row, carries no version, not ready

        // The row is written again (its own version climbs) but still lacks DiscoCard: the cell's value does not move.
        uint before = albums.Version[slot];
        Stage("spotify:album:stamp-thin", AlbumFields.Identity, "Still half");
        Assert.True(albums.Version[slot] >= before);
        Assert.Equal(thin, DiscoCellRules.StampOf(albums, slot));
    }

    [Fact]
    public void The_card_group_landing_moves_the_stamp_and_so_does_a_later_write_to_a_ready_row()
    {
        TestScope.Fresh();
        int slot = Stage("spotify:album:stamp-card", AlbumFields.DiscoCard, "Whole card");
        var albums = Entities.Current.Albums;
        var ready = DiscoCellRules.StampOf(albums, slot);
        Assert.True(ready.Ready);
        Assert.Equal(slot, ready.Slot);
        Assert.Equal(albums.Version[slot], ready.Version);
        Assert.NotEqual(new DiscoCellStamp(slot, 0, false), ready);

        // Another group lands on the SAME row: the version climbs, so the card re-reads its title/meta/cover.
        var s = Staging.Rent();
        ref var row = ref s.Albums.RowFor(s.Text("spotify:album:stamp-card"), Authority.Full, (uint)AlbumFields.Publishing);
        row.Label = s.Text("A label");
        TestScope.CommitAndPublish(s);
        var later = DiscoCellRules.StampOf(albums, slot);
        Assert.True(later.Ready);
        Assert.NotEqual(ready, later);
        Assert.True(later.Version > ready.Version);
    }

    [Fact]
    public void A_write_to_another_row_leaves_this_rows_stamp_equal()
    {
        TestScope.Fresh();
        int mine = Stage("spotify:album:stamp-mine", AlbumFields.DiscoCard, "Mine");
        var albums = Entities.Current.Albums;
        var before = DiscoCellRules.StampOf(albums, mine);
        Stage("spotify:album:stamp-other", AlbumFields.DiscoCard, "Somebody else");
        Assert.Equal(before, DiscoCellRules.StampOf(albums, mine));
    }
}

/// <summary>W2-A5: the grid re-pushes a <see cref="Artist.DiscoCellProps"/> per realized cell on every grid render; its
/// equality is the cell's re-render gate, so it must hold across fresh delegate closures and break on any datum the
/// cell paints from.</summary>
public class DiscoCellPropsTests
{
    static readonly Func<ColorF> s_accentA = static () => default, s_accentB = static () => default;
    static readonly Action<int> s_toggleA = static _ => { }, s_toggleB = static _ => { };

    static Artist.DiscoCellProps Props() => new(new Artist(3), DiscoFacet.Albums, 4, 200f, false, s_accentA, s_toggleA);

    [Fact]
    public void Fresh_delegates_over_the_same_data_are_equal_and_hash_alike()
    {
        var a = Props();
        var b = new Artist.DiscoCellProps(new Artist(3), DiscoFacet.Albums, 4, 200f, false, s_accentB, s_toggleB);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotSame(a.Accent, b.Accent);                    // the delegates really differ — they are just not data
        Assert.NotSame(a.OnToggle, b.OnToggle);
    }

    [Fact]
    public void Every_painted_datum_breaks_the_gate()
    {
        var p = Props();
        Assert.NotEqual(p, p with { Expanded = true });        // the accent border + fill
        Assert.NotEqual(p, p with { CardW = 201f });           // the card height
        Assert.NotEqual(p, p with { Index = 5 });              // a different position → a different row
        Assert.NotEqual(p, p with { Facet = DiscoFacet.Singles });
        Assert.NotEqual(p, p with { A = new Artist(4) });
    }
}
