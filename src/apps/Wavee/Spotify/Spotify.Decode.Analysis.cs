// ── Spotify/Spotify.Decode.Analysis.cs ─────────────────────────────────────────────────────────────────────────────
// the audio-analysis JSON → Edges.TrackBeats
//
// Role: CORE
// Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §4.5.2

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>`{"beats":[{"start":s,"duration":d,"confidence":c},…],"bars":[…],…}` → every beat's start in ms with
        /// the bars folded in as downbeat flags (<see cref="BeatGrid.MarkDownbeats"/>). Unknown members are skipped; a 2xx
        /// with no beats stages an EMPTY run — a real "no grid" answer, never a re-ask. Non-JSON (a captive portal, a
        /// truncated body) stages NOTHING (the door re-asks later) — the reader throws <see cref="JsonException"/>, caught here (V-D19).</summary>
        public static void AudioAnalysis(ReadOnlySpan<byte> json, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var track = Identity(s, entityUri);
            if (track.IsEmpty) return;
            uint[] beats = ArrayPool<uint>.Shared.Rent(BeatGrid.MaxBeats);
            uint[] bars = ArrayPool<uint>.Shared.Rent(BeatGrid.MaxBars);
            try
            {
                int nb = 0, nbars = 0;
                try
                {
                    var r = new Utf8JsonReader(json);
                    if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;   // not an object: no run, no state change
                    for (int root = r.CurrentDepth; Next(ref r, root);)
                    {
                        if (r.ValueTextEquals("beats"u8)) nb = ReadStarts(ref r, beats.AsSpan(0, BeatGrid.MaxBeats));
                        else if (r.ValueTextEquals("bars"u8)) nbars = ReadStarts(ref r, bars.AsSpan(0, BeatGrid.MaxBars));
                        else SkipValue(ref r);
                    }
                }
                catch (JsonException)
                {
                    return;                                                  // garbage: no run, no state change (V-D19)
                }
                ref var run = ref s.TraitRuns.Add();
                run.Parent = track;
                run.Relation = TraitRelation.TrackBeats;
                run.Start = s.Traits.Count;
                run.Length = 0;
                if (nb == 0) return;                                         // no beats: an empty grid is the answer
                BeatGrid.MarkDownbeats(beats.AsSpan(0, nb), bars.AsSpan(0, nbars));
                run.Bytes = s.AddText(MemoryMarshal.AsBytes(beats.AsSpan(0, nb)));
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(beats);
                ArrayPool<uint>.Shared.Return(bars);
            }
        }

        /// <summary>`[{"start": seconds, …}, …]` → start ms in wire order; stops at the span's capacity.</summary>
        static int ReadStarts(ref Utf8JsonReader r, Span<uint> into)
        {
            int n = 0;
            if (!EnterArray(ref r)) return 0;
            for (int list = r.CurrentDepth; Element(ref r, list);)
            {
                double start = double.NaN;
                for (int item = r.CurrentDepth; Next(ref r, item);)
                {
                    if (!r.ValueTextEquals("start"u8)) { SkipValue(ref r); continue; }
                    r.Read();
                    if (r.TokenType == JsonTokenType.Number && r.TryGetDouble(out double seconds)) start = seconds;
                    else r.Skip();
                }
                if (!double.IsNaN(start) && start >= 0 && n < into.Length) into[n++] = (uint)Math.Min(start * 1000.0 + 0.5, BeatGrid.MsMask);
            }
            return n;
        }
    }
}
