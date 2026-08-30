using System;
using System.Collections.Generic;
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

// ── The kind-185 projector (design §2.4), ported from TrackPlayCountHydratorTests + TrackPlayCountTests ───────────────
// The fill rules move over intact — decorate a resident row, never mint one, never invent a 0, and say "unchanged" when
// the row already agrees. What is gone is everything the hydrator owned around them (the 300-slice, the per-session
// memo, the bulk scope, the transport guard): the pipeline owns all four, and the outcome enum is how this projector
// tells it what to remember.
public class PlayCountProjectorTests
{
    static string U(string id) => "spotify:track:" + id;

    static Track T(string id, long plays = 0) =>
        new(id, U(id), "T" + id, [], new AlbumRef("", "", ""), 1000, false, null, PlayCount: plays);

    // ── the decoder (from TrackPlayCountTests) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decoder_ReadsField3AsTheStreamCount()
    {
        Assert.True(PlayCountProjector.TryReadPlayCount(Varint(3, 1_400_000), out long plays));
        Assert.Equal(1_400_000, plays);
        Assert.True(PlayCountProjector.TryReadPlayCount(Varint(3, 2_100_000_000), out plays));
        Assert.Equal(2_100_000_000, plays);
    }

    [Fact]
    public void Decoder_SkipsUnknownFieldsByWireType()
    {
        // A real payload is not guaranteed to lead with field 3, and the message grows — an unknown length-delimited or
        // fixed field in front of it must be walked over, not treated as a parse failure.
        var bytes = new List<byte>();
        bytes.AddRange([0x0A, 0x02, 0x41, 0x42]);   // f1, length-delimited "AB"
        bytes.AddRange([0x11, 1, 2, 3, 4, 5, 6, 7, 8]);   // f2, fixed64
        bytes.AddRange(Varint(3, 777));
        Assert.True(PlayCountProjector.TryReadPlayCount(bytes.ToArray(), out long plays));
        Assert.Equal(777, plays);
    }

    [Fact]
    public void Decoder_RefusesToInventACount()
    {
        // A zero, a truncated varint, a wrong wire type on field 3, and an empty body are all "unknown" — never 0-as-a-fact.
        Assert.False(PlayCountProjector.TryReadPlayCount(Varint(3, 0), out _));
        Assert.False(PlayCountProjector.TryReadPlayCount([0x18, 0x80], out _));         // f3 varint, truncated
        Assert.False(PlayCountProjector.TryReadPlayCount([0x1A, 0x01, 0x05], out _));   // f3 as length-delimited
        Assert.False(PlayCountProjector.TryReadPlayCount([], out _));
    }

    // ── the fill rules ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_WritesTheCountOntoTheResidentRow()
    {
        var (store, batch, p) = Build(T("a"));
        Assert.Equal(TraitOutcome.Applied, p.Project(batch, U("a"), Answer(U("a"), 1_400_000)));
        Assert.Equal(1_400_000, store.GetTrack(U("a"))!.PlayCount);
    }

    [Fact]
    public void Project_NeverMintsARowForAUriTheStoreDoesNotHave()
    {
        // Counts DECORATE a row the list already projected. Minting one would materialise a nameless track that paints
        // as a blank line — and NotResident is never memoized, so the count is still wanted when the row lands.
        var (store, batch, p) = Build();
        Assert.Equal(TraitOutcome.NotResident, p.Project(batch, U("ghost"), Answer(U("ghost"), 99)));
        Assert.Null(store.GetTrack(U("ghost")));
        Assert.Equal(0, batch.Writes);
    }

    [Fact]
    public void Project_TheSameCountIsUnchanged_AndWritesNothing()
    {
        var (store, batch, p) = Build(T("a", 500));
        Assert.Equal(TraitOutcome.Unchanged, p.Project(batch, U("a"), Answer(U("a"), 500)));
        Assert.Equal(500, store.GetTrack(U("a"))!.PlayCount);
        Assert.Equal(0, batch.Writes);   // no write ⇒ no bulk scope ⇒ no store change signal for a warm page
    }

    [Fact]
    public void Project_AZeroOrUndecodableAnswerIsNegative_AndTheRowStaysUnknown()
    {
        var (store, batch, p) = Build(T("a"), T("b"));
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, U("a"), Answer(U("a"), 0)));
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, U("b"), Payload(U("b"), ByteString.CopyFrom([0x18, 0x80]))));
        Assert.Equal(0, store.GetTrack(U("a"))!.PlayCount);   // never an invented 0-as-a-fact
        Assert.Equal(0, store.GetTrack(U("b"))!.PlayCount);
    }

    [Fact]
    public void Project_A404IsNegative_AndAnUnansweredUriIsNot()
    {
        var (_, batch, p) = Build(T("a"), T("b"));
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, U("a"),
            Payload(U("a"), null)));                                     // explicit "no such extension" → memoized
        Assert.Equal(TraitOutcome.NotResident, p.Project(batch, U("b"),
            new TraitPayloads(new Dictionary<(string, Xm.ExtensionKind), CachedExtension>(), U("b"))));   // omitted → re-askable
    }

    [Fact]
    public void AlreadyHas_IsAResidentCount()
    {
        var (store, _, p) = Build(T("a", 500), T("b"));
        var now = DateTimeOffset.UtcNow;
        Assert.True(p.AlreadyHas(store, U("a"), now));
        Assert.False(p.AlreadyHas(store, U("b"), now));    // 0 is "unknown" by contract, never "filled"
        Assert.False(p.AlreadyHas(store, U("ghost"), now));
    }

    [Fact]
    public void AppliesTo_IsPlayablesOnly()
    {
        // The kind is POLYMORPHIC (shows/audiobooks answer a `rating` arm off the same 185), so a container uri must
        // never reach the request. Episodes are ask-once, not excluded.
        var (_, _, p) = Build();
        Assert.True(p.AppliesTo(EntityKind.Track));
        Assert.True(p.AppliesTo(EntityKind.Episode));
        Assert.False(p.AppliesTo(EntityKind.Album));
        Assert.False(p.AppliesTo(EntityKind.Artist));
        Assert.False(p.AppliesTo(EntityKind.Playlist));
        Assert.False(p.AppliesTo(EntityKind.Show));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static (InMemoryStore Store, TraitBatch Batch, PlayCountProjector P) Build(params Track[] resident)
    {
        var store = new InMemoryStore();
        foreach (var t in resident) store.UpsertTrack(t);
        return (store, new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.AlbumOpen), new PlayCountProjector());
    }

    static TraitPayloads Answer(string uri, long plays) => Payload(uri, ByteString.CopyFrom(Varint(3, plays)));

    static TraitPayloads Payload(string uri, ByteString? payload)
    {
        var map = new Dictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>
        {
            [(uri, Xm.ExtensionKind.OnPlatformReputationTrait)] =
                new(uri, Xm.ExtensionKind.OnPlatformReputationTrait, null, 0, payload, Missing: payload is null),
        };
        return new TraitPayloads(map, uri);
    }

    /// <summary>proto2 on the wire: a field tag then a varint. The kind-185 track payload is exactly this, six bytes.</summary>
    static byte[] Varint(int field, long value)
    {
        var bytes = new List<byte> { (byte)((field << 3) | 0) };
        ulong v = (ulong)value;
        do { byte b = (byte)(v & 0x7F); v >>= 7; if (v != 0) b |= 0x80; bytes.Add(b); } while (v != 0);
        return bytes.ToArray();
    }
}
