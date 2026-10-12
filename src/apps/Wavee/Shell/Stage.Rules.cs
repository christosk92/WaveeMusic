// ── Shell/Stage.Rules.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the stage's DECISIONS that used to sit in view files (themes plan C4, X14): the Artist pane's arithmetic (`ArtistRules`),
// the caption's karaoke wipe rows (`CaptionWipeRows`), the gallery's keyboard arithmetic (`Visualizer.GalleryNav`) and the
// tile subtitle rule (`Visualizer.SubtitleOf`)
//
// Role: CORE
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.10, ch 21
//
// MOVED VERBATIM from `Stage.Artist.UI.cs`, `Stage.UI.cs` and `Visualizer.Gallery.UI.cs`: the same types under the same names
// (so `Wavee.Tests/StageArtistRulesTests.cs` and the gallery / caption suites pin them unchanged), now in a file that holds
// no tree. A theme's stage view calls them; it does not own them.

using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee;

public static partial class Stage
{
    // ══ THE ARTIST PANE'S RULES (Wavee.Tests/StageArtistRulesTests.cs) ══════════════════════════════════════════════

    /// <summary>Every Artist-pane decision that is not a tree: the column split, the header height, the reading measure,
    /// the listener line, credits grouped by person, how many avatars / tiles fit. System-only (no Element, no signal, no
    /// entity read) so the tests drive the real arithmetic. Public: this assembly has no <c>InternalsVisibleTo</c>.</summary>
    public static class ArtistRules
    {
        /// <summary>Two columns from this pane width; one column under it.</summary>
        public const float TwoColumnMinW = 1100f;
        /// <summary>The header band's share of the pane height and its clamp (the floor grows with the type scale, so a
        /// 72-DIP name never overflows its band).</summary>
        public const float HeaderFrac = 0.3f, HeaderMin = 200f, HeaderMax = 480f;
        /// <summary>The left column's share of the two-column row: at least / at most this much of it.</summary>
        public const float LeftMinFrac = 0.45f, LeftMaxFrac = 0.6f;
        /// <summary>The reading measure in ems: 68 characters at Segoe's ~0.5 em average advance.</summary>
        public const float MeasureEms = 34f;
        public const int PopularCount = 5, CityCount = 5, GalleryCount = 4, FansMax = 8, CreditPeopleMax = 10, BioLines = 6;

        /// <summary>One contributor: the name, its distinct roles ", " joined in wire order, and the artist it links to
        /// (0 = an unlinked contributor).</summary>
        public readonly record struct CreditPerson(string Name, string Roles, int ArtistSlot);

        public static bool TwoColumns(float paneW) => paneW >= TwoColumnMinW;

        /// <summary>0.3 · the pane height, rounded, clamped to [HeaderMin · scale, HeaderMax].</summary>
        public static float HeaderHeight(float paneH, float scale)
            => Math.Clamp(MathF.Round(paneH * HeaderFrac), MathF.Min(HeaderMin * MathF.Max(1f, scale), HeaderMax), HeaderMax);

        /// <summary>~68 characters of <paramref name="typeSize"/> type.</summary>
        public static float ReadingMeasure(float typeSize) => MathF.Round(typeSize * MeasureEms);

        /// <summary>The left (About) column: the reading measure, held between 45 % and 60 % of the row; the right column
        /// takes the rest.</summary>
        public static float LeftColumnW(float paneW, float gap, float measure)
        {
            float row = MathF.Max(0f, paneW - gap);
            return MathF.Floor(Math.Clamp(measure, row * LeftMinFrac, row * LeftMaxFrac));
        }

        /// <summary>"7.9M" — the compact count the rest of the app uses (Detail.Text.CompactCount, current culture).</summary>
        public static string ListenerCount(long n) => Detail.Text.CompactCount(n);

        /// <summary>The non-empty parts " · " joined ("7.9M monthly listeners · #312 in the world").</summary>
        public static string MetaLine(string listeners, string rank)
            => listeners.Length == 0 ? rank : rank.Length == 0 ? listeners : listeners + " · " + rank;

        /// <summary>Does a bio of <paramref name="plainChars"/> characters overflow <paramref name="lines"/> lines of
        /// <paramref name="typeSize"/> type at <paramref name="width"/>? (Only then does Read more show.)</summary>
        public static bool BioClamps(int plainChars, float width, float typeSize, int lines)
        {
            if (plainChars <= 0 || lines <= 0) return false;
            int perLine = Math.Max(1, (int)(width / MathF.Max(1f, typeSize * 0.5f)));
            return plainChars > perLine * lines;
        }

        /// <summary>How many <paramref name="tile"/>-wide items fit in <paramref name="width"/> with <paramref name="gap"/>
        /// between them, capped by what is there and by <paramref name="max"/>.</summary>
        public static int FitCount(float width, float tile, float gap, int available, int max)
        {
            if (tile <= 0f || width <= 0f) return 0;
            int fit = (int)MathF.Floor((width + gap) / (tile + gap));
            return Math.Max(0, Math.Min(Math.Min(available, max), fit));
        }

        /// <summary>The edge of each of <paramref name="n"/> equal tiles across <paramref name="width"/>.</summary>
        public static float TileW(float width, float gap, int n)
            => n <= 0 ? 0f : MathF.Max(0f, MathF.Floor((width - gap * (n - 1)) / n));

        /// <summary>A city bar's share of the loudest city, 0..1.</summary>
        public static float CityFraction(uint listeners, uint max) => max == 0 ? 0f : Math.Clamp((float)listeners / max, 0f, 1f);

        /// <summary>The credits GROUPED BY PERSON, in first-appearance order: one entry per name (case-insensitive), its
        /// distinct roles in wire order, its artist link from the credit's own target or — when the credit carries none —
        /// from a billed artist of the same name. At most <paramref name="max"/> people.</summary>
        public static CreditPerson[] GroupCredits(ReadOnlySpan<string> names, ReadOnlySpan<string> roles, ReadOnlySpan<int> slots,
                                                  ReadOnlySpan<string> billedNames, ReadOnlySpan<int> billedSlots, int max)
        {
            var people = new List<string>(Math.Min(names.Length, Math.Max(0, max)));
            var roleLists = new List<List<string>>(people.Capacity);
            var links = new List<int>(people.Capacity);
            for (int i = 0; i < names.Length; i++)
            {
                string name = (names[i] ?? "").Trim();
                if (name.Length == 0) continue;
                int at = -1;
                for (int k = 0; k < people.Count && at < 0; k++)
                    if (string.Equals(people[k], name, StringComparison.OrdinalIgnoreCase)) at = k;
                if (at < 0)
                {
                    if (people.Count >= max) continue;
                    at = people.Count;
                    people.Add(name);
                    roleLists.Add(new List<string>(2));
                    links.Add(0);
                }
                string role = i < roles.Length ? (roles[i] ?? "").Trim() : "";
                if (role.Length > 0)
                {
                    var list = roleLists[at];
                    bool seen = false;
                    for (int r = 0; r < list.Count && !seen; r++) seen = string.Equals(list[r], role, StringComparison.OrdinalIgnoreCase);
                    if (!seen) list.Add(role);
                }
                if (links[at] <= 0 && i < slots.Length && slots[i] > 0) links[at] = slots[i];
            }
            var result = new CreditPerson[people.Count];
            for (int k = 0; k < people.Count; k++)
            {
                int link = links[k];
                for (int b = 0; link <= 0 && b < billedNames.Length && b < billedSlots.Length; b++)
                    if (billedSlots[b] > 0 && string.Equals(billedNames[b], people[k], StringComparison.OrdinalIgnoreCase)) link = billedSlots[b];
                result[k] = new CreditPerson(people[k], string.Join(", ", roleLists[k]), Math.Max(0, link));
            }
            return result;
        }
    }

    // ══ THE CAPTION'S KARAOKE WIPE ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The caption's karaoke wipe on its active line's two text nodes (the main glyphs and the bloom). Playing, the
    /// RENDER THREAD poses it: <see cref="Seed"/> lays the exact split function from now to where it settles
    /// (<see cref="Lyrics.Wipe.SplitKeyframes"/>) on <c>AnimChannel.GlyphWipeSplit</c>, once per line and again when a report
    /// moves the clock. A line handing off is <see cref="Hold">held</see> where it stands, its on-screen split written back
    /// as the authored one; paused, <see cref="Cancel"/> drops the rows and <see cref="DriveUi"/> writes the split on the same
    /// tick. Every split, the UI's and the render thread's, steps in whole DIPs of the measured reading-order run
    /// (<see cref="GlyphWipe.Run"/>), so a wrapped line moves exactly as the UI wrote it.</summary>
    public sealed class CaptionWipeRows
    {
        int _line = -1;
        NodeHandle _main, _glow;
        long _clock;

        /// <summary>The line the rows were seeded for; -1 = none (the UI writes the split, or nothing is wiping).</summary>
        public int Line => _line;

        /// <summary>LineRow's split: the char-weighted sung fraction on TRUE time, nudged by the wipe's own lead mid-line.</summary>
        public static float SplitAt(Lyrics.Line line, long now)
        {
            float split = Lyrics.Wipe.ComputeSplit(line, now);
            return split > 0f && split < 1f ? Math.Clamp(split + Lyrics.Wipe.LeadFrac, 0f, 1f) : split;
        }

        /// <summary>The split the UI writes at <paramref name="now"/>: stepped in whole DIPs of the run (as the render thread
        /// steps a <see cref="GlyphWipe.Run"/> of the same length).</summary>
        public static float UiSplit(Lyrics.Line line, long now, float runLen) => GlyphWipe.Quantize(SplitAt(line, now), Run(runLen));

        static float Run(float runLen) => runLen > 1f ? runLen : 0f;

        /// <summary>Seed the rows on <paramref name="main"/> (and <paramref name="glow"/> when it carries a wipe) for line
        /// <paramref name="anchor"/>; <paramref name="clockNowMs"/> is the frame clock <paramref name="now"/> was estimated
        /// on (a report that moves position - clock re-seeds). False = the line's nodes are not realized yet.</summary>
        public bool Seed(AnimEngine anim, SceneStore scene, Lyrics.Line line, int anchor, NodeHandle main, NodeHandle glow,
            long now, long clockNowMs, float runLen)
        {
            if (main.IsNull || !scene.IsLive(main) || !scene.TryGetGlyphWipe(main, out _)) return false;
            bool halo = !glow.IsNull && scene.IsLive(glow) && scene.TryGetGlyphWipe(glow, out _);
            long clock = now - clockNowMs;
            if (_line == anchor && _main == main && _glow == (halo ? glow : default) && Math.Abs(clock - _clock) <= 1L) return true;
            if (_line >= 0 && (_line != anchor || _main != main)) Hold(anim, scene);
            float softness = Lyrics.Wipe.SoftnessOfLine(runLen > 1f ? runLen : MathF.Max(1f, scene.Bounds(main).W), large: true);
            var keys = Lyrics.Wipe.SplitKeyframes(line, now, lead: true, out long endMs);
            _line = anchor; _main = main; _glow = halo ? glow : default; _clock = clock;
            if (keys is null)
            {
                // settled: the split is 1 - written once, as the UI path would
                anim.Cancel(main, AnimChannel.GlyphWipeSplit);
                if (halo) anim.Cancel(glow, AnimChannel.GlyphWipeSplit);
                WriteSplit(scene, main, 1f, softness, runLen);
                if (halo) WriteSplit(scene, glow, 1f, softness, runLen);
                return true;
            }
            float durationMs = endMs - now;
            // the rows start from the value at `now`: written as the authored split too, so a row not posed yet shows the same
            WriteSplit(scene, main, keys[0].Value, softness, runLen);
            anim.Keyframes(main, AnimChannel.GlyphWipeSplit, keys, durationMs);
            if (halo)
            {
                WriteSplit(scene, glow, keys[0].Value, softness, runLen);
                anim.Keyframes(glow, AnimChannel.GlyphWipeSplit, keys, durationMs);   // the bloom tracks the main layer exactly
            }
            return true;
        }

        /// <summary>Stop the rows where they STAND (<see cref="AnimEngine.SetHeld"/>): an outgoing line (it exits keyed) or a
        /// break keeps the split on screen, as the UI path simply stopped writing it. The on-screen split is written back as
        /// the authored one too, so a row that is ever lost (an overlay-row overflow, a later cancel) leaves the line where it
        /// stands instead of snapping it back to its seed.</summary>
        public void Hold(AnimEngine? anim, SceneStore? scene)
        {
            if (_line < 0) return;
            if (anim is not null)
            {
                HoldOne(anim, scene, _main);
                HoldOne(anim, scene, _glow);
            }
            _line = -1;
            _main = _glow = default;
        }

        static void HoldOne(AnimEngine anim, SceneStore? scene, NodeHandle node)
        {
            if (node.IsNull) return;
            if (scene is not null && scene.IsLive(node) && scene.TryGetGlyphWipe(node, out var w)
                && anim.TryGetTrackValue(node, AnimChannel.GlyphWipeSplit, out float value))
            {
                float shown = w.QuantizeSplit(value, scene.Bounds(node).W);
                if (shown != w.Split)
                {
                    scene.SetGlyphWipe(node, w with { Split = shown });
                    scene.Mark(node, NodeFlags.PaintDirty);
                }
            }
            anim.SetHeld(node, AnimChannel.GlyphWipeSplit, true);
        }

        /// <summary>Hand the split back to the UI on the SAME line (paused, buffering): the rows go, and the caller writes the
        /// split from the clock on this same tick (<see cref="DriveUi"/>), so a cancelled row's pose never stays on screen.</summary>
        public void Cancel(AnimEngine? anim)
        {
            if (_line < 0) return;
            if (anim is not null)
            {
                if (!_main.IsNull) anim.Cancel(_main, AnimChannel.GlyphWipeSplit);
                if (!_glow.IsNull) anim.Cancel(_glow, AnimChannel.GlyphWipeSplit);
            }
            _line = -1;
            _main = _glow = default;
        }

        /// <summary>The UI's own write of the wipe — the lyrics view's write path in shape: the split stepped along the run,
        /// the per-line DIP feather, the settled/eps write-stops (a tick that moves nothing writes nothing).</summary>
        public static void DriveUi(SceneStore scene, NodeHandle main, NodeHandle glow, Lyrics.Line line, long now, float runLen)
        {
            if (main.IsNull || !scene.IsLive(main) || !scene.TryGetGlyphWipe(main, out var mw)) return;
            float split = UiSplit(line, now, runLen);
            float softness = Lyrics.Wipe.SoftnessOfLine(runLen > 1f ? runLen : MathF.Max(1f, scene.Bounds(main).W), large: true);
            if (WipeMoved(split, softness, in mw))
            {
                scene.SetGlyphWipe(main, mw with { Split = split, Softness = softness, Run = Run(runLen) });
                scene.Mark(main, NodeFlags.PaintDirty);
            }
            if (glow.IsNull || !scene.IsLive(glow) || !scene.TryGetGlyphWipe(glow, out var gw) || !WipeMoved(split, softness, in gw)) return;
            scene.SetGlyphWipe(glow, gw with { Split = split, Softness = softness, Run = Run(runLen) });   // the bloom tracks the main layer exactly
            scene.Mark(glow, NodeFlags.PaintDirty);
        }

        static bool WipeMoved(float split, float softness, in GlyphWipe w)
            => !Lyrics.Wipe.SplitSettled(split, w.Split)
               && (MathF.Abs(split - w.Split) > Lyrics.Wipe.SplitEps || MathF.Abs(softness - w.Softness) > Lyrics.Wipe.SoftnessEps);

        /// <summary>The authored split (and its feather and run), written only when it differs.</summary>
        static void WriteSplit(SceneStore scene, NodeHandle node, float split, float softness, float runLen)
        {
            if (node.IsNull || !scene.IsLive(node) || !scene.TryGetGlyphWipe(node, out var w)) return;
            float run = Run(runLen);
            if (w.Split == split && w.Run == run && MathF.Abs(softness - w.Softness) <= Lyrics.Wipe.SoftnessEps) return;
            scene.SetGlyphWipe(node, w with { Split = split, Softness = softness, Run = run });
            scene.Mark(node, NodeFlags.PaintDirty);
        }
    }

}

public static partial class Visualizer
{
    /// <summary>The gallery's keyboard arithmetic over the grouped grid: the shown faces in order, each group starting a
    /// fresh row of <c>cols</c>. ↑ / ↓ keep the column across a group boundary (clamped to the shorter row).</summary>
    public static class GalleryNav
    {
        /// <summary>The index the key moves to from <paramref name="index"/>; −1 for a key the grid does not take.</summary>
        public static int Move(int index, int keyCode, int cols)
        {
            int n = Catalog.ShownCount;
            cols = Math.Max(1, cols);
            index = Math.Clamp(index, 0, n - 1);
            switch (keyCode)
            {
                case Keys.Left: return Math.Max(0, index - 1);
                case Keys.Right: return Math.Min(n - 1, index + 1);
                case Keys.Home: return 0;
                case Keys.End: return n - 1;
                case Keys.Up:
                case Keys.Down:
                {
                    var (start, count) = GroupSpan(index);
                    int row = (index - start) / cols, col = (index - start) % cols, rows = (count + cols - 1) / cols;
                    if (keyCode == Keys.Down)
                    {
                        if (row + 1 < rows) return Math.Min(start + count - 1, start + (row + 1) * cols + col);
                        if (start + count >= n) return index;
                        var (ns, nc) = GroupSpan(start + count);
                        return ns + Math.Min(col, nc - 1);
                    }
                    if (row > 0) return start + (row - 1) * cols + col;
                    if (start == 0) return index;
                    var (ps, pc) = GroupSpan(start - 1);
                    int lastRow = (pc - 1) / cols;
                    return ps + Math.Min(lastRow * cols + col, pc - 1);
                }
                default: return -1;
            }
        }

        /// <summary>The first index and the size of the group <paramref name="index"/> belongs to (groups are contiguous in
        /// <see cref="Catalog.Shown"/>).</summary>
        public static (int Start, int Count) GroupSpan(int index)
        {
            var shown = Catalog.Shown;
            var g = Catalog.GroupOf(shown[index]);
            int start = index, end = index;
            while (start > 0 && Catalog.GroupOf(shown[start - 1]) == g) start--;
            while (end + 1 < shown.Length && Catalog.GroupOf(shown[end + 1]) == g) end++;
            return (start, end - start + 1);
        }
    }

    /// <summary>The tile's second line: the face's blurb, or — while it is the active kind falling back — where its motion comes from.
    /// <paramref name="current"/> is the context's kind SIGNAL value (never a registry read — O8).</summary>
    static string SubtitleOf(Kind k, Kind current, Source live)
    {
        if (current != k || live == Source.Live) return Loc.Get(SubKey(k));
        return Loc.Get(live switch { Source.Precomputed => Strings.Stage.Source.Precomputed, Source.TempoGrid => Strings.Stage.Source.Tempo, _ => Strings.Stage.Source.Breath });
    }
}
