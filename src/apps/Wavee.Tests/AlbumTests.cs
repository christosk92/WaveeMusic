// ── Wavee.Tests/AlbumTests.cs — the album columns, the card group, prerelease-as-a-flag, and the top-track pick ───
//
// Wave 1's gate for `Entities/Album.cs`. The load-bearing facts here are the two the chapters argue hardest for:
// a prerelease is an ALBUM row with a flag and a date (never a second entity kind), and the top-track star is derived
// ONCE at commit rather than scanned per render.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class AlbumTests
{
    // ── the groups ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Card_is_identity_plus_the_release_date()
    {
        // ch 08 GAP 17: name | cover | year | release date + precision | track count | kind. The discography grid gates
        // on the GROUP, so a card can never paint half-filled.
        Assert.Equal(AlbumFields.Identity | AlbumFields.Release, AlbumFields.Card);
        Assert.True(AlbumFields.Card.HasFlag(AlbumFields.Kind));
        Assert.True(AlbumFields.Card.HasFlag(AlbumFields.TrackCount));
    }

    [Fact]
    public void Ep_falls_through_to_the_plain_album_layout_ordinals()
    {
        // ch 05 §8, `DetailPage.ResolveConfig`: the values are 0.2.9's ordinals so a persisted byte survives the
        // rewrite, and EP is deliberately its own value that the layout table maps onto Album.
        Assert.Equal(0, (byte)AlbumKind.Single);
        Assert.Equal(1, (byte)AlbumKind.EP);
        Assert.Equal(2, (byte)AlbumKind.Album);
        Assert.Equal(3, (byte)AlbumKind.Compilation);
    }

    // ── prerelease is a flag and a date, not a kind (§9.6 Q4, ch 05 D3/D9) ──────────────────────────────────────────

    [Fact]
    public void A_prerelease_uri_addresses_an_album_row()
    {
        var uri = EntityUri.Parse("spotify:prerelease:abc123");
        Assert.Equal(EntityKind.Album, uri.Kind);
        Assert.True(EntityUri.IsPrerelease("spotify:prerelease:abc123".AsSpan()));

        TestScope.Fresh();
        var album = Entities.Album(uri);
        Assert.True(album.IsValid);
        Assert.Equal(EntityKind.Album, Entities.Current.Albums.Kind);
    }

    [Fact]
    public void The_prerelease_verdict_and_the_resolved_link_are_separate_groups()
    {
        TestScope.Fresh();

        var s = Staging.Rent();
        ref var row = ref s.Albums.Add();
        row.Id = s.Text("spotify:album:upcoming");
        row.PreReleaseEnd = 1_700_000_000;
        row.Flags = (uint)AlbumFlags.PreRelease;
        row.Known = (uint)AlbumFields.Availability;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var album = Entities.Album(EntityUri.Parse("spotify:album:upcoming"));
        Assert.True(album.IsPreRelease);
        Assert.Equal(1_700_000_000, album.PreReleaseEnd);
        // The kind-138 pairing is its own request and its own bit: the heart falls back to the album uri until it
        // resolves, and it must never block the CTA (ch 05 §7).
        Assert.False(album.Knows(AlbumFields.PreReleaseLink));
        Assert.True(album.PreReleaseUriId.IsEmpty);

        var link = Staging.Rent();
        ref var resolved = ref link.Albums.Add();
        resolved.Id = link.Text("spotify:album:upcoming");
        resolved.PreReleaseUri = link.Text("spotify:prerelease:abc123");
        resolved.Known = (uint)AlbumFields.PreReleaseLink;
        resolved.Authority = Authority.Full;
        TestScope.CommitAndPublish(link);

        Assert.True(album.Knows(AlbumFields.PreReleaseLink));
        Assert.Equal("spotify:prerelease:abc123", Entities.Strings.Resolve(album.PreReleaseUriId));
    }

    // ── the commit ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_release_panel_arrives_whole_and_a_thin_answer_cannot_blank_it()
    {
        // ch 05 §7: "the whole record or nothing — the panel must not grow a Label line later".
        TestScope.Fresh();

        var s = Staging.Rent();
        ref var full = ref s.Albums.Add();
        full.Id = s.Text("spotify:album:panel");
        full.Label = s.Text("Just Music");
        full.Copyright = s.Text("© 2011 Just Music");
        full.Courtesy = s.Text("Courtesy of Just Music");
        full.ShareUrl = s.Text("https://open.spotify.com/album/panel");
        full.DiscCount = 1;
        full.Known = (uint)AlbumFields.Publishing;
        full.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var album = Entities.Album(EntityUri.Parse("spotify:album:panel"));
        Assert.True(album.Knows(AlbumFields.Publishing));
        Assert.Equal("Just Music", Entities.Strings.Resolve(album.LabelId));

        var thin = Staging.Rent();
        ref var stub = ref thin.Albums.Add();
        stub.Id = thin.Text("spotify:album:panel");
        stub.Label = default;                                        // a search hit that knows no label
        stub.Known = (uint)AlbumFields.Publishing;
        stub.Authority = Authority.Thin;
        TestScope.CommitAndPublish(thin);

        Assert.Equal("Just Music", Entities.Strings.Resolve(album.LabelId));   // D16 refused the downgrade
    }

    [Fact]
    public void Identity_and_release_are_two_requests_and_two_authorities()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Albums.Add();
        row.Id = s.Text("spotify:album:two");
        row.Title = s.Text("Ambient 1");
        row.Image = s.Text("spotify:image:ab67616d0000b273bbbbbbbbbbbbbbbbbbbbbbbb");
        row.Year = 1978;
        row.TrackCount = 4;
        row.Kind = (byte)AlbumKind.Album;
        row.ReleaseDateIso = s.Text("1978-09-01");
        row.DatePrecision = 2;
        row.ReleaseAt = 273_456_000;
        row.Known = (uint)(AlbumFields.Identity | AlbumFields.Release);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var album = Entities.Album(EntityUri.Parse("spotify:album:two"));
        Assert.True(album.Knows(AlbumFields.Card));
        Assert.Equal("Ambient 1", album.Title);
        Assert.Equal((ushort)1978, album.Year);
        Assert.Equal(4, album.TrackCount);
        Assert.Equal(AlbumKind.Album, album.Kind);
        Assert.Equal("1978-09-01", Entities.Strings.Resolve(album.ReleaseDateIsoId));
        Assert.Equal((byte)2, album.DatePrecision);
        Assert.Equal(273_456_000, album.ReleaseAt);
    }

    // ── the one derived fact this file owns (ch 01 GAP 4, ch 04 §7, ch 05 §8) ───────────────────────────────────────

    [Fact]
    public void The_top_track_is_the_highest_play_count_and_ties_keep_the_earlier_row()
    {
        TestScope.Fresh();
        var albums = Entities.Current.Albums;
        var tracks = Entities.Current.Tracks;
        int album = albums.Slot("spotify:album:star".AsSpan());

        int a = tracks.Slot("spotify:track:a".AsSpan());
        int b = tracks.Slot("spotify:track:b".AsSpan());
        int c = tracks.Slot("spotify:track:c".AsSpan());
        tracks.PlayCount[a] = 900;
        tracks.PlayCount[b] = 4_200;
        tracks.PlayCount[c] = 4_200;                                 // a tie with b, which came first
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album, [a, b, c], default);

        Album.DeriveTopTrack(album);

        Assert.Equal(b, albums.TopTrackSlot[album]);
        Assert.True(new Album(album).Knows(AlbumFields.TopTrack));
    }

    [Fact]
    public void No_row_with_plays_is_a_known_answer_of_none_not_a_missing_one()
    {
        TestScope.Fresh();
        var albums = Entities.Current.Albums;
        var tracks = Entities.Current.Tracks;
        int album = albums.Slot("spotify:album:nostar".AsSpan());
        int a = tracks.Slot("spotify:track:x".AsSpan());
        int b = tracks.Slot("spotify:track:y".AsSpan());
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album, [a, b], default);

        Album.DeriveTopTrack(album);

        Assert.Equal(Table.None, albums.TopTrackSlot[album]);
        Assert.True(new Album(album).Knows(AlbumFields.TopTrack));   // "nobody has plays" is an answer: no star, no shimmer
    }

    [Fact]
    public void Deriving_the_same_answer_twice_is_not_news()
    {
        // The star must not bump a row's Version on every play-count batch that changes nothing, or every album page
        // re-renders whenever any track in it is enriched.
        TestScope.Fresh();
        var albums = Entities.Current.Albums;
        var tracks = Entities.Current.Tracks;
        int album = albums.Slot("spotify:album:idem".AsSpan());
        int a = tracks.Slot("spotify:track:only".AsSpan());
        tracks.PlayCount[a] = 5;
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album, [a], default);

        Album.DeriveTopTrack(album);
        uint version = albums.Version[album];
        Album.DeriveTopTrack(album);

        Assert.Equal(version, albums.Version[album]);
    }

    // ── the packed identity: prerelease as a FLAG on the id, and the text the row owns ───────────────────────

    /// <summary>A prerelease uri is an ALBUM id with one flag bit set — not a second kind, and not a lost spelling: it
    /// must format back as <c>prerelease:</c>, because <c>spotify:album:&lt;its own gid&gt;</c> would name a record that
    /// does not exist (doc §6). The verdict is readable straight off the id, so a row nothing has committed yet — one
    /// allocated as some other answer's target — still knows what it is.</summary>
    [Fact]
    public void A_prerelease_gid_uri_is_an_album_id_with_a_flag_and_it_round_trips_as_prerelease()
    {
        const string uri = "spotify:prerelease:4uLU6hMCjMI75M1A2tKUQC";
        var id = EntityId.Parse(uri.AsSpan());
        Assert.Equal(EntityKind.Album, id.Kind);
        Assert.Equal(EntityForm.Gid, id.Form);
        Assert.True(id.IsPrerelease);
        Assert.Equal(uri, id.Text);

        TestScope.Fresh();
        var album = Entities.Album(new EntityUri(id));
        Assert.True(album.IsValid);
        Assert.True(album.IsPreRelease);                             // never committed, and still correct
        Assert.Equal(EntityKind.Album, Entities.Current.Albums.Kind);
    }

    /// <summary>Two ids one bit apart are two rows — exactly as the two uri STRINGS were two rows before the change
    /// (doc §6). The countdown surface and the record it becomes are different entities with different fetches, and a
    /// slot-based model must never migrate a row between them.</summary>
    [Fact]
    public void The_prerelease_and_the_album_it_becomes_are_two_rows_one_flag_bit_apart()
    {
        TestScope.Fresh();
        var t = Entities.Current.Albums;
        int pre = t.Slot("spotify:prerelease:4uLU6hMCjMI75M1A2tKUQC".AsSpan());
        int album = t.Slot("spotify:album:4uLU6hMCjMI75M1A2tKUQC".AsSpan());

        Assert.NotEqual(pre, album);
        Assert.Equal(t.Id[pre].Gid, t.Id[album].Gid);                // the same 128 bits …
        Assert.NotEqual(t.Id[pre], t.Id[album]);                     // … and not the same identity
        Assert.True(new Album(pre).IsPreRelease);
        Assert.False(new Album(album).IsPreRelease);
    }

    /// <summary>Ch 05 D3's second half, which nothing implemented before: the verdict comes from the wire's own flag OR
    /// from a <c>spotify:prerelease:</c> uri. Both spellings answer — a 22-base62 id carries the flag on the id itself,
    /// and a fixture-shaped one (the TEXT form, whose id never went through <c>TryParseGid</c>) is read off the staged
    /// bytes the commit already holds.</summary>
    [Fact]
    public void A_prerelease_uri_sets_the_verdict_at_commit_even_when_the_wire_flag_is_absent()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var packed = ref s.Albums.Add();
        packed.Id = s.Text("spotify:prerelease:4uLU6hMCjMI75M1A2tKUQC");
        packed.Title = s.Text("Not out yet");
        packed.Known = (uint)AlbumFields.Identity;                   // NOT Availability: the wire said nothing about it
        packed.Authority = Authority.Full;
        ref var spelled = ref s.Albums.Add();
        spelled.Id = s.Text("spotify:prerelease:abc123");
        spelled.Title = s.Text("Also not out yet");
        spelled.Known = (uint)AlbumFields.Identity;
        spelled.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var albums = Entities.Current.Albums;
        int gidRow = albums.Slot("spotify:prerelease:4uLU6hMCjMI75M1A2tKUQC".AsSpan());
        int textRow = albums.Slot("spotify:prerelease:abc123".AsSpan());

        // The COLUMN carries it, which is what a filter scan over `Flags` reads — not just the handle's fallback.
        Assert.NotEqual(0u, albums.Flags[gidRow] & (uint)AlbumFlags.PreRelease);
        Assert.NotEqual(0u, albums.Flags[textRow] & (uint)AlbumFlags.PreRelease);
        Assert.Equal(EntityForm.Text, albums.Id[textRow].Form);      // an id that is not 22 base62 chars
        Assert.True(new Album(textRow).IsPreRelease);
    }

    /// <summary>The kind-138 pairing names its OWN row, and packs to an album id with the prerelease flag — which is
    /// what a pre-save call and a route want. It stays a text column on purpose (file header): 20 more bytes on every
    /// album row would buy a fact one album in twenty has.</summary>
    [Fact]
    public void The_resolved_pre_save_target_packs_to_an_album_id_with_the_prerelease_flag()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Albums.Add();
        row.Id = s.Text("spotify:album:4uLU6hMCjMI75M1A2tKUQC");
        row.PreReleaseUri = s.Text("spotify:prerelease:4uLU6hMCjMI75M1A2tKUQC");
        row.Known = (uint)AlbumFields.PreReleaseLink;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var album = Entities.Album(EntityUri.Parse("spotify:album:4uLU6hMCjMI75M1A2tKUQC"));
        var target = album.PreReleaseId;
        Assert.Equal(EntityKind.Album, target.Kind);
        Assert.True(target.IsPrerelease);
        Assert.Equal(album.Id.Gid, target.Gid);
        Assert.NotEqual(album.Id, target);
    }

    /// <summary>DEFECT 1, for this kind's columns: eight of them, and the count is the check. Miss one in
    /// <c>ReleaseText</c> and the store's trim reclaims the row and none of its publishing panel (doc §4.4).</summary>
    [Fact]
    public void A_freed_album_row_hands_back_all_eight_of_its_strings_and_its_uri()
    {
        TestScope.Fresh();
        var t = Entities.Current.Albums;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:album:teardown-20260912".AsSpan());            // a TEXT-form row: the uri is owned too
        t.SetText(ref t.Title, slot, Entities.Strings.Intern("album teardown title 20260912"));
        t.SetText(ref t.Image, slot, Entities.Strings.Intern("album teardown cover 20260912"));
        t.SetText(ref t.ReleaseDateIso, slot, Entities.Strings.Intern("album teardown 1978-09-01 20260912"));
        t.SetText(ref t.Label, slot, Entities.Strings.Intern("album teardown label 20260912"));
        t.SetText(ref t.Copyright, slot, Entities.Strings.Intern("album teardown copyright 20260912"));
        t.SetText(ref t.Courtesy, slot, Entities.Strings.Intern("album teardown courtesy 20260912"));
        t.SetText(ref t.ShareUrl, slot, Entities.Strings.Intern("album teardown share 20260912"));
        t.SetText(ref t.PreReleaseUri, slot, Entities.Strings.Intern("album teardown presave 20260912"));
        Assert.Equal(before + 9, Entities.Strings.MapCount);

        t.FreeSlot(slot);
        Assert.Equal(before, Entities.Strings.MapCount);
        Assert.True(t.Label[slot].IsEmpty);
        Assert.True(t.PreReleaseUri[slot].IsEmpty);
    }

    // ── Wave 5 (owner M): the rules read these columns ──────────────────────────────────────────────────────────────

    /// <summary>The release instant the upcoming ladder reads is the ISO column when the wire gave one (0.2.9's input
    /// was that string) and the parsed column otherwise — so a year-only ISO never becomes a January countdown, while a
    /// protobuf answer with no text still counts down.</summary>
    [Fact]
    public void The_upcoming_instant_reads_the_iso_column_first_and_the_parsed_column_without_one()
    {
        TestScope.Fresh();
        const long now = 1_700_000_000;
        var s = Staging.Rent();
        ref var yearOnly = ref s.Albums.RowFor(s.Text("spotify:album:yearonly"), Authority.Full, (uint)AlbumFields.Release);
        yearOnly.ReleaseDateIso = s.Text("2033");
        yearOnly.ReleaseAt = 1_988_150_400;                           // what a decoder that parsed the year would write
        ref var proto = ref s.Albums.RowFor(s.Text("spotify:album:proto"), Authority.Full, (uint)AlbumFields.Release);
        proto.ReleaseAt = 1_800_000_000;                              // no ISO text: the protobuf shape
        TestScope.CommitAndPublish(s);

        Assert.Equal(0, Album.Upcoming.Of(Entities.Album(EntityUri.Parse("spotify:album:yearonly")), now));
        Assert.Equal(1_800_000_000, Album.Upcoming.Of(Entities.Album(EntityUri.Parse("spotify:album:proto")), now));
    }

    /// <summary>The kind-138 gate: a flagged prerelease always asks; an ordinary released album never does.</summary>
    [Fact]
    public void NeedsLink_is_the_flag_or_anything_upcoming()
    {
        TestScope.Fresh();
        const long now = 1_700_000_000;
        var s = Staging.Rent();
        ref var flagged = ref s.Albums.RowFor(s.Text("spotify:album:flagged"), Authority.Full, (uint)AlbumFields.Availability);
        flagged.Flags = (uint)AlbumFlags.PreRelease;
        ref var plain = ref s.Albums.RowFor(s.Text("spotify:album:plain"), Authority.Full, (uint)AlbumFields.Release);
        plain.ReleaseDateIso = s.Text("2001-03-12");
        TestScope.CommitAndPublish(s);

        Assert.True(Album.Upcoming.NeedsLink(Entities.Album(EntityUri.Parse("spotify:album:flagged")), now));
        Assert.False(Album.Upcoming.NeedsLink(Entities.Album(EntityUri.Parse("spotify:album:plain")), now));
    }
}
