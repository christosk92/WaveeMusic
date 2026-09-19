// ── Wavee.Tests/ArtistTests.cs — the overview group, the commit-time derivations, and the sparse side tables ──────
//
// Wave 1's gate for `Entities/Artist.cs`. Three of these facts pin chapter regressions rather than mechanisms: a
// name-only stub must not blank a portrait (ch 07 G9), the chart's readiness is a BIT and not a row count (ch 08
// GAP 18), and a re-answered pick reuses its sparse row instead of growing the slab.

using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ArtistTests
{
    [Fact]
    public void Overview_is_one_answer_and_the_page_renders_it_as_a_unit()
    {
        // ch 08 §7: world rank, monthly listeners and followers "render as a unit or not at all", and the bio, pick,
        // upcoming, latest and tour all ride the same `queryArtistOverview`.
        Assert.True(ArtistFields.Overview.HasFlag(ArtistFields.Identity));
        Assert.True(ArtistFields.Overview.HasFlag(ArtistFields.Stats));
        Assert.True(ArtistFields.Overview.HasFlag(ArtistFields.Bio));
        Assert.True(ArtistFields.Overview.HasFlag(ArtistFields.Tour));
        // The chart is NOT part of it: it is a different transport with a different answer.
        Assert.False(ArtistFields.Overview.HasFlag(ArtistFields.Chart));
    }

    [Fact]
    public void The_overview_commit_fills_the_stats_the_lead_sentence_and_the_tour_banner()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Artists.Add();
        row.Id = s.Text("spotify:artist:marconi");
        row.Name = s.Text("Marconi Union");
        row.Image = s.Text("spotify:image:ab6761610000f178cccccccccccccccccccccccc");
        row.Header = s.Text("spotify:image:ab6761670000dddddddddddddddddddddddddddd");
        row.HeaderAccent = 0xFF204060;
        row.Bio = s.Text("<p>Manchester ambient trio.</p> More prose.");
        row.BioLead = s.Text("Manchester ambient trio.");
        row.Monthly = 1_234_567;
        row.Followers = 89_000;
        row.WorldRank = 4_212;
        row.TourEyebrow = s.Text("ON TOUR");
        row.TourHeadline = s.Text("Marconi Union live");
        row.TourSubline = s.Text("6 shows");
        row.Flags = (uint)(ArtistFlags.Verified | ArtistFlags.TourLive);
        row.Known = (uint)(ArtistFields.Identity | ArtistFields.Header | ArtistFields.Stats
                         | ArtistFields.Bio | ArtistFields.Tour);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:marconi"));
        Assert.Equal("Marconi Union", artist.Name);
        Assert.True(artist.IsVerified);
        Assert.True(artist.IsTourLive);
        Assert.Equal(1_234_567u, artist.MonthlyListeners);
        Assert.Equal(89_000u, artist.Followers);
        Assert.Equal((ushort)4_212, artist.WorldRank);
        // ch 08 GAP 3 / P11: the stripped first sentence is a COLUMN, computed once, not re-derived per render.
        Assert.Equal("Manchester ambient trio.", Entities.Strings.Resolve(artist.BioLeadId));
        Assert.Equal(0xFF204060u, artist.HeaderAccent);
        // The hero prefers the landscape header and falls back to the avatar (ch 08 §7's first row).
        Assert.Equal(artist.HeaderId, artist.HeroImageId);
    }

    [Fact]
    public void A_name_only_stub_does_not_blank_a_portrait_the_overview_already_gave_us()
    {
        // ch 07 G9: a thin credit line satisfies Identity, and 0.2.9's plain Ensure therefore left face piles on
        // initials forever. Filling a hole is allowed; erasing a filled column is not.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var full = ref s.Artists.Add();
        full.Id = s.Text("spotify:artist:portrait");
        full.Name = s.Text("Brian Eno");
        full.Image = s.Text("spotify:image:ab6761610000f178eeeeeeeeeeeeeeeeeeeeeeee");
        full.Known = (uint)ArtistFields.Identity;
        full.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:portrait"));
        var portrait = artist.ImageId;
        Assert.False(portrait.IsEmpty);

        var thin = Staging.Rent();
        ref var stub = ref thin.Artists.Add();
        stub.Id = thin.Text("spotify:artist:portrait");
        stub.Name = thin.Text("Brian Eno");
        stub.Image = default;                                       // a credit line knows the name and nothing else
        stub.Known = (uint)ArtistFields.Identity;
        stub.Authority = Authority.Thin;
        TestScope.CommitAndPublish(thin);

        Assert.Equal(portrait, artist.ImageId);
    }

    [Fact]
    public void The_chart_readiness_is_a_bit_because_a_row_count_cannot_express_it()
    {
        // ch 08 GAP 18: a niche artist's real chart is six rows, so "we asked and this is all there is" and "we never
        // asked" are indistinguishable by counting. 0.2.9 needed a whole `ChartFetchedAt` field to say this.
        TestScope.Fresh();
        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:niche"));
        Assert.False(artist.Knows(ArtistFields.Chart));
        Assert.True(artist.PopularSlots.IsEmpty);

        var s = Staging.Rent();
        ref var row = ref s.Artists.Add();
        row.Id = s.Text("spotify:artist:niche");
        row.Known = (uint)ArtistFields.Chart;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        Assert.True(artist.Knows(ArtistFields.Chart));               // answered — and the chart is legitimately empty
        Assert.True(artist.PopularSlots.IsEmpty);
    }

    [Fact]
    public void A_pick_takes_one_sparse_row_and_a_re_answer_reuses_it()
    {
        TestScope.Fresh();
        var picks = Entities.Current.ArtistPicks;
        int before = picks.Count;

        var s = Staging.Rent();
        ref var row = ref s.Artists.Add();
        row.Id = s.Text("spotify:artist:pick");
        row.PickEyebrow = s.Text("ARTIST PICK");
        row.PickTitle = s.Text("Ambient 1");
        row.PickComment = s.Text("Still the one.");
        row.PickItemUri = s.Text("spotify:album:ambient1");
        row.PickItemKind = (byte)EntityKind.Album;
        row.PickReleaseAt = 273_456_000;
        row.Known = (uint)ArtistFields.Pick;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:pick"));
        Assert.True(artist.HasPick);
        Assert.Equal("Still the one.", Entities.Strings.Resolve(artist.Pick.Comment));
        Assert.Equal(before + 1, picks.Count);

        var again = Staging.Rent();
        ref var second = ref again.Artists.Add();
        second.Id = again.Text("spotify:artist:pick");
        second.PickComment = again.Text("A new note.");
        second.Known = (uint)ArtistFields.Pick;
        second.Authority = Authority.Full;
        TestScope.CommitAndPublish(again);

        Assert.Equal("A new note.", Entities.Strings.Resolve(artist.Pick.Comment));
        Assert.Equal(before + 1, picks.Count);                       // the slab did not grow: a pick is replaced whole
    }

    [Fact]
    public void An_artist_with_no_pick_reads_slot_zero_without_a_branch()
    {
        TestScope.Fresh();
        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:nopick"));
        Assert.False(artist.HasPick);
        Assert.True(artist.Pick.Comment.IsEmpty);                    // slot 0 is permanently blank
        Assert.False(artist.HasPreRelease);
        Assert.True(artist.PreRelease.Name.IsEmpty);
    }

    [Fact]
    public void The_three_discography_facets_are_three_edges_with_three_totals()
    {
        // ch 08 GAP 8: one edge with a `DiscographyEdge(Kind)` payload cannot carry three independently paged states
        // and three server totals, and "See all N" gates on the true total.
        TestScope.Fresh();
        var edges = Entities.Current.Edges;
        var artists = Entities.Current.Artists;
        var albums = Entities.Current.Albums;
        int artist = artists.Slot("spotify:artist:facets".AsSpan());
        int one = albums.Slot("spotify:album:one".AsSpan());
        int two = albums.Slot("spotify:album:two".AsSpan());

        edges.ArtistAlbums.ReplacePage(artist, 0, [one], default, total: 40);
        edges.ArtistSingles.ReplaceRun(artist, [two], default);

        Assert.Equal(EdgeState.Partial, edges.ArtistAlbums.State(artist));
        Assert.Equal(40, edges.ArtistAlbums.Total(artist));
        Assert.Equal(EdgeState.Complete, edges.ArtistSingles.State(artist));
        Assert.Equal(1, edges.ArtistSingles.Total(artist));
        Assert.Equal(EdgeState.Unknown, edges.ArtistCompilations.State(artist));
    }

    // ── the packed identity, and the text an artist row owns — columns AND side slabs (defect 1) ──────────────

    /// <summary>Kind and provider are FIELDS of the id, so <c>.Uri</c> is a view and not a parse — which matters most
    /// here, where a credit line asks every artist on the row for its kind on the way to a click target (defect 3, the
    /// 45-100 ns <c>EntityUri.Of</c> re-parse).</summary>
    [Fact]
    public void The_uri_is_a_view_over_the_id_and_a_catalog_artist_interns_nothing_for_it()
    {
        TestScope.Fresh();
        var t = Entities.Current.Artists;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:artist:4uLU6hMCjMI75M1A2tKUQC".AsSpan());
        Assert.Equal(before, Entities.Strings.MapCount);

        var artist = new Artist(slot);
        Assert.Equal(EntityKind.Artist, artist.Id.Kind);
        Assert.Equal(EntityProvider.Spotify, artist.Uri.Provider);
        Assert.Equal(artist.Id, artist.Uri.Id);
        Assert.Equal("spotify:artist:4uLU6hMCjMI75M1A2tKUQC", artist.Uri.Text);
    }

    /// <summary>DEFECT 1 at its widest point in <c>Entities/</c>: an artist owns eight text columns AND the two sparse
    /// side rows nothing else points at — eight more strings in the pick, four in the upcoming release. A
    /// <c>ReleaseText</c> that stopped at the columns would leave twelve strings per artist behind, and they are the
    /// long ones (file header note 3).</summary>
    [Fact]
    public void A_freed_artist_row_hands_back_its_columns_and_the_two_side_rows_it_owns()
    {
        TestScope.Fresh();
        var t = Entities.Current.Artists;
        var picks = Entities.Current.ArtistPicks;
        var upcoming = Entities.Current.ArtistPreReleases;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:artist:teardown-20260912".AsSpan());           // a TEXT-form row: the uri is owned too
        t.SetText(ref t.Name, slot, Entities.Strings.Intern("artist teardown name 20260912"));
        t.SetText(ref t.Image, slot, Entities.Strings.Intern("artist teardown avatar 20260912"));
        t.SetText(ref t.Header, slot, Entities.Strings.Intern("artist teardown header 20260912"));
        t.SetText(ref t.Bio, slot, Entities.Strings.Intern("artist teardown bio 20260912"));
        t.SetText(ref t.BioLead, slot, Entities.Strings.Intern("artist teardown lead 20260912"));
        t.SetText(ref t.TourEyebrow, slot, Entities.Strings.Intern("artist teardown eyebrow 20260912"));
        t.SetText(ref t.TourHeadline, slot, Entities.Strings.Intern("artist teardown headline 20260912"));
        t.SetText(ref t.TourSubline, slot, Entities.Strings.Intern("artist teardown subline 20260912"));
        Assert.Equal(before + 9, Entities.Strings.MapCount);

        int pick = picks.Alloc();
        t.Pick[slot] = pick;
        ref var p = ref picks.Row[pick];
        Entities.RetainText(ref p.Eyebrow, Entities.Strings.Intern("pick teardown eyebrow 20260912"));
        Entities.RetainText(ref p.Title, Entities.Strings.Intern("pick teardown title 20260912"));
        Entities.RetainText(ref p.Subtitle, Entities.Strings.Intern("pick teardown subtitle 20260912"));
        Entities.RetainText(ref p.Comment, Entities.Strings.Intern("pick teardown comment 20260912"));
        Entities.RetainText(ref p.Cover, Entities.Strings.Intern("pick teardown cover 20260912"));
        Entities.RetainText(ref p.Uri, Entities.Strings.Intern("pick teardown uri 20260912"));
        Entities.RetainText(ref p.ItemUri, Entities.Strings.Intern("pick teardown item 20260912"));
        Entities.RetainText(ref p.Background, Entities.Strings.Intern("pick teardown background 20260912"));

        int pre = upcoming.Alloc();
        t.PreRelease[slot] = pre;
        ref var u = ref upcoming.Row[pre];
        Entities.RetainText(ref u.Uri, Entities.Strings.Intern("upcoming teardown uri 20260912"));
        Entities.RetainText(ref u.Name, Entities.Strings.Intern("upcoming teardown name 20260912"));
        Entities.RetainText(ref u.Cover, Entities.Strings.Intern("upcoming teardown cover 20260912"));
        Entities.RetainText(ref u.Type, Entities.Strings.Intern("upcoming teardown type 20260912"));
        Assert.Equal(before + 21, Entities.Strings.MapCount);

        t.FreeSlot(slot);
        Assert.Equal(before, Entities.Strings.MapCount);
        Assert.Equal(0, t.Pick[slot]);                               // the pointer goes with the text …
        Assert.Equal(0, t.PreRelease[slot]);                         // … so a recycled slot cannot read a dead row
        Assert.True(picks.Row[pick].Comment.IsEmpty);
    }

    /// <summary>A pick REPLACED whole is the overwrite case for a side slab: the existing fact next door proves the
    /// slab does not grow, this one proves the interner does not either. Without the release half, every re-answer of
    /// an artist overview would leave eight more strings behind for good.</summary>
    [Fact]
    public void Re_answering_a_pick_releases_the_strings_the_previous_one_owned()
    {
        TestScope.Fresh();
        const string uri = "spotify:artist:pick-overwrite-20260912";
        int settled = 0;

        for (int i = 0; i <= 4; i++)
        {
            var s = Staging.Rent();
            ref var row = ref s.Artists.Add();
            row.Id = s.Text(uri);
            row.PickEyebrow = s.Text("ARTIST PICK");                 // unchanged every time: a no-op retain
            row.PickComment = s.Text("pick overwrite note " + i + " 20260912");
            row.PickItemUri = s.Text("spotify:album:4uLU6hMCjMI75M1A2tKUQC");
            row.Known = (uint)ArtistFields.Pick;
            row.Authority = Authority.Full;
            TestScope.CommitAndPublish(s);
            if (i == 0) settled = Entities.Strings.MapCount;
        }

        Assert.Equal(settled, Entities.Strings.MapCount);
        var artist = Entities.Artist(EntityUri.Parse(uri));
        Assert.Equal("pick overwrite note 4 20260912", Entities.Strings.Resolve(artist.Pick.Comment));
    }

    // ── Wave 5 (N-B): "none" is an answer, a derived banner survives the overview, payload relations own their text ──

    static Artist ArtistOf(string uri) => Entities.Artist(EntityUri.Parse(uri));

    [Fact]
    public void An_answer_that_names_no_pick_releases_the_previous_pick_row()
    {
        TestScope.Fresh();
        const string uri = "spotify:artist:pick-withdrawn";
        var s = Staging.Rent();
        ref var row = ref s.Artists.Add();
        row.Id = s.Text(uri);
        row.PickTitle = s.Text("pick withdrawn title 20260914");
        row.PickItemUri = s.Text("pick withdrawn item 20260914");
        row.Known = (uint)ArtistFields.Pick;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);
        var artist = ArtistOf(uri);
        Assert.True(artist.HasPick);
        int withPick = Entities.Strings.MapCount;

        var none = Staging.Rent();
        ref var bare = ref none.Artists.Add();
        bare.Id = none.Text(uri);
        bare.Known = (uint)ArtistFields.Pick;                       // the answer spoke for the pick and carried none
        bare.Authority = Authority.Full;
        TestScope.CommitAndPublish(none);

        Assert.False(artist.HasPick);                                // it used to allocate an empty side row, and lie
        Assert.True(artist.Knows(ArtistFields.Pick));
        Assert.True(artist.Pick.Title.IsEmpty);
        Assert.Equal(withPick - 2, Entities.Strings.MapCount);       // the title and the item uri handed back
    }

    [Fact]
    public void An_answer_with_nothing_upcoming_clears_the_side_row_and_the_bit()
    {
        TestScope.Fresh();
        const string uri = "spotify:artist:upcoming-gone";
        var s = Staging.Rent();
        ref var row = ref s.Artists.Add();
        row.Id = s.Text(uri);
        row.UpcomingUri = s.Text("spotify:album:upcoming-gone");
        row.UpcomingName = s.Text("Soon");
        row.UpcomingReleaseAt = 1_900_000_000;
        row.Flags = (uint)ArtistFlags.Upcoming;
        row.Known = (uint)ArtistFields.PreRelease;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);
        var artist = ArtistOf(uri);
        Assert.True(artist.HasPreRelease);
        Assert.True(artist.HasUpcoming);

        var none = Staging.Rent();
        ref var bare = ref none.Artists.Add();
        bare.Id = none.Text(uri);
        bare.Known = (uint)ArtistFields.PreRelease;
        bare.Authority = Authority.Full;
        TestScope.CommitAndPublish(none);

        Assert.False(artist.HasPreRelease);
        Assert.False(artist.HasUpcoming);
        Assert.True(artist.PreRelease.Name.IsEmpty);
    }

    [Fact]
    public void An_overview_answer_does_not_blank_the_banner_the_concert_list_derived()
    {
        TestScope.Fresh();
        const string uri = "spotify:artist:banner-kept";
        var s = Staging.Rent();
        ref var derived = ref s.Artists.Add();
        derived.Id = s.Text(uri);
        derived.Name = s.Text("Banner");
        derived.TourEyebrow = s.Text("On tour now");
        derived.TourHeadline = s.Text("Banner — on tour");
        derived.TourSubline = s.Text("Next: Jun 25");
        derived.Flags = (uint)ArtistFlags.TourLive;
        derived.Known = (uint)(ArtistFields.Identity | ArtistFields.Tour);
        derived.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var overview = Staging.Rent();
        ref var row = ref overview.Artists.Add();
        row.Id = overview.Text(uri);
        row.Known = (uint)ArtistFields.Tour;                         // speaks for the group, carries no banner text
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(overview);

        var artist = ArtistOf(uri);
        Assert.Equal("On tour now", Entities.Strings.Resolve(artist.TourEyebrowId));
        Assert.True(artist.IsTourLive);
        Assert.True(artist.Knows(ArtistFields.Tour));
    }

    [Fact]
    public void The_palette_keys_on_the_header_and_falls_back_to_the_avatar()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var avatarOnly = ref s.Artists.Add();
        avatarOnly.Id = s.Text("spotify:artist:palette-avatar");
        avatarOnly.Name = s.Text("Avatar only");
        avatarOnly.Image = s.Text("https://cdn/palette-avatar");
        avatarOnly.Known = (uint)(ArtistFields.Identity | ArtistFields.Header);
        avatarOnly.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var more = Staging.Rent();
        ref var wide = ref more.Artists.Add();
        wide.Id = more.Text("spotify:artist:palette-header");
        wide.Name = more.Text("With header");
        wide.Image = more.Text("https://cdn/palette-avatar-2");
        wide.Header = more.Text("https://cdn/palette-header");
        wide.Known = (uint)(ArtistFields.Identity | ArtistFields.Header);
        wide.Authority = Authority.Full;
        TestScope.CommitAndPublish(more);

        var a = ArtistOf("spotify:artist:palette-avatar");
        var b = ArtistOf("spotify:artist:palette-header");
        Assert.Equal("https://cdn/palette-avatar", Entities.Strings.Resolve(a.PaletteImageId));
        Assert.Equal("https://cdn/palette-header", Entities.Strings.Resolve(b.PaletteImageId));
    }

    [Fact]
    public void Payload_runs_of_one_answer_concatenate_and_an_empty_one_lands_complete()
    {
        // The overview carries THREE playlist lists (profile, featuring, discovered on): a later run must never erase an
        // earlier one, and a playlist on two lists lands once, with the first list's subtitle.
        TestScope.Fresh();
        const string uri = "spotify:artist:payload";
        var s = Staging.Rent();
        var artist = new StagedId(s.Text(uri));

        var profile = s.RunArtistExtra(ArtistExtraKind.Playlists);
        ref var p1 = ref profile.Add();
        p1.Target = s.Text("spotify:playlist:payload-1");
        p1.T0 = s.Text("Owner One");
        profile.End(in artist);

        var featuring = s.RunArtistExtra(ArtistExtraKind.Playlists);
        ref var p2 = ref featuring.Add();
        p2.Target = s.Text("spotify:playlist:payload-2");
        p2.T0 = s.Text("Owner Two");
        ref var again = ref featuring.Add();
        again.Target = s.Text("spotify:playlist:payload-1");
        again.T0 = s.Text("Owner Again");
        featuring.End(in artist);

        s.RunArtistExtra(ArtistExtraKind.Gallery).End(in artist);   // answered: no gallery
        TestScope.CommitAndPublish(s);

        var a = ArtistOf(uri);
        var e = Entities.Current.Edges;
        Assert.Equal(2, a.PlaylistSlots.Length);
        Assert.Equal("Owner One", Entities.Strings.Resolve(a.PlaylistSubtitleIds[0]));
        Assert.Equal("Owner Two", Entities.Strings.Resolve(a.PlaylistSubtitleIds[1]));
        Assert.Equal(EdgeState.Complete, e.ArtistGallery.State(a.Slot));
        Assert.True(a.GallerySlots.IsEmpty);
        Assert.False(ArtistReadiness.ShelfPresent(ArtistReadiness.Shelf(e.ArtistGallery, a.Slot), a.GallerySlots.Length));
        Assert.Equal(EdgeState.Unknown, e.ArtistLinks.State(a.Slot));   // never answered is not "none"
    }

    [Fact]
    public void Re_answering_the_payload_relations_hands_back_the_text_they_replaced()
    {
        TestScope.Fresh();
        const string uri = "spotify:artist:payload-text";
        int settled = 0;
        for (int i = 0; i <= 3; i++)
        {
            var s = Staging.Rent();
            var artist = new StagedId(s.Text(uri));
            var cities = s.RunArtistExtra(ArtistExtraKind.Cities);
            ref var c = ref cities.Add();
            c.T0 = s.Text("payload city " + i + " 20260914");
            c.T1 = s.Text("GR");
            c.U0 = (uint)(1000 + i);
            cities.End(in artist);

            var links = s.RunArtistExtra(ArtistExtraKind.Links);
            ref var l = ref links.Add();
            l.T0 = s.Text("Instagram");
            l.T1 = s.Text("https://instagram.com/payload-" + i);
            l.B0 = (byte)ArtistCatalog.LinkKind.Instagram;
            links.End(in artist);

            var merch = s.RunArtistExtra(ArtistExtraKind.Merch);
            ref var m = ref merch.Add();
            m.T0 = s.Text("payload tee " + i + " 20260914");
            m.T1 = s.Text("$30");
            merch.End(in artist);

            var videos = s.RunArtistExtra(ArtistExtraKind.Videos);
            ref var v = ref videos.Add();
            v.Target = s.Text("spotify:track:payload-video");
            v.T0 = s.Text("https://cdn/payload-thumb-" + i);
            v.I0 = 200_000;
            videos.End(in artist);

            TestScope.CommitAndPublish(s);
            if (i == 0) settled = Entities.Strings.MapCount;
        }

        Assert.Equal(settled, Entities.Strings.MapCount);
        var a = ArtistOf(uri);
        Assert.Equal("payload city 3 20260914", Entities.Strings.Resolve(a.TopCities[0].City));
        Assert.Equal(1003u, a.TopCities[0].Listeners);
        Assert.Equal("https://instagram.com/payload-3", Entities.Strings.Resolve(a.Links[0].Url));
        Assert.Equal("payload tee 3 20260914", Entities.Strings.Resolve(Album.MerchAt(Assert.Single(a.MerchSlots.ToArray())).Name));
        Assert.Equal("https://cdn/payload-thumb-3", Entities.Strings.Resolve(a.VideoPayload[0].Thumb));
    }

    [Fact]
    public void The_tour_banner_is_derived_from_the_concert_list_against_the_clock_it_is_given()
    {
        TestScope.Fresh();
        const string uri = "spotify:artist:touring";
        const long now = 1_782_000_000_000L;
        var s = Staging.Rent();
        var artist = new StagedId(s.Text(uri));
        ref var named = ref s.Artists.RowFor(artist, Authority.Full, (uint)ArtistFields.Identity);
        named.Name = s.Text("Touring");
        var run = s.ConcertRun(ConcertLink.ArtistConcerts);
        for (int i = 0; i < 4; i++)
        {
            var id = new StagedId(s.Text("spotify:concert:touring" + i));
            ref var c = ref s.Concerts.RowFor(id, Authority.Full, (uint)ConcertFields.Identity);
            c.Title = s.Text("Show " + i);
            c.Venue = s.Text("Venue " + i);
            c.City = s.Text("City " + i);
            c.Date = now + (4 - i) * 86_400_000L;                    // the LAST one staged is the nearest: one day out
            run.Add(in id);
        }
        run.End(in artist);
        TestScope.CommitAndPublish(s);

        var a = ArtistOf(uri);
        Artist.DeriveTour(a.Slot, now);
        Assert.Equal(Loc.Get(ArtistTour.EyebrowKey(TourArm.OnTourNow)), Entities.Strings.Resolve(a.TourEyebrowId));
        Assert.True(a.IsTourLive);
        Assert.True(a.Knows(ArtistFields.Tour));
        Assert.False(a.TourSublineId.IsEmpty);

        Artist.DeriveTour(a.Slot, now - 30L * 86_400_000L);          // a month earlier: the same four dates are a tour ahead
        Assert.Equal(Loc.Get(ArtistTour.EyebrowKey(TourArm.UpcomingTour)), Entities.Strings.Resolve(a.TourEyebrowId));
        Assert.False(a.IsTourLive);

        var quiet = Staging.Rent();
        quiet.ConcertRun(ConcertLink.ArtistConcerts).End(new StagedId(quiet.Text(uri)));
        TestScope.CommitAndPublish(quiet);                           // the concert commit re-derives: no dates, no banner
        Assert.True(a.TourEyebrowId.IsEmpty);
        Assert.False(a.IsTourLive);
    }
}

public class ArtistTextTests
{
    [Theory]
    [InlineData("<p>Maroon 5 is a band from Los Angeles. They formed in 1994.</p>", "Maroon 5 is a band from Los Angeles.")]
    [InlineData("Mr. Brightside is a song. It charted.", "Mr. Brightside is a song. It charted.")]   // first ". " at 2: no cut
    [InlineData("A short bio.", "A short bio.")]
    [InlineData("<b></b>", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void FirstSentence_is_the_verbatim_port(string? html, string expected)
        => Assert.Equal(expected, ArtistText.FirstSentence(html));

    [Fact]
    public void StripHtml_drops_tags_and_line_breaks_then_trims()
        => Assert.Equal("Line oneLine two", ArtistText.StripHtml("  <p>Line one</p>\r\n<p>Line two</p>  "));

    [Theory]
    [InlineData("<p>Maroon 5 is a band from Los Angeles. They formed in 1994.</p>")]
    [InlineData("Mr. Brightside is a song. It charted.")]
    [InlineData("A short bio.")]
    [InlineData("Björk Guðmundsdóttir is an Icelandic singer. She began young.")]
    public void Lead_over_utf8_cuts_where_the_string_rule_does(string html)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(html);
        var into = new byte[utf8.Length];
        int n = ArtistText.Lead(utf8, into);
        Assert.Equal(ArtistText.FirstSentence(html), System.Text.Encoding.UTF8.GetString(into, 0, n));
    }

    /// <summary>0.3 gap A3: the wire spells quotes as <c>&amp;#34;</c> and apostrophes as <c>&amp;#8217;</c>; the string
    /// rule and its UTF-8 twin decode the same set (one <c>HtmlEntities</c> table), and the cut lands on the decoded text.</summary>
    [Fact]
    public void StripHtml_and_Lead_decode_the_same_entities()
    {
        const string html = "<p>Nirvana &#34;Nevermind&#34; wasn&#8217;t small. It sold.</p>";
        const string first = "Nirvana \"Nevermind\" wasn’t small.";

        Assert.Equal("Nirvana \"Nevermind\" wasn’t small. It sold.", ArtistText.StripHtml(html));
        Assert.Equal(first, ArtistText.FirstSentence(html));                       // ". " at character 32 > MinSentenceIndex

        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(html);
        var into = new byte[utf8.Length];                                           // sized to the html: decoding never grows it
        int n = ArtistText.Lead(utf8, into);
        Assert.Equal(first, System.Text.Encoding.UTF8.GetString(into, 0, n));
    }

    [Fact]
    public void Lead_decodes_the_entities_a_biography_carries()
    {
        byte[] utf8 = "Tom &amp; Jerry &quot;live&quot; &#39;23 &lt;3"u8.ToArray();
        var into = new byte[utf8.Length];
        int n = ArtistText.Lead(utf8, into);
        Assert.Equal("Tom & Jerry \"live\" '23 <3", System.Text.Encoding.UTF8.GetString(into, 0, n));
    }
}

public class ArtistTourTests
{
    const long Day = 86_400_000L;
    const long Now = 1_782_000_000_000L;

    [Theory]
    [InlineData(0, 1, TourArm.None)]
    [InlineData(1, 1, TourArm.UpcomingShow)]
    [InlineData(1, 60, TourArm.UpcomingShow)]
    [InlineData(2, 60, TourArm.UpcomingDates)]
    [InlineData(3, 1, TourArm.UpcomingDates)]
    [InlineData(4, 3, TourArm.OnTourNow)]
    [InlineData(4, 7, TourArm.OnTourNow)]
    [InlineData(4, 8, TourArm.UpcomingTour)]
    [InlineData(12, 30, TourArm.UpcomingTour)]
    public void The_ladder_is_FakeData_TourBannerFor(int count, int nextInDays, TourArm expected)
        => Assert.Equal(expected, ArtistTour.ArmFor(count, Now + nextInDays * Day, Now));

    [Fact]
    public void Soon_is_within_seven_days_and_not_already_past()
    {
        Assert.True(ArtistTour.IsSoon(Now, Now));
        Assert.True(ArtistTour.IsSoon(Now + 7 * Day, Now));
        Assert.False(ArtistTour.IsSoon(Now + 7 * Day + 1, Now));
        Assert.False(ArtistTour.IsSoon(Now - 1, Now));
    }

    [Fact]
    public void The_next_date_is_the_earliest_and_the_first_on_a_tie()
    {
        Assert.Equal(-1, ArtistTour.NextIndex([]));
        Assert.Equal(2, ArtistTour.NextIndex([Now + 3 * Day, Now + 2 * Day, Now + Day, Now + Day]));
    }

    [Fact]
    public void Every_arm_has_its_own_key_and_none_has_none()
    {
        Assert.Equal("", ArtistTour.EyebrowKey(TourArm.None));
        var keys = new HashSet<string>
        {
            ArtistTour.EyebrowKey(TourArm.UpcomingShow), ArtistTour.EyebrowKey(TourArm.UpcomingDates),
            ArtistTour.EyebrowKey(TourArm.OnTourNow), ArtistTour.EyebrowKey(TourArm.UpcomingTour),
        };
        Assert.Equal(4, keys.Count);
    }

    [Fact]
    public void The_date_label_reads_the_providers_local_clock()
    {
        // 23:30 UTC on 24 June is already 25 June at +02:00.
        long instant = new DateTimeOffset(2026, 6, 24, 23, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("Jun 25", ArtistTour.DateLabel(instant, 120, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Jun 24", ArtistTour.DateLabel(instant, 0, System.Globalization.CultureInfo.InvariantCulture));
    }
}
