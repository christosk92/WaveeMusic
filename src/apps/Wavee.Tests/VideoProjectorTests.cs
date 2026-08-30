using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Hydration.Projectors;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

// EntityKind: the ONE uri vocabulary (Wavee.Core), not Backend.Metadata's thin transport projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.Tests;

// ── The kind-99 projector (design §2.4), ported from VideoAssociationTests ────────────────────────────────────────────
// Everything the old service tests pinned about the FOLD and about alias recovery, restated against the projector
// contract: a TraitBatch over an InMemoryStore and hand-built CachedExtension payloads, no transport. What is NOT here
// is the request framing (the 300-slice, the conditional etag, the co-batched 182 query) — the pipeline owns all three
// now, so those assertions belong to its own tests rather than being re-pinned per projector.
public class VideoProjectorTests
{
    static CancellationToken CT => TestContext.Current.CancellationToken;

    // Real captured VIDEO_ASSOCIATIONS payload for spotify:track:2ZTU8atPwouhoQSvxv9aQj → associated video track
    // 3dzYeVS4L1mfAdqlxYxB12 with three file variants (2560x1440 / 1280x720 / 2560x1440).
    const string RealPayloadB64 =
        "CogBCiRzcG90aWZ5OnRyYWNrOjNkelllVlM0TDFtZkFkcWx4WXhCMTISYAoeChSrZ0LTAABTt1GrEGocjt1j+pNFMBAAGIAUIKALCh4KFKtnQtMAAFK3UasQahyO3WP6k0UwEAIYgAog0AUKHgoUq2dC0wAAU7dRqxBqHI7dY/qTRTAQBBiAFCCgCw==";
    const string GidHex = "3c14b1c9a7d94f0e9d2b8a6f5e4c3b2a";

    // ── the fold ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fold_WritesThePlaneFromABareAnswer_WithNoTrackRowInvolved()
    {
        var store = new InMemoryStore();   // deliberately NO track row upserted — the plane needs none
        VideoProjector.Fold(store, "spotify:track:2ZTU8atPwouhoQSvxv9aQj",
            Ext(ByteString.FromBase64(RealPayloadB64), etag: "etag1", ttl: 2592000), DateTimeOffset.UtcNow);

        var a = store.GetVideoAssociation("spotify:track:2ZTU8atPwouhoQSvxv9aQj");
        Assert.NotNull(a);
        Assert.True(a!.HasVideo);
        Assert.Equal("spotify:track:3dzYeVS4L1mfAdqlxYxB12", a.CounterpartUri);
        Assert.Equal(3, a.Files.Count);
        // The three file variants, verbatim off the wire (this subsumes the old standalone parse test): the hex file
        // ids, the variant discriminator and the dimensions all survive the fold unchanged.
        Assert.Equal(("ab6742d3000053b751ab106a1c8edd63fa934530", 0, 2560, 1440),
            (a.Files[0].FileIdHex, a.Files[0].Variant, a.Files[0].Width, a.Files[0].Height));
        Assert.Equal(("ab6742d3000052b751ab106a1c8edd63fa934530", 2, 1280, 720),
            (a.Files[1].FileIdHex, a.Files[1].Variant, a.Files[1].Width, a.Files[1].Height));
        Assert.Equal((4, 2560, 1440), (a.Files[2].Variant, a.Files[2].Width, a.Files[2].Height));
        Assert.Equal("etag1", a.Etag);
        Assert.Equal(2592000, a.OfflineTtlSeconds);
    }

    [Fact]
    public void Fold_AMissingAnswerWritesTheNegative()
    {
        var store = new InMemoryStore();
        Assert.True(VideoProjector.Fold(store, "spotify:track:X", Missing(), DateTimeOffset.UtcNow));

        var a = store.GetVideoAssociation("spotify:track:X");
        Assert.NotNull(a);
        Assert.False(a!.HasVideo);   // negative cached, so a list realize stops re-asking (for 30 minutes)
    }

    [Fact]
    public void Project_AMissingNeverDowngradesAResidentPositive()
    {
        // D9. Recovery lands a positive under an ALIAS; the alias's own kind-99 stays a 404 forever, so a Missing that
        // overwrote it made the row light up and go dark again on the very next pass.
        var store = new InMemoryStore();
        var positive = new VideoAssociation("spotify:track:ALIAS", true, "spotify:track:VID",
            [new VideoFileRef("abcd", 0, 2560, 1440)], null, DateTimeOffset.UtcNow.AddHours(-12), 2592000);
        store.UpsertVideoAssociation(positive);

        var now = DateTimeOffset.UtcNow;
        VideoProjector.Fold(store, "spotify:track:ALIAS", Missing(), now);

        var a = store.GetVideoAssociation("spotify:track:ALIAS")!;
        Assert.True(a.HasVideo);
        Assert.Equal("spotify:track:VID", a.CounterpartUri);
        Assert.Single(a.Files);
        Assert.Equal(now, a.FetchedAt);   // the freshness is refreshed instead — the re-ask stops either way
    }

    [Fact]
    public void Project_AMissingLeavesABridgedPositiveAlone()
    {
        // The plane answered under the track's CanonicalUri (the store's miss-bridge). Writing anything under the alias
        // would SHADOW that bridge with a negative, so nothing is written at all.
        var store = new InMemoryStore();
        store.UpsertVideoAssociation(new VideoAssociation("spotify:track:CANON", true, "spotify:track:VID",
            [new VideoFileRef("abcd", 0, 2560, 1440)], "etag", DateTimeOffset.UtcNow, 2592000));
        store.UpsertTrack(Trk("ALIAS") with { CanonicalUri = "spotify:track:CANON" });

        Assert.False(VideoProjector.Fold(store, "spotify:track:ALIAS", Missing(), DateTimeOffset.UtcNow));
        Assert.True(store.GetVideoAssociation("spotify:track:ALIAS")!.HasVideo);   // still the bridged answer
    }

    // The bridge itself (ported from VideoAssociationTests): the projector's alias handling is only correct because the
    // STORE resolves a miss through Track.CanonicalUri, so that hop is pinned here rather than assumed.
    [Fact]
    public void GetVideoAssociation_MissBridge_RetriesCanonicalUri()
    {
        var store = new InMemoryStore();
        store.UpsertVideoAssociation(new VideoAssociation("spotify:track:CANON", true, "spotify:track:VID",
            [new VideoFileRef("abcd", 0, 2560, 1440)], "etag", DateTimeOffset.UtcNow, 2592000));
        store.UpsertTrack(Trk("ALIAS") with { CanonicalUri = "spotify:track:CANON" });

        Assert.Null(store.GetVideoAssociation("spotify:track:MISSING"));   // no row, no bridge, no answer
        var bridged = store.GetVideoAssociation("spotify:track:ALIAS");
        Assert.NotNull(bridged);
        Assert.Equal("spotify:track:CANON", bridged!.Uri);
        Assert.True(bridged.HasVideo);
    }

    [Fact]
    public void AlreadyHas_IsThePlanesOwnVerdictShapedFreshness()
    {
        var store = new InMemoryStore();
        var p = new VideoProjector(new FakeReader());
        var now = DateTimeOffset.UtcNow;

        Assert.False(p.AlreadyHas(store, "spotify:track:a", now));                       // nothing yet

        store.UpsertVideoAssociation(VideoAssociation.None("spotify:track:a", null, now.AddMinutes(-5), 0));
        Assert.True(p.AlreadyHas(store, "spotify:track:a", now));                        // negative: 30-minute window
        Assert.False(p.AlreadyHas(store, "spotify:track:a", now.AddMinutes(45)));        // …then re-askable

        store.UpsertVideoAssociation(new VideoAssociation("spotify:track:b", true, "spotify:track:v", [], null, now, 0));
        Assert.True(p.AlreadyHas(store, "spotify:track:b", now.AddHours(5)));            // positive: 6 hours
        Assert.False(p.AlreadyHas(store, "spotify:track:b", now.AddHours(7)));
    }

    [Fact]
    public void AppliesTo_IsPlayablesOnly_WithEpisodeAskOnce()
    {
        var p = new VideoProjector(new FakeReader());
        Assert.True(p.AppliesTo(EntityKind.Track));
        Assert.True(p.AppliesTo(EntityKind.Episode));   // ask once, honour the 404
        Assert.False(p.AppliesTo(EntityKind.Album));
        Assert.False(p.AppliesTo(EntityKind.Artist));
        Assert.False(p.AppliesTo(EntityKind.Playlist));
        Assert.Equal(Xm.ExtensionKind.ConsumptionExperienceTrait, Assert.Single(p.Companions.ToArray()));
    }

    // ── the 182 recovery gate ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_A404TheConsumptionTraitContradicts_QueuesTheAliasForRecovery()
    {
        var (store, batch) = Batch();
        var p = new VideoProjector(new FakeReader());

        var outcome = p.Project(batch, "spotify:track:ALIAS",
            Payloads("spotify:track:ALIAS", (Xm.ExtensionKind.VideoAssociations, Missing()),
                                            (Xm.ExtensionKind.ConsumptionExperienceTrait, Ext(CePayload(hasVideo: true)))));

        Assert.Equal(TraitOutcome.Applied, outcome);   // the negative IS a write; its own window stops the re-ask
        Assert.Equal(["spotify:track:ALIAS"], batch.FollowUp);
        Assert.False(store.GetVideoAssociation("spotify:track:ALIAS")!.HasVideo);
    }

    [Fact]
    public void Project_A404WithoutTheConsumptionHint_QueuesNothing()
    {
        var (_, batch) = Batch();
        var p = new VideoProjector(new FakeReader());

        p.Project(batch, "spotify:track:NONE",
            Payloads("spotify:track:NONE", (Xm.ExtensionKind.VideoAssociations, Missing()),
                                           (Xm.ExtensionKind.ConsumptionExperienceTrait, Ext(CePayload(hasVideo: false)))));

        Assert.Empty(batch.FollowUp);   // a plain "no video" must not cost a recovery pass
    }

    [Fact]
    public void Project_APositiveNeverQueuesRecovery()
    {
        var (_, batch) = Batch();
        var p = new VideoProjector(new FakeReader());

        p.Project(batch, "spotify:track:HAS",
            Payloads("spotify:track:HAS", (Xm.ExtensionKind.VideoAssociations, Ext(VaPayload("spotify:track:VID"))),
                                          (Xm.ExtensionKind.ConsumptionExperienceTrait, Ext(CePayload(hasVideo: true)))));

        Assert.Empty(batch.FollowUp);   // kind 182 is consulted ONLY for a kind-99 miss
    }

    [Fact]
    public void Project_AnUnansweredUriIsNotResident_AndWritesNothing()
    {
        // ABSENT IS NOT MISSING: a uri the response omitted has not been answered, and a memo on it is a 24h wedge.
        var (store, batch) = Batch();
        var p = new VideoProjector(new FakeReader());

        Assert.Equal(TraitOutcome.NotResident, p.Project(batch, "spotify:track:X", Payloads("spotify:track:X")));
        Assert.Null(store.GetVideoAssociation("spotify:track:X"));
        Assert.Equal(0, batch.Writes);
    }

    // ── canonical recovery (alias/relinked ids 404 on kind 99) ───────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteBatch_RecoversThroughTheCanonicalUri_AndStoresUnderTheAlias()
    {
        var (store, batch) = Batch();
        store.UpsertTrack(Trk("ALIAS"));
        var reader = new FakeReader();
        reader.Answers[("spotify:track:ALIAS", Xm.ExtensionKind.TrackV4)] =
            Ext(TrackV4Payload("spotify:track:CANON"));
        reader.Answers[("spotify:track:ALIAS", Xm.ExtensionKind.PlaybackTrait)] =
            Ext(PlaybackTraitPayload(GidHex));
        reader.Answers[("spotify:track:CANON", Xm.ExtensionKind.VideoAssociations)] =
            Ext(VaPayload("spotify:track:VID", ("ab6742d3000053b751ab106a1c8edd63fa934530", 0, 2560, 1440)), etag: "etagCANON");

        var p = new VideoProjector(reader);
        batch.FollowUp.Add("spotify:track:ALIAS");
        await p.CompleteBatchAsync(batch, CT);

        Assert.Equal(2, reader.Reads.Count);   // canonical lookup (TrackV4+212) → kind 99 on the canonical id

        var a = store.GetVideoAssociation("spotify:track:ALIAS");   // keyed by the REQUESTED uri, not the canonical one
        Assert.NotNull(a);
        Assert.True(a!.HasVideo);
        Assert.Equal("spotify:track:VID", a.CounterpartUri);
        Assert.Equal("ab6742d3000053b751ab106a1c8edd63fa934530", Assert.Single(a.Files).FileIdHex);
        Assert.Equal(GidHex, a.VideoGidHex);   // kind 212 field 2 → Connect's associated_video_id
        Assert.Null(a.Etag);                   // the canonical entity's etag must never ride the alias row
        Assert.Null(store.GetVideoAssociation("spotify:track:CANON"));
        Assert.Equal("spotify:track:CANON", store.GetTrack("spotify:track:ALIAS")!.CanonicalUri);
    }

    [Fact]
    public async Task CompleteBatch_UsesTheResidentCanonicalUri_WithoutReReadingTrackV4()
    {
        // ProjectTrack already stamps Track.CanonicalUri from the SAME TrackV4 field, so a resident row answers for free.
        var (store, batch) = Batch();
        store.UpsertTrack(Trk("ALIAS") with { CanonicalUri = "spotify:track:CANON" });
        var reader = new FakeReader();
        reader.Answers[("spotify:track:CANON", Xm.ExtensionKind.VideoAssociations)] =
            Ext(VaPayload("spotify:track:VID"));

        var p = new VideoProjector(reader);
        batch.FollowUp.Add("spotify:track:ALIAS");
        await p.CompleteBatchAsync(batch, CT);

        var read = Assert.Single(reader.Reads);
        Assert.Equal([("spotify:track:CANON", Xm.ExtensionKind.VideoAssociations)], read);
        Assert.True(store.GetVideoAssociation("spotify:track:ALIAS")!.HasVideo);
    }

    [Fact]
    public async Task CompleteBatch_TriesAnAliasAtMostOncePerSession()
    {
        var (store, batch) = Batch();
        store.UpsertTrack(Trk("ALIAS"));
        var reader = new FakeReader();   // answers nothing: the alias has no canonical, so the pass dead-ends

        var p = new VideoProjector(reader);
        batch.FollowUp.Add("spotify:track:ALIAS");
        await p.CompleteBatchAsync(batch, CT);
        Assert.Single(reader.Reads);

        using var second = new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.PlaylistOpen);
        second.FollowUp.Add("spotify:track:ALIAS");
        await p.CompleteBatchAsync(second, CT);

        Assert.Single(reader.Reads);   // the second page re-listing the same relinked track costs nothing
    }

    [Fact]
    public async Task CompleteBatch_ACanonicalWithoutAVideo_LeavesTheAliasNegative()
    {
        var (store, batch) = Batch();
        store.UpsertTrack(Trk("ALIAS") with { CanonicalUri = "spotify:track:CANON" });
        store.UpsertVideoAssociation(VideoAssociation.None("spotify:track:ALIAS", null, DateTimeOffset.UtcNow, 0));
        var reader = new FakeReader();
        reader.Answers[("spotify:track:CANON", Xm.ExtensionKind.VideoAssociations)] = Missing();

        var p = new VideoProjector(reader);
        batch.FollowUp.Add("spotify:track:ALIAS");
        await p.CompleteBatchAsync(batch, CT);

        Assert.False(store.GetVideoAssociation("spotify:track:ALIAS")!.HasVideo);
        Assert.Equal(0, batch.Writes);   // only a real hit counts
    }

    [Fact]
    public async Task CompleteBatch_WithNoFollowUps_ReadsNothing()
    {
        var (_, batch) = Batch();
        var reader = new FakeReader();
        await new VideoProjector(reader).CompleteBatchAsync(batch, CT);
        Assert.Empty(reader.Reads);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static (InMemoryStore Store, TraitBatch Batch) Batch()
    {
        var store = new InMemoryStore();
        return (store, new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.PlaylistOpen));
    }

    // The projector never reads Uri/Kind off the answer (the pipeline keys the dictionary), so the shape is just
    // payload + the two freshness facets the plane persists.
    static CachedExtension Ext(ByteString payload, string? etag = null, long ttl = 0)
        => new("", Xm.ExtensionKind.VideoAssociations, etag, ttl, payload, Missing: false);

    static CachedExtension Missing()
        => CachedExtension.MissingValue("", Xm.ExtensionKind.VideoAssociations);

    static TraitPayloads Payloads(string uri, params (Xm.ExtensionKind Kind, CachedExtension Value)[] entries)
    {
        var map = new Dictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>();
        foreach (var (kind, value) in entries) map[(uri, kind)] = value;
        return new TraitPayloads(map, uri);
    }

    static Track Trk(string id) => new(id, "spotify:track:" + id, "T " + id,
        [new ArtistRef("a", "spotify:artist:a", "A")], new AlbumRef("al", "spotify:album:al", "Al"), 1000, false, null);

    // Kind 182 on the wire: field 4 (length-delimited) is the experience-id blob; 0x02 = music video.
    static ByteString CePayload(bool hasVideo)
        => hasVideo
            ? ByteString.CopyFrom([0x22, 0x03, 0x01, 0x02, 0x04])
            : ByteString.CopyFrom([0x22, 0x02, 0x01, 0x04]);

    // Kind 212 on the wire: field 2 (length-delimited) carries the associated video's 16-byte gid.
    static ByteString PlaybackTraitPayload(string gidHex)
    {
        var gid = Convert.FromHexString(gidHex);
        var bytes = new byte[gid.Length + 2];
        bytes[0] = 0x12;
        bytes[1] = (byte)gid.Length;
        gid.CopyTo(bytes, 2);
        return ByteString.CopyFrom(bytes);
    }

    static ByteString TrackV4Payload(string canonicalUri)
        => new Wavee.Protocol.Metadata.Track { Name = "aliased", CanonicalUri = canonicalUri }.ToByteString();

    static ByteString VaPayload(string counterpartUri, params (string FileHex, int Variant, int W, int H)[] files)
    {
        var group = new Xm.VideoFileGroup();
        foreach (var (hex, variant, w, h) in files)
            group.File.Add(new Xm.VideoFile { FileId = ByteString.CopyFrom(Convert.FromHexString(hex)), Variant = variant, Width = w, Height = h });
        var va = new Xm.VideoAssociations { Association = new Xm.Association { AssociatedUri = counterpartUri, Files = group } };
        return va.ToByteString();
    }

    /// <summary>A recording <see cref="IExtensionReader"/>: only the raw multi-kind read is exercised (recovery is its
    /// one caller), and every read is logged so "once per alias" is assertable.</summary>
    sealed class FakeReader : IExtensionReader
    {
        public readonly List<List<(string Uri, Xm.ExtensionKind Kind)>> Reads = new();
        public readonly Dictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> Answers = new();

        public Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>> ReadRawAsync(
            IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> reqs, TraitSurface surface,
            CancellationToken ct = default, ReadOptions options = default)
        {
            Reads.Add([.. reqs]);
            var result = new Dictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>();
            foreach (var r in reqs) if (Answers.TryGetValue(r, out var v)) result[r] = v;
            return Task.FromResult<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>>(result);
        }

        public Task<T?> ReadAsync<T>(string uri, Xm.ExtensionKind kind, Func<ByteString, T?> parse, TraitSurface surface,
                                     CancellationToken ct = default, ReadOptions options = default) where T : class
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, T>> ReadManyAsync<T>(IReadOnlyList<string> uris, Xm.ExtensionKind kind,
                                                                     Func<ByteString, T?> parse, TraitSurface surface,
                                                                     CancellationToken ct = default) where T : class
            => throw new NotSupportedException();

        public void Seed<T>(string uri, Xm.ExtensionKind kind, T? answer) where T : class => throw new NotSupportedException();
    }
}
