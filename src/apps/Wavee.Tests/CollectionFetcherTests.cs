using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Spotify;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

// The live collection fetcher: POST /collection/v2/{paging|delta} → apply items onto the Store's set + hydrate + advance
// the sync token. HTTP faked; the proto request/response shapes are exercised for real.
public class CollectionFetcherTests
{
    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);

    [Fact]
    public async Task FetchSet_FullPage_AppliesItems_Hydrates_AndAdvancesRevision()
    {
        var store = new InMemoryStore();
        var page = new Col.PageResponse { SyncToken = "tok-1", NextPageToken = "" };
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 1 });
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:album:b", AddedAt = 2 });

        HttpReq? captured = null;
        var hydrated = new List<string>();
        var revs = new Dictionary<string, string?>();
        var http = new FakeExchange((req, _) => { captured = req; return Ok(page.ToByteArray()); });
        var fetcher = new CollectionFetcher(http, () => "https://spclient.test", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r,
            (uris, ct) => { hydrated.AddRange(uris); return Task.CompletedTask; });

        await fetcher.FetchSetAsync("albums", TestContext.Current.CancellationToken);

        Assert.Equal("POST", captured!.Method);
        Assert.Contains("/collection/v2/paging", captured.Url);
        // The collection2v2 route only accepts its vendor media type — `application/protobuf` 400s. Guard both headers.
        Assert.Equal("application/vnd.collection-v2.spotify.proto", captured.Headers["Content-Type"]);
        Assert.Equal("application/vnd.collection-v2.spotify.proto", captured.Headers["Accept"]);
        Assert.True(store.IsSaved("albums", "spotify:album:a"));
        Assert.True(store.IsSaved("albums", "spotify:album:b"));
        Assert.Equal("tok-1", revs["albums"]);
        Assert.Equal(2, hydrated.Count);

        var sent = Col.PageRequest.Parser.ParseFrom(captured.Body);   // the request body is a real PageRequest
        Assert.Equal("bob", sent.Username);
        Assert.Equal("collection", sent.Set);   // albums has no wire set of its own — it rides inside "collection"
    }

    [Fact]
    public async Task FetchSet_CollectionSharedWireSet_SplitsTracksToLiked_AndAlbumsToAlbums()
    {
        // One wire "collection" page mixes tracks + albums; "liked" must keep only spotify:track:, "albums" only spotify:album:.
        var store = new InMemoryStore();
        var mixed = new Col.PageResponse { SyncToken = "tok-1", NextPageToken = "" };
        mixed.Items.Add(new Col.CollectionItem { Uri = "spotify:track:t1", AddedAt = 1 });
        mixed.Items.Add(new Col.CollectionItem { Uri = "spotify:album:al1", AddedAt = 2 });
        mixed.Items.Add(new Col.CollectionItem { Uri = "spotify:track:t2", AddedAt = 3 });

        var sentSets = new List<string>();
        var revs = new Dictionary<string, string?>();
        var http = new FakeExchange((req, _) =>
        {
            sentSets.Add(Col.PageRequest.Parser.ParseFrom(req.Body).Set);
            return Ok(mixed.ToByteArray());
        });
        var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, (u, c) => Task.CompletedTask);

        await fetcher.FetchSetAsync("liked", TestContext.Current.CancellationToken);
        await fetcher.FetchSetAsync("albums", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "collection", "collection" }, sentSets);   // both hit the same wire set
        Assert.True(store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(store.IsSaved("liked", "spotify:track:t2"));
        Assert.False(store.IsSaved("liked", "spotify:album:al1"));      // album filtered OUT of liked
        Assert.True(store.IsSaved("albums", "spotify:album:al1"));
        Assert.False(store.IsSaved("albums", "spotify:track:t1"));      // track filtered OUT of albums
    }

    [Fact]
    public async Task FetchSet_WireSetNames_AreTheSingularServerNames()
    {
        // Regression guard for the other half of the 400: the server sets are singular ("artist"/"show") + "listenlater".
        foreach (var (setId, wire) in new[] { ("artists", "artist"), ("shows", "show"), ("episodes", "listenlater") })
        {
            var store = new InMemoryStore();
            var page = new Col.PageResponse { SyncToken = "t", NextPageToken = "" };
            string? sentSet = null;
            var http = new FakeExchange((req, _) => { sentSet = Col.PageRequest.Parser.ParseFrom(req.Body).Set; return Ok(page.ToByteArray()); });
            var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob", store,
                _ => null, (_, _) => { }, (u, c) => Task.CompletedTask);

            await fetcher.FetchSetAsync(setId, TestContext.Current.CancellationToken);
            Assert.Equal(wire, sentSet);
        }
    }

    [Fact]
    public async Task FetchSet_Delta_WhenPriorTokenPresent_AddsAndRemoves()
    {
        var store = new InMemoryStore();
        // Non-zero addedAt — otherwise AllTimestampless() clears the sync token and the fetcher falls through to
        // paging; this test's fake only returns a DeltaResponse, so paging would spin forever.
        store.SetSaved("liked", "spotify:track:old", true, SyncState.Confirmed, addedAtMs: 1_700_000_000_000);
        var delta = new Col.DeltaResponse { DeltaUpdatePossible = true, SyncToken = "tok-2" };
        delta.Items.Add(new Col.CollectionItem { Uri = "spotify:track:new", AddedAt = 9 });
        delta.Items.Add(new Col.CollectionItem { Uri = "spotify:track:old", IsRemoved = true });

        HttpReq? captured = null;
        var revs = new Dictionary<string, string?> { ["liked"] = "tok-1" };   // prior token → delta path
        var http = new FakeExchange((req, _) => { captured = req; return Ok(delta.ToByteArray()); });
        var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, (u, c) => Task.CompletedTask);

        await fetcher.FetchSetAsync("liked", TestContext.Current.CancellationToken);

        Assert.Contains("/collection/v2/delta", captured!.Url);
        Assert.True(store.IsSaved("liked", "spotify:track:new"));
        Assert.False(store.IsSaved("liked", "spotify:track:old"));
        Assert.Equal("tok-2", revs["liked"]);

        var sent = Col.DeltaRequest.Parser.ParseFrom(captured.Body);
        Assert.Equal("collection", sent.Set);        // liked → wire set name "collection"
        Assert.Equal("tok-1", sent.LastSyncToken);
    }
}
