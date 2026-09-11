using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Sync;
using Wavee.Backend.Spotify;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

public class CollectionFetcherTests
{
    const long NowMs = 1_800_000_000_000L;
    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);
    static Col.PageResponse Page(string token, string next, params (string Uri, int AddedAt)[] items)
    {
        var page = new Col.PageResponse { SyncToken = token, NextPageToken = next };
        foreach (var (uri, at) in items) page.Items.Add(new Col.CollectionItem { Uri = uri, AddedAt = at });
        return page;
    }
    static CollectionFetcher Fetcher(Func<HttpReq, int, HttpResp> respond)
        => new(new FakeExchange(respond), () => "https://spclient.test", () => "bob", nowMs: () => NowMs);

    [Fact]
    public async Task Full_page_returns_membership_and_verified_token_with_exact_wire_headers()
    {
        HttpReq? request = null;
        var fetcher = Fetcher((req, _) => { request = req; return Ok(Page("token", "", ("spotify:track:t", 1), ("spotify:album:a", 2)).ToByteArray()); });
        var read = await fetcher.FetchWireSetAsync("collection", null);
        Assert.True(read.IsSnapshot);
        Assert.True(read.Verified);
        Assert.Equal("token", read.Token);
        Assert.Equal(2, read.Items.Length);
        Assert.Equal(1000, read.Items[0].AddedAt);
        Assert.Equal("POST", request!.Method);
        Assert.Equal("application/vnd.collection-v2.spotify.proto", request.Headers["Content-Type"]);
        Assert.Equal("application/vnd.collection-v2.spotify.proto", request.Headers["Accept"]);
        var wire = Col.PageRequest.Parser.ParseFrom(request.Body);
        Assert.Equal("bob", wire.Username);
        Assert.Equal("collection", wire.Set);
    }

    [Theory]
    [InlineData("artist", "artists", "spotify:artist:a")]
    [InlineData("show", "shows", "spotify:show:s")]
    [InlineData("listenlater", "episodes", "spotify:episode:e")]
    public async Task Wire_names_fan_out_to_their_logical_sets(string wire, string logical, string uri)
    {
        await using var host = new ReplicaTestHost();
        var read = await Fetcher((_, _) => Ok(Page("token", "", (uri, 1)).ToByteArray())).FetchWireSetAsync(wire, null);
        await host.Replicas.AdoptCollectionAsync(read);
        Assert.Equal(uri, Assert.Single(host.Replicas.ReadCollection(logical).Items).Uri);
    }

    [Fact]
    public async Task Mixed_wire_collection_commits_liked_and_albums_together()
    {
        await using var host = new ReplicaTestHost();
        var read = await Fetcher((_, _) => Ok(Page("token", "", ("spotify:track:t", 1), ("spotify:album:a", 2)).ToByteArray()))
            .FetchWireSetAsync("collection", null);
        Assert.Empty(host.Replicas.ReadCollection("liked").Items);
        await host.Replicas.AdoptCollectionAsync(read);
        Assert.Equal("spotify:track:t", Assert.Single(host.Replicas.ReadCollection("liked").Items).Uri);
        Assert.Equal("spotify:album:a", Assert.Single(host.Replicas.ReadCollection("albums").Items).Uri);
        Assert.Equal("token", host.Replicas.ReadConfirmedCollection("liked").WireRevision);
        Assert.Equal("token", host.Replicas.ReadConfirmedCollection("albums").WireRevision);
    }

    [Fact]
    public async Task Unknown_wire_set_fails_before_transport()
    {
        int calls = 0;
        var fetcher = Fetcher((_, _) => { calls++; return Ok([]); });
        await Assert.ThrowsAsync<ArgumentException>(() => fetcher.FetchWireSetAsync("artistban", null));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Existing_token_uses_delta_and_carries_its_expected_base()
    {
        HttpReq? request = null;
        var response = new Col.DeltaResponse { DeltaUpdatePossible = true, SyncToken = "new" };
        response.Items.Add(new Col.CollectionItem { Uri = "spotify:track:t", IsRemoved = true });
        var read = await Fetcher((req, _) => { request = req; return Ok(response.ToByteArray()); }).FetchWireSetAsync("collection", "old");
        Assert.False(read.IsSnapshot);
        Assert.True(read.Verified);
        Assert.Equal("old", read.ExpectedToken);
        Assert.Equal("new", read.Token);
        Assert.True(Assert.Single(read.Items).Removed);
        Assert.Contains("/delta", request!.Url);
        Assert.Equal("old", Col.DeltaRequest.Parser.ParseFrom(request.Body).LastSyncToken);
    }

    [Fact]
    public async Task Impossible_delta_falls_back_to_verified_snapshot()
    {
        int calls = 0;
        var fetcher = Fetcher((req, _) => { calls++; return req.Url.Contains("/delta")
            ? Ok(new Col.DeltaResponse { DeltaUpdatePossible = false }.ToByteArray())
            : Ok(Page("first", "", ("spotify:track:t", 1)).ToByteArray()); });
        var read = await fetcher.FetchWireSetAsync("collection", "expired");
        Assert.True(read.IsSnapshot && read.Verified);
        Assert.Equal(2, calls);
        Assert.Equal("first", read.Token);
    }

    [Fact]
    public async Task Multiple_pages_keep_earliest_token_and_all_members()
    {
        int calls = 0;
        var fetcher = Fetcher((_, _) => ++calls == 1
            ? Ok(Page("first", "next", ("spotify:track:a", 1)).ToByteArray())
            : Ok(Page("later", "", ("spotify:track:b", 2)).ToByteArray()));
        var read = await fetcher.FetchWireSetAsync("collection", null);
        Assert.True(read.Verified);
        Assert.Equal("first", read.Token);
        Assert.Equal(2, read.Items.Length);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Repeated_page_cursor_is_bounded_and_never_certifies_a_snapshot()
    {
        int calls = 0;
        var read = await Fetcher((_, _) => { calls++; return Ok(Page("t", "same", ("spotify:track:a", 1)).ToByteArray()); })
            .FetchWireSetAsync("collection", null);
        Assert.Equal(2, calls);
        Assert.False(read.Verified);
        Assert.Single(read.Items);
    }

    [Fact]
    public async Task Overlap_keeps_observations_but_never_sweeps_or_advances_token()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedCollectionAsync("collection", [new CollectionItem("spotify:track:old", false, 1)], "old-token");
        int calls = 0;
        var read = await Fetcher((_, _) => Ok(Page("new-token", ++calls == 1 ? "next" : "", ("spotify:track:new", 1)).ToByteArray()))
            .FetchWireSetAsync("collection", null);
        Assert.False(read.Verified);
        await host.Replicas.AdoptCollectionAsync(read);
        Assert.Equal(2, host.Replicas.ReadCollection("liked").Items.Length);
        Assert.Equal("old-token", host.Replicas.ReadConfirmedCollection("liked").WireRevision);
    }

    [Fact]
    public async Task Terminal_without_token_is_unverified()
    {
        var read = await Fetcher((_, _) => Ok(Page("", "", ("spotify:track:a", 1)).ToByteArray())).FetchWireSetAsync("collection", null);
        Assert.False(read.Verified);
        Assert.Null(read.Token);
        Assert.Single(read.Items);
    }

    [Fact]
    public async Task Mid_paging_failure_returns_partial_observations_without_deletion_authority()
    {
        int calls = 0;
        var read = await Fetcher((_, _) => ++calls == 1 ? Ok(Page("t", "next", ("spotify:track:a", 1)).ToByteArray())
            : throw new InvalidOperationException("lost page")).FetchWireSetAsync("collection", null);
        Assert.False(read.Verified);
        Assert.Single(read.Items);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_partial_success()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fetcher = new CollectionFetcher(new FakeExchange((_, _) => throw new OperationCanceledException(cancellation.Token)),
            () => "https://x", () => "bob");
        await Assert.ThrowsAsync<OperationCanceledException>(() => fetcher.FetchWireSetAsync("collection", null, cancellation.Token));
    }

    [Fact]
    public async Task Verified_sweep_keeps_recent_confirmation_and_pending_projection()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedCollectionAsync("collection", [new("spotify:track:old", false, 1), new("spotify:track:recent", false, NowMs - 1000)]);
        await host.Mutations.SaveAsync("liked", "spotify:track:pending", true);
        var read = await Fetcher((_, _) => Ok(Page("new", "").ToByteArray())).FetchWireSetAsync("collection", null);
        await host.Replicas.AdoptCollectionAsync(read);
        Assert.DoesNotContain(host.Replicas.ReadConfirmedCollection("liked").Items, x => x.Uri == "spotify:track:old");
        Assert.Contains(host.Replicas.ReadConfirmedCollection("liked").Items, x => x.Uri == "spotify:track:recent");
        Assert.Contains(host.Replicas.ReadCollection("liked").Items, x => x.Uri == "spotify:track:pending");
        Assert.DoesNotContain(host.Replicas.ReadConfirmedCollection("liked").Items, x => x.Uri == "spotify:track:pending");
    }

    [Fact]
    public async Task Reconcile_uses_the_same_verified_observation_commit_path()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedCollectionAsync("collection", [new("spotify:track:old", false, 1)]);
        var read = await Fetcher((_, _) => Ok(Page("new", "", ("spotify:track:new", 1)).ToByteArray()))
            .ReconcileWireSetAsync("collection", "test");
        await host.Replicas.AdoptCollectionAsync(read);
        Assert.Equal("spotify:track:new", Assert.Single(host.Replicas.ReadConfirmedCollection("liked").Items).Uri);
        Assert.Equal("new", host.Replicas.ReadConfirmedCollection("liked").WireRevision);
    }
}
