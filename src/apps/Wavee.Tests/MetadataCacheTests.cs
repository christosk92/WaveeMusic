using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

public class MetadataCacheTests
{
    static byte[] Request(int count = 1)
    {
        var batch = new Xm.BatchedEntityRequest { Header = new Xm.BatchedEntityRequestHeader { Country = "NL", Catalogue = "premium" } };
        for (int i = 0; i < count; i++) batch.EntityRequest.Add(new Xm.EntityRequest
        { EntityUri = "spotify:episode:test" + i, Query = { new Xm.ExtensionQuery { ExtensionKind = Xm.ExtensionKind.EpisodeV4 } } });
        return batch.ToByteArray();
    }

    static Spotify.Api.Result Reply(byte[] request, int status = 200, long ttl = 10)
    {
        var batch = Xm.BatchedEntityRequest.Parser.ParseFrom(request);
        var response = new Xm.BatchedExtensionResponse();
        var group = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.EpisodeV4 };
        response.ExtendedMetadata.Add(group);
        foreach (var entity in batch.EntityRequest)
        {
            var data = new Xm.EntityExtensionData
            {
                EntityUri = entity.EntityUri,
                Header = new Xm.EntityExtensionDataHeader { StatusCode = status, Etag = "v1", CacheTtlInSeconds = ttl },
            };
            if (status == 200) data.ExtensionData = new Any { TypeUrl = "test/episode", Value = ByteString.CopyFromUtf8("episode-payload") };
            group.ExtensionData.Add(data);
        }
        return new Spotify.Api.Result(200, response.ToByteArray());
    }

    [Fact]
    public void Fresh_entries_avoid_a_second_network_request()
    {
        var cache = new Spotify.Api.MetadataCache();
        int sent = 0;
        Spotify.Api.Result Send(byte[] body) { sent++; return Reply(body); }
        byte[] request = Request();
        var first = cache.Execute(request, "account", 1000, Send);
        var second = cache.Execute(request, "account", 9999, Send);
        Assert.Equal(1, sent);
        Assert.Equal(first.Body, second.Body);
    }

    [Fact]
    public void Expired_entries_send_etag_and_304_reuses_the_authoritative_payload()
    {
        var cache = new Spotify.Api.MetadataCache();
        byte[] request = Request();
        cache.Execute(request, "account", 1000, body => Reply(body, ttl: 1));
        var answer = cache.Execute(request, "account", 2001, body =>
        {
            var sent = Xm.BatchedEntityRequest.Parser.ParseFrom(body);
            Assert.Equal("v1", sent.EntityRequest[0].Query[0].Etag);
            return Reply(body, status: 304);
        });
        var data = Xm.BatchedExtensionResponse.Parser.ParseFrom(answer.Body).ExtendedMetadata[0].ExtensionData[0];
        Assert.Equal(200, data.Header.StatusCode);
        Assert.Equal("episode-payload", data.ExtensionData.Value.ToStringUtf8());
    }

    [Fact]
    public void Batches_never_exceed_300_entities_and_return_every_requested_entity()
    {
        var cache = new Spotify.Api.MetadataCache();
        var sizes = new List<int>();
        var result = cache.Execute(Request(601), "account", 1000, body =>
        {
            sizes.Add(Xm.BatchedEntityRequest.Parser.ParseFrom(body).EntityRequest.Count);
            return Reply(body);
        });
        Assert.Equal(new[] { 300, 300, 1 }, sizes);
        Assert.Equal(601, Xm.BatchedExtensionResponse.Parser.ParseFrom(result.Body).ExtendedMetadata[0].ExtensionData.Count);
    }

    [Fact]
    public void Cache_does_not_cross_account_epochs()
    {
        var cache = new Spotify.Api.MetadataCache();
        cache.Execute(Request(), "first-account", 1000, body => Reply(body));
        int calls = 0;
        cache.Execute(Request(), "second-account", 1100, body => { calls++; return Reply(body); });
        Assert.Equal(1, calls);
    }
    [Fact]
    public void Explicit_invalidation_reloads_before_ttl_expiry()
    {
        var cache = new Spotify.Api.MetadataCache();
        int sent = 0;
        Spotify.Api.Result Send(byte[] body) { sent++; return Reply(body); }
        cache.Execute(Request(), "account", 1000, Send);
        cache.Invalidate("spotify:episode:test0");
        cache.Execute(Request(), "account", 1100, Send);
        Assert.Equal(2, sent);
    }

    static byte[] RequestFor(params int[] ids)
    {
        var batch = new Xm.BatchedEntityRequest { Header = new Xm.BatchedEntityRequestHeader { Country = "NL", Catalogue = "premium" } };
        foreach (int i in ids) batch.EntityRequest.Add(new Xm.EntityRequest
        { EntityUri = "spotify:episode:test" + i, Query = { new Xm.ExtensionQuery { ExtensionKind = Xm.ExtensionKind.EpisodeV4 } } });
        return batch.ToByteArray();
    }

    /// <summary>The uris a send carried — what the cache did NOT answer itself.</summary>
    static List<string> Asked(byte[] body)
        => Xm.BatchedEntityRequest.Parser.ParseFrom(body).EntityRequest.Select(e => e.EntityUri).ToList();

    [Fact]
    public void The_byte_budget_bounds_what_the_cache_holds()
    {
        // Room for roughly three entries: a 601-entity answer still comes back whole, the cache just keeps the newest few.
        var probe = new Spotify.Api.MetadataCache();
        probe.Execute(RequestFor(0), "account", 1000, body => Reply(body, ttl: 600));
        long one = probe.Bytes;
        Assert.True(one > 0);

        long budget = one * 3 + 64;                          // the 601 uris run a few characters longer than "test0"
        var cache = new Spotify.Api.MetadataCache(budgetBytes: budget);
        var result = cache.Execute(Request(601), "account", 1000, body => Reply(body, ttl: 600));

        Assert.Equal(601, Xm.BatchedExtensionResponse.Parser.ParseFrom(result.Body).ExtendedMetadata[0].ExtensionData.Count);
        Assert.True(cache.Bytes <= budget, $"held {cache.Bytes} B against a {budget} B budget");
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void Eviction_takes_the_least_recently_used_entry()
    {
        var probe = new Spotify.Api.MetadataCache();
        probe.Execute(RequestFor(0), "account", 1000, body => Reply(body, ttl: 600));
        var cache = new Spotify.Api.MetadataCache(budgetBytes: probe.Bytes * 2);
        cache.Execute(RequestFor(0), "account", 1000, body => Reply(body, ttl: 600));
        cache.Execute(RequestFor(1), "account", 1001, body => Reply(body, ttl: 600));
        cache.Execute(RequestFor(0), "account", 1002, body => throw new InvalidOperationException("0 is fresh"));   // a hit: 0 is now the most recent

        cache.Execute(RequestFor(2), "account", 1003, body => Reply(body, ttl: 600));                                // over budget: 1 goes, not 0

        var asked = new List<string>();
        cache.Execute(RequestFor(0, 1, 2), "account", 1004, body => { asked.AddRange(Asked(body)); return Reply(body, ttl: 600); });
        Assert.Equal(new[] { "spotify:episode:test1" }, asked);
    }

    [Fact]
    public void Trim_sheds_down_to_the_target_and_reports_what_it_freed()
    {
        var cache = new Spotify.Api.MetadataCache();
        cache.Execute(Request(50), "account", 1000, body => Reply(body, ttl: 600));
        long before = cache.Bytes;

        long freed = cache.Trim(before / 2);

        Assert.True(cache.Bytes <= before / 2);
        Assert.Equal(before - cache.Bytes, freed);
        Assert.Equal(0, cache.Trim(before));                 // already under the target: nothing to give back
        Assert.True(cache.Trim(0) > 0);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void An_evicted_entry_is_asked_again_without_an_etag()
    {
        var cache = new Spotify.Api.MetadataCache();
        cache.Execute(Request(), "account", 1000, body => Reply(body, ttl: 1));
        cache.Trim(0);
        var answer = cache.Execute(Request(), "account", 2001, body =>
        {
            Assert.True(string.IsNullOrEmpty(Xm.BatchedEntityRequest.Parser.ParseFrom(body).EntityRequest[0].Query[0].Etag));   // no payload to 304 against
            return Reply(body);
        });
        var data = Xm.BatchedExtensionResponse.Parser.ParseFrom(answer.Body).ExtendedMetadata[0].ExtensionData[0];
        Assert.Equal("episode-payload", data.ExtensionData.Value.ToStringUtf8());
    }
}
