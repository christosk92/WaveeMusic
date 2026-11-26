// ── Playback/Playback.Mix.Auto.cs — CORE (#48 Mix, reference §7.2) ───────────────────────────────────────────────────
//
// SPOTIFY'S BEATMATCHED AUTO. The official client decides per pair whether to beatmatch, where, how long, at which speed
// and with which preset, from the two tracks' analysis alone. Fitted against the client's own Auto answers for 229
// adjacent pairs (`mix autocheck`): with the full inputs the rule below gives the client's answer for every pair.
//
//   1. Windows. Per track, the downbeats of the kind 217 grid (a downbeat whose next downbeat is exactly 4 beats on, and
//      the last one). A window is 2, 4, 8 or 16 bars from one downbeat to another: shorter than 35 s, bar lengths steady
//      (variance of the bar durations < 0.0005 s²), no weak downbeat on it (confidence ≤ 0.40, BeatGrid.WeakDownbeatBit).
//   2. Pairs. A window of A and one of B with the same bar count whose lengths differ by at most 10 % (B's speed).
//   3. Scores. r0 = the window's place: A's start late in A, B's end early in B (0 at the middle, 1 within the last / first
//      15 % or 90 s, a window longer than 30 % of its track scores 0); a pair with r0 < 0.40 is dropped. r1 = the cue
//      score (half A's fade-out cue, half B's fade-in cue: the best cue, else the first candidate in answer order, that
//      sits within 0.5 s of the window's middle (1.0), end or start (0.8); 0.95 times that for a candidate). r2 = 1 −
//      (32ths of the window where both tracks sing, kind 218 mean ≥ 0.2). r3 = key (Camelot same, ±1 or the relative key:
//      1; else 0.9 / 0.8 / 0.6 for ≤ 4 / ≤ 8 / more bars). r4 = genre (kind 6: a pop, rock or hip hop concept on either
//      track favours short windows, techno / house / EDM on both favours long ones). Total = r0 × (r1 + r2 + r3 + r4).
//   4. Choice. Sorted by bars then total, five per bar count kept, ranked by Pareto layer over (r0..r4), then total, then
//      bars. No pair left: not beatmatched (the plain rule, MixPlanner.Auto).
//   5. Recipe. startAMs / startBMs = the windows' first downbeats, durationMs = A's window, B covers its own window: speedB
//      = B's window / A's window in float seconds. The preset is a SHA-1 pick: {1, 10, 19} for 2 bars, {1, 2, 3, 4, 5, 17,
//      18, 8, 9, 10, 19} otherwise, indexed by the digest's first 8 bytes (little-endian) of uriA, uriB, bars, startA ms,
//      startB ms.
//
// What Wavee has live is all of it: the kind 217 grid (downbeats, weak downbeats), kind 222 Camelot, kind 28's best cues
// and both candidate lists in answer order, kind 218's vocal frames, kind 6's genre facts (Edges.TrackBeats, TrackCues,
// TrackCueCandidates, TrackVocals, TrackMixGenres; BeatmatchFacts.Live reads them). A track whose kind 218 or kind 6 has not
// landed plans without it (no vocals: r2 = 1; no genres: the short-window relation).
//
// Role: CORE (pure; values in, one plan out)

using System.Buffers;
using System.Security.Cryptography;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The genre facts the beatmatched Auto reads of one track (kind 6 descriptor concepts against the client's two
    /// genre groups).</summary>
    [Flags]
    public enum MixGenres : byte
    {
        /// <summary>Kind 6 was not read: the pair takes the short-window relation, the reference set's commonest (70 of 73).</summary>
        Unknown = 0,
        Known = 1 << 0,
        /// <summary>Pop, rock, hip hop, indie / alternative / modern rock, or the client's seventh short concept.</summary>
        Short = 1 << 1,
        /// <summary>Techno, house, EDM, or the client's fourth long concept.</summary>
        Long = 1 << 2,
    }

    /// <summary>One track's inputs to the beatmatched Auto beyond <see cref="PairFacts"/>: its kind 217 grid in
    /// <see cref="BeatGrid"/> packing, kind 28's best cues (−1 when absent) and candidate lists in answer order
    /// (<see cref="CueCandidate"/> packing, both lists in one span), kind 218's vocal frames and their rate, and its genre facts.</summary>
    public readonly ref struct BeatmatchFacts
    {
        public readonly ReadOnlySpan<uint> Beats;
        public readonly long BestStartMs, BestEndMs;
        public readonly ReadOnlySpan<uint> Candidates;
        public readonly ReadOnlySpan<byte> Vocal;
        public readonly float VocalFps;
        public readonly MixGenres Genres;

        public BeatmatchFacts(ReadOnlySpan<uint> beats, long bestStartMs, long bestEndMs, ReadOnlySpan<uint> candidates,
            ReadOnlySpan<byte> vocal, float vocalFps, MixGenres genres)
        {
            Beats = beats; BestStartMs = bestStartMs; BestEndMs = bestEndMs; Candidates = candidates;
            Vocal = vocal; VocalFps = vocalFps; Genres = genres;
        }

        /// <summary>The inputs from what the app stores for a track (the decoders' own shapes): the grid, the best cues of
        /// <paramref name="f"/>, the candidate lists, the <see cref="VocalBlock"/> run and the genre facts. On the reference set
        /// this is the rule with full inputs (`mix autocheck`'s live line runs the decoders on the wire bytes and this).</summary>
        public static BeatmatchFacts Live(in PairFacts f, ReadOnlySpan<uint> beats, ReadOnlySpan<uint> candidates,
            ReadOnlySpan<VocalBlock> vocals, MixGenres genres)
        {
            var c = f.Cues;
            var frames = VocalBlock.Read(vocals, out float fps);
            return new BeatmatchFacts(beats, c.HasStartCue ? c.StartCueMs : -1, c.HasEndCue ? c.EndCueMs : -1, candidates, frames, fps, genres);
        }

        /// <summary><see cref="Live(in PairFacts, ReadOnlySpan{uint}, ReadOnlySpan{uint}, ReadOnlySpan{VocalBlock}, MixGenres)"/>
        /// over the track's stored analysis (UI thread; the spans are the edge tables' own, read them before the next commit).</summary>
        public static BeatmatchFacts Live(in PairFacts f, int trackSlot, Edges edges)
        {
            var genres = edges.TrackMixGenres.Payload(trackSlot);
            return Live(in f, edges.TrackBeats.Payload(trackSlot), edges.TrackCueCandidates.Payload(trackSlot),
                edges.TrackVocals.Payload(trackSlot), genres.Length > 0 ? genres[0] : MixGenres.Unknown);
        }

        /// <summary>A version that moves whenever any input of a track's beatmatched Auto lands (the follow key's).</summary>
        public static uint Version(int trackSlot, Edges edges)
            => edges.TrackBeats.Version(trackSlot) * 31u + edges.TrackCues.Version(trackSlot) * 961u + edges.TrackVocals.Version(trackSlot) * 29791u
               + edges.TrackMixGenres.Version(trackSlot) * 923521u + edges.TrackCueCandidates.Version(trackSlot);
    }

    /// <summary>The scores of the window the beatmatched Auto chose (file header, step 3), for the inspector.</summary>
    public readonly record struct BeatmatchScores(float R0, float R1, float R2, float R3, float R4, float Total, int Layer, int Kept, int Found);

    public static partial class MixPlanner
    {
        // The client's defaults (core-automix; max_auto_transition_length_fraction_permil = 300 is the server's value).
        const double MaxWindowS = 35.0, BarVariance = 0.0005;
        const float LengthFraction = 0.3f, SpeedSpread = 0.1f, BaseThreshold = 0.4f, KneeFraction = 0.15f, KneeS = 90f;
        const float CueMargin = 0.5f, CueMiddle = 1.0f, CueEdge = 0.8f, CueNonBest = 0.95f, VocalThreshold = 0.2f;
        const float KeyShort = 0.9f, KeyMedium = 0.8f, KeyLong = 0.6f, GenreIdeal = 1.0f, GenreFair = 0.8f, GenrePoor = 0.6f;
        const int KeepPerBars = 5;
        static readonly byte[] WindowBars = [2, 4, 8, 16];
        static readonly byte[] PresetsShort = [1, 10, 19];
        static readonly byte[] PresetsLong = [1, 2, 3, 4, 5, 17, 18, 8, 9, 10, 19];

        struct Window
        {
            public byte Bars;
            public uint StartMs, EndMs;
            public float S, E;
            public double Cue;
            public uint Vocal;   // the 32ths whose mean vocal activity is at least VocalThreshold
        }

        struct Pairing
        {
            public int A, B, Order;
            public byte Bars;
            public float R0, R1, R2, R3, R4, Total;
        }

        /// <summary>Spotify's beatmatched Auto for A → B (file header): true with the plan when the client would beatmatch the
        /// pair, false when it would not (the caller takes <see cref="Auto"/>).</summary>
        public static bool AutoBeatmatched(in PairFacts a, in BeatmatchFacts ia, in PairFacts b, in BeatmatchFacts ib, out MixTiming timing)
            => AutoBeatmatched(in a, in ia, in b, in ib, out timing, out _);

        /// <inheritdoc cref="AutoBeatmatched(in PairFacts, in BeatmatchFacts, in PairFacts, in BeatmatchFacts, out MixTiming)"/>
        public static bool AutoBeatmatched(in PairFacts a, in BeatmatchFacts ia, in PairFacts b, in BeatmatchFacts ib, out MixTiming timing,
            out BeatmatchScores scores)
        {
            timing = default;
            scores = default;
            if (a.DurationMs <= 0 || b.DurationMs <= 0 || ia.Beats.Length < 3 || ib.Beats.Length < 3) return false;
            var wa = Windows(in ia, outgoing: true);
            var wb = Windows(in ib, outgoing: false);
            if (wa.Length == 0 || wb.Length == 0) return false;
            float durA = (float)(a.DurationMs * 0.001), durB = (float)(b.DurationMs * 0.001);
            int key = KeyRelation(a.Camelot, b.Camelot);
            int genre = GenreRelation(ia.Genres, ib.Genres);

            // A in (start, end) order, B in (length, start) order: the client's tree and its length-sorted list.
            Array.Sort(wa, static (x, y) => x.S != y.S ? x.S.CompareTo(y.S) : x.E.CompareTo(y.E));
            Array.Sort(wb, static (x, y) =>
            {
                float lx = x.E - x.S, ly = y.E - y.S;
                return lx != ly ? lx.CompareTo(ly) : x.S.CompareTo(y.S);
            });
            var found = new List<Pairing>();
            for (int i = 0; i < wa.Length; i++)
            {
                ref readonly var x = ref wa[i];
                double la = (double)x.E - x.S;
                for (int j = 0; j < wb.Length; j++)
                {
                    ref readonly var y = ref wb[j];
                    if (y.Bars != x.Bars) continue;
                    double lb = (double)y.E - y.S;
                    if (Math.Abs(lb / la - 1) > SpeedSpread) continue;
                    double r0 = PlaceScore(in x, in y, durA, durB);
                    if (r0 < BaseThreshold) continue;
                    int both = System.Numerics.BitOperations.PopCount(x.Vocal & y.Vocal);
                    var p = new Pairing
                    {
                        A = i, B = j, Order = found.Count, Bars = x.Bars,
                        R0 = (float)r0, R1 = (float)(0.5 * (x.Cue + y.Cue)), R2 = (float)(1 - both / 32.0),
                        R3 = KeyScore(key, x.Bars), R4 = GenreScore(genre, x.Bars),
                    };
                    p.Total = Total(in p);
                    if (p.Total > 0) found.Add(p);
                }
            }
            if (found.Count == 0) return false;

            // Bars first, then the total; five per bar count; Pareto layers over the five scores; layer, total, bars.
            found.Sort(static (x, y) => x.Bars != y.Bars ? y.Bars.CompareTo(x.Bars) : x.Total != y.Total ? y.Total.CompareTo(x.Total) : x.Order.CompareTo(y.Order));
            var kept = new List<Pairing>();
            int run = 0, runBars = -1;
            foreach (var p in found)
            {
                run = p.Bars == runBars ? run + 1 : 1;
                runBars = p.Bars;
                if (run <= KeepPerBars) kept.Add(p with { Order = kept.Count });
            }
            var layer = new int[kept.Count];
            Array.Fill(layer, -1);
            for (int l = 0, left = kept.Count; left > 0; l++)
            {
                var front = new List<int>();
                for (int i = 0; i < kept.Count; i++)
                {
                    if (layer[i] >= 0) continue;
                    bool beaten = false;
                    for (int j = 0; j < kept.Count && !beaten; j++)
                        beaten = j != i && layer[j] < 0 && Dominates(kept[j], kept[i]);
                    if (!beaten) front.Add(i);
                }
                foreach (int i in front) layer[i] = l;
                left -= front.Count;
            }
            int best = 0;
            for (int i = 1; i < kept.Count; i++)
            {
                var c = kept[i]; var w = kept[best];
                int d = layer[i] != layer[best] ? layer[i].CompareTo(layer[best]) : c.Total != w.Total ? w.Total.CompareTo(c.Total) : w.Bars.CompareTo(c.Bars);
                if (d < 0) best = i;
            }
            var win = kept[best];
            scores = new BeatmatchScores(win.R0, win.R1, win.R2, win.R3, win.R4, win.Total, layer[best], kept.Count, found.Count);
            ref readonly var wA = ref wa[win.A];
            ref readonly var wB = ref wb[win.B];
            float k = 0.001f;
            float spanA = (float)wA.EndMs * k - (float)wA.StartMs * k;
            float spanB = (float)wB.EndMs * k - (float)wB.StartMs * k;
            float speedB = spanB / spanA;
            int preset = Preset(a.Uri, b.Uri, win.Bars, wA.S, wB.S);
            timing = MixTiming.Of(MixSource.Auto, preset, default, win.Bars, wA.StartMs, wA.EndMs - wA.StartMs, wB.StartMs,
                wB.EndMs - wB.StartMs, speedB, a.TempoX10 / 10f);
            return true;
        }

        static float Total(in Pairing p)
        {
            float s = p.R4;
            s = p.R3 + s;
            s = p.R2 + s;
            s = p.R1 + s;
            return s * p.R0;
        }

        static bool Dominates(in Pairing x, in Pairing y)
            => x.R0 >= y.R0 && x.R1 >= y.R1 && x.R2 >= y.R2 && x.R3 >= y.R3 && x.R4 >= y.R4
               && (x.R0 > y.R0 || x.R1 > y.R1 || x.R2 > y.R2 || x.R3 > y.R3 || x.R4 > y.R4);

        static float Seconds(uint ms) => (float)ms * 0.001f;

        /// <summary>The track's windows (file header, step 1) with their cue score (A: fade-out cues, B: fade-in cues) and vocal
        /// 32ths.</summary>
        static Window[] Windows(in BeatmatchFacts f, bool outgoing)
        {
            uint[] down = ArrayPool<uint>.Shared.Rent(f.Beats.Length);
            try
            {
                int nd = 0;
                var beats = f.Beats;
                for (int i = 0; i < beats.Length;)
                {
                    if (!BeatGrid.IsDownbeat(beats[i])) { i++; continue; }
                    int j = i + 1;
                    while (j < beats.Length && !BeatGrid.IsDownbeat(beats[j])) j++;
                    // A downbeat kept with its weak flag (bit 31 of `down`): no window may touch it.
                    uint kept = BeatGrid.StartMs(beats[i]) | (BeatGrid.IsWeakDownbeat(beats[i]) ? 0x8000_0000u : 0u);
                    if (j >= beats.Length) { down[nd++] = kept; i++; continue; }
                    if (j - i == 4) { down[nd++] = kept; i += 4; continue; }
                    i++;   // an irregular bar: the client's 2/4 paths are not modelled (no 2/4 grid in the reference set)
                }
                var list = new List<Window>();
                foreach (byte n in WindowBars)
                {
                    for (int s0 = 0; s0 + n < nd; s0++)
                    {
                        float s = Seconds(down[s0] & BeatGrid.MsMask), e = Seconds(down[s0 + n] & BeatGrid.MsMask);
                        if (!((double)e - s < MaxWindowS)) continue;
                        bool weak = false;
                        for (int x = s0; x <= s0 + n && !weak; x++) weak = (down[x] & 0x8000_0000u) != 0;
                        if (weak) continue;
                        double sum = 0, sq = 0;
                        for (int x = s0; x < s0 + n; x++)
                        {
                            double bar = (double)Seconds(down[x + 1] & BeatGrid.MsMask) - Seconds(down[x] & BeatGrid.MsMask);
                            sum += bar; sq += bar * bar;
                        }
                        double mean = sum / n;
                        if (!(sq / n - mean * mean < BarVariance)) continue;
                        list.Add(new Window
                        {
                            Bars = n, StartMs = down[s0] & BeatGrid.MsMask, EndMs = down[s0 + n] & BeatGrid.MsMask, S = s, E = e,
                            Cue = CueScore(outgoing ? f.BestEndMs : f.BestStartMs, f.Candidates, outgoing, s, e),
                            Vocal = VocalMask(f.Vocal, f.VocalFps, s, e),
                        });
                    }
                }
                return list.ToArray();
            }
            finally { ArrayPool<uint>.Shared.Return(down); }
        }

        /// <summary>r1's half for one track (file header, step 3): the best cue, else the first candidate of the list (end
        /// candidates for A, start candidates for B) in answer order that lands.</summary>
        static double CueScore(long bestMs, ReadOnlySpan<uint> candidates, bool end, float s, float e)
        {
            double mid = s + ((double)e - s) * 0.5;
            float bestPos = -1f;
            if (bestMs >= 0)
            {
                bestPos = Seconds((uint)bestMs);
                double r = At(bestPos, 1.0, mid, s, e);
                if (r >= 0) return r;
            }
            foreach (uint c in candidates)
            {
                if (CueCandidate.IsEnd(c) != end) continue;
                float pos = Seconds(CueCandidate.Ms(c));
                if (pos == bestPos) continue;
                double r = At(pos, CueNonBest, mid, s, e);
                if (r >= 0) return r;
            }
            return 0;

            static double At(float c, double factor, double mid, float s, float e)
            {
                if (Math.Abs(c - mid) < CueMargin) return factor * CueMiddle;
                if (Math.Abs(c - (double)e) < CueMargin) return factor * CueEdge;
                if (Math.Abs(c - (double)s) < CueMargin) return factor * CueEdge;
                return -1;
            }
        }

        static uint VocalMask(ReadOnlySpan<byte> frames, float fps, float s, float e)
        {
            if (frames.IsEmpty || fps <= 0 || s >= e) return 0;
            Span<double> sum = stackalloc double[32];
            Span<int> count = stackalloc int[32];
            sum.Clear(); count.Clear();
            int i0 = Math.Clamp((int)MathF.Floor(fps * s), 0, frames.Length), i1 = Math.Clamp((int)MathF.Ceiling(fps * e), 0, frames.Length);
            double len = (double)e - s;
            for (int i = i0; i < i1; i++)
            {
                int bin = (int)((i / (double)fps - s) * 32 / len);
                if ((uint)bin >= 32) continue;
                sum[bin] += frames[i] / 255.0;
                count[bin]++;
            }
            uint mask = 0;
            for (int b = 0; b < 32; b++)
                if (count[b] > 0 && sum[b] / count[b] >= VocalThreshold) mask |= 1u << b;
            return mask;
        }

        /// <summary>r0: A's window late in A, B's window early in B (file header, step 3).</summary>
        static double PlaceScore(in Window a, in Window b, float durA, float durB)
        {
            double la = (double)a.E - a.S, lb = (double)b.E - b.S;
            if (la > (double)LengthFraction * durA || lb > (double)LengthFraction * durB || la > MaxWindowS || lb > MaxWindowS) return 0;
            double kA = Math.Min(KneeFraction, KneeS / (double)durA), kB = Math.Min(KneeFraction, KneeS / (double)durB);
            double xa = (a.S / (double)durA - 0.5 - 15 / (double)durA) / (1 - kA - (0.5 + 15 / (double)durA));
            double mid = Math.Max(0.5 - 15 / (double)durB, kB + 0.01);
            double xb = (b.E / (double)durB - mid) / (kB - mid);
            return Math.Clamp(xa, 0, 1) * Math.Clamp(xb, 0, 1);
        }

        /// <summary>The client's Camelot relation: 0 same or ±1 on the same ring, 1 the relative key, 2 ±1 across rings, 3 ±2
        /// on the same ring, 4 anything else (an unknown key: 0). Its ±1 is (n + 1) mod 12, so 11 → 12 does not count.</summary>
        static int KeyRelation(byte from, byte to)
        {
            if (from is 0 or > 24 || to is 0 or > 24) return 0;
            int na = (from + 1) / 2, nb = (to + 1) / 2;
            bool sameRing = ((from - 1) & 1) == ((to - 1) & 1), sameNumber = na == nb;
            bool adjacent = (na + 1) % 12 == nb || (nb + 1) % 12 == na;
            bool two = (na + 2) % 12 == nb || (nb + 2) % 12 == na;
            if (sameNumber && sameRing) return 0;
            if (sameRing && adjacent) return 0;
            if (sameNumber) return 1;
            if (adjacent) return 2;
            if (two) return sameRing ? 3 : 4;
            return 4;
        }

        static float KeyScore(int relation, int bars) => relation <= 2 ? 1f : bars <= 4 ? KeyShort : bars <= 8 ? KeyMedium : KeyLong;

        static int GenreRelation(MixGenres a, MixGenres b)
        {
            if ((a & MixGenres.Known) == 0 && (b & MixGenres.Known) == 0) return 1;
            if (((a | b) & MixGenres.Short) != 0) return 1;
            if ((a & b & MixGenres.Long) != 0) return 2;
            return 3;
        }

        static float GenreScore(int relation, int bars) => relation switch
        {
            1 => bars <= 4 ? GenreIdeal : bars <= 8 ? GenreFair : GenrePoor,
            2 => bars >= 16 ? GenreIdeal : bars >= 8 ? GenreFair : GenrePoor,
            _ => GenreIdeal,
        };

        /// <summary>The client's two genre groups (kind 6 descriptor concepts): a track carrying one of the first favours short
        /// windows, a pair both carrying one of the second favours long ones.</summary>
        internal static readonly string[] ShortGenres =
        [
            "spotify:concept:24pGOSaKeoU6bobuwqnMbJ", "spotify:concept:3LiuRrANWrpeGfMuENUG04", "spotify:concept:08Q9hhtRafUxsXWyH2Bo9V",
            "spotify:concept:6HmfjrBSjLZsidG2E8Iggk", "spotify:concept:3hvUlYaIyCcKNGFiVnqZaQ", "spotify:concept:0lLE9kiNdPuaXWpH2jhWUj",
            "spotify:concept:4UjCilZFeXnwJOPLmjiE42",
        ];
        internal static readonly string[] LongGenres =
        [
            "spotify:concept:4NyuO3zCN13qIP8k4vNS0D", "spotify:concept:6Vwq57ZEeDu8TNVzd5b9rC", "spotify:concept:1J9pUDx6minjAWzoY3j9jv",
            "spotify:concept:0d5BcmOXdnNbXcgd7KiZrl",
        ];

        /// <summary>A track's <see cref="MixGenres"/> from its kind 6 descriptor concept uris.</summary>
        public static MixGenres GenresOf(IEnumerable<string> concepts)
        {
            var g = MixGenres.Known;
            foreach (string c in concepts) g |= GenresOf(System.Text.Encoding.UTF8.GetBytes(c));
            return g;
        }

        /// <summary>The genre group one concept uri (UTF-8) belongs to: <see cref="MixGenres.Short"/>, <see cref="MixGenres.Long"/>
        /// or none. No allocation (the kind 6 decoder's per-descriptor call).</summary>
        public static MixGenres GenresOf(ReadOnlySpan<byte> concept)
        {
            var g = MixGenres.Unknown;
            foreach (string c in ShortGenres) if (Same(concept, c)) g |= MixGenres.Short;
            foreach (string c in LongGenres) if (Same(concept, c)) g |= MixGenres.Long;
            return g;

            static bool Same(ReadOnlySpan<byte> utf8, string ascii)
            {
                if (utf8.Length != ascii.Length) return false;
                for (int i = 0; i < utf8.Length; i++) if (utf8[i] != ascii[i]) return false;
                return true;
            }
        }

        /// <summary>The preset (file header, step 5). Without the uris: 19, the reference set's commonest.</summary>
        static int Preset(string? uriA, string? uriB, int bars, float startA, float startB)
        {
            var table = bars < 4 ? PresetsShort : PresetsLong;
            if (string.IsNullOrEmpty(uriA) || string.IsNullOrEmpty(uriB)) return 19;
            int na = System.Text.Encoding.UTF8.GetByteCount(uriA), nb = System.Text.Encoding.UTF8.GetByteCount(uriB);
            byte[] data = new byte[na + nb + 12];
            System.Text.Encoding.UTF8.GetBytes(uriA, data);
            System.Text.Encoding.UTF8.GetBytes(uriB, data.AsSpan(na));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(na + nb), bars);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(na + nb + 4), (int)(startA * 1000f));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(na + nb + 8), (int)(startB * 1000f));
            Span<byte> digest = stackalloc byte[20];
            SHA1.HashData(data, digest);
            ulong h = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest);
            return table[(int)(h % (ulong)table.Length)];
        }
    }
}
