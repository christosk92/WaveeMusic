// ── Playback/Playback.Dj.TalkOver.cs ─────────────────────────────────────────────────────────────────────────────────
// AI DJ talk-over and radio automix (plan-dj D3, D3b): every seam of a DJ queue becomes ONE MixTiming for the pump's Mix join
// (Playback.Audio.Mix.cs), so a voice talks over the music and DJ tracks no longer butt-join.
//
// Role: CORE (the planners and the curves are PURE) + SHELL (`Dj.Seam`: the UI thread's read of the queue, the DJ book and the cues)
// Spec: docs/plans/wavee/dj-implementation.md §1.6 (talk-over), §1.7 (radio), §2.4, §2.5, §2.6; dj-protocol.md §1.3, §1.4, §7
//
// THE SEAMS. Mix.Follow asks `Dj.Seam` for the playing pair while a lexicon session plays (instead of the playlist planner):
//
//   A           B            kind        timing                                                   B becomes current
//   narration → track        talk-over   human cue: B at max(0, cue − d − 1600), max(0, d − (cue − 1600)) into the voice,
//                                        its window [start, cue]; otherwise (ml, no cue data) B from 0, d − 3400 into the
//                                        voice, a 5000 ms window. The voice is never faded.          jump: when B starts;
//                                                                                                     intro / welcome: at the voice's end
//   track     → narration    outro       A ducks 1 → 0.25 over 4000 ms from (A's end − 5500), stays at 0.25 to its end (never 0);
//                                        the voice starts when the duck has landed (A's end − 1500)   when the voice starts
//   narration → narration    sequential  the base hand-off (gapless: an intro follows its outro when the outro ends)
//   track     → track        radio       A fades out (0,1)(.2,1)(1,0) and ends on its end cue; B with a confident start cue is
//                                        pre-rolled so its fade-in ends on that cue, else from 0 in A's last 350 ms   when B starts
//
// A join whose B becomes current when it starts leaves A playing under it to the end of A's window (the pump's "A outlives the
// announce"). An intro's target that started under it lifts the narration restrictions while the voice is still current
// (`AudioSignal.TargetStarted` → `State.TargetUnder`).
//
// DECISIONS WHERE THE CAPTURES LEAVE ROOM (each one sample or none; plan-dj §6):
// - The outro window: one sample ran 5507 ms to the track's end and the voice started 1507 ms before it, so the window is the last
//   5500 ms (`OutroWindowMs`) and the voice starts 4000 ms in. A narration after a track that is not its outro (the outro was dropped)
//   rides the same duck.
// - The radio fade length (3.0 to 5.4 s observed, the rule open): 8 beats of A's tempo, kept in 2.5 to 5.5 s, 4 s without a tempo.
// - A "confident" start cue (0.88 and 0.90 used, 0.21, 0.29 and 0.39 not): a human cue, or confidence ≥ 0.5.
// - Curves are read as the playlist Mix reads them (MixCurves: 2 points a line, 4 a cubic Bézier; the radio fade-out's 3 points a
//   quadratic, sampled as its exact cubic).
// - A voice whose length is unknown when the seam is planned (a streamed clip) plays in full, then the track (§1.6's last row).

using System.Globalization;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The AI DJ's curves (plan-dj §1.6, §1.7): the duck under a voice, the outro's duck, the radio pair. PURE.</summary>
    public static class DjCurves
    {
        /// <summary>The music's gain under a voice (−12 dB).</summary>
        public const float Duck = 0.25f;

        /// <summary>The volume curve of one side of <paramref name="t"/> (a DJ shape), as Mix segments (the radio fade-out's quadratic as its
        /// exact cubic). Writes at most 3; returns the count.</summary>
        public static int Segments(in MixTiming t, bool incoming, Span<CurveSegment> into)
        {
            switch (t.Shape)
            {
                case MixShape.DuckMl when incoming:      // 0.25 for 60 % of the 5 s window, then up (the music is full 600 ms after the voice)
                    into[0] = CurveSegment.Flat(0, .6f, Duck);
                    into[1] = CurveSegment.S(.6f, 1, Duck, 1);
                    return 2;
                case MixShape.DuckHuman when incoming:   // 0 → 0.25 over 1 s, 0.25, then up over the window's last 2 s (to the cue)
                {
                    float w = Math.Max(1, t.InDurationMs);
                    float b1 = Math.Clamp(1000f / w, 0f, 1f), b2 = Math.Clamp((w - 2000f) / w, b1, 1f);
                    into[0] = new CurveSegment(0, b1, false, 0, 0, 0, 0, 0, 0, 1, Duck);
                    into[1] = CurveSegment.Flat(b1, b2, Duck);
                    into[2] = CurveSegment.S(b2, 1, Duck, 1);
                    return 3;
                }
                case MixShape.DuckOutro when !incoming:  // the last track: 1 → 0.25 by the time the voice starts, then 0.25 to its end
                {
                    float knee = t.OutDurationMs == 0 ? 1f : Math.Clamp(t.InDelayMs / (float)t.OutDurationMs, 0f, 1f);
                    into[0] = CurveSegment.S(0, knee, 1, Duck);
                    into[1] = CurveSegment.Flat(knee, 1, Duck);
                    return 2;
                }
                case MixShape.Radio when !incoming:      // (0,1)(.2,1)(1,0) quadratic = the cubic (0,1)(2/15,1)(7/15,2/3)(1,0)
                    into[0] = new CurveSegment(0, 1, true, 0, 1, 2f / 15, 1, 7f / 15, 2f / 3, 1, 0);
                    return 1;
                case MixShape.Radio:                     // (0,0)(.9,0)(.9,1)(1,1)
                    into[0] = new CurveSegment(0, 1, true, 0, 0, .9f, 0, .9f, 1, 1, 1);
                    return 1;
                default:                                 // the voice: never faded
                    into[0] = CurveSegment.Flat(0, 1, 1);
                    return 1;
            }
        }

        /// <summary>The <c>audio.fade_{in,out}_curves</c> value of one side, as the official player spells it (the radio fade-out with its
        /// three points).</summary>
        public static string Json(in MixTiming t, bool incoming)
        {
            if (t.Shape == MixShape.Radio && !incoming)
                return "[{\"end_point\":1,\"fade_curve\":[{\"x\":0,\"y\":1},{\"x\":" + MixCurves.F(.2f) + ",\"y\":1},{\"x\":1,\"y\":0}],\"start_point\":0}]";
            Span<CurveSegment> seg = stackalloc CurveSegment[3];
            int n = Segments(in t, incoming, seg);
            Span<byte> buf = stackalloc byte[640];
            return System.Text.Encoding.UTF8.GetString(buf[..MixCurves.WriteJson(seg[..n], buf)]);
        }

        /// <summary>The gain of one side <paramref name="ms"/> after that side's window starts (before it: its first value; after it: its
        /// last). PURE; the inspector samples it.</summary>
        public static float GainAt(in MixTiming t, bool incoming, long ms)
        {
            Span<CurveSegment> seg = stackalloc CurveSegment[3];
            int n = Segments(in t, incoming, seg);
            uint window = incoming ? t.InDurationMs : t.OutDurationMs;
            return MixCurves.Evaluate(seg[..n], window == 0 ? 1f : Math.Clamp(ms / (float)window, 0f, 1f));
        }
    }

    /// <summary>The talk-over (plan-dj §1.6, §2.4): a narration and the track after it, or a track and the narration after it. PURE.</summary>
    public static class TalkOverPlanner
    {
        /// <summary>A human cue: the voice ends this long before it.</summary>
        public const uint HumanLeadMs = 1_600;
        /// <summary>The ml rule (and no cue data): the track starts this long before the voice ends, at its own 0.</summary>
        public const uint MlOverlapMs = 3_400;
        /// <summary>The ml rule's fade window (its curve reaches the plateau's end at 60 %).</summary>
        public const uint MlWindowMs = 5_000;
        /// <summary>The human rule's window must hold its 1 s ramp and its 2 s rise.</summary>
        public const uint HumanMinWindowMs = 3_000;
        /// <summary>The outro: the last track's window, and how long it ducks before the voice starts.</summary>
        public const uint OutroWindowMs = 5_500, OutroDuckMs = 4_000;

        /// <summary>A voice of <paramref name="voiceMs"/> over the track after it (<paramref name="cues"/>: its kind 28, default when not
        /// known; <paramref name="trackMs"/>: its length, 0 unknown). <paramref name="jump"/>: the track becomes current as soon as it starts
        /// (a jump clip, 4 of 4); otherwise the voice stays current to its end (an intro or a welcome, 3 of 3). Refused when the voice's
        /// length is unknown (it plays in full, then the track).</summary>
        public static MixTiming VoiceToTrack(long voiceMs, bool jump, in TrackCues cues, long trackMs)
        {
            if (voiceMs <= 0) return MixTiming.Refused(MixWhyNot.NoData);
            MixAnnounceAt announce = jump ? MixAnnounceAt.InStart : MixAnnounceAt.OutEnd;
            long cue = cues.StartCueMs;
            if (cues.StartIsHuman && cue > HumanLeadMs && (trackMs <= 0 || cue < trackMs))
            {
                long inStart = Math.Max(0, cue - voiceMs - HumanLeadMs);
                long outStart = Math.Max(0, voiceMs - (cue - HumanLeadMs));
                long window = cue - inStart;
                if (window >= HumanMinWindowMs && voiceMs - outStart > 0)
                    return new MixTiming(MixSource.TalkOver, MixWhyNot.None, 0, (uint)outStart, (uint)(voiceMs - outStart), (uint)inStart,
                        (uint)window, MixShape.DuckHuman, announce);
            }
            long mlStart = Math.Max(0, voiceMs - MlOverlapMs);
            uint inDur = trackMs > 0 ? (uint)Math.Min(MlWindowMs, trackMs) : MlWindowMs;
            return new MixTiming(MixSource.TalkOver, MixWhyNot.None, 0, (uint)mlStart, (uint)(voiceMs - mlStart), 0, inDur, MixShape.DuckMl, announce);
        }

        /// <summary>A track of <paramref name="trackMs"/> and the voice after it (its outro, or a narration whose outro was dropped): the
        /// track ducks under the voice and never fades to 0; the voice becomes current when it starts.</summary>
        public static MixTiming TrackToVoice(long trackMs, long voiceMs)
        {
            if (trackMs <= 0) return MixTiming.Refused(MixWhyNot.NoData);
            long window = Math.Min(trackMs, OutroWindowMs);
            long duck = window * OutroDuckMs / OutroWindowMs;
            return new MixTiming(MixSource.TalkOver, MixWhyNot.None, 0, (uint)(trackMs - window), (uint)window, 0,
                (uint)Math.Max(1, voiceMs > 0 ? voiceMs : window - duck), MixShape.DuckOutro, MixAnnounceAt.InStart, (uint)duck);
        }
    }

    /// <summary>The radio automix between two DJ tracks (plan-dj §1.7, §2.5). PURE.</summary>
    public static class RadioPlanner
    {
        /// <summary>The fade length's bounds and its value without a tempo (observed 2.6 to 5.4 s; the rule is open).</summary>
        public const uint MinFadeMs = 2_500, MaxFadeMs = 5_500, DefaultFadeMs = 4_000;
        /// <summary>B without a confident start cue joins in A's last this-many ms, from its own 0.</summary>
        public const uint BlindOverlapMs = 350;
        /// <summary>A start cue at or above this confidence is used (0.88 and 0.90 were, 0.21 to 0.39 were not).</summary>
        public const float ConfidentAt = 0.5f;

        /// <summary>Is <paramref name="cues"/>' start cue one B is pre-rolled to?</summary>
        public static bool Confident(in TrackCues cues) => cues.StartCueMs > 0 && (cues.StartIsHuman || cues.StartConfidence >= ConfidentAt);

        /// <summary>A's fade-out length: 8 beats of its tempo, bounded.</summary>
        public static uint FadeMs(in TrackCues a)
            => a.TempoX10 == 0 ? DefaultFadeMs : (uint)Math.Clamp((long)Math.Round(8 * 600_000.0 / a.TempoX10), MinFadeMs, MaxFadeMs);

        /// <summary>The join of A (<paramref name="aMs"/> long) into B. Refused without A's end cue (the base hand-off runs).</summary>
        public static MixTiming Plan(long aMs, in TrackCues a, in TrackCues b, long bMs)
        {
            long end = a.EndCueMs;
            if (end == 0 || aMs <= 0 || end > aMs) return MixTiming.Refused(MixWhyNot.NoData);
            long fade = Math.Min(FadeMs(in a), end);
            long overlap, inStart;
            if (Confident(in b)) { overlap = Math.Min(fade, b.StartCueMs); inStart = b.StartCueMs - overlap; }
            else { overlap = Math.Min(BlindOverlapMs, fade); inStart = 0; }
            if (overlap <= 0 || (bMs > 0 && inStart + overlap > bMs)) return MixTiming.Refused(MixWhyNot.TooShort);
            return new MixTiming(MixSource.Radio, MixWhyNot.None, 0, (uint)(end - fade), (uint)fade, (uint)inStart, (uint)overlap,
                MixShape.Radio, MixAnnounceAt.InStart, (uint)(fade - overlap));
        }
    }

    public static partial class Dj
    {
        /// <summary>What a DJ seam is.</summary>
        public enum SeamKind : byte { None, TalkOver, Outro, Sequential, Radio }

        /// <summary>One seam of the DJ queue: what A and B are, the timing, and the cues it read (the Connect keys name them).</summary>
        public readonly record struct SeamFacts(SeamKind Kind, MixTiming Timing, Slot? VoiceA, Slot? VoiceB, long AMs, long BMs, TrackCues CuesA,
            TrackCues CuesB, EntityId A, EntityId B);

        /// <summary>The seam from the row at <paramref name="at"/> to the row the queue plays after it, while <paramref name="context"/> plays
        /// a lexicon session; <see cref="SeamKind.None"/> otherwise (the playlist planner decides). A DJ seam that cannot join gets a refused
        /// timing (the base hand-off: gapless at a narration, the user's crossfade between tracks). UI thread: reads the queue, the DJ book,
        /// the narrations and the cue edges; asks for the cues of the two tracks.</summary>
        public static SeamFacts Seam(EntityId context, ReadOnlySpan<QueueEdge> rows, int at)
        {
            if (Of(context) is null || (uint)at >= (uint)rows.Length || Entities.Current is not { } scope) return default;
            int next = Queue.NextIndex(rows, at);
            if (next < 0) return default;
            EntityRef a = Queue.RefAt(at), b = Queue.RefAt(next);
            if (a.Kind != EntityKind.Track || b.Kind != EntityKind.Track || a.IsNone || b.IsNone) return default;
            Spoken? va = NarrationOf(rows[at].ItemId), vb = NarrationOf(rows[next].ItemId);
            bool djA = va is not null || RowOf(rows[at].ItemId) is not null, djB = vb is not null || RowOf(rows[next].ItemId) is not null;
            if (!djA) return default;
            TrackCues ca = va is null ? CuesOf(a) : default, cb = vb is null ? CuesOf(b) : default;
            long aMs = va is not null ? VoiceMs(va) : new Track(a.Slot).DurationMs, bMs = vb is not null ? VoiceMs(vb) : new Track(b.Slot).DurationMs;
            EntityId ida = scope.Tracks.Id[a.Slot], idb = scope.Tracks.Id[b.Slot];
            if (!djB || rows[next].Provider is not ((byte)QueueProvider.Context or (byte)QueueProvider.Narration))
                return new SeamFacts(SeamKind.None, MixTiming.Refused(MixWhyNot.UserQueued), va?.Slot, vb?.Slot, aMs, bMs, ca, cb, ida, idb);
            (SeamKind kind, MixTiming timing) = (va, vb) switch
            {
                ({ } voice, null) => (SeamKind.TalkOver, TalkOverPlanner.VoiceToTrack(aMs, voice.Slot == Slot.Jump, in cb, bMs)),
                (null, { }) => (SeamKind.Outro, TalkOverPlanner.TrackToVoice(aMs, bMs)),
                ({ }, { }) => (SeamKind.Sequential, MixTiming.Refused(MixWhyNot.NotMixed)),
                _ => (SeamKind.Radio, RadioPlanner.Plan(aMs, in ca, in cb, bMs)),
            };
            return new SeamFacts(kind, timing, va?.Slot, vb?.Slot, aMs, bMs, ca, cb, ida, idb);
        }

        /// <summary>A voice's exact length: its clip's once the TTS client measured it (a streamed clip's when its body ended), else −1.</summary>
        static long VoiceMs(Spoken sp)
            => sp.DurationMs > 0 ? sp.DurationMs
               : Spotify.DjTts.TryGet(sp.Key, out Spotify.DjTts.ClipInfo clip) && clip.State == Spotify.DjTts.ClipState.Ready && clip.DurationMs > 0
                   ? clip.DurationMs : -1;

        /// <summary>A DJ track's kind 28 (default while it is not known); asks for it once.</summary>
        static TrackCues CuesOf(EntityRef track)
        {
            var edges = Entities.Current.Edges;
            ReadOnlySpan<TrackCues> cues = edges.TrackCues.Payload(track.Slot);
            if (cues.Length > 0) return cues[0];
            Entities.EnsureEdge(FetchEdge.TrackCues, track.Slot, priority: FetchPriority.Prefetch);
            return default;
        }

        /// <summary>A key over what a seam reads that can land late (the voices' lengths, the next row), for Mix.Follow's re-plan.</summary>
        internal static long SeamVersion(EntityId context, ReadOnlySpan<QueueEdge> rows, int at)
        {
            if (Of(context) is null || (uint)at >= (uint)rows.Length) return 0;
            int next = Queue.NextIndex(rows, at);
            long a = NarrationOf(rows[at].ItemId) is { } va ? VoiceMs(va) : 0;
            long b = next >= 0 && NarrationOf(rows[next].ItemId) is { } vb ? VoiceMs(vb) : 0;
            return HashCode.Combine(a, b, next >= 0 ? rows[next].ItemId : 0UL, next);
        }

        /// <summary>The PUT's Mix half for a DJ seam (dj-protocol §2.4, §4, §7): the fade keys on A (the current item) and on B (the row it
        /// joins), <c>audio.only_allow_fade_on_advance</c> (the fade applies only when the row is reached by advancing), the cue points a track
        /// side used, and the radio mode on tracks. Null for a seam that does not join.</summary>
        internal static MixWire? WireOf(in SeamFacts seam)
        {
            MixTiming t = seam.Timing;
            if (!t.Mix) return null;
            static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);
            var outMeta = new List<KeyValuePair<string, string>>(10);
            var inMeta = new List<KeyValuePair<string, string>>(10);
            outMeta.Add(new("audio.fade_out_start_time", Num(t.OutStartMs)));
            outMeta.Add(new("audio.fade_out_duration", Num(t.OutDurationMs)));
            outMeta.Add(new("audio.fade_out_curves", DjCurves.Json(in t, incoming: false)));
            outMeta.Add(new("audio.only_allow_fade_on_advance", "true"));
            if (seam.VoiceA is null) TrackSide(outMeta, "out", seam.CuesA, t.Shape == MixShape.Radio || seam.Kind == SeamKind.Outro);
            inMeta.Add(new("audio.fade_in_start_time", Num(t.InStartMs)));
            inMeta.Add(new("audio.fade_in_duration", Num(t.InDurationMs)));
            inMeta.Add(new("audio.fade_overlap", Num(t.OutStartMs + t.OutDurationMs - t.InAtMs)));
            inMeta.Add(new("audio.fade_in_curves", DjCurves.Json(in t, incoming: true)));
            inMeta.Add(new("audio.only_allow_fade_on_advance", "true"));
            if (seam.VoiceB is null) TrackSide(inMeta, "in", seam.CuesB, true);
            return new MixWire(false, PlaylistMixFlags.None, seam.B, outMeta.ToArray(), inMeta.ToArray());

            static void TrackSide(List<KeyValuePair<string, string>> meta, string side, TrackCues cues, bool cueUsed)
            {
                meta.Add(new("audio.automix_mode", "radio"));
                uint position = side == "in" ? cues.StartCueMs : cues.EndCueMs;
                if (!cueUsed || position == 0) return;
                string p = "automix.fade_" + side + "_cuepoint.";
                meta.Add(new(p + "position", Num(position)));
                if (cues.OriginKnown)
                    meta.Add(new(p + "origin", (side == "in" ? cues.StartIsHuman : cues.EndIsHuman) ? "human" : "ml"));
                if (cues.TempoX10 > 0) meta.Add(new(p + "tempo", (cues.TempoX10 / 10.0).ToString("F6", CultureInfo.InvariantCulture)));
            }
        }
    }
}
