// ── Wavee.Tests/FetchTraitNegativeTests.cs — a 200 that says "none" settles the group (RCA 2026-09-25 A/B, fix 1) ─────
//
// THE DEFECT. The trait kinds whose "nothing" is a value — 185 (play count), 99 / 182 (video), 6 (descriptors) — are
// answered by the server either with a payload that SAYS nothing (plays = 0, no association) or by leaving the entity out
// of the kind's array altogether. Before this fix the decoder staged nothing for either shape (185's zero, and every
// omission), so the row's group stayed un-settled, `ReviewMisses` called it a miss, retried it twice (the metadata cache
// answered the retry with the same bytes) and sealed it Failed: 230/324/105 Video misses a day, the Top tracks card's
// "Something went wrong" for a track with no counted plays (slot 8682).
//
// The facts: a zero and an omission both SETTLE the group as known-negative; an entity the server FAILED (a non-2xx
// entity status) is not a negative — that is the request being unlucky, and the hole stays for the retry; and a negative
// never blanks a positive twin in the same answer (182 saying "video" for an alias 99 left out). Everything goes
// through the real decoder door (`Spotify.Decode.ExtendedMetadata` with the asking batch) and the real commit.

using Google.Protobuf;
using Wavee;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class FetchTraitNegativeTests
{
    static byte[] Gid(byte seed)
    {
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed * 3 + i + 101);
        return gid;
    }

    static EntityId IdOf(byte seed) => EntityId.ForGid(EntityKind.Track, Gid(seed));
    static Track TrackOf(byte seed) => Entities.Track(IdOf(seed));

    /// <summary>The batch that asked: one Track row per seed, the need <paramref name="wanted"/> — the provider derives the
    /// kinds it POSTed from exactly this (the route walk), so the decoder can too.</summary>
    static FetchBatch Asked(uint wanted, params byte[] seeds) => new()
    {
        Provider = EntityProvider.Spotify, Kind = EntityKind.Track, Subject = FetchSubject.Entity, Wanted = wanted,
        Count = seeds.Length, Ids = seeds.Select(IdOf).ToArray(), Text = new string?[seeds.Length], Slots = new int[seeds.Length],
    };

    static Xm.EntityExtensionDataArray Array(Xm.ExtensionKind kind, params Xm.EntityExtensionData[] entities)
    {
        var a = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        a.ExtensionData.AddRange(entities);
        return a;
    }

    static Xm.EntityExtensionData Entity(byte seed, byte[] payload, int status = 200) => new()
    {
        Header = new Xm.EntityExtensionDataHeader { StatusCode = status },
        EntityUri = IdOf(seed).Text,
        ExtensionData = new Google.Protobuf.WellKnownTypes.Any { TypeUrl = "type.googleapis.com/x", Value = ByteString.CopyFrom(payload) },
    };

    static byte[] Plays(ulong value)
    {
        var bytes = new List<byte> { 3 << 3 };
        while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    static void Decode(FetchBatch asked, params Xm.EntityExtensionDataArray[] arrays)
    {
        var response = new Xm.BatchedExtensionResponse();
        response.ExtendedMetadata.AddRange(arrays);
        var s = Staging.Rent();
        Spotify.Decode.ExtendedMetadata(response.ToByteArray(), s, asked);
        TestScope.CommitAndPublish(s);
    }

    // ── failing first ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Slot 8682's shape: two tracks asked for PlayCount, the 185 array names only the first.</summary>
    [Fact]
    public void An_entity_the_play_count_answer_left_out_settles_as_known_zero()
    {
        TestScope.Fresh();
        Decode(Asked((uint)TrackFields.PlayCount, 1, 2),
            Array(Xm.ExtensionKind.OnPlatformReputationTrait, Entity(1, Plays(147_606))));

        Assert.True(TrackOf(1).Knows(TrackFields.PlayCount));
        Assert.Equal(147_606u, TrackOf(1).PlayCount);
        Assert.True(TrackOf(2).Knows(TrackFields.PlayCount));       // settled: the planner never re-asks it
        Assert.Equal(0u, TrackOf(2).PlayCount);                      // renders as the dash it always did
    }

    /// <summary>The 230/324/105-a-day shape: a video-less track's kind-99 answer leaves the entity out.</summary>
    [Fact]
    public void An_entity_the_video_answer_left_out_settles_as_no_video()
    {
        TestScope.Fresh();
        Decode(Asked((uint)TrackFields.Video, 3), Array(Xm.ExtensionKind.VideoAssociations));

        Assert.True(TrackOf(3).Knows(TrackFields.Video));
        Assert.False(TrackOf(3).HasVideo);
    }

    /// <summary>THE CAPTURED SHAPE (verify bundle xm-20260925-022444-load-missed.tsv): kind 99 answers every video-less
    /// track with a 404 ENTITY status inside a 200 body — the server's "no association exists", terminal like the TrackV4
    /// arm's 404. Before the fix the envelope dropped it, the row "missed" three times and sealed Failed.</summary>
    [Fact]
    public void A_terminal_video_status_settles_as_no_video()
    {
        TestScope.Fresh();
        var notFound = new Xm.EntityExtensionData
        {
            Header = new Xm.EntityExtensionDataHeader { StatusCode = 404 }, EntityUri = IdOf(8).Text,
        };
        Decode(Asked((uint)TrackFields.Video, 8), Array(Xm.ExtensionKind.VideoAssociations, notFound));

        Assert.True(TrackOf(8).Knows(TrackFields.Video));
        Assert.False(TrackOf(8).HasVideo);
    }

    /// <summary>A 2xx entity header with no extension_data at all is the same "nothing" as an omission.</summary>
    [Fact]
    public void A_header_with_no_data_settles_the_play_count_too()
    {
        TestScope.Fresh();
        var bare = new Xm.EntityExtensionData
        {
            Header = new Xm.EntityExtensionDataHeader { StatusCode = 200 }, EntityUri = IdOf(4).Text,
        };
        Decode(Asked((uint)TrackFields.PlayCount, 4), Array(Xm.ExtensionKind.OnPlatformReputationTrait, bare));

        Assert.True(TrackOf(4).Knows(TrackFields.PlayCount));
    }

    // ── guards (hold before and after) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A failed ENTITY is not a negative: the hole stays for the retry.</summary>
    [Fact]
    public void A_failed_entity_status_is_not_a_negative()
    {
        TestScope.Fresh();
        Decode(Asked((uint)TrackFields.PlayCount, 5),
            Array(Xm.ExtensionKind.OnPlatformReputationTrait, Entity(5, Plays(10), status: 503)));

        Assert.False(TrackOf(5).Knows(TrackFields.PlayCount));
    }

    /// <summary>A positive twin in the same answer always wins over an omission: kind 182 (canonical-computed) says
    /// "music video" for a track kind 99 left out — the relinked-alias case the 182 route exists for.</summary>
    [Fact]
    public void A_positive_twin_is_never_blanked_by_an_omission()
    {
        TestScope.Fresh();
        byte[] experiences = [0x22, 3, 1, 2, 4];                     // field 4, length 3: ids 01 02 04 (02 = music video)
        Decode(Asked((uint)TrackFields.Video, 6),
            Array(Xm.ExtensionKind.VideoAssociations),
            Array(Xm.ExtensionKind.ConsumptionExperienceTrait, Entity(6, experiences)));

        Assert.True(TrackOf(6).Knows(TrackFields.Video));
        Assert.True(TrackOf(6).HasVideo);
    }

    /// <summary>A group the batch did not ask is never settled by the answer's silence about it.</summary>
    [Fact]
    public void An_unasked_trait_is_not_settled_by_silence()
    {
        TestScope.Fresh();
        Decode(Asked((uint)TrackFields.PlayCount, 7), Array(Xm.ExtensionKind.OnPlatformReputationTrait, Entity(7, Plays(9))));

        Assert.False(TrackOf(7).Knows(TrackFields.Video));
        Assert.False(TrackOf(7).Knows(TrackFields.Tags));
    }
}
