// ── Wavee.Tests/TrackTests.cs — the track columns, the flag masks, the group gate and the D16 commit ──────────────
//
// Wave 1's gate for `Entities/Track.cs`. Everything here is a pure function over columns: no engine, no loop, no
// network, no mocks (D17). The four facts worth reading first are the ones that pin a chapter's regression bar rather
// than a mechanism — `Row_is_a_group_gate…`, `A_video_answer_keeps_the_users_own_override`,
// `An_unruled_row_is_playable…` and `Handle_is_exactly_one_int_wide…`.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class TrackTests
{
    // ── the field groups (ch 01 §7, ch 04 §7) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Identity_is_the_six_fields_one_wire_shape_fills()
    {
        const TrackFields expected = TrackFields.Title | TrackFields.Artists | TrackFields.Album
                                   | TrackFields.Duration | TrackFields.Explicit | TrackFields.Image;
        Assert.Equal(expected, TrackFields.Identity);
    }

    [Fact]
    public void Row_is_identity_plus_the_two_lanes_every_list_surface_shows()
    {
        // ch 04 §7: the Plays toggle must NOT change a surface's demand, or it renders a column of dashes.
        Assert.Equal(TrackFields.Identity | TrackFields.PlayCount | TrackFields.Availability, TrackFields.Row);
    }

    [Fact]
    public void The_three_flag_masks_do_not_overlap()
    {
        // A commit writes only its own group's bits; overlapping masks would let a kind-99 answer clear the
        // availability verdict.
        Assert.Equal(TrackFlags.None, TrackFlags.IdentityMask & TrackFlags.AvailabilityMask);
        Assert.Equal(TrackFlags.None, TrackFlags.IdentityMask & TrackFlags.VideoMask);
        Assert.Equal(TrackFlags.None, TrackFlags.AvailabilityMask & TrackFlags.VideoMask);
    }

    [Fact]
    public void The_files_mask_overlaps_nothing_so_a_kind_five_answer_touches_only_the_badge()
    {
        Assert.Equal(TrackFlags.None, TrackFlags.FilesMask & TrackFlags.IdentityMask);
        Assert.Equal(TrackFlags.None, TrackFlags.FilesMask & TrackFlags.AvailabilityMask);
        Assert.Equal(TrackFlags.None, TrackFlags.FilesMask & TrackFlags.VideoMask);
    }

    [Fact]
    public void Files_is_in_All_and_deliberately_not_in_Row()
    {
        // FLAC plan §5.2: the drawer fetches the ladder on expand and a play asks the opener, which fetches it itself.
        // A 300-row playlist demanding it would be 300 audio-uri POSTs for a bit nobody paints in a row.
        Assert.Equal(TrackFields.None, TrackFields.Row & TrackFields.Files);
        Assert.Equal(TrackFields.Files, TrackFields.All & TrackFields.Files);
    }

    // ── the table and D16, with no scope in sight ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_thin_answer_never_overwrites_a_full_identity_but_still_fills_a_cold_hole()
    {
        var t = new TrackTable();
        int slot = t.Alloc(Entities.Strings.Intern("spotify:track:d16"));

        Assert.True(t.Accepts(slot, (uint)TrackFields.Identity, Authority.Full, in t.IdentityAuthority));
        t.Title[slot] = Entities.Strings.Intern("The full answer");
        t.Applied(slot, (uint)TrackFields.Identity, Authority.Full, ref t.IdentityAuthority);

        // A search hit arrives later with a worse title. It must be refused …
        Assert.False(t.Accepts(slot, (uint)TrackFields.Identity, Authority.Thin, in t.IdentityAuthority));
        // … and yet the same thin answer may fill a group nobody has filled: a hole is never a downgrade.
        Assert.True(t.Accepts(slot, (uint)TrackFields.PlayCount, Authority.Thin, in t.ExtrasAuthority));
    }

    [Fact]
    public void Applied_marks_the_group_known_stamps_freshness_and_clears_inflight()
    {
        Entities.Now = 4242;
        var t = new TrackTable();
        int slot = t.Alloc(Entities.Strings.Intern("spotify:track:applied"));
        t.Inflight[slot] = 7;

        t.Applied(slot, (uint)TrackFields.Audio, Authority.Full, ref t.ExtrasAuthority);

        Assert.True(t.Knows(slot, (uint)TrackFields.Audio));
        Assert.Equal(4242, t.FetchedAt[slot]);
        Assert.Equal(0u, t.Inflight[slot]);
    }

    // ── the handle ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_is_exactly_one_int_wide_so_a_span_of_handles_reinterprets_as_slots()
    {
        // `Entities.Ensure(ReadOnlySpan<Track>, …)` forwards through `Entities.Slots`, which is a CAST, not a copy.
        // A second field on the handle would break every typed Ensure silently, so this fact guards the layout.
        Assert.Equal(sizeof(int), System.Runtime.CompilerServices.Unsafe.SizeOf<Track>());

        ReadOnlySpan<Track> handles = [new Track(3), new Track(9), new Track(27)];
        var slots = Entities.Slots(handles);
        Assert.Equal(3, slots.Length);
        Assert.Equal(3, slots[0]);
        Assert.Equal(9, slots[1]);
        Assert.Equal(27, slots[2]);
    }

    [Fact]
    public void An_unruled_row_is_playable_and_a_ruled_one_is_not()
    {
        TestScope.Fresh();
        var t = Entities.Current.Tracks;
        var track = Entities.Track(EntityUri.Parse("spotify:track:unruled"));

        // ch 04 DATA GAPS: 0.2.9's `Availability?` was nullable, and null ≠ Unavailable. Here the verdict is the
        // Known bit and the dimming is the flag — a row nobody has ruled on renders playable.
        Assert.False(track.Knows(TrackFields.Availability));
        Assert.True(track.IsPlayable);

        t.Flags[track.Slot] |= (uint)TrackFlags.Unavailable;
        Assert.False(track.IsPlayable);
    }

    [Theory]
    [InlineData(false, false, 0, false)]
    [InlineData(false, true, 0, false)]                    // a stray flag on an unruled row is not a verdict
    [InlineData(true, false, 0, false)]                    // ruled playable
    [InlineData(true, true, 0, true)]                      // ruled unavailable, nothing to wait for
    [InlineData(true, true, 1_800_000_000, false)]         // ruled unavailable WITH an instant: not-yet-out, never this
    [InlineData(true, true, 1, false)]                     // any instant at all, even a passed one, is the other rule's
    public void Unplayable_is_a_ruled_unavailable_with_no_release_instant(bool known, bool unavailable, int availableAt,
        bool expected)
        => Assert.Equal(expected, Track.Unplayable(known, unavailable, availableAt));

    [Fact]
    public void Unplayable_and_not_yet_out_split_a_ruled_verdict_on_the_release_instant()
    {
        // Two rows the catalog ruled unavailable: one names WHEN (a pending release — dimmed until the date, then healed
        // with no refetch), one names nothing (withdrawn, region-locked, a terminal envelope verdict — dimmed for good).
        TestScope.Fresh();
        const long now = 1_790_000_000;
        var s = Staging.Rent();
        ref var pending = ref s.Tracks.Add();
        pending.Id = s.Text("spotify:track:pending");
        pending.Title = s.Text("Announced");
        pending.Flags = (uint)TrackFlags.Unavailable;
        pending.AvailableAt = (int)(now + 86_400);
        pending.Known = (uint)(TrackFields.Identity | TrackFields.Availability);
        pending.Authority = Authority.Full;
        ref var gone = ref s.Tracks.Add();
        gone.Id = s.Text("spotify:track:gone");
        gone.Flags = (uint)TrackFlags.Unavailable;
        gone.Known = (uint)(TrackFields.Identity | TrackFields.Availability);
        gone.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var announced = Entities.Track(EntityUri.Parse("spotify:track:pending"));
        Assert.True(announced.NotYetOut(now));
        Assert.False(announced.Unplayable());
        Assert.False(announced.NotYetOut(now + 2 * 86_400));  // the release drop heals the pending row

        var withdrawn = Entities.Track(EntityUri.Parse("spotify:track:gone"));
        Assert.True(withdrawn.Unplayable());
        Assert.True(withdrawn.NotYetOut(now));                 // the shared dim/play gate also holds — same greyed row
        Assert.True(withdrawn.NotYetOut(now + 365 * 86_400));  // and no clock ever heals it
        Assert.False(withdrawn.IsPlayable);

        // Unruled stays out of both: the verdict is the Known bit, never the flag alone.
        var unruled = Entities.Track(EntityUri.Parse("spotify:track:never-asked"));
        Assert.False(unruled.Unplayable());
        Assert.False(unruled.NotYetOut(now));
    }

    // ── the commit (C1) ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Identity_commit_fills_the_hot_group_and_binds_the_album_before_anyone_fetched_it()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text("spotify:track:hot");
        row.Title = s.Text("Weightless");
        row.ArtistLine = s.Text("Marconi Union");
        row.Image = s.Text("spotify:image:ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa");
        row.AlbumUri = s.Text("spotify:album:ambient1");
        row.DurationMs = 484_000;
        row.Flags = (uint)TrackFlags.Explicit;
        row.Known = (uint)TrackFields.Identity;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:hot"));
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.Equal("Weightless", track.Title);
        Assert.Equal(484_000, track.DurationMs);
        Assert.True(track.IsExplicit);

        // D10: the album row is allocated by the commit, so the page can bind it and render its skeleton immediately —
        // and `Knows` says, correctly, that nothing about it is filled yet.
        Assert.True(track.Album.IsValid);
        Assert.Equal("spotify:album:ambient1", track.Album.Uri.Text);
        Assert.False(track.Album.Knows(AlbumFields.Identity));
    }

    [Fact]
    public void Row_is_a_group_gate_so_a_list_shimmers_until_plays_and_availability_land()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var identity = ref s.Tracks.Add();
        identity.Id = s.Text("spotify:track:gate");
        identity.Title = s.Text("Gate");
        identity.Known = (uint)TrackFields.Identity;
        identity.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:gate"));
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.False(track.Knows(TrackFields.Row));                 // the row still shimmers

        var late = Staging.Rent();
        ref var enrich = ref late.Tracks.Add();
        enrich.Id = late.Text("spotify:track:gate");
        enrich.PlayCount = 1_850_000_000;
        enrich.Known = (uint)(TrackFields.PlayCount | TrackFields.Availability);
        enrich.Authority = Authority.Thin;                          // kind 185 is a thin enrichment
        TestScope.CommitAndPublish(late);

        Assert.True(track.Knows(TrackFields.Row));
        Assert.Equal(1_850_000_000u, track.PlayCount);
    }

    [Fact]
    public void A_video_answer_keeps_the_users_own_override_and_leaves_the_availability_verdict_alone()
    {
        TestScope.Fresh();
        var t = Entities.Current.Tracks;
        var track = Entities.Track(EntityUri.Parse("spotify:track:override"));

        // The curation store wrote the user's own mp4 at Local authority, and a previous answer ruled the row
        // unavailable.
        t.Flags[track.Slot] |= (uint)(TrackFlags.VideoOverride | TrackFlags.Unavailable);
        t.LocalVideo[track.Slot] = Entities.Strings.Intern(@"D:\clips\mine.mp4");

        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text("spotify:track:override");
        row.VideoUri = s.Text("spotify:track:thevideo");
        row.Flags = 0;                                              // kind 99: "no catalogue video"
        row.Known = (uint)TrackFields.Video;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        Assert.True(track.HasVideo);                                // ch 01 GAP 7: the user's plane still lights the lane
        Assert.Equal(@"D:\clips\mine.mp4", Entities.Strings.Resolve(track.LocalVideoId));
        Assert.False(track.IsPlayable);                             // the video group did not touch the verdict
        Assert.True(track.VideoCounterpart.IsValid);
    }

    [Fact]
    public void The_audio_group_lands_late_without_disturbing_anything_the_row_already_painted()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text("spotify:track:k222");
        row.Title = s.Text("Settled");
        row.Known = (uint)TrackFields.Identity;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:k222"));
        uint before = track.Version;

        var late = Staging.Rent();
        ref var audio = ref late.Tracks.Add();
        audio.Id = late.Text("spotify:track:k222");
        audio.Tempo = 1284;                                         // ×10: 128.4 BPM
        audio.Key = 6;
        audio.Camelot = 8;
        audio.CamelotColor = 0xFF3366CC;
        audio.Known = (uint)TrackFields.Audio;
        audio.Authority = Authority.Full;
        TestScope.CommitAndPublish(late);

        Assert.True(track.Knows(TrackFields.Audio));
        Assert.Equal((ushort)1284, track.Tempo);
        Assert.Equal((byte)8, track.Camelot);
        Assert.Equal(0xFF3366CCu, track.CamelotColor);
        Assert.Equal("Settled", track.Title);                       // identity untouched
        Assert.True(track.Version > before);                        // but the row is news, so a bound list re-runs
    }

    [Fact]
    public void The_lossless_badge_is_one_load_and_one_mask_and_the_ladder_is_the_edge()
    {
        // FLAC plan §5.2: a surface that only wants a BADGE reads two bits off the row and never walks an edge; the
        // ladder itself is `Edges.TrackFormats`, payload-only, in the wire's order.
        TestScope.Fresh();
        var s = Staging.Rent();
        StagedId id = s.Text("spotify:track:flac");
        ref var row = ref s.Tracks.Add();
        row.Id = id;
        row.Title = s.Text("Bit Perfect");
        row.Flags = (uint)(TrackFlags.Lossless | TrackFlags.Lossless24);
        row.Known = (uint)(TrackFields.Identity | TrackFields.Files);
        row.Authority = Authority.Full;

        var ladder = s.Run(Relation.TrackFormats);
        ladder.Add().B0 = 22;
        ladder.Add().U0 = 320;
        ladder.EndEvenIfEmpty(in id);
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:flac"));
        Assert.True(track.Knows(TrackFields.Files));
        Assert.True(track.IsLossless);
        Assert.True(track.IsLossless24);

        // Two rungs, in the order they were staged — and the second one's FormatId is 0 (Ogg Vorbis 96), which is a
        // real rung: the payload is the row, so there is nothing to "identify" and nothing to squeeze out.
        Assert.Equal(2, track.Formats.Length);
        Assert.Equal(new FormatEdge(22, 0), track.Formats[0]);
        Assert.Equal(new FormatEdge(0, 320), track.Formats[1]);
    }

    [Fact]
    public void A_sixteen_bit_only_ladder_lights_the_badge_but_not_the_twenty_four_bit_rung()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text("spotify:track:flac16");
        row.Flags = (uint)TrackFlags.Lossless;
        row.Known = (uint)TrackFields.Files;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:flac16"));
        Assert.True(track.IsLossless);
        Assert.False(track.IsLossless24);
    }

    [Fact]
    public void A_play_count_batch_moves_the_albums_star_once_not_once_per_row()
    {
        // ch 04 §7: the top-track star is DERIVED on the model at commit. 0.2.9 rescanned the list per snapshot; here
        // a 3-row kind-185 answer for one album runs the pick exactly once, after the loop.
        TestScope.Fresh();
        var albums = Entities.Current.Albums;
        var tracks = Entities.Current.Tracks;
        int album = albums.Slot("spotify:album:star185".AsSpan());

        var seed = Staging.Rent();
        for (int i = 0; i < 3; i++)
        {
            ref var row = ref seed.Tracks.Add();
            row.Id = seed.Text("spotify:track:s" + i);
            row.Title = seed.Text("S" + i);
            row.AlbumUri = seed.Text("spotify:album:star185");
            row.Known = (uint)TrackFields.Identity;
            row.Authority = Authority.Full;
        }
        TestScope.CommitAndPublish(seed);

        int winner = tracks.Slot("spotify:track:s1".AsSpan());
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album,
            [tracks.Slot("spotify:track:s0".AsSpan()), winner, tracks.Slot("spotify:track:s2".AsSpan())], default);

        var plays = Staging.Rent();
        for (int i = 0; i < 3; i++)
        {
            ref var row = ref plays.Tracks.Add();
            row.Id = plays.Text("spotify:track:s" + i);
            row.PlayCount = i == 1 ? 9_000u : 100u;
            row.Known = (uint)TrackFields.PlayCount;
            row.Authority = Authority.Thin;
        }
        TestScope.CommitAndPublish(plays);

        Assert.Equal(winner, albums.TopTrackSlot[album]);
        Assert.True(new Album(album).Knows(AlbumFields.TopTrack));
    }

    [Fact]
    public void One_batch_of_many_rows_costs_one_publication()
    {
        // C3/P10: a 300-row answer must be ONE re-render, not 300.
        TestScope.Fresh();
        uint before = Entities.Publication;

        var s = Staging.Rent();
        for (int i = 0; i < 300; i++)
        {
            ref var row = ref s.Tracks.Add();
            row.Id = s.Text("spotify:track:bulk" + i);
            row.Title = s.Text("Bulk " + i);
            row.Known = (uint)TrackFields.Identity;
            row.Authority = Authority.Full;
        }
        uint after = TestScope.CommitAndPublish(s);

        Assert.Equal(before + 1, after);
        Assert.Equal(300, Entities.Current.Tracks.LiveCount);
    }

    // ── identity: the packed id, and text the row OWNS (option 2 + defects 1-3, doc §1.2/§4.4) ─────────────────

    /// <summary>The 158 B/row of uri text a catalog row used to carry, and the 45-100 ns re-parse behind every
    /// <c>.Uri</c> read, in one fact: the row interns NOTHING for its identity, and kind and provider are field loads
    /// off the packed id. The text still exists — it is materialised on demand, at the cold call sites that talk in
    /// uris (a deep link, a log line, copy-link).</summary>
    [Fact]
    public void A_catalog_track_keeps_no_uri_string_and_answers_its_kind_from_a_field()
    {
        TestScope.Fresh();
        var t = Entities.Current.Tracks;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:track:4uLU6hMCjMI75M1A2tKUQC".AsSpan());
        Assert.Equal(before, Entities.Strings.MapCount);             // nothing interned: the gid IS the identity

        var track = new Track(slot);
        Assert.Equal(EntityForm.Gid, track.Id.Form);
        Assert.Equal(EntityKind.Track, track.Id.Kind);
        Assert.Equal(EntityProvider.Spotify, track.Uri.Provider);    // the VIEW reads the same field, it does not parse
        Assert.Equal("spotify:track:4uLU6hMCjMI75M1A2tKUQC", track.Uri.Text);
        Assert.Equal(track.Id, t.Id[slot]);
    }

    /// <summary>DEFECT 1, for this kind's own columns. <c>ReleaseText</c> has to name every
    /// <c>Column&lt;StringId&gt;</c> <c>TrackTable</c> declares: miss one and a trim frees the row's 89 B of columns
    /// while that string stays in the interner for the life of the process (doc §4.4). The count is the check — seven
    /// strings in, seven back.</summary>
    [Fact]
    public void A_freed_track_row_hands_back_all_six_of_its_strings_and_its_uri()
    {
        TestScope.Fresh();
        var t = Entities.Current.Tracks;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("wavee:local:file:track-teardown-20260912".AsSpan());   // a TEXT-form row: the uri is owned too
        t.SetText(ref t.Title, slot, Entities.Strings.Intern("track teardown title 20260912"));
        t.SetText(ref t.Image, slot, Entities.Strings.Intern("track teardown cover 20260912"));
        t.SetText(ref t.ArtistLine, slot, Entities.Strings.Intern("track teardown artists 20260912"));
        t.SetText(ref t.VideoImage, slot, Entities.Strings.Intern("track teardown thumb 20260912"));
        t.SetText(ref t.LocalVideo, slot, Entities.Strings.Intern("track teardown mp4 20260912"));
        t.SetText(ref t.Isrc, slot, Entities.Strings.Intern("TRACKTEARDOWN20260912"));
        Assert.Equal(before + 7, Entities.Strings.MapCount);

        t.FreeSlot(slot);
        Assert.Equal(before, Entities.Strings.MapCount);
        Assert.True(t.Title[slot].IsEmpty);                          // blanked, so a recycled slot reads nothing dead
        Assert.True(t.Isrc[slot].IsEmpty);
    }

    /// <summary>The OVERWRITE half of defect 1, through the real commit — the half that leaks silently, because a row
    /// re-answered by search, then TrackV4, then a playlist item looks perfectly correct afterwards while holding one
    /// title per answer in the interner. <c>SetText</c> releases what it replaces, so six answers cost one title.</summary>
    [Fact]
    public void Re_answering_a_track_costs_one_titles_worth_of_interner_not_one_per_answer()
    {
        TestScope.Fresh();
        const string uri = "wavee:local:file:track-overwrite-20260912";
        int settled = 0;

        for (int i = 0; i <= 5; i++)
        {
            var s = Staging.Rent();
            ref var row = ref s.Tracks.Add();
            row.Id = s.Text(uri);
            row.Title = s.Text("track overwrite take " + i + " 20260912");
            row.Image = s.Text("track overwrite cover 20260912");    // the SAME cover: RetainText is a no-op, not a churn
            row.Known = (uint)TrackFields.Identity;
            row.Authority = Authority.Full;
            TestScope.CommitAndPublish(s);
            if (i == 0) settled = Entities.Strings.MapCount;         // the uri, the first title and the cover
        }

        Assert.Equal(settled, Entities.Strings.MapCount);
        Assert.Equal("track overwrite take 5 20260912", Entities.Track(EntityUri.Parse(uri)).Title);
    }

    // ── ForDisplay: the relink redirection (Track.Rules.cs §5b) ─────────────────────────────────────────────────────

    /// <summary>A row that knows its own title paints itself, whether or not it points at a canonical row: the
    /// versions-panel pointer is not a redirect for a titled row.</summary>
    [Fact]
    public void A_row_that_knows_its_title_displays_itself_even_with_a_canonical_pointer()
    {
        TestScope.Fresh();
        const string self = "wavee:local:file:fordisplay-titled-20260916";
        const string other = "wavee:local:file:fordisplay-titled-canonical-20260916";
        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text(self);
        row.Title = s.Text("titled row 20260916");
        row.CanonicalUri = s.Text(other);
        row.Known = (uint)(TrackFields.Identity | TrackFields.Canonical);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse(self));
        Assert.True(track.Canonical.IsValid);
        Assert.Equal(track.Slot, track.ForDisplay.Slot);
        Assert.Equal("titled row 20260916", track.ForDisplay.Title);
    }

    /// <summary>A row with NO title that points at a valid canonical row paints the canonical row's facts — the shape a
    /// relinked id takes when only its pointer has landed (a persisted column, a partial answer).</summary>
    [Fact]
    public void A_row_without_a_title_displays_its_canonical_row()
    {
        TestScope.Fresh();
        const string alias = "wavee:local:file:fordisplay-alias-20260916";
        const string canonical = "wavee:local:file:fordisplay-canonical-20260916";
        var s = Staging.Rent();

        ref var target = ref s.Tracks.Add();
        target.Id = s.Text(canonical);
        target.Title = s.Text("canonical title 20260916");
        target.DurationMs = 201_000;
        target.Known = (uint)TrackFields.Identity;
        target.Authority = Authority.Full;

        ref var row = ref s.Tracks.Add();              // `Add` may reallocate: `target` is not read after this line
        row.Id = s.Text(alias);
        row.CanonicalUri = s.Text(canonical);
        row.Known = (uint)TrackFields.Canonical;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var asked = Entities.Track(EntityUri.Parse(alias));
        var shown = asked.ForDisplay;
        Assert.False(asked.Knows(TrackFields.Title));
        Assert.Equal(Entities.Track(EntityUri.Parse(canonical)).Slot, shown.Slot);
        Assert.NotEqual(asked.Slot, shown.Slot);
        Assert.Equal("canonical title 20260916", shown.Title);
        Assert.Equal(201_000, shown.DurationMs);
        // Identity stays the asked row's: the redirect is for what is READ, never for who the row is.
        Assert.Equal(alias, asked.Uri.Text);
    }

    /// <summary>A row with neither a title nor a canonical pointer is thin and displays itself — never slot 0.</summary>
    [Fact]
    public void A_row_without_a_title_or_a_canonical_displays_itself()
    {
        TestScope.Fresh();
        var bare = Entities.Track(EntityUri.Parse("wavee:local:file:fordisplay-bare-20260916"));
        Assert.False(bare.Knows(TrackFields.Title));
        Assert.False(bare.Canonical.IsValid);
        Assert.Equal(bare.Slot, bare.ForDisplay.Slot);

        // The permanent "none" row reads the same way: a default handle never redirects anywhere.
        Track none = default;
        Assert.Equal(none.Slot, none.ForDisplay.Slot);
    }
}
