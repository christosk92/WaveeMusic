// ── Wavee.Tests/ClusterDecodeTests.cs — the Connect half of Spotify.Decode (Wave 2, owner E) ─────────────────────
//
// Wave 2's gate for `Spotify/+Spotify.Decode.Connect.cs`: the cluster fold that Wave 3's `Playback` consumes, and
// the remote-command decode the dealer's REQUEST frames carry. Both are pure values over spans, so both are pinned
// here with no session, no socket and no reducer — which is the point of splitting the decode out of the glue.
//
// The three rules that are easy to lose in a port, each with a fact of its own below:
//   • the volume slider follows the ACTIVE device, and ours is the fallback only when nobody is active;
//   • a restriction is a restriction when its reason list is NON-EMPTY, never when the field is merely present;
//   • `message_id` is uint32 on the wire and routinely exceeds int.MaxValue — reading it narrow threw in 0.2.9 and
//     discarded the whole command through the outer catch.

using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using P = Wavee.Protocol.Player;

namespace Wavee.Tests;

public class ClusterDecodeTests
{
    const string Ours = "wavee-device";
    const string Phone = "phone-device";

    static readonly byte[] OursUtf8 = Encoding.UTF8.GetBytes(Ours);

    static P.Cluster Fixture() => new()
    {
        ActiveDeviceId = Phone,
        ServerTimestampMs = 1_700_000_000_000,
        StartedPlayingAtTimestamp = 1_600_000_000_000,
        PlayerState = new P.PlayerState
        {
            Timestamp = 1_699_999_000_000,
            ContextUri = "spotify:playlist:0dijb70Boi9TIdmiLLq13V",
            PositionAsOfTimestamp = 5_000,
            Duration = 234_959,
            IsPlaying = true,
            QueueRevision = "rev-1",
            Track = new P.ProvidedTrack
            {
                Uri = "spotify:track:7idegBIikag5rTZP4WZihP",
                Uid = "2a826aa43895001e",
                Provider = "context",
                Metadata =
                {
                    { "title", "Cold Brew Chapters" },
                    { "artist_name", "roti." },
                    { "album_title", "Let's work slow and easy" },
                    { "image_url", "https://i.scdn.co/image/small" },
                    { "image_xlarge_url", "https://i.scdn.co/image/xlarge" },
                    { "duration", "234959" },
                },
            },
            Options = new P.ContextPlayerOptions { ShufflingContext = true, RepeatingTrack = true },
            Restrictions = new P.Restrictions { DisallowSkippingPrevReasons = { "no_prev_track" } },
            NextTracks = { new P.ProvidedTrack { Uri = "spotify:track:1111111111111111111111" } },
            PrevTracks =
            {
                new P.ProvidedTrack { Uri = "spotify:track:2222222222222222222222" },
                new P.ProvidedTrack { Uri = "spotify:track:3333333333333333333333" },
            },
        },
        Device =
        {
            { Phone, new P.DeviceInfo { DeviceId = Phone, Name = "Christos's Phone", Volume = 40_000, DeviceType = P.DeviceType.Smartphone } },
            { Ours, new P.DeviceInfo { DeviceId = Ours, Name = "Wavee", Volume = 20_000, DeviceType = P.DeviceType.Computer } },
        },
    };

    // ── the cluster fold ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_cluster_carries_the_remotes_claim_and_both_clocks()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.Equal(Phone, Encoding.UTF8.GetString(buffer.Utf8(delta.ActiveDeviceId)));
        Assert.True(delta.IsPlaying);
        Assert.False(delta.IsPaused);
        Assert.Equal(5_000, delta.PositionAsOfMs);
        Assert.Equal(234_959, delta.DurationMs);
        Assert.Equal(1_699_999_000_000, delta.TimestampMs);
        Assert.Equal(1_700_000_000_000, delta.ServerTimestampMs);
        Assert.Equal(1_600_000_000_000, delta.ActiveStartedPlayingAt);
        Assert.Equal("rev-1", Encoding.UTF8.GetString(buffer.Utf8(delta.QueueRevision)));
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void The_current_track_comes_off_the_metadata_map_with_the_cover_falling_back_by_size()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.True(delta.HasTrack);
        Assert.Equal("Cold Brew Chapters", Encoding.UTF8.GetString(buffer.Utf8(delta.Track.Title)));
        Assert.Equal("roti.", Encoding.UTF8.GetString(buffer.Utf8(delta.Track.ArtistName)));
        Assert.Equal("https://i.scdn.co/image/xlarge", Encoding.UTF8.GetString(buffer.Utf8(delta.Track.Image)));
        Assert.Equal(234_959, delta.Track.DurationMs);
        Assert.Equal("2a826aa43895001e", Encoding.UTF8.GetString(buffer.Utf8(delta.Track.Uid)));
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void The_two_queues_are_their_own_contiguous_runs_and_the_current_track_is_in_neither()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.Equal(1, delta.NextCount);
        Assert.Equal(2, delta.PrevCount);
        Assert.Equal("spotify:track:1111111111111111111111",
            Encoding.UTF8.GetString(buffer.Utf8(buffer.Tracks(delta.NextStart, delta.NextCount)[0].Uri)));
        Assert.Equal("spotify:track:3333333333333333333333",
            Encoding.UTF8.GetString(buffer.Utf8(buffer.Tracks(delta.PrevStart, delta.PrevCount)[1].Uri)));
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void The_slider_follows_the_active_device_and_ours_is_only_the_fallback()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.Equal(2, delta.DeviceCount);
        Assert.Equal(20_000, delta.OurVolume);
        Assert.Equal(40_000, delta.ActiveVolume);

        var devices = buffer.Devices(delta.DeviceStart, delta.DeviceCount);
        bool sawUs = false, sawPhone = false;
        foreach (ref readonly var d in devices)
        {
            if (d.Kind == Spotify.Decode.DeviceKind.ThisDevice) { sawUs = true; Assert.Equal("Wavee", Encoding.UTF8.GetString(buffer.Utf8(d.Name))); }
            if (d.Kind == Spotify.Decode.DeviceKind.Phone) sawPhone = true;
        }
        Assert.True(sawUs);
        Assert.True(sawPhone);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void With_nobody_active_the_slider_falls_back_to_our_own_volume()
    {
        var cluster = Fixture();
        cluster.ActiveDeviceId = "";
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(cluster.ToByteArray(), OursUtf8, buffer);

        Assert.Equal(20_000, delta.ActiveVolume);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void A_restriction_is_a_restriction_only_when_its_reason_list_is_not_empty()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);

        Assert.True(delta.NoPrev);
        Assert.False(delta.NoNext);
        Assert.False(delta.NoSeek);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void Repeating_track_beats_repeating_context()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.Cluster(Fixture().ToByteArray(), OursUtf8, buffer);
        Assert.Equal(Spotify.Decode.RepeatMode.Track, delta.Repeat);
        Assert.True(delta.Shuffling);

        var context = Fixture();
        context.PlayerState.Options = new P.ContextPlayerOptions { RepeatingContext = true };
        var second = Spotify.Decode.Cluster(context.ToByteArray(), OursUtf8, buffer);
        Assert.Equal(Spotify.Decode.RepeatMode.Context, second.Repeat);
        Assert.False(second.Shuffling);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void A_cluster_update_stamps_the_push_origin_and_its_reason()
    {
        var update = new P.ClusterUpdate
        {
            Cluster = Fixture(),
            UpdateReason = P.ClusterUpdateReason.DeviceVolumeChanged,
        }.ToByteArray();

        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var delta = Spotify.Decode.ClusterUpdate(update, OursUtf8, buffer);

        Assert.Equal(Spotify.Decode.ClusterOrigin.Push, delta.Origin);
        Assert.Equal((int)P.ClusterUpdateReason.DeviceVolumeChanged, delta.UpdateReason);
        Assert.True(delta.HasTrack);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void A_warm_cluster_decode_allocates_nothing()
    {
        var bytes = Fixture().ToByteArray();
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        for (int i = 0; i < 2; i++) { Spotify.Decode.Cluster(bytes, OursUtf8, buffer); buffer.Reset(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Spotify.Decode.Cluster(bytes, OursUtf8, buffer);
        long after = GC.GetAllocatedBytesForCurrentThread();
        buffer.Reset();
        Spotify.Decode.ClusterBuffer.Return(buffer);

        Assert.Equal(0L, after - before);
    }

    // ── the remote command ──────────────────────────────────────────────────────────────────────────────────────────

    static Spotify.Decode.RemoteCommand Parse(string json)
        => Spotify.Decode.ConnectCommand(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_seek_survives_a_message_id_wider_than_an_int_and_a_position_spelled_as_a_float()
    {
        // Both shapes threw in 0.2.9 and discarded the whole command through the outer catch.
        var cmd = Parse("""{"message_id":4294967295,"sent_by_device_id":"abc","command":{"endpoint":"seek_to","position":42000.0}}""");

        Assert.Equal(Spotify.Decode.RemoteCmd.SeekTo, cmd.Kind);
        Assert.True(cmd.Ok);
        Assert.Equal(42_000, cmd.SeekToMs);
        Assert.Equal(int.MaxValue, cmd.MessageId);
        Assert.NotEqual(0ul, cmd.SenderHash);
    }

    [Fact]
    public void A_boolean_verb_reads_its_value_and_a_track_verb_reads_its_identity()
    {
        var shuffle = Parse("""{"command":{"endpoint":"set_shuffling_context","value":true}}""");
        Assert.Equal(Spotify.Decode.RemoteCmd.SetShufflingContext, shuffle.Kind);
        Assert.True(shuffle.BoolArg);

        var next = Parse("""{"command":{"endpoint":"skip_next","track":{"uri":"spotify:track:7idegBIikag5rTZP4WZihP","uid":"u"}}}""");
        Assert.Equal(Spotify.Decode.RemoteCmd.SkipNext, next.Kind);
        Assert.Equal(EntityKind.Track, next.Track.Kind);
        Assert.Equal("spotify:track:7idegBIikag5rTZP4WZihP", next.Track.Text);
    }

    [Fact]
    public void An_endpoint_we_do_not_know_is_not_ok_and_carries_no_verb()
    {
        var cmd = Parse("""{"message_id":7,"command":{"endpoint":"frobnicate"}}""");
        Assert.Equal(Spotify.Decode.RemoteCmd.Unknown, cmd.Kind);
        Assert.False(cmd.Ok);
        Assert.Equal(7, cmd.MessageId);
    }

    [Fact]
    public void The_dedupe_key_folds_the_endpoint_so_a_recycled_message_id_is_not_a_replay()
    {
        // Message ids are recycled ACROSS endpoints: without the endpoint term a fresh `set_shuffling_context`
        // landing on an old id looked like an exact replay of an unrelated command and was silently dropped.
        var a = Parse("""{"message_id":9,"sent_by_device_id":"abc","command":{"endpoint":"seek_to","position":1}}""");
        var b = Parse("""{"message_id":9,"sent_by_device_id":"abc","command":{"endpoint":"set_shuffling_context","value":true}}""");
        var c = Parse("""{"message_id":9,"sent_by_device_id":"abc","command":{"endpoint":"seek_to","position":2}}""");

        Assert.NotEqual(a.DedupeKey, b.DedupeKey);
        Assert.Equal(a.DedupeKey, c.DedupeKey);
    }

    // `PutState` was a named stub in Wave 2 and is a real encoder in Wave 3 (owner G, against the field map the stub
    // wrote down). Its facts live with the reducer that feeds it — `PlaybackRulesTests`, which round-trips a snapshot
    // through the generated `PutStateRequest` parser.

    // ── what an inbound play or transfer asks to play (G-071) ───────────────────────────────────────────────────────

    static string Str(Spotify.Decode.ClusterBuffer buffer, TextRef text) => Encoding.UTF8.GetString(buffer.Utf8(text));

    [Fact]
    public void An_inbound_play_carries_its_context_its_start_track_its_position_and_its_origin()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var load = Spotify.Decode.ConnectLoad(Encoding.UTF8.GetBytes("""
            {"message_id":12,"sent_by_device_id":"phone","command":{"endpoint":"play",
              "context":{"uri":"spotify:playlist:0dijb70Boi9TIdmiLLq13V","url":"context://spotify:playlist:0dijb70Boi9TIdmiLLq13V"},
              "play_origin":{"feature_identifier":"playlist","feature_version":"1.2.3","view_uri":"spotify:playlist:0dijb70Boi9TIdmiLLq13V"},
              "prepare_play_options":{"skip_to":{"track_uri":"spotify:track:7idegBIikag5rTZP4WZihP","track_uid":"2a826aa43895001e","track_index":3},
                                      "player_options_override":{"shuffling_context":true,"repeating_context":true}},
              "options":{"seek_to":61000,"initially_paused":true}}}
            """), buffer);

        Assert.Equal(Spotify.Decode.RemoteCmd.Play, load.Kind);
        Assert.Equal("spotify:playlist:0dijb70Boi9TIdmiLLq13V", Str(buffer, load.ContextUri));
        Assert.Equal("context://spotify:playlist:0dijb70Boi9TIdmiLLq13V", Str(buffer, load.ContextUrl));
        Assert.Equal("spotify:track:7idegBIikag5rTZP4WZihP", Str(buffer, load.SkipToUri));
        Assert.Equal("2a826aa43895001e", Str(buffer, load.SkipToUid));
        Assert.Equal(3, load.SkipToIndex);
        Assert.Equal(61_000, load.SeekToMs);
        Assert.True(load.InitiallyPaused);
        // 0.2.9 read `player_options_override` under `options` only and dropped the desktop's `prepare_play_options` one.
        Assert.Equal(1, load.Shuffle);
        Assert.Equal((sbyte)Spotify.Decode.RepeatMode.Context, load.Repeat);
        Assert.Equal("playlist", Str(buffer, load.FeatureIdentifier));
        Assert.Equal(0, load.TrackCount);                                  // no embedded pages: the host resolves the uri
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void A_play_with_embedded_pages_carries_its_rows_and_an_unstated_start_says_so()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var load = Spotify.Decode.ConnectLoad(Encoding.UTF8.GetBytes("""
            {"command":{"endpoint":"play","context":{"uri":"spotify:station:track:7idegBIikag5rTZP4WZihP","pages":[
              {"tracks":[{"uri":"spotify:track:7idegBIikag5rTZP4WZihP","uid":"a","metadata":{"title":"Cold Brew Chapters","duration":"234959"}},
                         {"uid":"no-uri"},
                         {"uri":"spotify:track:1111111111111111111111","uid":"b"}]}]}}}
            """), buffer);

        Assert.Equal(2, load.TrackCount);
        var rows = buffer.Tracks(load.TrackStart, load.TrackCount);
        Assert.Equal("Cold Brew Chapters", Str(buffer, rows[0].Title));
        Assert.Equal(234_959, rows[0].DurationMs);
        Assert.Equal("b", Str(buffer, rows[1].Uid));
        Assert.Equal(-1, load.SkipToIndex);
        Assert.Equal(-1, load.SeekToMs);
        Assert.Equal(-1, load.Shuffle);
        Assert.Equal(-1, load.Repeat);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void An_inbound_transfer_decodes_the_phones_whole_state_out_of_its_base64()
    {
        var state = new Wavee.Protocol.Transfer.TransferState
        {
            Options = new Wavee.Protocol.Transfer.TransferPlayerOptions { ShufflingContext = true, RepeatingTrack = true },
            Playback = new Wavee.Protocol.Transfer.TransferPlayback
            {
                Timestamp = 1_700_000_000_000,
                PositionAsOfTimestamp = 42_000,
                Speed = 1.0,
                Paused = true,
                CurrentTrack = new Wavee.Protocol.Transfer.TransferContextTrack
                {
                    Gid = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }),
                    Uid = "current-uid",
                    Metadata = { { "title", "the song on the phone" } },
                },
            },
            CurrentSession = new Wavee.Protocol.Transfer.TransferSession
            {
                Context = new Wavee.Protocol.Transfer.TransferContext { Uri = "spotify:album:1I80HwIDdWXtmA3Fqsbqnl", Url = "context://x" },
                CurrentUid = "session-uid",
            },
            Queue = new Wavee.Protocol.Transfer.TransferQueue
            {
                IsPlayingQueue = true,
                Tracks = { new Wavee.Protocol.Transfer.TransferContextTrack { Uri = "spotify:track:1111111111111111111111", Uid = "q1" } },
            },
        };
        // A JSON encoder may escape '/' as "\/": the decode must read the string it MEANS.
        string json = "{\"command\":{\"endpoint\":\"transfer\",\"data\":\"" + Convert.ToBase64String(state.ToByteArray()).Replace("/", "\\/")
                    + "\",\"options\":{\"restore_paused\":\"kill\",\"restore_position\":\"extrapolate\","
                    + "\"restore_track\":\"always_play_something\",\"retain_session\":\"do_not_retain\"}}}";

        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var load = Spotify.Decode.ConnectLoad(Encoding.UTF8.GetBytes(json), buffer);

        Assert.Equal(Spotify.Decode.RemoteCmd.Transfer, load.Kind);
        Assert.True(load.HasPlayback);
        Assert.Equal(1_700_000_000_000, load.TimestampMs);
        Assert.Equal(42_000, load.PositionAsOfMs);
        Assert.True(load.Paused);
        Assert.Equal(1, load.Shuffle);
        Assert.Equal((sbyte)Spotify.Decode.RepeatMode.Track, load.Repeat);
        Assert.True(load.HasCurrent);
        // A gid with no uri is spelled back as a track uri (0.2.9's rule), with no interner.
        Assert.Equal(EntityId.ForGid(EntityKind.Track, state.Playback.CurrentTrack.Gid.ToByteArray()).Text,
                     Str(buffer, load.Current.Uri));
        Assert.Equal("the song on the phone", Str(buffer, load.Current.Title));
        Assert.Equal("spotify:album:1I80HwIDdWXtmA3Fqsbqnl", Str(buffer, load.ContextUri));
        Assert.Equal("session-uid", Str(buffer, load.CurrentUid));
        Assert.True(load.IsPlayingQueue);
        Assert.Equal(1, load.TrackCount);
        Assert.Equal("q1", Str(buffer, buffer.Tracks(load.TrackStart, 1)[0].Uid));
        Assert.True(load.ForcePlay && load.Extrapolate && load.AlwaysPlaySomething && load.NewSession);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    [Fact]
    public void A_verb_that_is_not_a_load_stages_nothing()
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var load = Spotify.Decode.ConnectLoad(
            """{"command":{"endpoint":"pause","context":{"pages":[{"tracks":[{"uri":"spotify:track:x"}]}]}}}"""u8, buffer);
        Assert.Equal(Spotify.Decode.RemoteCmd.Unknown, load.Kind);
        Assert.Equal(0, buffer.TrackCount);
        Spotify.Decode.ClusterBuffer.Return(buffer);
    }

    // ── context-resolve and autoplay (G-045, G-070) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_resolved_context_lands_its_rows_in_order_with_the_first_next_page_and_its_sort()
    {
        var json = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotify", "context-resolve.json"));
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        var page = Spotify.Decode.ContextResolve(json, buffer);

        Assert.Equal("spotify:user:x:collection", Str(buffer, page.Uri));
        Assert.Equal("hm://context/page/2", Str(buffer, page.NextPageUrl));      // the FIRST non-empty one wins
        Assert.Equal("added_at DESC", Str(buffer, page.SortingCriteria));
        Assert.False(page.Infinite);
        Assert.Equal(3, page.TrackCount);                                        // the uri-less row is dropped
        var rows = buffer.Tracks(page.TrackStart, page.TrackCount);
        Assert.Equal("uidA", Str(buffer, rows[0].Uid));
        Assert.Equal("context", Str(buffer, rows[0].Provider));
        Assert.Equal("Cold Brew Chapters", Str(buffer, rows[0].Title));
        Assert.Equal("https://i.scdn.co/image/large", Str(buffer, rows[0].Image)); // large beats plain
        Assert.Equal(234_959, rows[0].DurationMs);
        Assert.Equal("autoplay", Str(buffer, rows[1].Provider));
        Assert.Equal("uidC", Str(buffer, rows[2].Uid));                          // page two, in order
        Spotify.Decode.ClusterBuffer.Return(buffer);

        var station = Spotify.Decode.ClusterBuffer.Rent();
        Assert.True(Spotify.Decode.ContextResolve("""{"uri":"spotify:station:track:7idegBIikag5rTZP4WZihP","tracks":[]}"""u8, station).Infinite);
        Spotify.Decode.ClusterBuffer.Return(station);
    }

    [Fact]
    public void The_autoplay_request_is_the_context_and_the_recent_tracks_as_full_uris()
    {
        var recent = new[]
        {
            EntityId.ForGid(EntityKind.Track, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 1 }),
            EntityId.ForGid(EntityKind.Track, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 2 }),
        };
        var body = new byte[256];
        int n = Spotify.Decode.AutoplayRequest("spotify:playlist:0dijb70Boi9TIdmiLLq13V"u8, recent, body);

        var parsed = Wavee.Protocol.Playback.AutoplayContextRequest.Parser.ParseFrom(body.AsSpan(0, n).ToArray());
        Assert.Equal("spotify:playlist:0dijb70Boi9TIdmiLLq13V", parsed.ContextUri);
        Assert.Equal(new[] { recent[0].Text, recent[1].Text }, parsed.RecentTrackUri.ToArray());
        Assert.False(parsed.IsVideo);
    }

    // ── the radio seed (G-251): inspiredby-mix's seed_to_playlist answer ─────────────────────────────────────────────

    [Fact]
    public void A_radio_seed_answer_names_its_first_playlist()
    {
        Assert.Equal("spotify:playlist:37i9dQZF1E8OX5RkYXtx51",
            Spotify.Decode.RadioPlaylistUri("""{"total":1,"mediaItems":[{"uri":"spotify:playlist:37i9dQZF1E8OX5RkYXtx51"}]}"""u8));
        // Extra fields before and inside the items are skipped; the FIRST playlist wins.
        Assert.Equal("spotify:playlist:first",
            Spotify.Decode.RadioPlaylistUri("""{"seed":{"uri":"spotify:track:x"},"total":2,"mediaItems":[{"name":"Radio","uri":"spotify:playlist:first","x":[1,2]},{"uri":"spotify:playlist:second"}]}"""u8));
    }

    [Fact]
    public void A_radio_seed_with_no_playlist_or_a_body_that_is_not_json_answers_null()
    {
        Assert.Null(Spotify.Decode.RadioPlaylistUri("""{"total":0,"mediaItems":[]}"""u8));
        Assert.Null(Spotify.Decode.RadioPlaylistUri("""{"total":1}"""u8));
        Assert.Null(Spotify.Decode.RadioPlaylistUri("""{"total":1,"mediaItems":[{"uri":"spotify:station:track:x"}]}"""u8));   // not a playlist
        Assert.Null(Spotify.Decode.RadioPlaylistUri("""[{"uri":"spotify:playlist:x"}]"""u8));                                  // not an object
        Assert.Null(Spotify.Decode.RadioPlaylistUri("<html>not found</html>"u8));
        Assert.Null(Spotify.Decode.RadioPlaylistUri("""{"total":1,"mediaItems":[{"uri":"spotify:playlist:"""u8));            // cut short
        Assert.Null(Spotify.Decode.RadioPlaylistUri(default));
    }
}
