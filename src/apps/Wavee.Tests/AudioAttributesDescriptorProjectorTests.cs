using System;
using System.Collections.Generic;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Hydration.Projectors;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Xunit;
using Aa = Wavee.Protocol.AudioAttributes;
using De = Wavee.Protocol.DescriptorExtension;
using Xm = Wavee.Protocol.ExtendedMetadata;

// EntityKind: the ONE uri vocabulary (Wavee.Core), not Backend.Metadata's thin transport projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.Tests;

// ── The kind-222 and kind-6 projectors (design §2.4), from SpotifyTrackAdornmentService.ParseAudio/ParseTags ──────────
// The two used to share one service, one request, one negative dictionary and — crucially — ONE mark: a resident tempo
// meant "adorned", so a track with a tempo and no descriptors was never asked for its tags again. Splitting them gives
// each its own mark, which is the whole reason the chips plane could be systematically short.
public class AudioAttributesDescriptorProjectorTests
{
    static string U(string id) => "spotify:track:" + id;

    static Track T(string id) => new(id, U(id), "T" + id, [], new AlbumRef("", "", ""), 1000, false, null);

    // ── 222: tempo / key / camelot ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Audio_WritesTempoKeyAndTheCamelotSlot()
    {
        // The captured shape: tempo 101.01, key "A", camelot 11B #56d9f8 — the wheel's MAJOR ring is 1B=B, 7B=F, 11B=A,
        // which is how key/camelot were identified rather than guessed.
        var (store, batch) = Batch(T("a"));
        var p = new AudioAttributesProjector();

        Assert.Equal(TraitOutcome.Applied, p.Project(batch, U("a"), Audio(U("a"), 101.01d, "A", "11B", "#56d9f8")));

        var row = store.GetTrack(U("a"))!;
        Assert.Equal(101.01d, row.TempoBpm);
        Assert.Equal("A", row.MusicalKey);
        Assert.Equal("11B", row.CamelotCode);
        Assert.Equal(SpotifyColor.FromHex("#56d9f8"), row.CamelotColor);
    }

    [Fact]
    public void Audio_AZeroTempoIsUnknown_NotZeroBpm()
    {
        var (store, batch) = Batch(T("a"));
        Assert.Equal(TraitOutcome.Negative,
            new AudioAttributesProjector().Project(batch, U("a"), Audio(U("a"), 0d, null, null, null)));
        Assert.Null(store.GetTrack(U("a"))!.TempoBpm);
    }

    [Fact]
    public void Audio_NeverMintsARow_AndAMissIsNegative()
    {
        var (store, batch) = Batch();
        var p = new AudioAttributesProjector();

        Assert.Equal(TraitOutcome.NotResident, p.Project(batch, U("ghost"), Audio(U("ghost"), 90d, "A", null, null)));
        Assert.Null(store.GetTrack(U("ghost")));
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, U("ghost"), Raw(U("ghost"), Xm.ExtensionKind.AudioAttributesV2, null)));
    }

    [Fact]
    public void Audio_AlreadyHasIsAResidentTempo()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(T("a") with { TempoBpm = 120d });
        store.UpsertTrack(T("b"));
        var p = new AudioAttributesProjector();
        var now = DateTimeOffset.UtcNow;

        Assert.True(p.AlreadyHas(store, U("a"), now));
        Assert.False(p.AlreadyHas(store, U("b"), now));
        Assert.False(p.AlreadyHas(store, U("ghost"), now));
    }

    [Fact]
    public void Audio_TheSameAttributesAreUnchanged()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(T("a") with { TempoBpm = 101.01d, MusicalKey = "A", CamelotCode = "11B", CamelotColor = SpotifyColor.FromHex("#56d9f8") });
        using var batch = new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.PlaylistOpen);

        Assert.Equal(TraitOutcome.Unchanged,
            new AudioAttributesProjector().Project(batch, U("a"), Audio(U("a"), 101.01d, "A", "11B", "#56d9f8")));
        Assert.Equal(0, batch.Writes);
    }

    // ── 6: descriptor tags ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Descriptors_TakeTheDisplayName_InWireOrder_CappedAtSix()
    {
        // Wire order is descending weight, so the first N are the strongest; no re-sorting. display_name is the
        // presentation form ("K-Pop"), the lowercase `text` token is the fallback when the server omits it.
        var (store, batch) = Batch(T("a"));
        var tags = new (string Text, string? Display)[]
        {
            ("k-pop", "K-Pop"), ("energetic", "Energetic"), ("dance", null), ("d", "D"), ("e", "E"), ("f", "F"), ("g", "G"),
        };

        Assert.Equal(TraitOutcome.Applied, new DescriptorProjector().Project(batch, U("a"), Descriptors(U("a"), tags)));

        Assert.Equal(["K-Pop", "Energetic", "dance", "D", "E", "F"], store.GetTrack(U("a"))!.Tags);
        Assert.Equal(6, DescriptorProjector.MaxTagsPerTrack);
    }

    [Fact]
    public void Descriptors_AnEmptyListIsARealNone_AndIsWritten()
    {
        // Finding 27: null means "not fetched" and empty means "this track genuinely has none". Collapsing the two left
        // the content-filter chips unable to tell an unasked track from an answered one.
        var (store, batch) = Batch(T("a"));

        Assert.Equal(TraitOutcome.Applied, new DescriptorProjector().Project(batch, U("a"), Descriptors(U("a"), [])));

        var row = store.GetTrack(U("a"))!;
        Assert.NotNull(row.Tags);
        Assert.Empty(row.Tags!);
    }

    [Fact]
    public void Descriptors_AlreadyHasCountsTheEmptyList()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(T("a") with { Tags = [] });
        store.UpsertTrack(T("b"));
        var p = new DescriptorProjector();
        var now = DateTimeOffset.UtcNow;

        Assert.True(p.AlreadyHas(store, U("a"), now));   // answered "none" — never ask again
        Assert.False(p.AlreadyHas(store, U("b"), now));
    }

    [Fact]
    public void Descriptors_NeverMintARow_AndAMissIsNegative()
    {
        var (store, batch) = Batch();
        var p = new DescriptorProjector();

        Assert.Equal(TraitOutcome.NotResident, p.Project(batch, U("ghost"), Descriptors(U("ghost"), [("k-pop", "K-Pop")])));
        Assert.Null(store.GetTrack(U("ghost")));
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, U("ghost"), Raw(U("ghost"), Xm.ExtensionKind.TrackDescriptor, null)));
    }

    [Fact]
    public void Descriptors_TheSameTagsAreUnchanged()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(T("a") with { Tags = ["K-Pop"] });
        using var batch = new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.LikedSongs);

        Assert.Equal(TraitOutcome.Unchanged,
            new DescriptorProjector().Project(batch, U("a"), Descriptors(U("a"), [("k-pop", "K-Pop")])));
        Assert.Equal(0, batch.Writes);
    }

    [Fact]
    public void BothApplyToPlayablesOnly()
    {
        ITraitProjector[] both = [new AudioAttributesProjector(), new DescriptorProjector()];
        foreach (var p in both)
        {
            Assert.True(p.AppliesTo(EntityKind.Track));
            Assert.True(p.AppliesTo(EntityKind.Episode));   // ask once, honour the 404
            Assert.False(p.AppliesTo(EntityKind.Album));
            Assert.False(p.AppliesTo(EntityKind.Artist));
        }
        Assert.Equal(TraitSet.AudioAttributes, both[0].Trait);
        Assert.Equal(TraitSet.Descriptors, both[1].Trait);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static (InMemoryStore Store, TraitBatch Batch) Batch(params Track[] resident)
    {
        var store = new InMemoryStore();
        foreach (var t in resident) store.UpsertTrack(t);
        return (store, new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.PlaylistOpen));
    }

    static TraitPayloads Audio(string uri, double tempo, string? key, string? camelot, string? colour)
    {
        var msg = new Aa.AudioAttributes { Tempo = tempo };
        if (key is not null)
        {
            msg.Key = new Aa.MusicalKey { Name = key, Mode = 2 };
            if (camelot is not null) msg.Key.Camelot = new Aa.Camelot { Code = camelot, Color = colour ?? "" };
        }
        return Raw(uri, Xm.ExtensionKind.AudioAttributesV2, msg.ToByteString());
    }

    static TraitPayloads Descriptors(string uri, IReadOnlyList<(string Text, string? Display)> tags)
    {
        var msg = new De.ExtensionDescriptorData();
        foreach (var (text, display) in tags)
        {
            var d = new De.ExtensionDescriptor { Text = text };
            if (display is not null) d.DisplayName = display;
            msg.Descriptors.Add(d);
        }
        return Raw(uri, Xm.ExtensionKind.TrackDescriptor, msg.ToByteString());
    }

    static TraitPayloads Raw(string uri, Xm.ExtensionKind kind, ByteString? payload)
    {
        var map = new Dictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>
        {
            [(uri, kind)] = new(uri, kind, null, 0, payload, Missing: payload is null),
        };
        return new TraitPayloads(map, uri);
    }
}
