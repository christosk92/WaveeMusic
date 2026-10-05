// ── Shell/Visualizer.Covers.cs ─────────────────────────────────────────────────────────────────────────────────────
// Visualizer.Covers: the Zune family's pure rules — the cover pool (queue + recently played), the Mosaic wall plan, its
// beat flips, the Spotlight photo schedule with its Ken Burns params, and the Type face's drift
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 260 lines
// Spec: viz-app-plan.md §2.15-§2.17, §3.6
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THE ZUNE FACES DECIDE, AND WHERE. Visualizer.Zune.UI.cs reads the entity tables and binds the slab; every choice
// it makes about WHICH cover, WHICH tile, WHICH photo and HOW FAST is a function here, over plain ints and floats:
//
//   Collect   current cover → up next in order → recently played, newest first; deduped by image id; capped at 40
//   Tiles     a cols × rows Metro wall, mostly 1×1 with ~14 % 2×2 cells, seeded (the wall is stable per song)
//   Flips     two tiles per beat (one on a weak GPU), never a tile that flipped on the previous beat
//   Spot      one photo per 8 bars, switched ON the downbeat; a Ken Burns pan per photo, alternating direction
//   Drift     the three giant rows' speeds from the energy, and the wrap that keeps an offset inside one period
//
// NOTHING IS RANDOM. Every "pick" is a hash of (seed, index, salt), so the same song, the same beat and the same pool
// give the same wall — tests pin it, and a screenshot of a bug can be reproduced.
//
// Rules: `System`-only; no allocation outside `Tiles.Plan` (mount time); no LINQ, no closures (P8, P9). Image ids are
// the raw `StringId.Value` ints (0 = no image) so the rules stay engine-free; the UI resolves them through
// `Controls.ArtUrl`. `public` because Wavee.Tests is a ProjectReference.

namespace Wavee;

public static partial class Visualizer
{
    public static class Covers
    {
        /// <summary>The pool's ceiling: forty distinct covers is more than one wall shows at once.</summary>
        public const int Cap = 40;
        /// <summary>Below this the wall repeats covers visibly, so Mosaic varies them by saturation instead (§2.16).</summary>
        public const int MinPool = 12;

        /// <summary>The pool in order — <paramref name="current"/>, then <paramref name="upNext"/> as queued, then
        /// <paramref name="recents"/> newest-first — deduped by image id, zeros skipped, capped at <see cref="Cap"/> (or
        /// <paramref name="into"/>'s length). Returns how many were written.</summary>
        public static int Collect(int current, ReadOnlySpan<int> upNext, ReadOnlySpan<int> recents, Span<int> into)
        {
            int cap = Math.Min(into.Length, Cap), n = 0;
            n = Add(current, into, n, cap);
            for (int i = 0; i < upNext.Length && n < cap; i++) n = Add(upNext[i], into, n, cap);
            for (int i = 0; i < recents.Length && n < cap; i++) n = Add(recents[i], into, n, cap);
            return n;
        }

        static int Add(int id, Span<int> into, int n, int cap)
        {
            if (id == 0 || n >= cap) return n;
            for (int i = 0; i < n; i++) if (into[i] == id) return n;
            into[n] = id;
            return n + 1;
        }

        /// <summary>Does the pool fill a wall without visible repeats?</summary>
        public static bool Ready(int count) => count >= MinPool;

        /// <summary>A well-mixed 32-bit hash of (seed, a, b) — murmur3's finaliser over a simple combine.</summary>
        public static uint Hash(uint seed, int a, int b)
        {
            unchecked
            {
                uint h = seed ^ 0x9E3779B9u;
                h ^= (uint)a * 0x85EBCA6Bu;
                h = ((h << 13) | (h >> 19)) * 5u + 0xE6546B64u;
                h ^= (uint)b * 0xC2B2AE35u;
                h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
                return h;
            }
        }

        /// <summary><see cref="Hash"/> as a float in [0, 1).</summary>
        public static float Unit(uint seed, int a, int b) => (Hash(seed, a, b) >> 8) * (1f / 16777216f);

        /// <summary>One cell of the wall: its top-left column/row and its edge in cells (1 or 2).</summary>
        public readonly record struct Tile(int Col, int Row, int Size);

        // ── Mosaic: the wall ───────────────────────────────────────────────────────────────────────────────────────

        public static class Tiles
        {
            public const int Cols = 12, PreviewCols = 6;
            public const int MaxRows = 9, PreviewMaxRows = 3;
            /// <summary>The chance a free 1×1 slot becomes a 2×2 (the prototype's 0.14).</summary>
            public const float BigChance = 0.14f;
            /// <summary>The spectrum span the columns map onto (the prototype's 44 of 48 bands — the top four are mostly air).</summary>
            public const int BandSpan = 44;

            /// <summary>Plan a <paramref name="cols"/> × <paramref name="rows"/> wall row-major: each free cell becomes a 2×2
            /// with <see cref="BigChance"/> when the three cells it would take are free and inside the wall, else a 1×1.
            /// Every cell is covered exactly once; the same seed gives the same plan. <paramref name="big"/> false (weak GPU,
            /// preview) plans 1×1 only.</summary>
            public static Tile[] Plan(int cols, int rows, uint seed, bool big = true)
            {
                if (cols <= 0 || rows <= 0) return [];
                var used = new bool[cols * rows];
                var tiles = new List<Tile>(cols * rows);
                int k = 0;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        int at = r * cols + c;
                        if (used[at]) continue;
                        bool two = big && c < cols - 1 && r < rows - 1 && !used[at + 1] && Unit(seed, k, 21) < BigChance;
                        used[at] = true;
                        if (two) { used[at + 1] = true; used[at + cols] = true; used[at + cols + 1] = true; }
                        tiles.Add(new Tile(c, r, two ? 2 : 1));
                        k++;
                    }
                return tiles.ToArray();
            }

            /// <summary>Rows of square cells (edge <c>w / cols</c>) that cover <paramref name="h"/>, at least one.</summary>
            public static int RowsFor(float w, float h, int cols, int maxRows)
            {
                if (cols <= 0 || w <= 0f || h <= 0f) return 1;
                return Math.Clamp((int)MathF.Ceiling(h / (w / cols)), 1, Math.Max(1, maxRows));
            }

            /// <summary>The band a column dims and brightens with: low on the left, high on the right.</summary>
            public static int BandOf(int col, int cols) => cols <= 0 ? 0 : Math.Clamp(col * BandSpan / cols, 0, BandSpan - 1);

            /// <summary>The column veil: 0.55 at silence, clear by a level of 1.1 (the prototype's <c>.55 − .5·band</c>).</summary>
            public static float VeilAlpha(float band) => MathF.Max(0f, 0.55f - 0.5f * band);

            /// <summary>ONE decode size for the whole wall (the largest tile's edge, on a 32-px grid), so every pool cover is
            /// resident at the size any tile asks for and a flip never reveals a placeholder.</summary>
            public static int DecodeFor(float cell, int maxSize, bool preview)
            {
                if (preview) return 48;
                int px = (int)MathF.Ceiling(MathF.Max(1f, cell * maxSize) / 32f) * 32;
                return Math.Clamp(px, 64, 320);
            }

            /// <summary>The thin-pool variation: with fewer than <see cref="MinPool"/> covers a third of the tiles show their
            /// cover at 0.55 and a third at 0.2 saturation, so a wall of three covers still reads as many.</summary>
            public static float FallbackSaturation(int tile, uint seed) => (Hash(seed, tile, 23) % 3u) switch { 0u => 1f, 1u => 0.55f, _ => 0.2f };

            /// <summary>The tile that shows the current cover: the FIRST 2×2, else tile 0.</summary>
            public static int LeadOf(ReadOnlySpan<Tile> plan)
            {
                for (int t = 0; t < plan.Length; t++) if (plan[t].Size == 2) return t;
                return 0;
            }

            /// <summary>Each tile's starting pool index: the lead tile (<see cref="LeadOf"/>) shows the current cover (index
            /// 0); the rest are hashed, never repeating the previous tile's. −1 for every tile when the pool is empty.</summary>
            public static void Assign(ReadOnlySpan<Tile> plan, int poolCount, uint seed, Span<int> into)
            {
                int n = Math.Min(plan.Length, into.Length);
                if (poolCount <= 0) { into[..n].Fill(-1); return; }
                int lead = LeadOf(plan[..n]);
                for (int t = 0; t < n; t++)
                {
                    int c = t == lead ? 0 : (int)(Hash(seed, t, 22) % (uint)poolCount);
                    if (t != lead && poolCount > 1 && t > 0 && c == into[t - 1]) c = (c + 1) % poolCount;
                    into[t] = c;
                }
            }
        }

        // ── Mosaic: the flips ──────────────────────────────────────────────────────────────────────────────────────

        public static class Flips
        {
            /// <summary>The prototype's 0.07 per 30 Hz tick: a full flip (1 → −1) takes ≈ 0.95 s.</summary>
            public const float RatePerSec = 2.1f;
            /// <summary>Calm halves the flip rate (§1.2).</summary>
            public const float CalmScale = 0.5f;
            const uint Salt = 0x5EEDF11Fu;

            /// <summary>Pick up to <paramref name="want"/> (≤ 2) distinct tiles to flip on <paramref name="beat"/>, never
            /// <paramref name="lastA"/> or <paramref name="lastB"/> (the previous beat's picks). Returns how many were picked;
            /// an unpicked out-parameter is −1.</summary>
            public static int Pick(int beat, int tileCount, int lastA, int lastB, int want, out int a, out int b)
            {
                a = -1; b = -1;
                if (tileCount <= 0 || want <= 0) return 0;
                a = Probe((int)(Hash(Salt, beat, 30) % (uint)tileCount), tileCount, lastA, lastB, -1);
                if (a < 0) return 0;
                if (want < 2) return 1;
                b = Probe((int)(Hash(Salt, beat, 31) % (uint)tileCount), tileCount, lastA, lastB, a);
                return b < 0 ? 1 : 2;
            }

            static int Probe(int start, int n, int x, int y, int z)
            {
                for (int i = 0; i < n; i++)
                {
                    int t = (start + i) % n;
                    if (t != x && t != y && t != z) return t;
                }
                return -1;
            }

            /// <summary>The pool index a flipping tile turns to: one of the next eight covers after its own, never itself
            /// while the pool holds two or more.</summary>
            public static int NextCover(int current, int beat, int tile, int poolCount)
            {
                if (poolCount <= 1) return 0;
                int from = (uint)current < (uint)poolCount ? current : 0;
                int hop = 1 + (int)(Hash(Salt, beat, tile * 2 + 40) % (uint)Math.Min(8, poolCount - 1));
                return (from + hop) % poolCount;
            }

            /// <summary><see cref="NextCover"/> restricted to covers already RESIDENT — shown on some tile (<paramref name="shown"/>
            /// is each tile's pool index, −1 = none). The wall decodes every tile at one size, so a shown cover is a decoded
            /// one and a flip never lands on a placeholder. The first resident of the next eight after the tile's own, from
            /// NextCover's hashed hop; else a hashed tile's cover; −1 when no other cover is resident.</summary>
            public static int NextResident(int current, int beat, int tile, ReadOnlySpan<int> shown, int poolCount)
            {
                if (poolCount <= 1 || shown.IsEmpty) return -1;
                int from = (uint)current < (uint)poolCount ? current : 0;
                int span = Math.Min(8, poolCount - 1);
                int start = (int)(Hash(Salt, beat, tile * 2 + 40) % (uint)span);
                for (int i = 0; i < span; i++)
                {
                    int c = (from + 1 + (start + i) % span) % poolCount;
                    if (c != current && shown.IndexOf(c) >= 0) return c;
                }
                int t0 = (int)(Hash(Salt, beat, tile * 2 + 41) % (uint)shown.Length);
                for (int i = 0; i < shown.Length; i++)
                {
                    int c = shown[(t0 + i) % shown.Length];
                    if ((uint)c < (uint)poolCount && c != current) return c;
                }
                return -1;
            }

            /// <summary>How far a flip travels in <paramref name="dtSec"/>.</summary>
            public static float Step(float dtSec, bool calm) => RatePerSec * MathF.Max(0f, dtSec) * (calm ? CalmScale : 1f);

            /// <summary>The bound ScaleX of a flip in [−1, 1]: a card seen edge-on is a hairline, never zero (a zero scale
            /// is a degenerate transform).</summary>
            public static float ScaleOf(float flip) => MathF.Max(0.02f, MathF.Min(1f, MathF.Abs(flip)));
        }

        // ── Spotlight: the photo schedule ──────────────────────────────────────────────────────────────────────────

        public static class Spot
        {
            public const int BarsPerPhoto = 8;
            /// <summary>The gallery photos the face cycles (the artist page shows a dozen too).</summary>
            public const int MaxPhotos = 12;
            /// <summary>Without a tempo an 8-bar span is taken as 24 s (8 bars of 4/4 at 80 bpm).</summary>
            public const float DefaultSpanMs = 24_000f;

            /// <summary>Ken Burns for one photo: a uniform scale from → to and a pan of (Dx, Dy) box fractions over the span.</summary>
            public readonly record struct KenBurns(float ScaleFrom, float ScaleTo, float Dx, float Dy);

            /// <summary>The 8-bar cycle a downbeat's bar index falls in. It changes only ON a downbeat whose bar is a
            /// multiple of <see cref="BarsPerPhoto"/>.</summary>
            public static int CycleOf(int downbeatBar) => downbeatBar <= 0 ? 0 : downbeatBar / BarsPerPhoto;

            /// <summary>The photo a cycle shows, wrapping; −1 with no photos.</summary>
            public static int PhotoAt(int cycle, int count) => count <= 0 ? -1 : (int)((uint)Math.Max(0, cycle) % (uint)count);

            /// <summary>The 8-bar span in ms for a tempo in tenths of a bpm (<c>Track.Tempo</c>), clamped to 8–60 s.</summary>
            public static float SpanMs(int tempoX10)
            {
                if (tempoX10 <= 0) return DefaultSpanMs;
                float beatMs = 600_000f / tempoX10;
                return Math.Clamp(beatMs * 4f * BarsPerPhoto, 8_000f, 60_000f);
            }

            /// <summary>The cross-fade: 8 % of the span, 0.9–2.4 s.</summary>
            public static float FadeMs(float spanMs) => Math.Clamp(spanMs * 0.08f, 900f, 2_400f);

            /// <summary>The next photo is decoded under the current one for at most the last two bars of a cycle, and no
            /// more than 10 s ahead — but never less than the last bar, so the switch never reveals a
            /// placeholder. Outside that window only the shown photo (and, while it fades out, the previous) is resident.</summary>
            public const int PreloadBarsMax = 2;
            public const float PreloadAheadMs = 10_000f;

            /// <summary>How many bars before the switch the next photo mounts, for an 8-bar span of <paramref name="spanMs"/>.</summary>
            public static int PreloadBars(float spanMs)
            {
                float barMs = spanMs / BarsPerPhoto;
                if (!(barMs > 0f)) return 1;
                return Math.Clamp((int)MathF.Floor(PreloadAheadMs / barMs), 1, PreloadBarsMax);
            }

            /// <summary>Is the next photo due: <paramref name="bar"/> (the bar under the playhead) is within the last
            /// <see cref="PreloadBars"/> bars of <paramref name="cycle"/>, or past its end before the switching downbeat.</summary>
            public static bool Preload(int bar, int cycle, float spanMs)
                => (long)(cycle + 1) * BarsPerPhoto - bar <= PreloadBars(spanMs);

            /// <summary>The pan for a cycle: 1.04 → 1.14, ±3 % across alternating by cycle, a hashed ±1.5 % drift down or up.</summary>
            public static KenBurns Pan(int cycle)
            {
                float dx = (cycle & 1) == 0 ? 0.03f : -0.03f;
                float dy = (Unit(0x5B07u, cycle, 50) - 0.5f) * 0.03f;
                return new KenBurns(1.04f, 1.14f, dx, dy);
            }

            /// <summary>The scrim follows the level ±0.1 about 0.9, so a loud passage lifts the photo a little.</summary>
            public static float ScrimAlpha(float level) => Math.Clamp(0.9f - 0.2f * (Math.Clamp(level, 0f, 1f) - 0.5f), 0.8f, 1f);

            /// <summary>The photo decode: min(W, 1920) on the stage, 160 in a gallery tile.</summary>
            public static int DecodeFor(float w, bool preview)
                => preview ? 160 : Math.Clamp((int)MathF.Ceiling(w), 320, 1920);
        }

        // ── Type: the drift ────────────────────────────────────────────────────────────────────────────────────────

        public static class Drift
        {
            public const int Rows = 3;
            /// <summary>The space between two copies of a row's word, in em of that row.</summary>
            public const float GapEm = 0.35f;

            /// <summary>Row 0 artist, 1 title, 2 album — the prototype's sizes at a 920-DIP-tall stage.</summary>
            public static float SizeOf(int row) => row switch { 0 => 420f, 1 => 300f, _ => 150f };
            public static ushort WeightOf(int row) => row == 1 ? (ushort)600 : (ushort)300;
            /// <summary>Where the row's baseline sits, as a fraction of the face height.</summary>
            public static float BaselineOf(int row) => row switch { 0 => 0.36f, 1 => 0.66f, _ => 0.90f };
            /// <summary>The ink rung of the two quiet rows (the title is the accent): stronger on dark, where ink is light.</summary>
            public static float InkAlpha(int row, bool dark) => row == 0 ? (dark ? 0.10f : 0.08f) : (dark ? 0.16f : 0.13f);

            /// <summary>The drive the speeds follow: mostly the 4-bar energy (smooth), a little of the live level.</summary>
            public static float Drive(float energy, float level) => Math.Clamp(0.7f * energy + 0.3f * level, 0f, 1f);

            /// <summary>Px per ms at scale 1 (the prototype's): the title runs against the other two.</summary>
            public static float Speed(int row, float drive) => row switch
            {
                0 => 0.012f + 0.02f * drive,
                1 => -(0.02f + 0.03f * drive),
                _ => 0.03f + 0.04f * drive,
            };

            /// <summary>The title's alpha: 0.55 at rest, up to 1 on a kick over a bass line.</summary>
            public static float TitleAlpha(float kick, float low) => MathF.Min(1f, 0.55f + 0.35f * kick + 0.1f * low);

            /// <summary>Advance an offset by <paramref name="pxPerMs"/> over <paramref name="dtMs"/>, wrapped into one period
            /// once the period is known (so a long session never loses float precision).</summary>
            public static float Step(float offset, float pxPerMs, float dtMs, float period)
            {
                float o = offset + pxPerMs * dtMs;
                return period > 1f ? Wrap(o, period) : o;
            }

            /// <summary>An offset folded into [0, period); 0 while the period is unknown.</summary>
            public static float Wrap(float offset, float period)
            {
                if (period <= 1f || !float.IsFinite(offset)) return 0f;
                float o = offset % period;
                if (o < 0f) o += period;
                return o < period ? o : 0f;                       // a tiny negative remainder can round up to the period
            }
        }
    }
}
