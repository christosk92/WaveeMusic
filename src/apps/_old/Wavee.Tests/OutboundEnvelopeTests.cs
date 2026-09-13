using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Phase D: the outbound player-command envelope matches the decoded desktop captures (golden shape), and honors the
// collection-vs-pages rule. Pure (Backend) → unit-tested directly with fixed ids/time.
public class OutboundEnvelopeTests
{
    [Fact]
    public void Play_Album_MatchesDesktopEnvelopeShape()
    {
        var json = OutboundEnvelope.Play(
            fromDeviceId: "dev1", contextUri: "spotify:album:abc", contextUrl: null,
            skipToIndex: 0, skipToTrackUri: "spotify:track:t", skipToTrackUid: "uid1",
            pageTracks: null, shuffle: false,
            featureIdentifier: "album", featureVersion: "xpui-x",
            commandId: "cmd1", intentId: "intent1", initiatedTimeMs: 123);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("intent1", root.GetProperty("intent_id").GetString());

        var cmd = root.GetProperty("command");
        Assert.Equal("play", cmd.GetProperty("endpoint").GetString());

        var ctx = cmd.GetProperty("context");
        Assert.Equal("spotify:album:abc", ctx.GetProperty("uri").GetString());
        Assert.Equal("spotify:album:abc", ctx.GetProperty("entity_uri").GetString());
        Assert.Equal("context://spotify:album:abc", ctx.GetProperty("url").GetString());
        Assert.False(ctx.TryGetProperty("pages", out _));   // plain album → URI-only

        var po = cmd.GetProperty("play_origin");
        Assert.Equal("album", po.GetProperty("feature_identifier").GetString());
        Assert.Equal("xpui-x", po.GetProperty("feature_version").GetString());

        var skip = cmd.GetProperty("prepare_play_options").GetProperty("skip_to");
        Assert.Equal("spotify:track:t", skip.GetProperty("track_uri").GetString());
        Assert.Equal("uid1", skip.GetProperty("track_uid").GetString());
        Assert.Equal(0, skip.GetProperty("track_index").GetInt32());

        var play = cmd.GetProperty("play_options");
        Assert.Equal("interactive", play.GetProperty("reason").GetString());
        Assert.Equal("replace", play.GetProperty("operation").GetString());
        Assert.Equal("immediately", play.GetProperty("trigger").GetString());

        var log = cmd.GetProperty("logging_params");
        Assert.Equal("cmd1", log.GetProperty("command_id").GetString());
        Assert.Equal("dev1", log.GetProperty("device_identifier").GetString());
        Assert.Equal(123, log.GetProperty("command_initiated_time").GetInt64());
        Assert.Equal(123, log.GetProperty("command_received_time").GetInt64());   // echoed = initiated (field-set parity)
        Assert.Empty(log.GetProperty("interaction_ids").EnumerateArray());        // play mints no interaction id → []
        Assert.Empty(log.GetProperty("page_instance_ids").EnumerateArray());
    }

    [Fact]
    public void Play_Collection_IsUriOnly_EvenWithPageTracks()
    {
        var json = OutboundEnvelope.Play(
            "dev1", "spotify:user:x:collection", "context://spotify:user:x:collection?sort=added", null, null, null,
            new[] { new QueuedRef("spotify:track:a", "u1") }, false, "collection", "v", "c", "i", 1);

        using var doc = JsonDocument.Parse(json);
        var ctx = doc.RootElement.GetProperty("command").GetProperty("context");
        Assert.False(ctx.TryGetProperty("pages", out _));   // collection → URI-only even when pageTracks supplied
        Assert.Equal("context://spotify:user:x:collection?sort=added", ctx.GetProperty("url").GetString());   // sort rides on url
    }

    [Fact]
    public void Play_NonCollection_WithPageTracks_EmbedsPagesWithUids()
    {
        var json = OutboundEnvelope.Play(
            "dev1", "spotify:playlist:p", null, null, null, null,
            new[] { new QueuedRef("spotify:track:a", "u1"), new QueuedRef("spotify:track:b", "u2") },
            false, "playlist", "v", "c", "i", 1);

        using var doc = JsonDocument.Parse(json);
        var tracks = doc.RootElement.GetProperty("command").GetProperty("context").GetProperty("pages")[0].GetProperty("tracks");
        Assert.Equal(2, tracks.GetArrayLength());
        Assert.Equal("spotify:track:a", tracks[0].GetProperty("uri").GetString());
        Assert.Equal("u1", tracks[0].GetProperty("uid").GetString());
    }

    [Fact]
    public void Play_OrderedPages_PreserveSuppliedOrder_AndSkipTo_NoMetadataNoOptions()
    {
        // The embedded page order is VERBATIM (no re-sort), skip_to carries the start row's uri/uid/index, and the
        // envelope stays honest: no per-track metadata + no command.options (no decoded play capture confirms either).
        var json = OutboundEnvelope.Play(
            "us", "spotify:playlist:p", null,
            skipToIndex: 1, skipToTrackUri: "spotify:track:a", skipToTrackUid: "ua",
            pageTracks: new[]
            {
                new QueuedRef("spotify:track:c", "uc"),
                new QueuedRef("spotify:track:a", "ua"),
                new QueuedRef("spotify:track:b", "ub"),
            },
            shuffle: false, featureIdentifier: "playlist", featureVersion: "v",
            commandId: "c", intentId: "i", initiatedTimeMs: 1);

        using var doc = JsonDocument.Parse(json);
        var cmd = doc.RootElement.GetProperty("command");
        var tracks = cmd.GetProperty("context").GetProperty("pages")[0].GetProperty("tracks");
        Assert.Equal(3, tracks.GetArrayLength());
        Assert.Equal("spotify:track:c", tracks[0].GetProperty("uri").GetString());   // verbatim, NOT re-sorted
        Assert.Equal("spotify:track:a", tracks[1].GetProperty("uri").GetString());
        Assert.Equal("spotify:track:b", tracks[2].GetProperty("uri").GetString());
        Assert.Equal("uc", tracks[0].GetProperty("uid").GetString());

        var skip = cmd.GetProperty("prepare_play_options").GetProperty("skip_to");
        Assert.Equal("spotify:track:a", skip.GetProperty("track_uri").GetString());
        Assert.Equal("ua", skip.GetProperty("track_uid").GetString());
        Assert.Equal(1, skip.GetProperty("track_index").GetInt32());

        Assert.False(tracks[0].TryGetProperty("metadata", out _));   // honest-shape guard
        Assert.False(cmd.TryGetProperty("options", out _));          // honest-shape guard
    }

    // ── bug 4: a forwarded play carries the video intent, when known ────────────────────────────────────────────────
    [Fact]
    public void Play_PreferVideo_AddsModesMediaVideoOverride()
    {
        var json = OutboundEnvelope.Play(
            "dev1", "spotify:track:t", null, null, null, null, null, false,
            "harmony", "v", "c", "i", 1, preferVideo: true);

        using var doc = JsonDocument.Parse(json);
        var modes = doc.RootElement.GetProperty("command").GetProperty("prepare_play_options")
            .GetProperty("player_options_override").GetProperty("modes");
        Assert.Equal("VIDEO", modes.GetProperty("media").GetString());
    }

    [Fact]
    public void Play_NoPreferVideo_OmitsModesEntirely()
    {
        var json = OutboundEnvelope.Play(
            "dev1", "spotify:track:t", null, null, null, null, null, false,
            "harmony", "v", "c", "i", 1);

        using var doc = JsonDocument.Parse(json);
        var playerOptions = doc.RootElement.GetProperty("command").GetProperty("prepare_play_options").GetProperty("player_options_override");
        Assert.False(playerOptions.TryGetProperty("modes", out _));
    }

    [Fact]
    public void AddToQueue_SingleTrack_MatchesDesktopEnvelopeShape()
    {
        var json = OutboundEnvelope.AddToQueue(
            fromDeviceId: "us", trackUri: "spotify:track:x", trackUid: "",
            overrideRestrictions: false, onlyForLocalDevice: false, systemInitiated: false,
            commandId: "cmd1", intentId: "intent1", initiatedTimeMs: 7);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("intent1", root.GetProperty("intent_id").GetString());

        var cmd = root.GetProperty("command");
        Assert.Equal("add_to_queue", cmd.GetProperty("endpoint").GetString());
        Assert.False(cmd.TryGetProperty("uri", out _));                 // NOT the legacy flat command.uri
        Assert.False(cmd.TryGetProperty("prepare_play_options", out _));

        var track = cmd.GetProperty("track");
        Assert.Equal("spotify:track:x", track.GetProperty("uri").GetString());
        Assert.Equal("", track.GetProperty("uid").GetString());         // present even when empty
        Assert.Equal(JsonValueKind.Object, track.GetProperty("metadata").ValueKind);
        Assert.Empty(track.GetProperty("metadata").EnumerateObject());  // explicit {}

        var opts = cmd.GetProperty("options");
        Assert.False(opts.GetProperty("override_restrictions").GetBoolean());
        Assert.False(opts.GetProperty("only_for_local_device").GetBoolean());
        Assert.False(opts.GetProperty("system_initiated").GetBoolean());

        var log = cmd.GetProperty("logging_params");
        Assert.Equal("us", log.GetProperty("device_identifier").GetString());
        Assert.Equal(7, log.GetProperty("command_initiated_time").GetInt64());
        Assert.Equal(7, log.GetProperty("command_received_time").GetInt64());   // echoed = initiated (FIXTURE add-to-queue-remote)
        Assert.Empty(log.GetProperty("interaction_ids").EnumerateArray());      // default interactionId "" → []
        Assert.Empty(log.GetProperty("page_instance_ids").EnumerateArray());
    }

    [Fact]
    public void AddToQueue_UidPresentWhenProvided()
    {
        var json = OutboundEnvelope.AddToQueue("us", "spotify:track:x", "q1", false, false, false, "c", "i", 1);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("q1", doc.RootElement.GetProperty("command").GetProperty("track").GetProperty("uid").GetString());
    }

    // ── bug 5: a richer caller (EnqueueAsync(Track)) writes the full display + video metadata map ─────────────────────
    [Fact]
    public void AddToQueue_WithMetadata_WritesTheFullMap()
    {
        var json = OutboundEnvelope.AddToQueue("us", "spotify:track:x", "", false, false, false, "c", "i", 1,
            metadata: new Dictionary<string, string> { ["title"] = "T", ["track_player"] = "video", ["media.type"] = "video" });

        using var doc = JsonDocument.Parse(json);
        var meta = doc.RootElement.GetProperty("command").GetProperty("track").GetProperty("metadata");
        Assert.Equal("T", meta.GetProperty("title").GetString());
        Assert.Equal("video", meta.GetProperty("track_player").GetString());
        Assert.Equal("video", meta.GetProperty("media.type").GetString());
    }

    [Fact]
    public void AddToQueue_InteractionId_PopulatesInteractionIdsArray()
    {
        var json = OutboundEnvelope.AddToQueue("us", "spotify:track:x", "", false, false, false, "c", "i", 1,
            interactionId: "ee0a6a80-8b55-410c-8916-6e056238fce6");
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("command").GetProperty("logging_params").GetProperty("interaction_ids");
        Assert.Equal(1, ids.GetArrayLength());
        Assert.Equal("ee0a6a80-8b55-410c-8916-6e056238fce6", ids[0].GetString());   // caller-supplied id rides in interaction_ids
    }

    [Fact]
    public void SetQueue_EmitsFullSnapshotShape()
    {
        var json = OutboundEnvelope.SetQueue("us", 2487768622004702740UL,
            prevTracks: Array.Empty<QueueWireEntry>(),
            nextTracks: new[]
            {
                new QueueWireEntry("spotify:track:n1", "q1", true,
                    new Dictionary<string, string> { ["title"] = "Nice", ["is_explicit"] = "false" }),
                new QueueWireEntry("spotify:track:n2", "", true),
                new QueueWireEntry("spotify:track:cx", "hex", false),
            },
            commandId: "c", intentId: "i", initiatedTimeMs: 5, interactionId: "iact-9");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("i", root.GetProperty("intent_id").GetString());

        var cmd = root.GetProperty("command");
        Assert.Equal("set_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Equal(JsonValueKind.Number, cmd.GetProperty("queue_revision").ValueKind);   // bare number, not a string
        Assert.Equal(2487768622004702740UL, cmd.GetProperty("queue_revision").GetUInt64());
        Assert.Empty(cmd.GetProperty("prev_tracks").EnumerateArray());                     // no history model → empty

        var next = cmd.GetProperty("next_tracks");
        Assert.Equal(3, next.GetArrayLength());

        // queue rows: provider:"queue" + metadata.is_queued is the STRING "true"
        Assert.Equal("queue", next[0].GetProperty("provider").GetString());
        var md0 = next[0].GetProperty("metadata").GetProperty("is_queued");
        Assert.Equal(JsonValueKind.String, md0.ValueKind);
        Assert.Equal("true", md0.GetString());
        Assert.Equal("Nice", next[0].GetProperty("metadata").GetProperty("title").GetString());          // supplied entry metadata rides through
        Assert.Equal("false", next[0].GetProperty("metadata").GetProperty("is_explicit").GetString());
        Assert.Equal("q1", next[0].GetProperty("uid").GetString());
        Assert.Equal("", next[1].GetProperty("uid").GetString());                          // uid always written, may be ""

        // context row: provider:"context", no is_queued
        Assert.Equal("context", next[2].GetProperty("provider").GetString());
        Assert.False(next[2].GetProperty("metadata").TryGetProperty("is_queued", out _));

        // every entry: removed/blocked empty arrays + restrictions = the 22-key object, each an empty array
        foreach (var entry in next.EnumerateArray())
        {
            Assert.Empty(entry.GetProperty("removed").EnumerateArray());
            Assert.Empty(entry.GetProperty("blocked").EnumerateArray());
            int rk = 0;
            foreach (var p in entry.GetProperty("restrictions").EnumerateObject()) { rk++; Assert.Equal(JsonValueKind.Array, p.Value.ValueKind); }
            Assert.Equal(22, rk);
        }

        var opts = cmd.GetProperty("options");
        Assert.False(opts.GetProperty("override_restrictions").GetBoolean());
        Assert.False(opts.GetProperty("only_for_local_device").GetBoolean());
        Assert.False(opts.GetProperty("system_initiated").GetBoolean());
        var log = cmd.GetProperty("logging_params");
        Assert.Equal("us", log.GetProperty("device_identifier").GetString());
        Assert.Equal(5, log.GetProperty("command_initiated_time").GetInt64());
        Assert.Equal(5, log.GetProperty("command_received_time").GetInt64());     // echoed = initiated (FIXTURE set-queue-*)
        var ids = log.GetProperty("interaction_ids");
        Assert.Equal(1, ids.GetArrayLength());
        Assert.Equal("iact-9", ids[0].GetString());                              // caller-supplied interaction id
    }

    [Fact]
    public void SetQueue_RevisionExceedingInt64_SerializesAsBareNumber()
    {
        var json = OutboundEnvelope.SetQueue("us", 10355548321371651421UL,
            Array.Empty<QueueWireEntry>(), new[] { new QueueWireEntry("spotify:track:x", "", true) }, "c", "i", 1);
        Assert.Contains("\"queue_revision\":10355548321371651421", json);   // unquoted bare number, > Int64.MaxValue
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(10355548321371651421UL, doc.RootElement.GetProperty("command").GetProperty("queue_revision").GetUInt64());
    }

    [Fact]
    public void NextTrack_QueueRow_MatchesFixtureBFieldSet()
    {
        // FIXTURE-B req1 (que.saz): clicking user-queue row uid "q2" targeting another device — endpoint "next_track",
        // the three options bools, track.uri/uid + verbatim metadata + top-level album_uri/artist_uri, logging_params.
        var meta = new Dictionary<string, string>
        {
            ["is_queued"] = "true",
            ["album_uri"] = "spotify:album:4xi27YjXGPBNbrONMJBnfm",
            ["artist_uri"] = "spotify:artist:7bWYN0sHvyH7yv1uefX07U",
            ["title"] = "Fall In Love",
        };
        var track = new Track("13jOUAJE5tHgidRLrmSpO5", "spotify:track:13jOUAJE5tHgidRLrmSpO5", "Fall In Love",
            new[] { new ArtistRef("7bWYN0sHvyH7yv1uefX07U", "spotify:artist:7bWYN0sHvyH7yv1uefX07U", "Jukjae") },
            new AlbumRef("4xi27YjXGPBNbrONMJBnfm", "spotify:album:4xi27YjXGPBNbrONMJBnfm", "Nevertheless"), 240000, false, null);
        var row = new QueueEntry(new QueueItemId(7), "i7", track, QueueBucket.UserQueue, QueueProvider.Queue, false, "q2", meta);

        var json = OutboundEnvelope.NextTrack(row, "dev9", "cmd1", "intent1", 1783530967413);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("intent1", root.GetProperty("intent_id").GetString());

        var cmd = root.GetProperty("command");
        Assert.Equal("next_track", cmd.GetProperty("endpoint").GetString());

        var opts = cmd.GetProperty("options");
        Assert.False(opts.GetProperty("override_restrictions").GetBoolean());
        Assert.False(opts.GetProperty("only_for_local_device").GetBoolean());
        Assert.False(opts.GetProperty("system_initiated").GetBoolean());

        var t = cmd.GetProperty("track");
        Assert.Equal("spotify:track:13jOUAJE5tHgidRLrmSpO5", t.GetProperty("uri").GetString());
        Assert.Equal("q2", t.GetProperty("uid").GetString());                                    // uid-first identity
        Assert.Equal("spotify:album:4xi27YjXGPBNbrONMJBnfm", t.GetProperty("album_uri").GetString());   // top-level, always present
        Assert.Equal("spotify:artist:7bWYN0sHvyH7yv1uefX07U", t.GetProperty("artist_uri").GetString());
        var md = t.GetProperty("metadata");
        Assert.Equal("true", md.GetProperty("is_queued").GetString());                           // metadata passthrough verbatim
        Assert.Equal("Fall In Love", md.GetProperty("title").GetString());

        var log = cmd.GetProperty("logging_params");
        Assert.Equal("cmd1", log.GetProperty("command_id").GetString());
        Assert.Equal("dev9", log.GetProperty("device_identifier").GetString());
        Assert.Equal(1783530967413, log.GetProperty("command_initiated_time").GetInt64());
        Assert.Equal(1783530967413, log.GetProperty("command_received_time").GetInt64());        // FIXTURE-B carries both time keys
    }

    [Fact]
    public void NextTrack_AutoplayRow_UsesHexUid_AndAlwaysWritesAlbumArtistKeys()
    {
        // FIXTURE-B req2: an autoplay-station row jump — uid is the 16-hex, autoplay metadata rides through, and the
        // top-level album_uri/artist_uri keys are ALWAYS emitted (present in the capture even for autoplay rows).
        var meta = new Dictionary<string, string> { ["autoplay.is_autoplay"] = "true" };
        var track = new Track("37dkyQQNJLaqk09kkNr7In", "spotify:track:37dkyQQNJLaqk09kkNr7In", "Amazing You",
            System.Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 297000, false, null);
        var row = new QueueEntry(new QueueItemId(3), "i3", track, QueueBucket.NextUp, QueueProvider.Autoplay, true, "8e7f7ecb4ffb81fa", meta);

        var json = OutboundEnvelope.NextTrack(row, "dev9", "c", "i", 1);
        using var doc = JsonDocument.Parse(json);
        var t = doc.RootElement.GetProperty("command").GetProperty("track");
        Assert.Equal("8e7f7ecb4ffb81fa", t.GetProperty("uid").GetString());
        Assert.Equal("true", t.GetProperty("metadata").GetProperty("autoplay.is_autoplay").GetString());
        Assert.True(t.TryGetProperty("album_uri", out _));    // always present, even when the row carries no album/artist uri
        Assert.True(t.TryGetProperty("artist_uri", out _));
    }

    [Fact]
    public void Command_WrapsEnvelope_WithLoggingParams()
    {
        var json = OutboundEnvelope.Command("dev1", "pause", Array.Empty<(string, object)>(), "c", "i", 5, interactionId: "iact-cmd");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("pause", root.GetProperty("command").GetProperty("endpoint").GetString());
        var log = root.GetProperty("command").GetProperty("logging_params");
        Assert.Equal("dev1", log.GetProperty("device_identifier").GetString());
        Assert.Equal(5, log.GetProperty("command_initiated_time").GetInt64());
        Assert.Equal(5, log.GetProperty("command_received_time").GetInt64());
        Assert.Equal("iact-cmd", log.GetProperty("interaction_ids")[0].GetString());
    }

    [Fact]
    public void Command_DefaultInteractionId_LeavesInteractionIdsEmpty()
    {
        var json = OutboundEnvelope.Command("dev1", "pause", Array.Empty<(string, object)>(), "c", "i", 5);
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("command").GetProperty("logging_params").GetProperty("interaction_ids").EnumerateArray());
    }

    [Fact]
    public void ConnectVolumeBody_RoundTripsToTheCapturedBytes()
    {
        // SetVolumeCommand{ volume=19496, logging_params={}, connection_type="wlan" } == the decoded desktop capture.
        Assert.Equal("CKiYARoAIgR3bGFu", Convert.ToBase64String(OutboundEnvelope.ConnectVolumeBody(19496)));
    }

    [Fact]
    public void Transfer_MatchesDesktopConnectTransferBodyShape()
    {
        var json = OutboundEnvelope.Transfer("transfer-id", "command-id", "interaction-id", "premium");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var options = root.GetProperty("options");
        Assert.Equal("restore", options.GetProperty("restore_paused").GetString());
        Assert.Equal("extrapolate", options.GetProperty("restore_position").GetString());
        Assert.Equal("only_current", options.GetProperty("restore_track").GetString());
        Assert.Equal("premium", options.GetProperty("license").GetString());
        Assert.Equal("transfer-id", root.GetProperty("transfer_intent_id").GetString());
        Assert.Equal("command-id", root.GetProperty("command_id").GetString());
        Assert.Equal("interaction-id", root.GetProperty("interaction_id").GetString());
    }

    // ── bug 8: a transfer-away that hands over a locally-hosted video says so ───────────────────────────────────────
    [Fact]
    public void Transfer_HostingVideo_AddsVideoPersistenceMode()
    {
        var json = OutboundEnvelope.Transfer("transfer-id", "command-id", "interaction-id", "premium", hostingVideo: true);
        using var doc = JsonDocument.Parse(json);
        var modes = doc.RootElement.GetProperty("options").GetProperty("modes");
        Assert.Equal("VIDEO", modes.GetProperty("video_persistence").GetString());
    }

    [Fact]
    public void Transfer_NotHostingVideo_OmitsModesEntirely()
    {
        var json = OutboundEnvelope.Transfer("transfer-id", "command-id", "interaction-id", "premium");
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("options").TryGetProperty("modes", out _));
    }

    [Fact]
    public async Task LiveOutboundControl_Transfer_PostsToConnectTransferEndpoint()
    {
        var stub = new StubTransport();
        await new LiveOutboundControl(stub, "us", () => "conn-123").TransferAsync("active", "target");
        Assert.Equal("/connect-state/v1/connect/transfer/from/active/to/target", stub.LastRequestRoute);
        Assert.Equal("POST", stub.LastRequestMethod);
        Assert.Equal("application/x-www-form-urlencoded", stub.LastRequestHeaders!["Content-Type"]);
        Assert.Equal("conn-123", stub.LastRequestHeaders!["X-Spotify-Connection-Id"]);

        using var doc = JsonDocument.Parse(stub.LastRequestBody!);
        Assert.Equal("restore", doc.RootElement.GetProperty("options").GetProperty("restore_paused").GetString());
        Assert.Equal("extrapolate", doc.RootElement.GetProperty("options").GetProperty("restore_position").GetString());
        Assert.Equal("only_current", doc.RootElement.GetProperty("options").GetProperty("restore_track").GetString());
    }

    [Fact]
    public async Task LiveOutboundControl_SetVolume_PutsToConnectVolumeEndpoint()
    {
        var stub = new StubTransport();
        await new LiveOutboundControl(stub, "us").SetVolumeAsync("target", 19496);
        Assert.Equal("/connect-state/v1/connect/volume/from/us/to/target", stub.LastRequestRoute);
        Assert.Equal("PUT", stub.LastRequestMethod);
        Assert.Equal("CKiYARoAIgR3bGFu", Convert.ToBase64String(stub.LastRequestBody!));
    }

    [Fact]
    public async Task LiveOutboundControl_Command_GzipFraming_HeadersRouteAndBody()
    {
        var stub = new StubTransport();
        await new LiveOutboundControl(stub, "us", () => "conn-123").SendAsync("target", "{\"command\":{}}");

        Assert.Equal("/connect-state/v1/player/command/from/us/to/target", stub.LastRequestRoute);
        Assert.Equal("POST", stub.LastRequestMethod);

        var h = stub.LastRequestHeaders!;
        Assert.NotNull(h);
        Assert.Equal("application/x-www-form-urlencoded", h["Content-Type"]);
        Assert.Equal("gzip", h["X-Transfer-Encoding"]);                       // custom header — NOT Content-Encoding
        Assert.Equal("conn-123", h["X-Spotify-Connection-Id"]);
        Assert.False(h.ContainsKey("Content-Encoding"));                      // would make intermediaries auto-inflate
        Assert.False(h.ContainsKey("client-token"));                         // middleware stamps it downstream, not here

        var body = stub.LastRequestBody!;
        Assert.Equal(0x1F, body[0]); Assert.Equal(0x8B, body[1]);            // gzip magic bytes
        Assert.Equal("{\"command\":{}}", Encoding.UTF8.GetString(HttpCompression.Gunzip(body)));
    }

    [Fact]
    public async Task LiveOutboundControl_Command_OmitsConnectionIdWhenAbsent()
    {
        var noDelegate = new StubTransport();
        await new LiveOutboundControl(noDelegate, "us").SendAsync("t", "{\"command\":{}}");

        var emptyConn = new StubTransport();
        await new LiveOutboundControl(emptyConn, "us", () => "").SendAsync("t", "{\"command\":{}}");

        var nullConn = new StubTransport();
        await new LiveOutboundControl(nullConn, "us", () => null).SendAsync("t", "{\"command\":{}}");

        foreach (var stub in new[] { noDelegate, emptyConn, nullConn })
        {
            var h = stub.LastRequestHeaders!;
            Assert.False(h.ContainsKey("X-Spotify-Connection-Id"));          // omitted when delegate null / returns null|""
            Assert.Equal("application/x-www-form-urlencoded", h["Content-Type"]);   // framing still present
            Assert.Equal("gzip", h["X-Transfer-Encoding"]);
        }
    }

    [Fact]
    public async Task LiveOutboundControl_Volume_SendsNoCommandFraming()
    {
        // The volume PUT must not pick up the player-command gzip/form framing even when a connection-id is available.
        var stub = new StubTransport();
        await new LiveOutboundControl(stub, "us", () => "conn-123").SetVolumeAsync("target", 19496);
        Assert.Equal("PUT", stub.LastRequestMethod);
        Assert.Null(stub.LastRequestHeaders);   // SetVolumeAsync passes no headers → application/protobuf default, no gzip
    }
}
