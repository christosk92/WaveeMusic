// ── Wavee.Tests/AudioAnalysisDecodeTests.cs — the audio-analysis JSON → Edges.TrackBeats ──────────────────────────
//
// Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §4.5.2 (WP-D2), §5.2. `Spotify.Decode.AudioAnalysis` is a
// hand-written `Utf8JsonReader` fold over `{"beats":[{"start":s,…}],"bars":[…],…}`: the beats' starts in ms, the bars folded
// in as downbeat flags (bit 31, `BeatGrid`). Fixtures are hand-written JSON — the shape is the public Web-API analysis shape;
// whether spclient serves it is the O5 probe's question (§4.5.3), not this file's.
//
// An answer with no beats is a real "no grid" answer (an EMPTY Complete run — Pulse falls back to the tempo grid and the door
// stops asking); a body that is not JSON stages NOTHING (V-D19 — the door re-asks later).

using System.Globalization;
using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class AudioAnalysisDecodeTests
{
    static byte[] Gid(byte seed)
    {
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return gid;
    }

    static EntityId IdOf(byte seed) => EntityId.ForGid(EntityKind.Track, Gid(seed));
    static byte[] UriBytes(byte seed) => Encoding.UTF8.GetBytes(IdOf(seed).Text);
    static int SlotOf(byte seed) => Entities.Track(IdOf(seed)).Slot;

    /// <summary>Decode <paramref name="json"/> for the track <paramref name="seed"/> and commit it, the way one UI drain does.</summary>
    static void Land(byte seed, string json)
    {
        var s = Staging.Rent();
        Spotify.Decode.AudioAnalysis(Encoding.UTF8.GetBytes(json), UriBytes(seed), s);
        TestScope.CommitAndPublish(s);
    }

    [Fact]
    public void AudioAnalysis_stages_beats_in_ms_with_bars_as_downbeats_and_an_empty_answer_is_complete()
    {
        TestScope.Fresh();
        Land(50, """
            {"beats":[{"start":0.5,"duration":0.5,"confidence":0.9},{"start":1.0,"duration":0.5,"confidence":0.8},{"start":1.5,"duration":0.5,"confidence":0.7}],
             "bars":[{"start":1.02,"duration":2.0,"confidence":0.6}],"meta":{}}
            """);

        var beats = Entities.Current.Edges.TrackBeats;
        int track = SlotOf(50);
        Assert.Equal(EdgeState.Complete, beats.State(track));
        Assert.Equal(3, beats.Count(track));
        Assert.Equal(3, beats.Total(track));
        // The bar at 1.02 s lands on the beat at 1.0 s (within the 40 ms tolerance): that beat — and only that one — is a downbeat.
        Assert.Equal(new uint[] { 500, 1000 | BeatGrid.DownbeatBit, 1500 }, beats.Payload(track).ToArray());
        Assert.Equal(1000u, BeatGrid.StartMs(beats.Payload(track)[1]));
        Assert.True(BeatGrid.IsDownbeat(beats.Payload(track)[1]));
        Assert.False(BeatGrid.IsDownbeat(beats.Payload(track)[0]));

        // "The service has no grid for this track" is an answer: Complete, empty — the door stops asking.
        Land(51, """{"beats":[]}""");
        Assert.Equal(EdgeState.Complete, beats.State(SlotOf(51)));
        Assert.Equal(0, beats.Count(SlotOf(51)));

        // So is a well-formed answer that carries no beats member at all.
        Land(52, """{"meta":{"status_code":0},"bars":[{"start":1.0}]}""");
        Assert.Equal(EdgeState.Complete, beats.State(SlotOf(52)));
        Assert.Equal(0, beats.Count(SlotOf(52)));
    }

    [Fact]
    public void AudioAnalysis_ignores_non_json()
    {
        TestScope.Fresh();
        var beats = Entities.Current.Edges.TrackBeats;

        Land(60, "not json");                                   // a captive portal's page
        Land(61, """{"beats":[{"start":""");                    // a truncated body
        Land(62, "[1,2,3]");                                    // JSON, but not an object
        Land(63, """{"beats":[{"start":0.5},{"start":1.0}]""");  // the closing brace never arrived

        foreach (byte seed in new byte[] { 60, 61, 62, 63 })
        {
            Assert.Equal(EdgeState.Unknown, beats.State(SlotOf(seed)));
            Assert.Equal(0, beats.Count(SlotOf(seed)));
        }
    }

    [Fact]
    public void AudioAnalysis_skips_unknown_members_reads_either_member_order_and_ignores_unusable_starts()
    {
        TestScope.Fresh();
        // bars BEFORE beats; a nested "start" inside a beat's own object (not the beat's start); a string start, a negative
        // start and a beat with no start at all — none of those is a beat; unrelated members with their own "start"s.
        Land(70, """
            {"meta":{"analysis_time":1.5},
             "bars":[{"start":0.0,"duration":2.0,"confidence":1.0}],
             "track":{"tempo":120.0,"sections":[{"start":0}]},
             "beats":[{"confidence":0.9,"start":0.0,"duration":0.5,"extra":{"start":9}},{"start":"x"},{"start":0.5},{"start":-1.0},{"duration":0.5},{"start":1.0}],
             "segments":[{"start":0.1,"pitches":[0.1,0.2]}]}
            """);

        var beats = Entities.Current.Edges.TrackBeats;
        int track = SlotOf(70);
        Assert.Equal(EdgeState.Complete, beats.State(track));
        Assert.Equal(new uint[] { 0 | BeatGrid.DownbeatBit, 500, 1000 }, beats.Payload(track).ToArray());
    }

    [Fact]
    public void AudioAnalysis_flags_a_downbeat_only_within_the_tolerance()
    {
        TestScope.Fresh();
        // The bar at 1.2 s is 200 ms from the nearest beat (1.0 s): no beat is flagged.
        Land(80, """{"beats":[{"start":0.5},{"start":1.0},{"start":1.5}],"bars":[{"start":1.2}]}""");

        var beats = Entities.Current.Edges.TrackBeats;
        Assert.Equal(new uint[] { 500, 1000, 1500 }, beats.Payload(SlotOf(80)).ToArray());
    }

    [Fact]
    public void AudioAnalysis_keeps_at_most_MaxBeats_beats()
    {
        TestScope.Fresh();
        // 9 000 beats, 100 ms apart: the run is capped at the edge's target array (BeatGrid.MaxBeats), never rejected.
        var json = new StringBuilder("{\"beats\":[", 9000 * 20);
        for (int i = 0; i < 9000; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"start\":").Append((i / 10).ToString(CultureInfo.InvariantCulture)).Append('.')
                .Append((i % 10).ToString(CultureInfo.InvariantCulture)).Append('}');
        }
        json.Append("]}");
        Land(90, json.ToString());

        var beats = Entities.Current.Edges.TrackBeats;
        int track = SlotOf(90);
        Assert.Equal(EdgeState.Complete, beats.State(track));
        Assert.Equal(BeatGrid.MaxBeats, beats.Count(track));
        Assert.Equal(BeatGrid.MaxBeats, beats.Total(track));
        Assert.Equal(0u, BeatGrid.StartMs(beats.Payload(track)[0]));
        Assert.Equal((uint)((BeatGrid.MaxBeats - 1) * 100), BeatGrid.StartMs(beats.Payload(track)[^1]));
    }
}
