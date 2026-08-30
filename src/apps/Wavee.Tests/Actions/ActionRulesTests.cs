using System;
using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

// The pure decision core behind the action predicates (Actions/ActionRules.cs) + the queue verbs (DetailQueueActions)
// the PlayNext/AddToQueue/drag-drop paths ride: they ISSUE the intent and report how many tracks went (capped at
// MaxBatch), leaving the local-vs-remote routing — and the one refusal, "no local audio stack" — to the player.
public class ActionRulesTests
{
    // ── ToggleLike checked-state: checked iff ALL saved ────────────────────────────────────────────────────────────
    [Fact]
    public void AllSaved_TrueOnlyWhenEveryTrackSaved()
    {
        var saved = new HashSet<string> { "spotify:track:a" };
        var a = T.Mk("a");
        var b = T.Mk("b");

        Assert.True(ActionRules.AllSaved(new[] { a }, saved.Contains));
        Assert.False(ActionRules.AllSaved(new[] { a, b }, saved.Contains));   // 1 of 2 saved → unchecked

        saved.Add("spotify:track:b");
        Assert.True(ActionRules.AllSaved(new[] { a, b }, saved.Contains));
    }

    [Fact]
    public void AllSaved_EmptySet_OrUrilessTrack_IsFalse()
    {
        Assert.False(ActionRules.AllSaved(Array.Empty<Track>(), _ => true));
        var uriless = T.Mk("x", uriOverride: "");
        Assert.False(ActionRules.AllSaved(new[] { uriless }, _ => true));
    }

    // ── The container-navigation pair: a song goes to its ALBUM, an episode to its PODCAST ─────────────────
    // An EPISODE rides the same Track read-model (EpisodeAsTrack, design §1.5) with its SHOW in the album slot, so the
    // two gates are one decision read twice: exactly ONE of them may be true for any row, and "Go to album" pointing at
    // a show — which is what an Album.Uri.Length-only gate produced — is a route into a page that does not exist.
    [Fact]
    public void GoToAlbum_IsTheSongRow_GoToPodcast_IsTheEpisodeRow()
    {
        var song = ActionTarget.ForTracks(new[] { T.Mk("a") });
        var episode = ActionTarget.ForTracks(new[]
        {
            T.Mk("e1", uriOverride: "spotify:episode:e1", artists: 0, albumUri: "spotify:show:s1"),
        });

        Assert.True(ActionRules.CanGoToAlbum(in song));
        Assert.False(ActionRules.CanGoToPodcast(in song));

        Assert.False(ActionRules.CanGoToAlbum(in episode));
        Assert.True(ActionRules.CanGoToPodcast(in episode));
    }

    [Fact]
    public void NeitherNavigationRow_ForARefWithNoUri()
    {
        // A name-only show/album ref (an episode whose catalogue write carried no show gid) has nowhere to go: both
        // rows are ABSENT rather than dead — the GoToArtistItem rule.
        var nameOnly = ActionTarget.ForTracks(new[] { T.Mk("e2", uriOverride: "spotify:episode:e2", artists: 0, albumUri: "") });
        Assert.False(ActionRules.CanGoToAlbum(in nameOnly));
        Assert.False(ActionRules.CanGoToPodcast(in nameOnly));

        // Only a SHOW ref is excluded. A uri the parser cannot classify keeps the album row it has always had — the
        // gate narrows one wrong destination, it does not become a spotify-only allow-list.
        var unclassified = ActionTarget.ForTracks(new[] { T.Mk("u1", albumUri: "wavee:media:xyz") });
        Assert.True(ActionRules.CanGoToAlbum(in unclassified));
        Assert.False(ActionRules.CanGoToPodcast(in unclassified));
    }

    // ── Remove-from-this-playlist gate: editable host + resolved rows ─────────────────────────────────────────────
    [Fact]
    public void CanRemoveFromPlaylist_RequiresEditableCapsAndRows()
    {
        Assert.False(ActionRules.CanRemoveFromPlaylist(null));   // album/artist surface — no host, no Remove row

        var rows = new[] { new PlaylistRowRef(0, "spotify:track:a", "uid-a") };
        var readOnly = new PlaylistHost("spotify:playlist:p",
            new PlaylistCapabilities(CanView: true, CanEditItems: false, CanEditMetadata: false, IsCollaborative: false, IsOwner: false), rows);
        Assert.False(ActionRules.CanRemoveFromPlaylist(readOnly));   // foreign playlist → absent

        var editableNoRows = new PlaylistHost("spotify:playlist:p",
            new PlaylistCapabilities(true, true, true, false, true), Array.Empty<PlaylistRowRef>());
        Assert.False(ActionRules.CanRemoveFromPlaylist(editableNoRows));

        var editable = new PlaylistHost("spotify:playlist:p",
            new PlaylistCapabilities(true, true, true, false, true), rows);
        Assert.True(ActionRules.CanRemoveFromPlaylist(editable));    // owned playlist → present
    }

    // ── Container navigation routes (the app's go(key, name) scheme) ──────────────────────────────────────────────
    [Fact]
    public void RouteFor_MapsContainerKinds()
    {
        var album = ActionTarget.ForAlbum("spotify:album:x", "A");
        var artist = ActionTarget.ForArtist("spotify:artist:y", "B");
        var playlist = ActionTarget.ForPlaylist("spotify:playlist:z", "C");
        var liked = ActionTarget.ForPlaylist("spotify:collection:tracks", "Liked Songs");
        var tracks = ActionTarget.ForTracks(new[] { T.Mk("a") });

        Assert.Equal("album:spotify:album:x", ActionRules.RouteFor(in album));
        Assert.Equal("artist:spotify:artist:y", ActionRules.RouteFor(in artist));
        Assert.Equal("pl:spotify:playlist:z", ActionRules.RouteFor(in playlist));
        Assert.Equal("liked", ActionRules.RouteFor(in liked));
        Assert.Null(ActionRules.RouteFor(in tracks));
    }

    // ── PlayNext / AddToEnd routing (regression: the old remote-device gate swallowed every idle-LOCAL enqueue) ────
    [Fact]
    public void PlayNext_NoActiveDevice_StillQueuesAndReportsCount()
    {
        var p = new RecordingPlayer();   // ActiveDeviceId null — playback routes local
        int n = DetailQueueActions.PlayNext(p, new[] { T.Mk("a"), T.Mk("b") });

        Assert.Equal(2, n);                          // the caller confirms what was issued
        Assert.Single(p.PlayNextCalls);
        Assert.Equal(2, p.PlayNextCalls[0].Count);
    }

    [Fact]
    public void PlayNext_WithActiveDevice_ReportsCount()
    {
        var p = new RecordingPlayer { ActiveDeviceId = "dev-1" };
        int n = DetailQueueActions.PlayNext(p, new[] { T.Mk("a"), T.Mk("b"), T.Mk("c") });

        Assert.Equal(3, n);
        Assert.Single(p.PlayNextCalls);
        Assert.Equal("spotify:track:a", p.PlayNextCalls[0][0].Uri);
        Assert.Equal("uid-a", p.PlayNextCalls[0][0].Uid);   // ContextUid rides the wire row
    }

    [Fact]
    public void AddToEnd_NoActiveDevice_StillEnqueuesLocally()
    {
        var p = new RecordingPlayer();
        int n = DetailQueueActions.AddToEnd(p, new[] { T.Mk("a") });

        // Where it lands is the player's decision (local host / forwarded / refused with its own toast) — this table
        // no longer pre-empts it with a device gate that predated local playback.
        Assert.Equal(1, n);
        Assert.Equal(new[] { "spotify:track:a" }, p.Enqueued);
    }

    // ── bug 1: an inserted row for a video-capable track must not assert "audio" — the target has no reason to doubt
    // an explicit claim, so a hardcoded downgrade actively lies. ──────────────────────────────────────────────────────
    [Fact]
    public void PlayNext_VideoAssociatedTrack_DoesNotClaimAudio()
    {
        var track = T.Mk("v");
        var store = new Wavee.Backend.InMemoryStore();
        store.UpsertVideoAssociation(new VideoAssociation(track.Uri, true, "spotify:track:v-video",
            VideoAssociation.NoFiles, null, DateTimeOffset.UtcNow, 0));
        try
        {
            VideoPresence.Attach(null, store);
            var p = new RecordingPlayer();
            DetailQueueActions.PlayNext(p, new[] { track });

            var meta = p.PlayNextCalls[0][0].Metadata!;
            Assert.Equal("video", meta["track_player"]);
            Assert.Equal("video", meta["media.type"]);
            Assert.Equal("spotify:track:v-video", meta["video_association"]);
        }
        finally { VideoPresence.Attach(null); }
    }

    [Fact]
    public void PlayNext_PlainTrack_StillReportsAudio()
    {
        VideoPresence.Attach(null);   // nothing attached → every track answers "no video"
        var p = new RecordingPlayer();
        DetailQueueActions.PlayNext(p, new[] { T.Mk("a") });

        Assert.Equal("audio", p.PlayNextCalls[0][0].Metadata!["track_player"]);
        Assert.False(p.PlayNextCalls[0][0].Metadata!.ContainsKey("media.type"));
    }

    // ── the drag-drop slot insert: the queue-relative index rides the call, and the batch cap is reported ──────────
    [Fact]
    public void InsertAt_PassesSlotAndCapsTheBatch()
    {
        var p = new RecordingPlayer();
        var many = new Track[DetailQueueActions.MaxBatch + 7];
        for (int i = 0; i < many.Length; i++) many[i] = T.Mk("t" + i);

        Assert.Equal(2, DetailQueueActions.InsertAt(p, new[] { T.Mk("a"), T.Mk("b") }, 3));
        Assert.Equal(3, p.InsertCalls[0].Index);
        Assert.Equal("spotify:track:a", p.InsertCalls[0].Tracks[0].Uri);

        Assert.Equal(DetailQueueActions.MaxBatch, DetailQueueActions.InsertAt(p, many, 0));
        Assert.Equal(DetailQueueActions.MaxBatch, p.InsertCalls[1].Tracks.Count);   // truncated, and the caller is told

        Assert.Equal(0, DetailQueueActions.InsertAt(p, System.Array.Empty<Track>(), 0));
        Assert.Equal(0, DetailQueueActions.InsertAt(null, new[] { T.Mk("a") }, 0));
        Assert.Equal(2, p.InsertCalls.Count);
    }

    [Fact]
    public void InsertAt_NegativeSlot_ClampsToPlayNext()
    {
        var p = new RecordingPlayer();
        DetailQueueActions.InsertAt(p, new[] { T.Mk("a") }, -4);
        Assert.Equal(0, p.InsertCalls[0].Index);
    }

    [Fact]
    public void AddToEnd_WithActiveDevice_EnqueuesEach()
    {
        var p = new RecordingPlayer { ActiveDeviceId = "dev-1" };
        int n = DetailQueueActions.AddToEnd(p, new[] { T.Mk("a"), T.Mk("b") });

        Assert.Equal(2, n);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, p.Enqueued);
    }

    [Fact]
    public void PlayNext_NullPlayer_ReportsZero()
        => Assert.Equal(0, DetailQueueActions.PlayNext(null, new[] { T.Mk("a") }));

    // ── View-credits gate: a single track carrying a primary artist uri (the NPV fetch keys off artistUri+trackUri) ──
    [Fact]
    public void CanViewCredits_SingleTrackWithPrimaryArtist_IsTrue()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a") });   // T.Mk seeds spotify:artist:ar0
        Assert.True(ActionRules.CanViewCredits(in target));
    }

    [Fact]
    public void CanViewCredits_MultiSelect_IsFalse()   // single-track only
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a"), T.Mk("b") });
        Assert.False(ActionRules.CanViewCredits(in target));
    }

    [Fact]
    public void CanViewCredits_TrackWithNoArtists_IsFalse()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a", artists: 0) });
        Assert.False(ActionRules.CanViewCredits(in target));
    }

    // ── Radio gates: song radio = single spotify:track; artist radio = an Artist target with a spotify:artist uri ─────
    [Fact]
    public void CanStartTrackRadio_SingleSpotifyTrack_IsTrue()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a") });   // spotify:track:a
        Assert.True(ActionRules.CanStartTrackRadio(in target));
    }

    [Fact]
    public void CanStartTrackRadio_MultiSelect_OrNonSpotifyUri_IsFalse()
    {
        var multi = ActionTarget.ForTracks(new[] { T.Mk("a"), T.Mk("b") });
        Assert.False(ActionRules.CanStartTrackRadio(in multi));            // radio seeds a single track

        var local = ActionTarget.ForTracks(new[] { T.Mk("x", uriOverride: "local:file:1") });
        Assert.False(ActionRules.CanStartTrackRadio(in local));           // non-spotify:track uri disabled
    }

    [Fact]
    public void CanStartArtistRadio_ArtistTarget_IsTrue_OthersFalse()
    {
        Assert.True(ActionRules.CanStartArtistRadio(ActionTarget.ForArtist("spotify:artist:y", "B")));
        Assert.False(ActionRules.CanStartArtistRadio(ActionTarget.ForAlbum("spotify:album:x", "A")));   // not an artist
        Assert.False(ActionRules.CanStartArtistRadio(ActionTarget.ForArtist("local:artist:z", "C")));   // non-spotify uri
    }

    [Fact]
    public void CanViewCredits_ArtistWithoutUri_IsFalse()
    {
        var track = new Track("z", "spotify:track:z", "Track z",
            new[] { new ArtistRef("", "", "Unknown") },
            new AlbumRef("al1", "spotify:album:al1", "Album One"), 180_000, false, null);
        var target = ActionTarget.ForTracks(new[] { track });
        Assert.False(ActionRules.CanViewCredits(in target));
    }
}
