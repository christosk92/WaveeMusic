using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

public class MetadataChunkingTests
{
    // The unconditional Ranges(EntityRef[]) overload died with MetadataService's raw bulk arm (hydration-facade-plan.md
    // 1.6): every catalogue read is now conditional, so ExtensionRanges is THE chunker and these cases moved onto it.

    // ── the ONE chunker — ExtensionRanges ────────────────────────────────────────────────────────────────────────────

    static IReadOnlyList<(string Uri, Xm.ExtensionKind Kind, string? Etag)> Queries(int uris, int kindsPerUri)
    {
        var list = new List<(string, Xm.ExtensionKind, string?)>(uris * kindsPerUri);
        for (int u = 0; u < uris; u++)
            for (int k = 0; k < kindsPerUri; k++)
                list.Add(($"spotify:track:t{u}", (Xm.ExtensionKind)(k + 1), null));
        return list;
    }

    [Fact]
    public void ExtensionRanges_CapsByDISTINCTUris_NotQueryCount()
    {
        // Four kinds per uri is the RowBundle shape: 300 uris x 4 = 1200 queries must still be ONE POST, because the
        // server's ceiling is entities, not queries.
        var ranges = MetadataChunking.ExtensionRanges(Queries(MetadataChunking.MaxEntitiesPerRequest, 4)).ToList();
        Assert.Single(ranges);
        Assert.Equal((0, MetadataChunking.MaxEntitiesPerRequest * 4), ranges[0]);
    }

    [Fact]
    public void ExtensionRanges_NeverSplitsAUrisKindsAcrossChunks()
    {
        var reqs = Queries(MetadataChunking.MaxEntitiesPerRequest + 1, 4);
        var ranges = MetadataChunking.ExtensionRanges(reqs).ToList();

        Assert.Equal(2, ranges.Count);
        Assert.Equal(reqs.Count, ranges.Sum(r => r.Count));
        foreach (var (start, count) in ranges)
        {
            // A chunk boundary must fall on a uri boundary — a uri sent in two POSTs comes back as two partial
            // entity groups and the projector sees it twice.
            if (start > 0) Assert.NotEqual(reqs[start - 1].Uri, reqs[start].Uri);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = start; i < start + count; i++) seen.Add(reqs[i].Uri);
            Assert.True(seen.Count <= MetadataChunking.MaxEntitiesPerRequest);
        }
    }

    [Fact]
    public void ExtensionRanges_RespectsBodyBudget_AndCoversAllContiguously()
    {
        var reqs = Queries(200, 2);
        var ranges = MetadataChunking.ExtensionRanges(reqs, maxBodyBytes: 400, headerBytes: 0).ToList();

        Assert.True(ranges.Count > 1);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(reqs.Count, ranges.Sum(r => r.Count));
        for (int i = 1; i < ranges.Count; i++)
            Assert.Equal(ranges[i - 1].Start + ranges[i - 1].Count, ranges[i].Start);   // contiguous
    }

    [Fact]
    public void ExtensionRanges_Packs10kTracks_IntoWholeEntityPages()
    {
        // Body size alone would pack all 10k into ONE body (~300 KB < 4 MB); the ENTITY cap is the other bound, and it
        // is the one the server actually enforces - so 10k goes out as ceil(10000/300) full pages.
        // (Ported from Ranges_Packs10kTracks_IntoWholeEntityPages when the EntityRef overload was deleted.)
        var reqs = Queries(10_000, 1);
        var ranges = MetadataChunking.ExtensionRanges(reqs).ToList();
        Assert.Equal(34, ranges.Count);
        Assert.Equal((0, MetadataChunking.MaxEntitiesPerRequest), ranges[0]);
        Assert.Equal(10_000, ranges.Sum(r => r.Count));
        foreach (var (_, count) in ranges) Assert.True(count <= MetadataChunking.MaxEntitiesPerRequest);
    }

    [Fact]
    public void ExtensionRanges_EntityCap_IsExactAtTheBoundary()
    {
        Assert.Single(MetadataChunking.ExtensionRanges(Queries(MetadataChunking.MaxEntitiesPerRequest, 1)));
        Assert.Equal(2, MetadataChunking.ExtensionRanges(Queries(MetadataChunking.MaxEntitiesPerRequest + 1, 1)).Count());
    }

    [Fact]
    public void ExtensionRanges_OversizedEntity_GetsItsOwnChunk()
    {
        // Ported from Ranges_OversizedEntity_GetsItsOwnChunk: a single query wider than the whole budget must still go
        // out rather than wedge the pass - a chunk never splits below one uri.
        var reqs = new (string, Xm.ExtensionKind, string?)[]
        {
            ("spotify:track:a", Xm.ExtensionKind.TrackV4, null),
            ("spotify:track:" + new string('x', 5000), Xm.ExtensionKind.TrackV4, null),
            ("spotify:track:b", Xm.ExtensionKind.TrackV4, null),
        };
        Assert.Equal(3, MetadataChunking.ExtensionRanges(reqs, maxBodyBytes: 100, headerBytes: 0).Count());
    }

    [Fact]
    public void ExtensionRanges_Empty_YieldsNothing()
        => Assert.Empty(MetadataChunking.ExtensionRanges(System.Array.Empty<(string, Xm.ExtensionKind, string?)>()));
}

public class HttpCompressionTests
{
    [Fact]
    public void Gzip_RoundTrips_AndShrinksRepetitiveBody()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("spotify:track:abcdef ", 1000)));
        var z = HttpCompression.Gzip(data);
        Assert.True(z.Length < data.Length);                 // a batched request body compresses well
        Assert.Equal(data, HttpCompression.Gunzip(z));
    }
}

// MetadataServiceTests is DELETED with MetadataService itself (hydration-facade-plan.md 1.6). Its cases live on:
//   SyncAll_HandsTheWholeBatchToTheSource / _ProjectsEveryEntity -> XmCatalogFetchTests.MixedKinds_RideOnePost +
//     .Landed_IsWhatProjected_NotWhatWasAsked (one POST per chunk, landed = what a projection actually wrote)
//   SyncAll_PartialCache / _FullyCached / Use_FetchesOnce      -> XmCatalogFetchTests.SecondPass_IsServedFromTheEtagCache
//     + HydrationLedgerTests.Seal_SealsEveryRungUpToReached / .RunOnce_CoalescesConcurrentCallers
//   SyncAll_SealsOnlyProjectedUris_OmittedStayUnsealed        -> HydrationLedgerTests.Seal_AboveReached_IsExhausted_NotFresh
//   MarkStale_ForcesRefetchOnNextSync                         -> HydrationLedgerTests.Invalidate_UnsealsEveryRung

public class StoreBulkTests
{
    sealed class CountObserver : IObserver<StoreChange>
    {
        public int Count;
        public void OnNext(StoreChange v) => Count++;
        public void OnError(Exception e) { }
        public void OnCompleted() { }
    }

    [Fact]
    public void BeginBulk_CoalescesManyWrites_IntoOneSignal()
    {
        var store = new InMemoryStore();
        var obs = new CountObserver();
        using var sub = store.Changes.Subscribe(obs);
        obs.Count = 0;   // ignore the BehaviorSubject replay on subscribe
        using (store.BeginBulk())
            for (int i = 0; i < 1000; i++)
                store.UpsertTrack(new Track("t" + i, "spotify:track:t" + i, "T", [], new AlbumRef("", "", ""), 0, false, null));
        Assert.Equal(1, obs.Count);                                 // ONE signal, not 1000
        Assert.Equal(1000, store.QueryTracks(limit: 5000).Count);   // all the data is present
    }
}
