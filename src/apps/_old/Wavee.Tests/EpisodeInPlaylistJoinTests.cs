using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Library;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Pb = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

// An EPISODE is a playable, and every list surface joins ONE projection of it (design §1.5 EpisodeAsTrack). Before
// this, a playlist holding a podcast episode joined `GetTrack` alone: the row vanished, the count was wrong, and a
// context resolve handed the player a uri-only placeholder that rendered as the raw uri.
//
// The half this file adds on top of the plain join is the show LINK. `Episode` now carries `ShowUri`, stamped by the
// EpisodeV4 projection off the payload's embedded show ref, and `EpisodeAsTrack` puts it in the album slot — which is
// what makes the row's subtitle navigable instead of dead text.
public class EpisodeInPlaylistJoinTests
{
    const string PlaylistUri = "spotify:playlist:mixed";
    const string EpisodeUri = "spotify:episode:e1";
    const string ShowUri = "spotify:show:s1";
    const string TrackUri = "spotify:track:t1";

    static Track Trk(string id) => new(id, "spotify:track:" + id, "T" + id, Array.Empty<ArtistRef>(),
        new AlbumRef("", "", ""), 1000, false, null);

    static Episode Ep(string? showUri) => new("e1", EpisodeUri, "Ep One", "The Show",
        new Image("https://img/ep"), 1_800_000, DateTimeOffset.UnixEpoch, ShowUri: showUri);

    static SwitchableEntityHydrator Offline(IStore store) => new(new OfflineEntityHydrator(store));

    static IStore MixedPlaylist(string? showUri)
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertEpisode(Ep(showUri));
        store.UpsertPlaylist(new Playlist("mixed", PlaylistUri, "Mixed", null, "Me", null, 0));
        store.SetMembership(PlaylistUri, new[]
        {
            new PlaylistMember("i1", TrackUri, null, 0),
            new PlaylistMember("i2", EpisodeUri, "alice", 1_700_000_000_000),
        }, null);
        return store;
    }

    static StoreLibrarySource SourceOver(IStore store)
        => new(store, Offline(store), OfflineOnlineCatalog.Instance);

    // ── the page read ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPlaylist_ShapesTheEpisodeRow_WithTheShowInTheAlbumSlot()
    {
        var pl = await SourceOver(MixedPlaylist(ShowUri)).GetPlaylistAsync(PlaylistUri);

        Assert.Equal(2, pl!.Tracks!.Count);
        var row = pl.Tracks[1];
        Assert.Equal("e1", row.Id);                 // the EPISODE's id — TrackRow.StateOf compares it, so the show's would light the wrong row
        Assert.Equal(EpisodeUri, row.Uri);
        Assert.Equal("Ep One", row.Title);
        Assert.Empty(row.Artists);                  // a podcast has a show, not artists
        Assert.Equal("The Show", row.Album.Name);
        Assert.Equal(ShowUri, row.Album.Uri);       // …and the show slot is a LINK
        Assert.Equal("podcast", row.Source);
    }

    // Unknown show uri is the old shape and must stay harmless: a named-but-unlinked ref, never a fabricated uri.
    [Fact]
    public async Task GetPlaylist_WithNoShowUri_KeepsTheNameAndLeavesTheRefUnlinked()
    {
        var pl = await SourceOver(MixedPlaylist(null)).GetPlaylistAsync(PlaylistUri);

        Assert.Equal("The Show", pl!.Tracks![1].Album.Name);
        Assert.Equal("", pl.Tracks[1].Album.Uri);
    }

    // ── the play path ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamTracks_IncludesTheEpisodeRow()
    {
        var titles = new List<string>();
        await foreach (var page in SourceOver(MixedPlaylist(ShowUri)).StreamTracksAsync(PlaylistUri))
            foreach (var t in page.Tracks) titles.Add(t.Title);

        Assert.Equal(new[] { "Tt1", "Ep One" }, titles);
    }

    // The context resolver's join used to ask GetTrack alone, so a podcast context reached the player as a placeholder
    // whose "title" was the raw uri.
    [Fact]
    public async Task ContextHydrate_YieldsTheEpisodeTitle_NotTheRawUri()
    {
        var store = MixedPlaylist(ShowUri);
        var resolver = new LiveContextResolver(new DeadTransport(), new OfflineEntityHydrator(store), store,
            () => SessionContext.LoggedOut);

        var rows = await resolver.HydrateAsync(new[]
        {
            new QueuedRef(TrackUri, "i1"),
            new QueuedRef(EpisodeUri, "i2"),
        });

        Assert.Equal(2, rows.Count);
        Assert.Equal("Ep One", rows[1].Track.Title);
        Assert.Equal("The Show", rows[1].Track.Album.Name);
        Assert.Equal(ShowUri, rows[1].Track.Album.Uri);
        Assert.Equal(QueueRowKind.Playable, rows[1].RowKind);
    }

    // ── where ShowUri comes from ─────────────────────────────────────────────────────────────────────────────────────

    // EpisodeV4 embeds the show ref as gid + name. Taking only the name is what left the row unlinkable; the id half
    // was on the wire the whole time.
    [Fact]
    public void ProjectEpisode_StampsTheShowUri_FromTheEmbeddedShowRef()
    {
        var store = new InMemoryStore();
        var ep = new Pb.Episode { Gid = Gid(0x22), Name = "Ep 1", Duration = 5000 };
        ep.Show = new Pb.Show { Gid = Gid(0x21), Name = "My Show" };
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.EpisodeV4 };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:episode:x", ExtensionData = Any.Pack(ep) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);

        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);

        var epi = store.GetEpisode("spotify:episode:" + Base62.Encode(Bytes(0x22)));
        Assert.Equal("My Show", epi!.ShowName);
        Assert.Equal("spotify:show:" + Base62.Encode(Bytes(0x21)), epi.ShowUri);
    }

    // A later write that does not know the gid (a cluster row, a blob persisted before the field existed) must not
    // strip the link — the same "thin write never downgrades a rich row" rule the rest of the merge applies.
    [Fact]
    public void EpisodeMerge_NeverStripsAResidentShowUri()
    {
        var store = new InMemoryStore();
        store.UpsertEpisode(Ep(ShowUri));
        store.UpsertEpisode(Ep(null));

        Assert.Equal(ShowUri, store.GetEpisode(EpisodeUri)!.ShowUri);
    }

    static byte[] Bytes(byte fill) { var a = new byte[16]; Array.Fill(a, fill); return a; }
    static ByteString Gid(byte fill) => ByteString.CopyFrom(Bytes(fill));

    /// <summary>HydrateAsync is a pure store join — it must not reach the wire at all, and this proves it by giving it
    /// a transport that answers nothing.</summary>
    sealed class DeadTransport : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
            => throw new InvalidOperationException("the context join must not touch the wire: " + route);
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }
}
