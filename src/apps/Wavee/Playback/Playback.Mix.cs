// ── Playback/Playback.Mix.cs — CORE (#48 Mix, plan §3.5, §3.6, §3.6a) ─────────────────────────────────────────────────
//
// THE PLANNER and its host follow. On the UI thread, after every drain, the host looks at the playing pair (the row on
// the deck and the row the queue would play next). When the context is a playlist Spotify has mixed, it computes ONE
// MixTiming for that pair (a saved recipe, or Wavee's Auto) and hands it to the pump (Audio.SetMix), which prepares B at
// the in-point, opens the endgame against the mix-out and schedules the join at an exact mixer frame
// (Playback.Audio.Mix.cs). Any pair that cannot mix gets MixTiming.Refused(why): the base hand-off (the user's Crossfade
// or gapless) runs unchanged, and the why is logged once per pair.
//
//   Drain ─▶ Mix.Follow(state) ─ pair changed? data landed? ─▶ PlanPair ─▶ MixPlanner.Plan ─▶ Audio.SetMix(active, next, timing)
//
// Shuffle (decision D5 (b)): any two rows of the mixed playlist that play one after the other get Auto; a saved recipe
// applies only when its own pair is adjacent in play order. A row queued from elsewhere, autoplay, an episode, a video
// or a local file gets the base hand-off.

using FluentGpu.Signals;

namespace Wavee;

public static partial class Playback
{
    /// <summary>What the planner reads of one track: a value built from the track row and its cue edge.</summary>
    public readonly record struct PairFacts(long DurationMs, ushort TempoX10, byte Camelot, TrackCues Cues, bool Mixable, bool IsLocal);

    public static class Mix
    {
        public enum KeyRelation : byte { Unknown, Same, Adjacent, Relative, EnergyUp, Clash }
        public enum TempoRelation : byte { Unknown, Matched, HalfTime, DoubleTime, Apart }
        public const double TempoTolerance = 0.06;

        /// <summary>Camelot bytes are 1..24: number = (c + 1) / 2, ring = c &amp; 1 (Spotify.Decode.CamelotCode).</summary>
        public static KeyRelation Key(byte from, byte to)
        {
            if (from is 0 or > 24 || to is 0 or > 24) return KeyRelation.Unknown;
            int nf = (from + 1) / 2, nt = (to + 1) / 2;
            bool sameRing = (from & 1) == (to & 1);
            int step = ((nt - nf) % 12 + 12) % 12;
            if (step == 0) return sameRing ? KeyRelation.Same : KeyRelation.Relative;
            if (sameRing && (step == 1 || step == 11)) return KeyRelation.Adjacent;
            if (sameRing && step == 2) return KeyRelation.EnergyUp;
            return KeyRelation.Clash;
        }

        /// <summary>Tempo ×10 on both sides; half and double time count as matched.</summary>
        public static TempoRelation Tempo(ushort outX10, ushort inX10)
        {
            if (outX10 == 0 || inX10 == 0) return TempoRelation.Unknown;
            double r = (double)outX10 / inX10;
            if (Math.Abs(r - 1) <= TempoTolerance) return TempoRelation.Matched;
            if (Math.Abs(r / 2 - 1) <= TempoTolerance) return TempoRelation.DoubleTime;
            if (Math.Abs(r * 2 - 1) <= TempoTolerance) return TempoRelation.HalfTime;
            return TempoRelation.Apart;
        }

        // ── the playing pair (UI thread) ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The timing of the playing pair: what the pump holds. <see cref="MixTiming.Refused"/> when it cannot mix.</summary>
        public static MixTiming Current { get; private set; }
        /// <summary>The playing pair's incoming identity (the row the timing joins into).</summary>
        public static EntityId CurrentNext { get; private set; }
        /// <summary>Bumped whenever <see cref="Current"/> changes: the seek rail, the time label and the queue read it.</summary>
        public static readonly Signal<int> Changed = new(0);

        readonly record struct FollowKey(EntityId Cur, EntityId Next, EntityId Context, int MixVersion, int Overrides,
            uint BeatsA, uint BeatsB, uint CuesA, uint CuesB, uint ListVersion, bool ListenWithout);

        static FollowKey s_key;
        static (EntityId, EntityId) s_ensured;

        /// <summary>After every drain: re-plan when the pair, the playlist's mix data, the overrides, or the analysis of
        /// either track changed. Cheap when nothing did (one key compare).</summary>
        internal static void Follow(in State s)
        {
            EntityRef cur = s.Current, next = default;
            QueueProvider provider = QueueProvider.Context;
            if (s.HasCurrent && !s.Cursor.IsNone) Queue.TryPeek(in s.Cursor, out next, out provider);
            var edges = Entities.Current.Edges;
            int pl = PlaylistSlotOf(s.Context);
            var key = new FollowKey(s.CurrentId, next.IsNone ? default : next.Id, s.Context, MixData.Version, MixOverrides.Version,
                cur.Kind == EntityKind.Track ? edges.TrackBeats.Version(cur.Slot) : 0,
                next.Kind == EntityKind.Track ? edges.TrackBeats.Version(next.Slot) : 0,
                cur.Kind == EntityKind.Track ? edges.TrackCues.Version(cur.Slot) : 0,
                next.Kind == EntityKind.Track ? edges.TrackCues.Version(next.Slot) : 0,
                pl > 0 ? edges.PlaylistTracks.Version(pl) : 0,
                pl > 0 && MixListenWithout.Is(Entities.MixPlaylistId(pl)));
            if (key == s_key) return;
            s_key = key;

            var timing = PlanPair(in s, cur, next, provider, pl);
            if (timing.Mix && s_ensured != (key.Cur, key.Next))
            {
                // The analysis the planner reads, for the playing and the next row (plan §3.3: "for the playing track and
                // the next two rows"; the third rides the next re-plan).
                s_ensured = (key.Cur, key.Next);
                EnsureAnalysis(cur);
                EnsureAnalysis(next);
            }
            else if (!timing.Mix && timing.WhyNot is MixWhyNot.NoData && pl > 0) { EnsureAnalysis(cur); EnsureAnalysis(next); }

            if (timing == Current && key.Next == CurrentNext) return;
            Current = timing;
            CurrentNext = key.Next;
            Changed.Value = Changed.Peek() + 1;
            if (timing.Mix)
                Log.Info("audio", $"[mix] plan source={timing.Source} preset={timing.Preset} out={timing.OutStartMs} outDur={timing.OutDurationMs} "
                    + $"in={timing.InStartMs} inDur={timing.InDurationMs}");
            else if (timing.WhyNot is not (MixWhyNot.None or MixWhyNot.NotMixed))
                Log.Info("audio", "[mix] base hand-off why=" + timing.WhyNot);
            Audio.SetMix(s.CurrentId, key.Next, next, in timing);
        }

        static void EnsureAnalysis(EntityRef row)
        {
            if (row.Kind != EntityKind.Track || row.IsNone) return;
            Entities.EnsureEdge(FetchEdge.TrackMixBeats, row.Slot, priority: FetchPriority.Prefetch);
            Entities.EnsureEdge(FetchEdge.TrackCues, row.Slot, priority: FetchPriority.Prefetch);
        }

        static int PlaylistSlotOf(EntityId context)
            => context.Kind == EntityKind.Playlist && Entities.Current.Playlists.TryGetSlot(context, out int slot) ? slot : 0;

        /// <summary>The playing pair's timing, or the reason it gets the base hand-off.</summary>
        static MixTiming PlanPair(in State s, EntityRef cur, EntityRef next, QueueProvider provider, int pl)
        {
            if (pl <= 0) return MixTiming.Refused(MixWhyNot.NotMixed);
            var p = new global::Wavee.Playlist(pl);
            if (!p.MixOn) return MixTiming.Refused(MixWhyNot.NotMixed);
            if (MixListenWithout.Is(Entities.MixPlaylistId(pl))) return MixTiming.Refused(MixWhyNot.ListenWithoutMix);
            if (cur.IsNone || next.IsNone) return MixTiming.Refused(MixWhyNot.NotMixed);
            if (provider != QueueProvider.Context) return MixTiming.Refused(MixWhyNot.UserQueued);
            if (cur.Kind != EntityKind.Track || next.Kind != EntityKind.Track || s.Kind != PlayableKind.Audio)
                return MixTiming.Refused(MixWhyNot.CrossKind);
            var a = new global::Wavee.Track(cur.Slot);
            var b = new global::Wavee.Track(next.Slot);
            if (a.IsLocal || b.IsLocal) return MixTiming.Refused(MixWhyNot.LocalFile);

            var rows = Entities.Current.Edges.PlaylistTracks;
            var targets = rows.Targets(pl);
            var payload = rows.Payload(pl);
            int ia = -1, ib = -1;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] != cur.Slot) continue;
                if (ia < 0) ia = i;
                if (i + 1 < targets.Length && targets[i + 1] == next.Slot) { ia = i; ib = i + 1; break; }
            }
            if (ib < 0) for (int i = 0; i < targets.Length; i++) if (targets[i] == next.Slot) { ib = i; break; }
            if (ia < 0 || ib < 0) return MixTiming.Refused(MixWhyNot.NotInMixedList);
            if (((payload[ia].Flags | payload[ib].Flags) & PlaylistItemFlags.NotMixable) != 0)
                return MixTiming.Refused(MixWhyNot.NotMixable);

            ulong itemA = MixRecipe.Hex64(Entities.Strings.Resolve(payload[ia].ItemId));
            ulong itemB = MixRecipe.Hex64(Entities.Strings.Resolve(payload[ib].ItemId));
            string uri = p.Id.Text;
            bool hasRecipe = false;
            MixRecipe recipe = default;
            if (MixOverrides.TryGet(uri, itemA, itemB, out var local))
            {
                if (local.Kind == MixOverrideKind.NoMix) return MixTiming.Refused(MixWhyNot.PairOptOut);
                if (local.Kind == MixOverrideKind.Recipe) { hasRecipe = true; recipe = local.Recipe; }
            }
            else if (ib == ia + 1 && MixData.TryGet(pl, itemA, out var stored)
                     && (stored.Recipe.ToItemId == 0 || stored.Recipe.ToItemId == itemB))
            {
                hasRecipe = true;
                recipe = stored.Recipe;
            }

            var edges = Entities.Current.Edges;
            var fa = Facts(a, edges);
            var fb = Facts(b, edges);
            return MixPlanner.Plan(in fa, edges.TrackBeats.Payload(cur.Slot), in fb, edges.TrackBeats.Payload(next.Slot), hasRecipe, in recipe);
        }

        static PairFacts Facts(global::Wavee.Track t, Edges edges)
        {
            var cues = edges.TrackCues.Payload(t.Slot);
            return new PairFacts(t.DurationMs, t.Tempo, t.Camelot, cues.Length > 0 ? cues[0] : default, true, t.IsLocal);
        }

        /// <summary>The timing for an arbitrary pair of the open playlist (the Transitions view, diagnostics): the same
        /// rules as the playing pair, with the list's own order deciding adjacency. UI thread.</summary>
        public static MixTiming PlanListPair(int playlistSlot, int fromIndex, int toIndex)
        {
            var p = new global::Wavee.Playlist(playlistSlot);
            if (!p.IsValid || !p.MixOn) return MixTiming.Refused(MixWhyNot.NotMixed);
            var rows = Entities.Current.Edges.PlaylistTracks;
            var targets = rows.Targets(playlistSlot);
            var payload = rows.Payload(playlistSlot);
            if ((uint)fromIndex >= (uint)targets.Length || (uint)toIndex >= (uint)targets.Length) return MixTiming.Refused(MixWhyNot.NotMixed);
            if (payload[fromIndex].Kind != PlaylistItemKind.Track || payload[toIndex].Kind != PlaylistItemKind.Track)
                return MixTiming.Refused(MixWhyNot.CrossKind);
            var a = new global::Wavee.Track(targets[fromIndex]);
            var b = new global::Wavee.Track(targets[toIndex]);
            if (a.IsLocal || b.IsLocal) return MixTiming.Refused(MixWhyNot.LocalFile);
            if (((payload[fromIndex].Flags | payload[toIndex].Flags) & PlaylistItemFlags.NotMixable) != 0) return MixTiming.Refused(MixWhyNot.NotMixable);
            ulong itemA = MixRecipe.Hex64(Entities.Strings.Resolve(payload[fromIndex].ItemId));
            ulong itemB = MixRecipe.Hex64(Entities.Strings.Resolve(payload[toIndex].ItemId));
            string uri = p.Id.Text;
            bool hasRecipe = false;
            MixRecipe recipe = default;
            if (MixOverrides.TryGet(uri, itemA, itemB, out var local))
            {
                if (local.Kind == MixOverrideKind.NoMix) return MixTiming.Refused(MixWhyNot.PairOptOut);
                if (local.Kind == MixOverrideKind.Recipe) { hasRecipe = true; recipe = local.Recipe; }
            }
            else if (toIndex == fromIndex + 1 && MixData.TryGet(playlistSlot, itemA, out var stored)
                     && (stored.Recipe.ToItemId == 0 || stored.Recipe.ToItemId == itemB))
            {
                hasRecipe = true;
                recipe = stored.Recipe;
            }
            var edges = Entities.Current.Edges;
            var fa = Facts(a, edges);
            var fb = Facts(b, edges);
            return MixPlanner.Plan(in fa, edges.TrackBeats.Payload(a.Slot), in fb, edges.TrackBeats.Payload(b.Slot), hasRecipe, in recipe);
        }
    }

    /// <summary>Wavee's planner (plan §3.5, decision D15). Pure: values in, one timing out.</summary>
    public static class MixPlanner
    {
        public const uint MinOverlapMs = 2_500, MaxOverlapMs = 16_000, DefaultOverlapMs = 2_510;
        public const uint MinTrackMs = 30_000, OutRegionMs = 60_000, ShortTargetMs = 2_500;
        const int MinGridBeats = 16;

        /// <summary>The timing of the join from A to B. A saved recipe wins; otherwise Wavee's Auto. Spans are the
        /// tracks' kind-217 grids in <see cref="BeatGrid"/> packing (may be empty).</summary>
        public static MixTiming Plan(in PairFacts a, ReadOnlySpan<uint> beatsA, in PairFacts b, ReadOnlySpan<uint> beatsB,
            bool hasRecipe, in MixRecipe recipe)
        {
            if (a.IsLocal || b.IsLocal) return MixTiming.Refused(MixWhyNot.LocalFile);
            long durA = a.DurationMs, durB = b.DurationMs;
            if (durA < MinTrackMs || durB <= 0) return MixTiming.Refused(MixWhyNot.TooShort);
            bool gridA = beatsA.Length >= MinGridBeats, gridB = beatsB.Length >= MinGridBeats;

            if (hasRecipe) return Saved(in recipe, durA, durB, beatsA, beatsB, gridA, gridB);

            bool cueA = a.Cues.EndCueMs > 0;
            if (!gridA && !cueA) return MixTiming.Refused(MixWhyNot.NoData);

            // 2. The out region: A's end cue inside its last minute, else its end.
            long outRegion = cueA && a.Cues.EndCueMs < durA && durA - a.Cues.EndCueMs <= OutRegionMs ? a.Cues.EndCueMs : durA;

            // 3-4. The overlap in whole beats of A, and the out start on a downbeat with the window inside the file.
            long overlap, outStart;
            float periodA = 0f;
            int beats = 0;
            if (gridA)
            {
                periodA = PeriodNear(beatsA, outRegion);
                beats = 8;
                if (beats * periodA > MaxOverlapMs) beats = 4;
                else if (beats * periodA < MinOverlapMs) beats = 16;
                overlap = Math.Min((long)MaxOverlapMs, (long)(beats * periodA + 0.5f));
                outStart = DownbeatAtOrBefore(beatsA, NearestDownbeat(beatsA, outRegion - overlap), durA - overlap);
                if (outStart < 0) outStart = Math.Max(0, Math.Min(outRegion, durA) - overlap);
            }
            else
            {
                overlap = DefaultOverlapMs;
                outStart = Math.Max(0, Math.Min(outRegion, durA) - overlap);
            }

            // 5. The in-point: B's start cue on B's nearest downbeat; 0 inside B's first bar or without a grid.
            long inStart = 0;
            float periodB = gridB ? PeriodNear(beatsB, 0) : 0f;
            if (gridB && b.Cues.StartCueMs > 0)
            {
                int db = NearestDownbeatIndex(beatsB, b.Cues.StartCueMs);
                if (db >= 0)
                {
                    long at = BeatGrid.StartMs(beatsB[db]);
                    int first = FirstDownbeatIndex(beatsB);
                    long firstBarEnd = first >= 0 ? BeatGrid.StartMs(beatsB[first]) + (long)(4 * PeriodNear(beatsB, BeatGrid.StartMs(beatsB[first]))) : 0;
                    inStart = at < firstBarEnd ? 0 : at;
                }
            }

            // 7. The style: Fade; a short Fade on the beat when the tempos are far apart (the capture's 117 → 77 BPM pair got
            // a 2.9 s window from the official client too). The mix-out END stays; the start moves.
            if (Mix.Tempo(a.TempoX10, b.TempoX10) == Mix.TempoRelation.Apart)
            {
                int shortBeats = gridA ? Math.Max(2, (int)Math.Round(ShortTargetMs / periodA)) : 0;
                long shortOverlap = gridA ? (long)(shortBeats * periodA + 0.5f) : DefaultOverlapMs;
                outStart += overlap - shortOverlap;
                overlap = shortOverlap;
                beats = shortBeats;
            }

            // 6. B's curves run over the same beat count in B's grid.
            long inDur = gridB && beats > 0 ? (long)(beats * PeriodNear(beatsB, inStart) + 0.5f) : overlap;
            if (inStart + inDur > durB) { inStart = 0; inDur = Math.Min(inDur, durB); }
            return new MixTiming(MixSource.Auto, MixWhyNot.None, (byte)MixPreset.Fade,
                (uint)outStart, (uint)overlap, (uint)inStart, (uint)Math.Max(1, inDur));
        }

        static MixTiming Saved(in MixRecipe r, long durA, long durB, ReadOnlySpan<uint> beatsA, ReadOnlySpan<uint> beatsB, bool gridA, bool gridB)
        {
            long overlap = Math.Clamp((long)r.OverlapMs, 1, MaxOverlapMs);
            long outStart = Math.Min(r.OutStartMs, Math.Max(0, durA - overlap));
            long inStart = r.InStartMs < durB ? r.InStartMs : 0;
            long inDur = overlap;
            if (gridA && gridB)
            {
                float pa = PeriodNear(beatsA, outStart + overlap / 2);
                int n = (int)Math.Round(overlap / Math.Max(1f, pa));
                n = n <= 6 ? 4 : n <= 12 ? 8 : 16;
                inDur = (long)(n * PeriodNear(beatsB, inStart) + 0.5f);
            }
            if (inStart + inDur > durB) inDur = Math.Max(1, durB - inStart);
            return new MixTiming(MixSource.Saved, MixWhyNot.None, r.Preset == 0 ? (byte)MixPreset.Fade : r.Preset,
                (uint)outStart, (uint)overlap, (uint)inStart, (uint)inDur);
        }

        /// <summary>The mean beat period over up to 8 beats around <paramref name="ms"/>.</summary>
        public static float PeriodNear(ReadOnlySpan<uint> beats, long ms)
        {
            if (beats.Length < 2) return 500f;
            int i = Math.Clamp(IndexAtOrBefore(beats, ms), 0, beats.Length - 1);
            int lo = Math.Max(0, i - 4), hi = Math.Min(beats.Length - 1, lo + 8);
            lo = Math.Max(0, hi - 8);
            return Math.Max(1f, (BeatGrid.StartMs(beats[hi]) - BeatGrid.StartMs(beats[lo])) / (float)(hi - lo));
        }

        static int IndexAtOrBefore(ReadOnlySpan<uint> beats, long ms)
        {
            int lo = 0, hi = beats.Length - 1, at = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (BeatGrid.StartMs(beats[mid]) <= ms) { at = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return at;
        }

        static int NearestDownbeatIndex(ReadOnlySpan<uint> beats, long ms)
        {
            int best = -1;
            long bestDist = long.MaxValue;
            for (int i = 0; i < beats.Length; i++)
            {
                if (!BeatGrid.IsDownbeat(beats[i])) continue;
                long d = Math.Abs(BeatGrid.StartMs(beats[i]) - ms);
                if (d < bestDist) { bestDist = d; best = i; } else if (BeatGrid.StartMs(beats[i]) > ms) break;
            }
            return best;
        }

        static long NearestDownbeat(ReadOnlySpan<uint> beats, long ms)
        {
            int i = NearestDownbeatIndex(beats, ms);
            return i < 0 ? -1 : BeatGrid.StartMs(beats[i]);
        }

        /// <summary>The downbeat at <paramref name="ms"/>, moved back a bar at a time until it is at most <paramref name="limit"/>.</summary>
        static long DownbeatAtOrBefore(ReadOnlySpan<uint> beats, long ms, long limit)
        {
            if (ms < 0) return -1;
            for (int i = beats.Length - 1; i >= 0; i--)
            {
                long t = BeatGrid.StartMs(beats[i]);
                if (t > ms || t > limit || !BeatGrid.IsDownbeat(beats[i])) continue;
                return t;
            }
            return -1;
        }

        static int FirstDownbeatIndex(ReadOnlySpan<uint> beats)
        {
            for (int i = 0; i < beats.Length; i++) if (BeatGrid.IsDownbeat(beats[i])) return i;
            return -1;
        }
    }
}
