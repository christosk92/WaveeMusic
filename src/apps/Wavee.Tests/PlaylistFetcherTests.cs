using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// The live membership fetcher, exercised end-to-end (GET → parse SelectedListContent → store thin header + membership →
// hydrate) over a faked HTTP exchange. The real GET is the only unverifiable part.
public class PlaylistFetcherTests
{
    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);
    /// <summary>I1 — the only storable revision shape: 4-byte big-endian counter + 20-byte hash.</summary>
    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }

    [Fact]
    public async Task FetchPlaylist_HitsTheRightUrl_StoresThinHeaderAndMembership_AndHydrates()
    {
        var store = new InMemoryStore();
        var rev24 = Rev24(7);   // I1 — only the 24-byte head is storable
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(rev24), Length = 2, OwnerUsername = "bob" };
        slc.Attributes = new Pl.ListAttributes { Name = "My Mix", Description = "d", Collaborative = true };
        slc.Capabilities = new Pl.Capabilities { CanView = true, CanEditItems = true, CanEditMetadata = true };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:a", Attributes = new Pl.ItemAttributes { AddedBy = "me", Timestamp = 5 } });
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:b" });
        slc.Contents = contents;
        var bytes = slc.ToByteArray();

        HttpReq? captured = null;
        var hydrated = new List<string>();
        var http = new FakeExchange((req, _) => { captured = req; return Ok(bytes); });
        var fetcher = new PlaylistFetcher(http, () => "https://spclient.test", store, (uris, ct) => { hydrated.AddRange(uris); return Task.CompletedTask; }, () => "");

        await fetcher.FetchPlaylistAsync("spotify:playlist:p", TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("GET", captured!.Method);
        Assert.Contains("/playlist/v2/playlist/p?decorate=", captured.Url);

        var m = store.Membership("spotify:playlist:p");
        Assert.Equal(2, m.Count);
        Assert.Equal("spotify:track:a", m[0].ItemUri);
        Assert.Equal("me", m[0].AddedBy);
        Assert.Equal(rev24, store.PlaylistRevision("spotify:playlist:p"));

        var header = store.GetPlaylist("spotify:playlist:p");
        Assert.NotNull(header);
        Assert.Equal("My Mix", header!.Name);
        Assert.Equal("bob", header.OwnerName);
        Assert.True(header.Capabilities.IsCollaborative);
        Assert.True(header.Capabilities.CanView);
        Assert.True(header.Capabilities.CanEditItems);
        Assert.True(header.Capabilities.CanEditMetadata);

        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, hydrated);   // membership uris handed to the hydrator
    }

    [Fact]
    public async Task FetchPlaylist_Throws_OnNon200()
    {
        var http = new FakeExchange((req, _) => new HttpResp(404, new Dictionary<string, string>(), Array.Empty<byte>()));
        var fetcher = new PlaylistFetcher(http, () => "https://x", new InMemoryStore(), (u, c) => Task.CompletedTask, () => "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.FetchPlaylistAsync("spotify:playlist:p", CancellationToken.None));
    }

    [Fact]
    public async Task FetchRootlist_ParsesMarkersIntoOrderedEntries()
    {
        var store = new InMemoryStore();
        var slc = new Pl.SelectedListContent();
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(new Pl.Item { Uri = "spotify:start-group:g1:Folder" });
        contents.Items.Add(new Pl.Item { Uri = "spotify:playlist:p1" });
        contents.Items.Add(new Pl.Item { Uri = "spotify:end-group:g1" });
        contents.Items.Add(new Pl.Item { Uri = "spotify:playlist:p2" });
        slc.Contents = contents;

        HttpReq? captured = null;
        var http = new FakeExchange((req, _) => { captured = req; return Ok(slc.ToByteArray()); });
        var fetcher = new PlaylistFetcher(http, () => "https://x", store, (u, c) => Task.CompletedTask, () => "");

        await fetcher.FetchRootlistAsync("spotify:user:bob:rootlist", TestContext.Current.CancellationToken);

        Assert.Contains("/playlist/v2/user/bob/rootlist?decorate=", captured!.Url);
        var rl = store.Rootlist();
        Assert.Equal(4, rl.Count);
        Assert.Equal(1, rl[0].Kind);             // start-group
        Assert.Equal("Folder", rl[0].GroupName);
        Assert.Equal("spotify:playlist:p1", rl[1].Uri);
        Assert.Equal(1, rl[1].Depth);            // inside the folder
        Assert.Equal(2, rl[2].Kind);             // end-group
        Assert.Equal(0, rl[3].Depth);            // back at top level
    }
}
