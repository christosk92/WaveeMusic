// ── Wavee.Tests/ConcertTests.cs — the tile group, the provider's own clock and accent, and the feed subject ───────
//
// Wave 1's gate for `Entities/Concert.cs`. Three facts carry the chapter's non-negotiables: the date's UTC offset is
// stored beside it (ch 17 §8 — the UI prints the provider's local clock), the accent is the provider's and never the
// cover palette's, and one filter tuple is one feed subject, which is what makes "reset pagination on a filter change"
// structural instead of something the page has to remember.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ConcertTests
{
    [Fact]
    public void Tile_is_identity_plus_art_and_detail_builds_on_tile()
    {
        // ch 17 §7's readiness bar: a shelf, a board or the grid appears only when every row it paints knows Tile.
        Assert.Equal(ConcertFields.Identity | ConcertFields.Art, ConcertFields.Tile);
        Assert.True(ConcertFields.Identity.HasFlag(ConcertFields.When));
        Assert.True(ConcertFields.Detail.HasFlag(ConcertFields.Tile));
        Assert.True(ConcertFields.Detail.HasFlag(ConcertFields.Coords));
    }

    [Fact]
    public void The_date_and_its_offset_arrive_as_one_fact()
    {
        // A show at 20:00 in Berlin says 20:00 in Sydney: storing the instant alone and formatting it in the viewer's
        // zone is the bug this pair prevents (ch 17 §8).
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Concerts.Add();
        row.Id = s.Text("spotify:concert:c1");
        row.Title = s.Text("Marconi Union");
        row.Venue = s.Text("Band on the Wall");
        row.City = s.Text("Manchester");
        row.Date = 1_760_000_000_000;
        row.OffsetMinutes = 60;
        row.Known = (uint)ConcertFields.Identity;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var concert = Entities.Concert(EntityUri.Parse("spotify:concert:c1"));
        Assert.True(concert.Knows(ConcertFields.Identity));
        Assert.False(concert.Knows(ConcertFields.Tile));            // no poster answer yet: still a skeleton tile
        Assert.Equal(1_760_000_000_000L, concert.Date);
        Assert.Equal((short)60, concert.OffsetMinutes);
    }

    [Fact]
    public void Known_art_with_no_poster_is_the_designed_fallback_not_a_skeleton()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Concerts.Add();
        row.Id = s.Text("spotify:concert:noart");
        row.Accent = 0xFF7A3B1F;                                    // the PROVIDER's extracted colour, not a grading
        row.Flags = 0;                                              // …and no HasArt bit: there is no poster
        row.Known = (uint)ConcertFields.Art;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var concert = Entities.Concert(EntityUri.Parse("spotify:concert:noart"));
        Assert.True(concert.Knows(ConcertFields.Art));              // answered …
        Assert.False(concert.HasArt);                               // … with "no poster", which paints the tinted pane
        Assert.Equal(0xFF7A3B1Fu, concert.Accent);
        Assert.True(concert.ImageId.IsEmpty);
    }

    [Fact]
    public void The_detail_answer_fills_the_cold_facts_and_the_near_you_bit()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Concerts.Add();
        row.Id = s.Text("spotify:concert:detail");
        row.DoorsOpenAt = 1_759_996_400_000;
        row.DoorsOffsetMinutes = 60;
        row.AgeRestriction = s.Text("14+");
        row.Status = 2;
        row.StatusText = s.Text("On sale");
        row.Region = s.Text("England");
        row.Country = s.Text("GB");
        row.VenuePlace = s.Text("venue:botw");
        row.MetroArea = s.Text("metro:manchester");
        row.Lat = 53.4839f;
        row.Lon = -2.2339f;
        row.Flags = (uint)ConcertFlags.NearUser;
        row.Known = (uint)(ConcertFields.Doors | ConcertFields.Ages | ConcertFields.Status
                         | ConcertFields.Region | ConcertFields.Country | ConcertFields.Coords);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var concert = Entities.Concert(EntityUri.Parse("spotify:concert:detail"));
        Assert.Equal((byte)2, concert.Status);
        Assert.Equal("On sale", Entities.Strings.Resolve(concert.StatusTextId));
        Assert.Equal("14+", Entities.Strings.Resolve(concert.AgeRestrictionId));
        Assert.Equal("metro:manchester", Entities.Strings.Resolve(concert.MetroAreaId));
        Assert.True(MathF.Abs(concert.Lat - 53.4839f) < 0.0001f);
        Assert.True(concert.IsNearUser);
        Assert.True(concert.Knows(ConcertFields.Coords));
    }

    // ── the three side tables (ch 17 DATA GAPS) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void One_filter_tuple_is_one_feed_subject_and_a_different_tuple_is_a_different_one()
    {
        // This is what makes "reset pagination when the radius changes" structural: the new tuple simply has no edges.
        TestScope.Fresh();
        var feeds = Entities.Current.ConcertFeeds;
        var manchester100 = Entities.Strings.Intern("place:manchester|100|0|0|");
        var manchester250 = Entities.Strings.Intern("place:manchester|250|0|0|");

        int a = feeds.Slot(manchester100);
        int again = feeds.Slot(manchester100);
        int wider = feeds.Slot(manchester250);

        Assert.Equal(a, again);
        Assert.NotEqual(a, wider);
        Assert.True(a > 0 && wider > 0);                            // slot 0 stays the permanent "none" row

        feeds.Row[a].PaginationKey = Entities.Strings.Intern("cursor-1");
        feeds.Row[a].Count = 412;
        Assert.Equal(412, feeds.Row[a].Count);
        Assert.True(feeds.Row[wider].PaginationKey.IsEmpty);        // the wider feed starts with no tail
    }

    [Fact]
    public void A_place_remembers_whether_it_was_chosen_or_guessed()
    {
        TestScope.Fresh();
        var places = Entities.Current.Places;
        var id = Entities.Strings.Intern("place:manchester");
        int slot = places.Slot(id);
        places.Row[slot].Id = id;
        places.Row[slot].Name = Entities.Strings.Intern("Manchester");
        places.Row[slot].Flags = (uint)PlaceFlags.Inferred;
        Entities.Current.SavedPlace = slot;

        // ch 17 §7: the Where pill says "set your location" until a place exists, and it says so differently when the
        // one it has was guessed.
        Assert.Equal(slot, Entities.Current.SavedPlace);
        Assert.True((places.Row[slot].Flags & (uint)PlaceFlags.Inferred) != 0);
    }

    [Fact]
    public void Place_concepts_keep_the_providers_weight_order()
    {
        // ch 17 §7: the ORDER is the UI's top-3 rule, so the edge stores the provider's order and the weight rides the
        // concept row rather than being recomputed into a rank.
        TestScope.Fresh();
        var concepts = Entities.Current.Concepts;
        int place = Entities.Current.Places.Slot(Entities.Strings.Intern("place:berlin"));
        int techno = concepts.Slot(Entities.Strings.Intern("concept:techno"));
        int ambient = concepts.Slot(Entities.Strings.Intern("concept:ambient"));
        concepts.Row[techno].Weight = 0.9f;
        concepts.Row[ambient].Weight = 0.4f;

        Entities.Current.Edges.PlaceConcepts.ReplaceRun(place, [techno, ambient], default);

        var order = Entities.Current.Edges.PlaceConcepts.Targets(place);
        Assert.Equal(techno, order[0]);
        Assert.Equal(ambient, order[1]);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.PlaceConcepts.State(place));
    }

    // ── the packed identity, and the text a concert row and its keyed side tables own (defect 1) ─────────────

    /// <summary>A concert uri is <c>spotify:concert:&lt;hex&gt;</c> — never 22 base62 characters — so the row takes the
    /// TEXT form and keeps its interned uri (doc §6, "non-Spotify rows get no memory win"). What it DOES get is the
    /// end of the per-read parse: kind and provider are bytes of the id, and <c>.Uri</c> is a view over it
    /// (defect 3).</summary>
    [Fact]
    public void A_concert_uri_takes_the_text_form_and_its_kind_is_still_a_field_not_a_parse()
    {
        TestScope.Fresh();
        int slot = Entities.Current.Concerts.Slot("spotify:concert:3ab7ff".AsSpan());

        var concert = new Concert(slot);
        Assert.Equal(EntityForm.Text, concert.Id.Form);
        Assert.Equal(EntityKind.Concert, concert.Id.Kind);
        Assert.Equal(EntityProvider.Spotify, concert.Uri.Provider);
        Assert.False(concert.Id.IsPlayable);
        Assert.Equal("spotify:concert:3ab7ff", concert.Uri.Text);
    }

    /// <summary>DEFECT 1 at the widest text row in <c>Entities/</c>: ten columns. The count is the check, and the case
    /// that makes it matter is the feed — the hub replaces a whole filter tuple's worth of shows every time the user
    /// drags the radius, and before the override each pass left ten strings per dropped show behind for good
    /// (doc §4.4).</summary>
    [Fact]
    public void A_freed_concert_row_hands_back_all_ten_of_its_strings_and_its_uri()
    {
        TestScope.Fresh();
        var t = Entities.Current.Concerts;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:concert:teardown20260912".AsSpan());
        t.SetText(ref t.Title, slot, Entities.Strings.Intern("concert teardown title 20260912"));
        t.SetText(ref t.Venue, slot, Entities.Strings.Intern("concert teardown venue 20260912"));
        t.SetText(ref t.City, slot, Entities.Strings.Intern("concert teardown city 20260912"));
        t.SetText(ref t.Image, slot, Entities.Strings.Intern("concert teardown poster 20260912"));
        t.SetText(ref t.AgeRestriction, slot, Entities.Strings.Intern("concert teardown ages 20260912"));
        t.SetText(ref t.StatusText, slot, Entities.Strings.Intern("concert teardown status 20260912"));
        t.SetText(ref t.Region, slot, Entities.Strings.Intern("concert teardown region 20260912"));
        t.SetText(ref t.Country, slot, Entities.Strings.Intern("concert teardown country 20260912"));
        t.SetText(ref t.VenuePlace, slot, Entities.Strings.Intern("concert teardown place 20260912"));
        t.SetText(ref t.MetroArea, slot, Entities.Strings.Intern("concert teardown metro 20260912"));
        Assert.Equal(before + 11, Entities.Strings.MapCount);

        t.FreeSlot(slot);
        Assert.Equal(before, Entities.Strings.MapCount);
        Assert.True(t.MetroArea[slot].IsEmpty);
    }

    /// <summary>A <c>KeyedTable</c>'s MAP owns its key, for the same reason <c>Table.BindId</c> does: <c>UriKeys</c>
    /// hashes the RESOLVED text, so a key whose last other owner released it would resolve to nothing and corrupt every
    /// bucket after it — not just its own. Here the caller that interned the tuple gives its reference back and the
    /// row stays findable; <c>ReleaseKeys</c> is how the table hands the whole set to the interner when its scope is
    /// retired (defect 1).</summary>
    [Fact]
    public void A_keyed_table_owns_its_key_until_it_releases_the_whole_set()
    {
        TestScope.Fresh();
        var places = Entities.Current.Places;
        int before = Entities.Strings.MapCount;

        var key = Entities.Strings.Intern("place:keyed-table-owns-its-key-20260912");
        Entities.Strings.AddRef(key);                                // a writer takes ownership, as the hub page would
        int slot = places.Slot(key);
        Entities.Strings.Release(key);                               // … and later hands ITS reference back

        Assert.Equal(before + 1, Entities.Strings.MapCount);         // the map still owns one
        Assert.True(places.TryGetSlot(key, out int found));
        Assert.Equal(slot, found);

        places.ReleaseKeys();
        Assert.Equal(before, Entities.Strings.MapCount);
        Assert.False(places.TryGetSlot(key, out _));
    }

    // ── Wave 5 (WP-5.N-C): the feed-subject key rule, the concert staging runs and what their commit owns ───────────

    [Fact]
    public void The_default_feed_subject_is_place_and_radius_and_the_tuple_extends_it_order_insensitively()
    {
        // The default key is the sidebar's own `placeId|radius`, so the hub and the sidebar share one feed.
        Assert.Equal("2643743|100", ConcertFeedKey.For("2643743", 100, null, null));
        Assert.Equal("geo:u33dc0|25", ConcertFeedKey.For(ConcertFeedKey.PlaceKey(null, "u33dc0"), 25, null, null));
        Assert.Equal("", ConcertFeedKey.PlaceKey(" ", null));

        var window = new ConcertDateRange(new DateOnly(2030, 7, 17), new DateOnly(2030, 7, 19));
        Assert.Equal("2643743|100|2030-07-17..2030-07-19", ConcertFeedKey.For("2643743", 100, window, null));
        Assert.Equal(
            ConcertFeedKey.For("2643743", 100, null, ["spotify:concept:rock", "spotify:concept:jazz"]),
            ConcertFeedKey.For("2643743", 100, null, ["spotify:concept:jazz", "spotify:concept:rock"]));
        Assert.EndsWith("|spotify:concept:jazz,spotify:concept:rock",
            ConcertFeedKey.For("2643743", 100, null, ["spotify:concept:rock", "spotify:concept:jazz"]));
    }

    [Fact]
    public void A_schedule_run_lands_Complete_even_when_it_is_empty()
    {
        // "This artist has no upcoming shows" is an ANSWER: the page must stop skeletoning, not wait forever.
        TestScope.Fresh();
        var s = Staging.Rent();
        var run = s.ConcertRun(ConcertLink.ArtistConcerts);
        run.End(new StagedId(s.Text("spotify:artist:resting")));
        TestScope.CommitAndPublish(s);

        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:resting"));
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.ArtistConcerts.State(artist.Slot));
        Assert.Equal(0, Entities.Current.Edges.ArtistConcerts.Count(artist.Slot));
    }

    [Fact]
    public void The_near_you_bit_is_the_answers_fact_whatever_authority_holds_the_identity()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var full = ref s.Concerts.RowFor(new StagedId(s.Text("spotify:concert:near")), Authority.Full, (uint)ConcertFields.Tile);
        full.Title = s.Text("Full title");
        full.Date = 1_760_000_000_000;
        full.Flags = (uint)ConcertFlags.NearUser;
        full.FlagsMask = (uint)ConcertFlags.NearMask;
        TestScope.CommitAndPublish(s);

        // a thinner schedule answer asked against ANOTHER place: it may not rewrite the title, but it clears the bit
        s = Staging.Rent();
        ref var thin = ref s.Concerts.RowFor(new StagedId(s.Text("spotify:concert:near")), Authority.Thin, (uint)ConcertFields.Tile);
        thin.Title = s.Text("Thin title");
        thin.Date = 1_760_000_000_000;
        thin.FlagsMask = (uint)ConcertFlags.NearMask;
        TestScope.CommitAndPublish(s);

        var concert = Entities.Concert(EntityUri.Parse("spotify:concert:near"));
        Assert.False(concert.IsNearUser);
        Assert.Equal("Full title", Entities.Strings.Resolve(concert.TitleId));
    }

    [Fact]
    public void A_keyed_concepts_run_creates_its_place_and_keeps_wire_order()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var run = s.ConcertRun(ConcertLink.PlaceConcepts);
        foreach (var (uri, name, weight) in new[] { ("spotify:concept:techno", "techno", 0.9f), ("spotify:concept:ambient", "ambient", 0.4f) })
        {
            ref var e = ref run.Add();
            e.T0 = s.Text(uri);
            e.T1 = s.Text(name);
            e.U0 = BitConverter.SingleToUInt32Bits(weight);
        }
        run.EndKeyed(s.Text("geo:u33dc0"));
        TestScope.CommitAndPublish(s);

        Assert.True(Entities.Current.Places.TryGetSlot(Entities.Strings.Intern("geo:u33dc0"), out int place));
        var concepts = ConcertPlaces.ConceptsOf(place);
        Assert.Equal(new[] { "spotify:concept:techno", "spotify:concept:ambient" }, concepts.Select(c => c.Uri).ToArray());
        Assert.Equal(0.9, concepts[0].Weight, 3);
    }

    [Fact]
    public void An_offer_list_rewrite_hands_back_the_text_the_old_list_owned()
    {
        // Defect 1 for payload edges: the commit AddRefs the new payload FIRST, then releases the list it replaces.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Concerts.RowFor(new StagedId(s.Text("spotify:concert:offers")), Authority.Full, (uint)ConcertFields.Tile);
        row.Date = 1_760_000_000_000;
        var first = s.ConcertRun(ConcertLink.Offers);
        StageOffer(s, ref first, "offer-rewrite-provider-a-20260914", "https://a.example/20260914", "AAA");
        first.End(new StagedId(s.Text("spotify:concert:offers")));
        TestScope.CommitAndPublish(s);
        int held = Entities.Strings.MapCount;

        s = Staging.Rent();
        var second = s.ConcertRun(ConcertLink.Offers);
        StageOffer(s, ref second, "offer-rewrite-provider-b-20260914", "https://b.example/20260914", "BBB");
        second.End(new StagedId(s.Text("spotify:concert:offers")));
        TestScope.CommitAndPublish(s);

        Assert.Equal(held, Entities.Strings.MapCount);               // three strings in, three strings out
        var offer = Assert.Single(Entities.Concert(EntityUri.Parse("spotify:concert:offers")).Offers.ToArray());
        Assert.Equal("offer-rewrite-provider-b-20260914", Entities.Strings.Resolve(offer.Provider));
    }

    static void StageOffer(Staging s, ref ConcertRun run, string provider, string url, string currency)
    {
        ref var o = ref run.Add();
        o.T0 = s.Text(provider);
        o.T1 = s.Text(url);
        o.T2 = s.Text(currency);
        o.B0 = (byte)ConcertOfferAvailability.Available;
    }

    [Fact]
    public void A_first_feed_page_replaces_and_an_append_merges_into_the_held_sections()
    {
        TestScope.Fresh();
        StagePage(append: false, ("spotify:concert:a", 0, "near"), ("spotify:concert:b", 2, "all"));
        StagePage(append: false, ("spotify:concert:c", 2, "all"));                 // a refetch: the old list is gone
        StagePage(append: true, ("spotify:concert:c", 2, "all"), ("spotify:concert:d", 2, "all"), ("spotify:concert:e", 1, "rec"));

        int feed = ConcertPlaces.FeedSlot("merge|100");
        var e = Entities.Current.Edges.FeedSection;
        var uris = e.Targets(feed).ToArray().Select(t => new Concert(t).Uri.Text).ToArray();
        Assert.Equal(new[] { "spotify:concert:c", "spotify:concert:d", "spotify:concert:e" }, uris);
        Assert.Equal(2, ConcertFeedMerge.SectionEnd(e.Payload(feed), 0));
    }

    static void StagePage(bool append, params (string Uri, byte Kind, string Section)[] members)
    {
        var s = Staging.Rent();
        foreach (var m in members)
        {
            ref var row = ref s.Concerts.RowFor(new StagedId(s.Text(m.Uri)), Authority.Thin, (uint)ConcertFields.Tile);
            row.Date = 1_760_000_000_000;
        }
        var run = s.ConcertRun(ConcertLink.FeedSection);
        foreach (var m in members)
        {
            ref var edge = ref run.Add(new StagedId(s.Text(m.Uri)));
            edge.B0 = m.Kind;
            edge.T0 = s.Text(m.Section);
        }
        if (append) run.AppendKeyed(s.Text("merge|100")); else run.EndKeyed(s.Text("merge|100"));
        TestScope.CommitAndPublish(s);
    }
}
