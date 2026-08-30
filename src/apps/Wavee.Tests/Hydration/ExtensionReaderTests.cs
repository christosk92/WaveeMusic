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
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests.Hydration;

// ── ExtensionReader: the display-only read path ──────────────────────────────────────────────────────────────────────
// Same harness discipline as the pipeline tests — the real transport stack, and the gzipped request decoded — because
// what is being pinned is request COUNT and request SHAPE. The three failure modes these guard are the ones the four
// services being replaced each got wrong in a different way: a duplicate load per concurrent opener, a slot stranded by
// a synchronously-completing load, and a 404 re-asked on every drawer open.

public class ExtensionReaderTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    sealed class Wire
    {
        public readonly List<Xm.BatchedEntityRequest> Requests = new();
        public readonly List<string?> FeatureIds = new();
        public FakeExchange Http = null!;
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Gated;

        /// <summary>Holds every request until the test releases it — ASYNCHRONOUSLY, because <see cref="FakeExchange"/>
        /// answers on the calling thread and a synchronous block there would deadlock the very coalescing it is meant
        /// to expose (the loader would never get to publish its slot).</summary>
        sealed class GatedExchange(IHttpExchange inner, Task gate) : IHttpExchange
        {
            public async Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
            {
                await gate.ConfigureAwait(false);
                return await inner.SendAsync(req, ct).ConfigureAwait(false);
            }
        }

        public static Wire Answering(Func<string, Xm.ExtensionKind, string?, (int Status, string? Etag, ByteString? Payload)> answer,
                                     bool gated = false)
        {
            var wire = new Wire { Gated = gated };
            wire.Http = new FakeExchange((req, _) =>
            {
                var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
                lock (wire.Requests)
                {
                    wire.Requests.Add(body);
                    wire.FeatureIds.Add(req.Headers.TryGetValue("client-feature-id", out var id) ? id : null);
                }
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
                        var (status, etag, payload) = answer(entity.EntityUri, query.ExtensionKind,
                            query.HasEtag ? query.Etag : null);
                        var header = new Xm.EntityExtensionDataHeader { StatusCode = status, OfflineTtlInSeconds = 60 };
                        if (etag is not null) header.Etag = etag;
                        var data = new Xm.EntityExtensionData { EntityUri = entity.EntityUri, Header = header };
                        if (payload is not null) data.ExtensionData = new Any { Value = payload };
                        array.ExtensionData.Add(data);
                    }
                return new HttpResp(200, new Dictionary<string, string>(), response.ToByteArray());
            });
            return wire;
        }

        public static Wire Ok(string payload = "body", bool gated = false)
            => Answering((_, _, _) => (200, "v1", ByteString.CopyFromUtf8(payload)), gated);

        public ExtensionEtagCache Cache()
        {
            IHttpExchange http = Gated ? new GatedExchange(Http, Gate.Task) : Http;
            return new ExtensionEtagCache(new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx), () => Ctx);
        }
    }

    sealed record Answer(string Text);

    static Answer? Parse(ByteString bytes) => new(bytes.ToStringUtf8());

    // ── Coalescing + slot lifetime ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Coalesces_ConcurrentReads_ToOneRequest()
    {
        // The wire is HELD until every reader is attached, so the coalescing is real rather than an artefact of the
        // first read having already finished.
        var wire = Wire.Ok(gated: true);
        var reader = new ExtensionReader(wire.Cache(), new NegativeMemo());

        var reads = Enumerable.Range(0, 8)
            .Select(_ => reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
                TraitSurface.Credits, TestContext.Current.CancellationToken))
            .ToArray();
        wire.Gate.SetResult();
        var answers = await Task.WhenAll(reads);

        Assert.Equal(1, wire.Http.Calls);
        Assert.All(answers, a => Assert.Equal("body", a!.Text));
        Assert.Equal(0, reader.InFlight);
    }

    [Fact]
    public async Task SynchronousCompletion_DoesNotStrandTheSlot()
    {
        var wire = Wire.Ok();
        var cache = wire.Cache();
        var reader = new ExtensionReader(cache, new NegativeMemo());

        // First read primes the etag cache; the SECOND is the one that can complete synchronously inside the load
        // (the cache answers from its LRU). If TryRemove used the key-only overload — or ran before publishing — the
        // slot would sit resolved under the key and every later Revalidate would be answered by a stale task.
        var first = await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.Credits, TestContext.Current.CancellationToken);
        Assert.Equal("body", first!.Text);
        Assert.Equal(0, reader.InFlight);

        var revalidated = await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.Credits, TestContext.Current.CancellationToken, new ReadOptions(Revalidate: true));
        Assert.Equal("body", revalidated!.Text);
        Assert.Equal(0, reader.InFlight);

        // And a third revalidate still reaches the wire rather than being served by a stranded slot.
        await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.Credits, TestContext.Current.CancellationToken, new ReadOptions(Revalidate: true));
        Assert.Equal(3, wire.Http.Calls);
        Assert.Equal(0, reader.InFlight);
    }

    // ── Negatives ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A404_IsANullAnswer_NotReasked()
    {
        var wire = Wire.Answering((_, _, _) => (404, null, null));
        var memo = new NegativeMemo();
        var reader = new ExtensionReader(wire.Cache(), memo);

        var first = await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.Credits, TestContext.Current.CancellationToken);
        Assert.Null(first);
        Assert.Equal(1, wire.Http.Calls);
        // Shared with the row pipeline — a "no" learned by a drawer stops the row pass re-asking too.
        Assert.True(memo.Contains("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait));

        var second = await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.Credits, TestContext.Current.CancellationToken);
        Assert.Null(second);
        Assert.Equal(1, wire.Http.Calls);
    }

    [Fact]
    public async Task Revalidate_SendsTheEtag_And304KeepsTheAnswer()
    {
        string? conditionalEtag = null;
        var wire = Wire.Answering((_, _, etag) =>
        {
            if (etag is { Length: > 0 })
            {
                conditionalEtag = etag;
                return (304, "v1", null);   // not modified: no body on the wire at all
            }
            return (200, "v1", ByteString.CopyFromUtf8("body"));
        });
        var reader = new ExtensionReader(wire.Cache(), new NegativeMemo());

        var first = await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.TrackExpansion, TestContext.Current.CancellationToken);
        Assert.Equal("body", first!.Text);

        var refreshed = await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.TrackExpansion, TestContext.Current.CancellationToken, new ReadOptions(Revalidate: true));

        Assert.Equal(2, wire.Http.Calls);
        Assert.Equal("v1", conditionalEtag);      // MarkStale ran FIRST, so the refetch was conditional
        Assert.Equal("body", refreshed!.Text);    // and the 304 kept the payload the cache already held
    }

    // ── Seeding + the raw arm ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Seed_PublishesUnderASecondKey()
    {
        var wire = Wire.Ok();
        var reader = new ExtensionReader(wire.Cache(), new NegativeMemo());

        // The pre-release shape: ONE payload answers for the prerelease uri AND the album it becomes.
        var seeded = new Answer("prerelease");
        reader.Seed("spotify:album:AL", Xm.ExtensionKind.Prerelease, seeded);

        var read = await reader.ReadAsync("spotify:album:AL", Xm.ExtensionKind.Prerelease, Parse,
            TraitSurface.PreRelease, TestContext.Current.CancellationToken);

        Assert.Same(seeded, read);
        Assert.Equal(0, wire.Http.Calls);
    }

    [Fact]
    public async Task ReadRaw_MultiKind_OnePost()
    {
        var wire = Wire.Ok();
        var reader = new ExtensionReader(wire.Cache(), new NegativeMemo());

        // The expand drawer's four kinds for one track — one POST, one uri group, four queries.
        Xm.ExtensionKind[] kinds =
        [
            Xm.ExtensionKind.VideoAssociations, Xm.ExtensionKind.AudioAssociations,
            Xm.ExtensionKind.TrackDescriptor, Xm.ExtensionKind.AudioAttributesV2,
        ];
        var raw = await reader.ReadRawAsync(kinds.Select(k => ("spotify:track:A", k)).ToArray(),
            TraitSurface.TrackExpansion, TestContext.Current.CancellationToken);

        Assert.Equal(1, wire.Http.Calls);
        var entity = Assert.Single(Assert.Single(wire.Requests).EntityRequest);
        Assert.Equal("spotify:track:A", entity.EntityUri);
        Assert.Equal(kinds.Length, entity.Query.Count);
        foreach (var kind in kinds)
        {
            Assert.Contains(kind, entity.Query.Select(q => q.ExtensionKind));
            Assert.False(raw[("spotify:track:A", kind)].Missing);
        }
    }

    [Fact]
    public async Task ReadMany_OnePost_ForTheWholePage()
    {
        var wire = Wire.Ok();
        var reader = new ExtensionReader(wire.Cache(), new NegativeMemo());

        var uris = Enumerable.Range(0, 12).Select(i => "spotify:user:u" + i).ToArray();
        var answers = await reader.ReadManyAsync(uris, Xm.ExtensionKind.UserProfile, Parse,
            TraitSurface.UserProfiles, TestContext.Current.CancellationToken);

        Assert.Equal(1, wire.Http.Calls);
        Assert.Equal(uris.Length, answers.Count);

        // Second pass is answered entirely from the parsed cache — the point of caching the PARSED object.
        await reader.ReadManyAsync(uris, Xm.ExtensionKind.UserProfile, Parse, TraitSurface.UserProfiles,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, wire.Http.Calls);
    }

    // ── Attribution ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClientFeatureId_OnEveryArm()
    {
        var wire = Wire.Ok();
        var reader = new ExtensionReader(wire.Cache(), new NegativeMemo());
        var ct = TestContext.Current.CancellationToken;

        await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse, TraitSurface.Credits, ct);
        await reader.ReadManyAsync(["spotify:track:B"], Xm.ExtensionKind.CreditsV2Trait, Parse, TraitSurface.Recents, ct);
        await reader.ReadRawAsync([("spotify:track:C", Xm.ExtensionKind.CreditsV2Trait)], TraitSurface.UserProfiles, ct);

        Assert.Equal(3, wire.Http.Calls);
        Assert.Equal(TraitSurface.Credits.ClientFeatureId(), wire.FeatureIds[0]);
        Assert.Equal("mdata_esperanto", wire.FeatureIds[1]);
        Assert.Null(wire.FeatureIds[2]);   // UserProfiles is unattributed in the capture, and stays that way
    }

    [Fact]
    public async Task TransportFailure_IsNotAnAnswer()
    {
        int calls = 0;
        var http = new FakeExchange((_, _) =>
        {
            calls++;
            return calls == 1
                ? new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>())
                : new HttpResp(200, new Dictionary<string, string>(),
                    ExtensionResponseBody("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, 200, "body"));
        });
        var cache = new ExtensionEtagCache(new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx), () => Ctx);
        var memo = new NegativeMemo();
        var reader = new ExtensionReader(cache, memo);

        var failed = await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.Credits, TestContext.Current.CancellationToken);
        Assert.Null(failed);
        Assert.False(memo.Contains("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait));   // nothing sealed

        var retried = await reader.ReadAsync("spotify:track:A", Xm.ExtensionKind.CreditsV2Trait, Parse,
            TraitSurface.Credits, TestContext.Current.CancellationToken);
        Assert.Equal("body", retried!.Text);
        Assert.Equal(2, calls);
    }

    static byte[] ExtensionResponseBody(string uri, Xm.ExtensionKind kind, int status, string? payload)
    {
        var header = new Xm.EntityExtensionDataHeader { StatusCode = status, OfflineTtlInSeconds = 60 };
        var data = new Xm.EntityExtensionData { EntityUri = uri, Header = header };
        if (payload is not null) data.ExtensionData = new Any { Value = ByteString.CopyFromUtf8(payload) };
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        array.ExtensionData.Add(data);
        var response = new Xm.BatchedExtensionResponse();
        response.ExtendedMetadata.Add(array);
        return response.ToByteArray();
    }
}
