// ── Wavee.Tests/ArtistTests.cs — the overview group, the commit-time derivations, and the sparse side tables ──────
//
// Wave 1's gate for `Entities/Artist.cs`. Three of these facts pin chapter regressions rather than mechanisms: a
// name-only stub must not blank a portrait (ch 07 G9), the chart's readiness is a BIT and not a row count (ch 08
// GAP 18), and a re-answered pick reuses its sparse row instead of growing the slab.

using FluentGpu.Foundation;
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
}
