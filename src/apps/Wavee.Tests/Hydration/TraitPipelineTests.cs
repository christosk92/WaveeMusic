using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using CoreKind = Wavee.Core.EntityKind;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests.Hydration;

// ── TraitPipeline: the wire shape, the plan, the memo and the bulk window ────────────────────────────────────────────
// These run over the REAL transport stack (FakeExchange → ExtendedMetadataSource → ExtensionEtagCache → TraitPipeline)
// and decode the gzipped BatchedEntityRequest, because every claim worth pinning here is a claim about what actually
// went on the wire — "one POST", "every kind under each uri", "a local file uri never leaves the process". A mock
// cache would assert the pipeline's intentions instead of its output.
//
// The projector is a FAKE on purpose: this file pins the pipeline, and coupling it to the real projectors would mean a
// tempo-parsing change could fail a batching test.

public class TraitPipelineTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    // ── Harness ────────────────────────────────────────────────────────────────────────────────────────────────────
    sealed class Wire
    {
        public readonly List<Xm.BatchedEntityRequest> Requests = new();
        public readonly List<string?> FeatureIds = new();
        public FakeExchange Http = null!;

        /// <summary>Answers whatever was asked, per (uri, kind), and records the decoded request + attribution header.</summary>
        public static Wire Answering(Func<string, Xm.ExtensionKind, (int Status, ByteString? Payload)> answer,
                                     Func<int, bool>? failCall = null)
        {
            var wire = new Wire();
            wire.Http = new FakeExchange((req, call) =>
            {
                var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
                lock (wire.Requests)
                {
                    wire.Requests.Add(body);
                    wire.FeatureIds.Add(req.Headers.TryGetValue("client-feature-id", out var id) ? id : null);
                }
                if (failCall is not null && failCall(call)) return new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>());

                var response = new Xm.BatchedExtensionResponse();
                var byKind = new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();
                foreach (var entity in body.EntityRequest)
                    foreach (var query in entity.Query)
                    {
                        if (!byKind.TryGetValue(query.ExtensionKind, out var array))
                        {
                            array = new Xm.EntityExtensionDataArray { ExtensionKind = query.ExtensionKind };
                            byKind[query.ExtensionKind] = array;
                            response.ExtendedMetadata.Add(array);
                        }
                        var (status, payload) = answer(entity.EntityUri, query.ExtensionKind);
                        var data = new Xm.EntityExtensionData
                        {
                            EntityUri = entity.EntityUri,
                            Header = new Xm.EntityExtensionDataHeader { StatusCode = status, OfflineTtlInSeconds = 60 },
                        };
                        if (payload is not null) data.ExtensionData = new Any { Value = payload };
                        array.ExtensionData.Add(data);
                    }
                return new HttpResp(200, new Dictionary<string, string>(), response.ToByteArray());
            });
            return wire;
        }

        public static Wire Ok() => Answering((_, _) => (200, ByteString.CopyFromUtf8("payload")));

        public ExtensionEtagCache Cache() =>
            new(new ExtendedMetadataSource(Http, () => "https://spclient.test", () => Ctx), () => Ctx);

        /// <summary>Every (uri, kind) pair the wire was ever asked for, across all POSTs.</summary>
        public HashSet<(string Uri, Xm.ExtensionKind Kind)> Asked()
        {
            var asked = new HashSet<(string, Xm.ExtensionKind)>();
            foreach (var request in Requests)
                foreach (var entity in request.EntityRequest)
                    foreach (var query in entity.Query)
                        asked.Add((entity.EntityUri, query.ExtensionKind));
            return asked;
        }
    }

    /// <summary>Records what the pipeline asked it and answers however the test scripts it. Never touches real payloads
    /// — the projectors' own decoding is their own tests' business.</summary>
    sealed class FakeProjector : ITraitProjector
    {
        public TraitSet Trait { get; init; } = TraitSet.AudioAttributes;
        public Xm.ExtensionKind Wanted { get; init; } = Xm.ExtensionKind.AudioAttributesV2;
        public Xm.ExtensionKind[] CompanionKinds { get; init; } = Array.Empty<Xm.ExtensionKind>();
        public Func<CoreKind, bool> Applicability { get; init; } = _ => true;
        public Func<string, bool> Mark { get; init; } = _ => false;
        public Func<string, TraitPayloads, TraitOutcome>? Projection { get; init; }

        public readonly List<string> MarkCalls = new();
        public readonly List<string> ProjectCalls = new();
        public int CompleteCalls;
        public int ProjectsSeenAtComplete = -1;

        public Xm.ExtensionKind Kind => Wanted;
        public ReadOnlySpan<Xm.ExtensionKind> Companions => CompanionKinds;
        public bool AppliesTo(CoreKind kind) => Applicability(kind);

        public bool AlreadyHas(IStore store, string uri, DateTimeOffset now) { MarkCalls.Add(uri); return Mark(uri); }

        public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
        {
            ProjectCalls.Add(uri);
            var outcome = Projection is null
                ? (payloads.Missing(Wanted) ? TraitOutcome.Negative : TraitOutcome.Applied)
                : Projection(uri, payloads);
            if (outcome == TraitOutcome.Applied) batch.Write(s => s.Bump(uri));
            return outcome;
        }

        public ValueTask CompleteBatchAsync(TraitBatch batch, CancellationToken ct)
        {
            CompleteCalls++;
            ProjectsSeenAtComplete = ProjectCalls.Count;
            return default;
        }
    }

    /// <summary>Counts the store's coalesced bulk signals — the "did this pass cost the UI a full recompute?" assertion.</summary>
    sealed class BulkCounter : IObserver<StoreChange>, IDisposable
    {
        readonly IDisposable _sub;
        public int Bulks;
        public int PerUri;
        public BulkCounter(IStore store) => _sub = store.Changes.Subscribe(this);
        public void OnNext(StoreChange value) { if (value.IsBulk) Bulks++; else PerUri++; }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() => _sub.Dispose();
    }

    static TraitPipeline Pipeline(IStore store, ExtensionEtagCache cache, NegativeMemo memo, params ITraitProjector[] projectors)
        => new(store, cache, memo, projectors);

    // ── The wire shape ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneRequest_CarriesEveryWantedKind_UnderEachUri()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        var tempo = new FakeProjector { Trait = TraitSet.AudioAttributes, Wanted = Xm.ExtensionKind.AudioAttributesV2 };
        var video = new FakeProjector
        {
            Trait = TraitSet.Video,
            Wanted = Xm.ExtensionKind.VideoAssociations,
            CompanionKinds = [Xm.ExtensionKind.ConsumptionExperienceTrait],
        };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), tempo, video);

        await pipeline.EnsureAsync(["spotify:track:A", "spotify:track:B"],
            TraitSet.AudioAttributes | TraitSet.Video, TraitSurface.PlaylistOpen, TestContext.Current.CancellationToken);

        // ONE POST — the whole point. The four services this replaces would have sent two.
        Assert.Equal(1, wire.Http.Calls);
        var body = Assert.Single(wire.Requests);
        Assert.Equal(2, body.EntityRequest.Count);
        foreach (var entity in body.EntityRequest)
        {
            var kinds = entity.Query.Select(q => q.ExtensionKind).ToHashSet();
            // Both wanted kinds AND the video companion ride under the SAME uri group.
            Assert.Contains(Xm.ExtensionKind.AudioAttributesV2, kinds);
            Assert.Contains(Xm.ExtensionKind.VideoAssociations, kinds);
            Assert.Contains(Xm.ExtensionKind.ConsumptionExperienceTrait, kinds);
        }
    }

    [Fact]
    public async Task Marks_SuppressHeldKinds()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        var held = new FakeProjector
        {
            Wanted = Xm.ExtensionKind.AudioAttributesV2,
            Mark = uri => uri == "spotify:track:HELD",
        };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), held);

        await pipeline.EnsureAsync(["spotify:track:HELD", "spotify:track:WANT"],
            TraitSet.AudioAttributes, TraitSurface.PlaylistOpen, TestContext.Current.CancellationToken);

        var asked = wire.Asked();
        Assert.DoesNotContain(("spotify:track:HELD", Xm.ExtensionKind.AudioAttributesV2), asked);
        Assert.Contains(("spotify:track:WANT", Xm.ExtensionKind.AudioAttributesV2), asked);
        // A held uri is never even projected — the mark is the whole reason a warm page costs nothing.
        Assert.DoesNotContain("spotify:track:HELD", held.ProjectCalls);
    }

    [Fact]
    public async Task NonSpotifyUris_NeverReachTheWire()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        var projector = new FakeProjector { Wanted = Xm.ExtensionKind.AudioAttributesV2 };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), projector);

        // A queue with a local import in it: a local file IS a Track, so a kind test alone would have sent its path
        // (which is a b64url of the absolute path) to spclient.
        await pipeline.EnsureAsync(
            ["spotify:track:A", "wavee:local:file:QzpcbXVzaWNcYS5tcDM", "local:track:x", "fake:tr1", ""],
            TraitSet.AudioAttributes, TraitSurface.Queue, TestContext.Current.CancellationToken);

        var body = Assert.Single(wire.Requests);
        Assert.Equal("spotify:track:A", Assert.Single(body.EntityRequest).EntityUri);
    }

    // ── Applicability ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Applicability_ByKind()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        var publishing = new FakeProjector
        {
            Trait = TraitSet.Publishing,
            Wanted = Xm.ExtensionKind.PublishingMetadataTrait,
            Applicability = kind => TraitApplicability.Applies(Xm.ExtensionKind.PublishingMetadataTrait, kind),
        };
        var tempo = new FakeProjector
        {
            Trait = TraitSet.AudioAttributes,
            Wanted = Xm.ExtensionKind.AudioAttributesV2,
            Applicability = kind => TraitApplicability.Applies(Xm.ExtensionKind.AudioAttributesV2, kind),
        };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), publishing, tempo);

        await pipeline.EnsureAsync(
            ["spotify:track:T", "spotify:episode:E", "spotify:album:AL", "spotify:artist:AR"],
            TraitSet.Publishing | TraitSet.AudioAttributes, TraitSurface.AlbumOpen, TestContext.Current.CancellationToken);

        var asked = wire.Asked();
        // 183 is an ALBUM fact — a track's ©/℗ comes from its album, so asking per track is pure waste.
        Assert.Contains(("spotify:album:AL", Xm.ExtensionKind.PublishingMetadataTrait), asked);
        Assert.DoesNotContain(("spotify:track:T", Xm.ExtensionKind.PublishingMetadataTrait), asked);
        Assert.DoesNotContain(("spotify:artist:AR", Xm.ExtensionKind.PublishingMetadataTrait), asked);
        // 222 is a playable fact — and the EPISODE is asked exactly once rather than assumed away.
        Assert.Contains(("spotify:track:T", Xm.ExtensionKind.AudioAttributesV2), asked);
        Assert.Contains(("spotify:episode:E", Xm.ExtensionKind.AudioAttributesV2), asked);
        Assert.DoesNotContain(("spotify:album:AL", Xm.ExtensionKind.AudioAttributesV2), asked);

        // And the table itself, directly — it is the contract the real projectors consult.
        Assert.True(TraitApplicability.Applies(Xm.ExtensionKind.VisualIdentityTrait, CoreKind.Show));
        Assert.True(TraitApplicability.Applies(Xm.ExtensionKind.IdentityTrait, CoreKind.Playlist));
        Assert.False(TraitApplicability.Applies(Xm.ExtensionKind.VisualIdentityTrait, CoreKind.Unknown));
    }

    [Fact]
    public async Task Episodes_AreAskedOnce_AndA404IsHonored()
    {
        // The wire says "this episode has no tempo" — the ask-once contract: pay one request, then never again.
        var wire = Wire.Answering((uri, _) => uri.Contains(":episode:") ? (404, null) : (200, ByteString.CopyFromUtf8("p")));
        var store = new InMemoryStore();
        var memo = new NegativeMemo();
        var tempo = new FakeProjector
        {
            Wanted = Xm.ExtensionKind.AudioAttributesV2,
            Applicability = kind => TraitApplicability.Applies(Xm.ExtensionKind.AudioAttributesV2, kind),
        };
        var cache = wire.Cache();
        var pipeline = Pipeline(store, cache, memo, tempo);

        await pipeline.EnsureAsync(["spotify:episode:E"], TraitSet.AudioAttributes, TraitSurface.ShowOpen,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, wire.Http.Calls);
        Assert.Equal("spotify:episode:E", Assert.Single(tempo.ProjectCalls));
        // The 404 was projected as Negative and memoized — that IS the ask-once contract.
        Assert.True(memo.Contains("spotify:episode:E", Xm.ExtensionKind.AudioAttributesV2));

        await pipeline.EnsureAsync(["spotify:episode:E"], TraitSet.AudioAttributes, TraitSurface.ShowOpen,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, wire.Http.Calls);   // the memo answered — no second POST
    }

    [Fact]
    public void NegativeMemo_IsSharedAcrossKinds_AndBounded()
    {
        var memo = new NegativeMemo();
        memo.Add("spotify:track:A", Xm.ExtensionKind.VideoAssociations);

        // Per (uri, kind) — a "no video" must not answer for "no tempo".
        Assert.True(memo.Contains("spotify:track:A", Xm.ExtensionKind.VideoAssociations));
        Assert.False(memo.Contains("spotify:track:A", Xm.ExtensionKind.AudioAttributesV2));
        Assert.False(memo.Contains("spotify:track:B", Xm.ExtensionKind.VideoAssociations));

        // Bounded by STOPPING, not evicting: past the cap the etag cache's own 24h negative answers instead, which
        // costs no request either — whereas evicting would re-ask the oldest entries forever.
        for (int i = 0; i < NegativeMemo.Cap + 64; i++)
            memo.Add("spotify:track:bulk" + i, Xm.ExtensionKind.TrackDescriptor);
        Assert.Equal(NegativeMemo.Cap, memo.Count);
        Assert.True(memo.Contains("spotify:track:A", Xm.ExtensionKind.VideoAssociations));   // early entries survive
    }

    // ── Attribution ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TraitSurface.Recents, "mdata_esperanto")]
    [InlineData(TraitSurface.AlbumOpen, "track_metadata_loader")]
    [InlineData(TraitSurface.PlaylistOpen, "track_metadata_loader")]
    [InlineData(TraitSurface.None, null)]
    public async Task ClientFeatureId_PerSurface(TraitSurface surface, string? expected)
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        var projector = new FakeProjector { Wanted = Xm.ExtensionKind.AudioAttributesV2 };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), projector);

        await pipeline.EnsureAsync(["spotify:track:A"], TraitSet.AudioAttributes, surface,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(wire.FeatureIds));
        Assert.Equal(expected, surface.ClientFeatureId());
    }

    // ── Paging + the bulk window ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pages_AtMaxEntitiesPerRequest_OneBulkPerPage()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        using var bulks = new BulkCounter(store);
        var projector = new FakeProjector { Wanted = Xm.ExtensionKind.AudioAttributesV2 };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), projector);

        var uris = Enumerable.Range(0, MetadataChunking.MaxEntitiesPerRequest + 1)
                             .Select(i => "spotify:track:" + i).ToArray();
        await pipeline.EnsureAsync(uris, TraitSet.AudioAttributes, TraitSurface.PlaylistOpen,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, wire.Http.Calls);                        // 301 uris ⇒ two POSTs at the 300 ceiling
        Assert.Equal(2, bulks.Bulks);                            // one coalesced signal per page, never one per row
        Assert.Equal(0, bulks.PerUri);                           // every write rode inside a bulk window
        Assert.Equal(uris.Length, projector.ProjectCalls.Count);
        Assert.Equal(MetadataChunking.MaxEntitiesPerRequest, wire.Requests[0].EntityRequest.Count);
        Assert.Single(wire.Requests[1].EntityRequest);
    }

    [Fact]
    public async Task AllHits_EmitNoBulkSignal()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        using var bulks = new BulkCounter(store);
        // Nothing to write: the row already carries the facet, so the pass plans nothing at all.
        var held = new FakeProjector { Wanted = Xm.ExtensionKind.AudioAttributesV2, Mark = _ => true };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), held);

        await pipeline.EnsureAsync(["spotify:track:A", "spotify:track:B"], TraitSet.AudioAttributes,
            TraitSurface.PlaylistOpen, TestContext.Current.CancellationToken);

        Assert.Equal(0, wire.Http.Calls);
        Assert.Equal(0, bulks.Bulks);
        Assert.Empty(held.ProjectCalls);

        // And the payload-arrived-but-nothing-changed case: a request, but still no store signal.
        var unchanged = new FakeProjector
        {
            Wanted = Xm.ExtensionKind.TrackDescriptor,
            Trait = TraitSet.Descriptors,
            Projection = (_, _) => TraitOutcome.Unchanged,
        };
        var second = Pipeline(store, wire.Cache(), new NegativeMemo(), unchanged);
        await second.EnsureAsync(["spotify:track:A"], TraitSet.Descriptors, TraitSurface.PlaylistOpen,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, bulks.Bulks);
    }

    [Fact]
    public async Task NeverMintsARow()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        using var bulks = new BulkCounter(store);
        var memo = new NegativeMemo();
        var absent = new FakeProjector
        {
            Wanted = Xm.ExtensionKind.AudioAttributesV2,
            Projection = (_, _) => TraitOutcome.NotResident,
        };
        var pipeline = Pipeline(store, wire.Cache(), memo, absent);

        await pipeline.EnsureAsync(["spotify:track:GHOST"], TraitSet.AudioAttributes, TraitSurface.Queue,
            TestContext.Current.CancellationToken);

        // No row minted (a minted row is a titleless placeholder every surface then paints)…
        Assert.Null(store.GetTrack("spotify:track:GHOST"));
        Assert.Equal(0, bulks.Bulks);
        // …and NOT memoized: the answer is wanted the moment the row lands.
        Assert.False(memo.Contains("spotify:track:GHOST", Xm.ExtensionKind.AudioAttributesV2));
    }

    // ── Failure + ordering ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransportFailure_MemoizesNothing()
    {
        var wire = Wire.Answering((_, _) => (200, ByteString.CopyFromUtf8("p")), failCall: call => call == 1);
        var store = new InMemoryStore();
        var memo = new NegativeMemo();
        var projector = new FakeProjector { Wanted = Xm.ExtensionKind.AudioAttributesV2 };
        var cache = wire.Cache();
        var pipeline = Pipeline(store, cache, memo, projector);

        // Never throws out of the pipeline — traits are polish (design §1.3).
        await pipeline.EnsureAsync(["spotify:track:A"], TraitSet.AudioAttributes, TraitSurface.PlaylistOpen,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, wire.Http.Calls);
        Assert.Empty(projector.ProjectCalls);
        // "The network was down" is not "this entity has no such extension".
        Assert.False(memo.Contains("spotify:track:A", Xm.ExtensionKind.AudioAttributesV2));

        await pipeline.EnsureAsync(["spotify:track:A"], TraitSet.AudioAttributes, TraitSurface.PlaylistOpen,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, wire.Http.Calls);   // retried, and this time it lands
        Assert.Single(projector.ProjectCalls);
    }

    [Fact]
    public async Task CompleteBatch_RunsAfterProjection()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        var projector = new FakeProjector { Wanted = Xm.ExtensionKind.AudioAttributesV2 };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), projector);

        await pipeline.EnsureAsync(["spotify:track:A", "spotify:track:B", "spotify:track:C"],
            TraitSet.AudioAttributes, TraitSurface.PlaylistOpen, TestContext.Current.CancellationToken);

        Assert.Equal(1, projector.CompleteCalls);                       // once per page, not once per uri
        Assert.Equal(3, projector.ProjectsSeenAtComplete);              // and only after every uri was projected
    }

    [Fact]
    public async Task CompleteBatch_IsSkipped_ForProjectorsWithNoWorkOnThePage()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        var idle = new FakeProjector
        {
            Trait = TraitSet.Publishing,
            Wanted = Xm.ExtensionKind.PublishingMetadataTrait,
            Applicability = kind => kind == CoreKind.Album,
        };
        var busy = new FakeProjector { Wanted = Xm.ExtensionKind.AudioAttributesV2 };
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), idle, busy);

        await pipeline.EnsureAsync(["spotify:track:A"], TraitSet.Publishing | TraitSet.AudioAttributes,
            TraitSurface.PlaylistOpen, TestContext.Current.CancellationToken);

        Assert.Equal(0, idle.CompleteCalls);
        Assert.Equal(1, busy.CompleteCalls);
    }

    /// <summary>The projection sweep's bulk scope must be CLOSED before <c>CompleteBatchAsync</c> runs.
    ///
    /// <para><c>IStore.BeginBulk</c> suppression is store-WIDE, and the video projector's completion arm makes two
    /// network round trips (alias → TrackV4/212, then kind 99 over the canonical). Holding the page's scope across them
    /// silenced every change signal in the app — the now-playing fold, a save toggle, a playlist mutation — for the
    /// length of a POST, and delayed this page's own tints and tempos behind it. The service this replaced closed its
    /// bulk first (<c>SpotifyVideoService</c>: <c>using (bulk) {…}</c> then <c>await RecoverCanonicalAsync</c>), and
    /// this pins that ordering back.</para></summary>
    [Fact]
    public async Task CompleteBatch_RunsWithThePagesBulkScopeAlreadyPublished()
    {
        var wire = Wire.Ok();
        var store = new InMemoryStore();
        using var bulks = new BulkCounter(store);
        int bulksSeenAtComplete = -1;
        var projector = new CompletionWriter(() => bulksSeenAtComplete = bulks.Bulks);
        var pipeline = Pipeline(store, wire.Cache(), new NegativeMemo(), projector);

        await pipeline.EnsureAsync(["spotify:track:A", "spotify:track:B"], TraitSet.AudioAttributes,
            TraitSurface.PlaylistOpen, TestContext.Current.CancellationToken);

        // The sweep's writes are already PUBLISHED when completion starts — so a completion that awaits the network
        // does it with no store-wide suppression held.
        Assert.Equal(1, bulksSeenAtComplete);
        // And a completion write still coalesces into ONE further signal of its own (the recovery page), never one
        // per row and never zero.
        Assert.Equal(2, bulks.Bulks);
        Assert.Equal(0, bulks.PerUri);
    }

    /// <summary>A projector that writes from <see cref="ITraitProjector.CompleteBatchAsync"/> (the video recovery
    /// shape), with a hook to observe the store's signal state at that moment.</summary>
    sealed class CompletionWriter(Action onComplete) : ITraitProjector
    {
        public TraitSet Trait => TraitSet.AudioAttributes;
        public Xm.ExtensionKind Kind => Xm.ExtensionKind.AudioAttributesV2;
        public bool AppliesTo(CoreKind kind) => true;
        public bool AlreadyHas(IStore store, string uri, DateTimeOffset now) => false;

        public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
        {
            batch.Write(s => s.Bump(uri));
            return TraitOutcome.Applied;
        }

        public ValueTask CompleteBatchAsync(TraitBatch batch, CancellationToken ct)
        {
            onComplete();
            batch.Write(s => s.Bump("spotify:track:RECOVERED"));
            return default;
        }
    }
}
