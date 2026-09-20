using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Library;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// End-to-end: the whole playlist vertical composing over the real CachedStore — fetch (faked HTTP) -> StoreLibrarySource
// read-model join -> MutationEngine edit -> DealerRouter push -> persistence across a "restart". Proves the units wire up.
public class LibraryVerticalTests
{

    // The facade every StoreLibrarySource read goes through. Offline = store-only, never networks (design 1.3).
    static SwitchableEntityHydrator Offline(IStore store) => new(new Wavee.Backend.Hydration.OfflineEntityHydrator(store));
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    // I1 — a stored playlist revision is always the 24-byte playlist4 head, so the crafted response carries one.
    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }

    static byte[] CraftPlaylist(byte rev, params (string Uri, string AddedBy)[] items)
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(rev)), Length = items.Length };
        slc.Attributes = new Pl.ListAttributes { Name = "Mix" };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        foreach (var it in items)
            contents.Items.Add(new Pl.Item { Uri = it.Uri, Attributes = new Pl.ItemAttributes { AddedBy = it.AddedBy } });
        slc.Contents = contents;
        return slc.ToByteArray();
    }

    [Fact]
    public async Task FullVertical_Fetch_Read_Edit_Push_Persist()
    {
        var path = TempDb();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            using (var store = new CachedStore(new SqliteColdStore(path)))
            {
                // hydrator stands in for MetadataService: upsert each membership uri as a Track entity
                Task Hydrate(IReadOnlyList<string> uris, CancellationToken c)
                {
                    foreach (var u in uris)
                        store.UpsertTrack(new Track(u.Split(':')[^1], u, "T-" + u.Split(':')[^1], Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null));
                    return Task.CompletedTask;
                }

                // 1) FETCH a playlist (faked spclient) → thin header + membership + hydrated tracks
                var http = new FakeExchange((req, _) => new HttpResp(200, new Dictionary<string, string>(),
                    CraftPlaylist(1, ("spotify:track:a", "alice"), ("spotify:track:b", "bob"))));
                var fetcher = new PlaylistFetcher(http, () => "https://spclient.test", store, Hydrate, () => "");
                await fetcher.FetchPlaylistAsync("spotify:playlist:p", ct);

                // 2) READ through the catalog bridge → joined read-model with membership facts stamped
                var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
                var pl = await src.GetPlaylistAsync("spotify:playlist:p");
                Assert.Equal("Mix", pl!.Name);
                Assert.Equal(2, pl.Tracks!.Count);
                Assert.Equal("alice", pl.Tracks[0].AddedBy);
                Assert.Equal("T-a", pl.Tracks[0].Title);

                // 3) EDIT optimistically (remove the first track) → the read reflects it immediately
                var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()) });
                eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) }, store.PlaylistRevision("spotify:playlist:p"));
                var edited = await src.GetPlaylistAsync("spotify:playlist:p");
                Assert.Equal("spotify:track:b", Assert.Single(edited!.Tracks!).Uri);

                // 3b) DRAIN the edit. I3(a): while a local intent is pending for a playlist, an inbound push is never
                //     applied in place — it only marks dirty. So the ack has to land before the push arrives, which is
                //     exactly the real ordering (the /changes response comes back before the dealer echo does).
                var transport = new StubTransport();
                await eng.Drain(transport, new SessionContext("bob", "US", "premium", "en", Tier.Premium, false), ct);
                Assert.Equal(0, eng.Pending);

                // 4) DEALER PUSH (parent-rev match) adds a track back → decoded + enqueued onto the sync loop, applied in place
                var collections = new Wavee.Backend.Collections.CollectionFetcher(http, () => "https://spclient.test", () => "bob", store,
                    _ => null, (_, _) => { }, Hydrate);
                await using var sync = new Wavee.Backend.Sync.LibrarySync(store, fetcher, collections, eng, new PlaylistResyncQueue(), transport,
                    () => new SessionContext("bob", "US", "premium", "en", Tier.Premium, false), () => "bob", default, ct);
                using var router = new DealerRouter(transport, sync);
                var mod = new Pl.PlaylistModificationInfo { Uri = ByteString.CopyFromUtf8("spotify:playlist:p"), ParentRevision = store.PlaylistRevision("spotify:playlist:p") is { } r ? ByteString.CopyFrom(r) : ByteString.Empty, NewRevision = ByteString.CopyFrom(Rev24(9)) };
                var add = new Pl.Add { AddLast = true };
                add.Items.Add(new Pl.Item { Uri = "spotify:track:b" });   // re-add b
                mod.Ops.Add(new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = add });
                transport.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", mod.ToByteArray()));
                await sync.WaitForIdleAsync();
                Assert.Equal(2, store.Membership("spotify:playlist:p").Count);                 // applied in place
                Assert.Equal(Rev24(9), store.PlaylistRevision("spotify:playlist:p"));           // revision advanced

                store.Flush();
            }

            // 5) RESTART → membership + entities persisted → the catalog reads offline from disk
            using (var store2 = new CachedStore(new SqliteColdStore(path)))
            {
                var src2 = new StoreLibrarySource(store2, Offline(store2), OfflineOnlineCatalog.Instance);
                var pl = await src2.GetPlaylistAsync("spotify:playlist:p");
                Assert.NotNull(pl);
                Assert.Equal(2, pl!.Tracks!.Count);                  // the post-push membership survived the restart
                Assert.Equal("spotify:track:b", pl.Tracks[1].Uri);
            }
        }
        finally { TryDelete(path); }
    }
}
