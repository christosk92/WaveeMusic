// ── Playback/Playback.Mix.cs — CORE (#48 Mix, plan §3.5, §3.6, §3.6a) ─────────────────────────────────────────────────
//
// THE PLANNER and its host follow. On the UI thread, after every drain, the host looks at the playing pair (the row on
// the deck and the row the queue would play next). When the context is a playlist Spotify has mixed, it computes ONE
// MixTiming for that pair (a saved recipe, or Spotify's Auto) and hands it to the pump (Audio.SetMix), which prepares B at
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

using FluentGpu.Localization;

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
        static MixTiming? s_bench;

        /// <summary>`--mix-bench` only (Diagnostics.MixBench.cs): every pair plays this timing, whatever the planner would say.</summary>
        internal static void Bench(MixTiming timing) => s_bench = timing;
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
                pl > 0 && MixOptOut.Is(new global::Wavee.Playlist(pl)));
            if (key == s_key) return;
            s_key = key;

            var timing = s_bench is { } bench && !next.IsNone ? bench : PlanPair(in s, cur, next, provider, pl);
            if (timing.Mix && s_ensured != (key.Cur, key.Next))
            {
                // The analysis the planner reads, for the playing and the next row (plan §3.3: "for the playing track and
                // the next two rows"; the third rides the next re-plan). Until kind 28 lands, Auto plans the rule's fallback
                // (MixPlanner.FallbackWindowMs) and re-plans when it does (the key holds the cue versions).
                s_ensured = (key.Cur, key.Next);
                EnsureAnalysis(cur);
                EnsureAnalysis(next);
            }

            bool contextMixed = pl > 0 && !key.ListenWithout && new global::Wavee.Playlist(pl).MixOn;
            if (timing == Current && key.Next == CurrentNext && contextMixed == ContextMixed) return;
            ContextMixed = contextMixed;
            Current = timing;
            CurrentNext = key.Next;
            Changed.Value = Changed.Peek() + 1;
            if (timing.Mix)
            {
                Log.Info("audio", $"[mix] plan source={timing.Source} preset={timing.Preset} bars={timing.Bars} out={timing.OutStartMs} outDur={timing.OutDurationMs} "
                    + $"in={timing.InStartMs} inDur={timing.InDurationMs} styles={timing.Styles.Volume}/{timing.Styles.Eq}/{timing.Styles.Filter}/{timing.Styles.Fx}"
                    + (timing.Notes != MixPlanNotes.None ? " notes=" + timing.Notes : ""));
                // Once per planned join: what the styles move that the pinned engine cannot render yet (M4's lanes).
                string skipped = MixRender.Lanes(in timing, rendered: false);
                if (skipped.Length > 0) Log.Info("audio", "[mix] lanes not rendered: " + skipped);
                if ((timing.Notes & MixPlanNotes.BeatmatchNotApplied) != 0)
                    Log.Info("audio", "[mix] beatmatch not applied (speedB=" + timing.SpeedB.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
                        + "): B plays at speed 1 over the overlap");
            }
            else if (timing.WhyNot is not (MixWhyNot.None or MixWhyNot.NotMixed))
                Log.Info("audio", "[mix] base hand-off why=" + timing.WhyNot);
            Audio.SetMix(s.CurrentId, key.Next, next, in timing);
        }

        /// <summary>The deck plays from a playlist whose Mix is on (and not listened to without it on this PC): the queue's Crossfade row
        /// then says Mix covers this playlist (its own pairs; a queued song, a local file still get Crossfade).</summary>
        public static bool ContextMixed { get; private set; }

        /// <summary>The queue's line between the playing and the next row, "Mixing · Rise · 2.9 s" (or "Mixing · Auto"), while the
        /// playing pair will mix; "" otherwise. Reads <see cref="Changed"/>. UI thread.</summary>
        public static string QueueLine()
        {
            _ = Changed.Value;
            var t = Current;
            return t.Mix && !CurrentNext.IsEmpty ? Strings.Mix.QueueLine(MixSeams.ChipText(in t)) : "";
        }

        /// <summary>The Crossfade row's note, "Mix is on for this playlist", while <see cref="ContextMixed"/>; "" otherwise.</summary>
        public static string CrossfadeNote()
        {
            _ = Changed.Value;
            return ContextMixed ? Loc.Get(Strings.Mix.CrossfadeNote) : "";
        }

        static string? s_mixingText;
        static EntityId s_mixingFor;

        /// <summary>"Mixing into &lt;next title&gt;" while a join is planned for the playing track; null otherwise. Reads
        /// <see cref="Changed"/> (a bind that calls it follows the plan). UI thread.</summary>
        public static string? LabelWhilePlanned()
        {
            _ = Changed.Value;
            var t = Current;
            if (!t.Mix || CurrentNext.IsEmpty) return null;
            if (s_mixingFor != CurrentNext || s_mixingText is null)
            {
                var row = Entities.Ref(CurrentNext);
                string title = row.Kind == EntityKind.Track && !row.IsNone ? new global::Wavee.Track(row.Slot).Title : "";
                s_mixingText = Strings.Mix.MixingInto(title);
                s_mixingFor = CurrentNext;
            }
            return s_mixingText;
        }

        /// <summary>The label inside the overlap (<paramref name="positionMs"/> in [mix-out, audible end)), else null.</summary>
        public static string? LabelAt(long positionMs)
        {
            var text = LabelWhilePlanned();
            var t = Current;
            return text is not null && positionMs >= t.OutStartMs && positionMs < t.AudibleEndMs ? text : null;
        }

        static MixWire? s_wire;
        static (int Version, EntityId Context, uint Flags, bool Without) s_wireKey;

        /// <summary>The PUT's Mix half for the deck's context (plan §3.7), rebuilt only when the plan, the context or its flags change.</summary>
        internal static MixWire? WireNow(in State s)
        {
            if (s.Context.Kind != EntityKind.Playlist || !Entities.Current.Playlists.TryGetSlot(s.Context, out int slot)) return null;
            var p = new global::Wavee.Playlist(slot);
            bool without = MixOptOut.Is(p);
            var key = (Changed.Peek(), s.Context, (uint)p.MixFlags, without);
            if (key == s_wireKey) return s_wire;
            s_wireKey = key;
            if (!p.MixOn || without) return s_wire = null;
            var t = Current;
            var outMeta = new List<KeyValuePair<string, string>>(8);
            var inMeta = new List<KeyValuePair<string, string>>(8);
            FadeMeta(in t, outMeta, inMeta);
            return s_wire = new MixWire(true, p.MixFlags, CurrentNext, outMeta.ToArray(), inMeta.ToArray());
        }

        /// <summary>A plan's Connect track metadata (protocol §6.3): the playing track's fade-out keys into <paramref name="outMeta"/>,
        /// the next track's fade-in keys into <paramref name="inMeta"/>: every lane the plan's styles move that has a known key
        /// (<see cref="MixStyles.ConnectKey"/>: volume, EQ low / mid / high gain, filter cutoff and resonance; noise, effect and
        /// looping keys are open), as the style table spells it at the plan's bars (<see cref="MixCurves.WriteJson"/>), whether
        /// the engine renders the lane yet or not. <c>audio.fade_in_duration</c> is B's own length. Nothing for a refused plan (a
        /// "none" seam carries no fade key). The publisher and the inspector's <c>mix curves</c> call it alike.</summary>
        public static void FadeMeta(in MixTiming t, List<KeyValuePair<string, string>> outMeta, List<KeyValuePair<string, string>> inMeta)
        {
            if (!t.Mix) return;
            static string Num(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            outMeta.Add(new("audio.fade_out_start_time", Num(t.OutStartMs)));
            outMeta.Add(new("audio.fade_out_duration", Num(t.OutDurationMs)));
            inMeta.Add(new("audio.fade_in_start_time", Num(t.InStartMs)));
            inMeta.Add(new("audio.fade_in_duration", Num(t.InDurationMs)));
            inMeta.Add(new("audio.fade_overlap", Num(t.OutDurationMs)));
            Span<CurveSegment> seg = stackalloc CurveSegment[MixStyles.MaxSegments];
            byte[] buf = new byte[160 * MixStyles.MaxSegments];
            foreach (MixCurveLane lane in Enum.GetValues<MixCurveLane>())
            {
                string? key = MixStyles.ConnectKey(lane);
                if (key is null) continue;
                int n = MixStyles.Segments(t.Styles, lane, false, t.Bars, seg);
                if (n > 0) outMeta.Add(new("audio.fade_out_" + key, System.Text.Encoding.UTF8.GetString(buf, 0, MixCurves.WriteJson(seg[..n], buf))));
                n = MixStyles.Segments(t.Styles, lane, true, t.Bars, seg);
                if (n > 0) inMeta.Add(new("audio.fade_in_" + key, System.Text.Encoding.UTF8.GetString(buf, 0, MixCurves.WriteJson(seg[..n], buf))));
            }
            outMeta.Add(new("automix.fade_out_cuepoint.position", Num(t.AudibleEndMs)));
            outMeta.Add(new("automix.mode", "auto"));
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
            if (MixOptOut.Is(p)) return MixTiming.Refused(MixWhyNot.ListenWithoutMix);
            if (s.Shuffle && !MixPrefs.WhenShuffling) return MixTiming.Refused(MixWhyNot.NotMixed);   // the Mix menu's "Mix when shuffling" off
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

    /// <summary>The planner (full-support plan §2.3.2): a saved recipe as saved, otherwise Spotify's own Auto. Pure: values in, one
    /// plan out.
    /// <para>Auto for a pair Spotify does not beatmatch (reference §7.1; 156 of 156 reference pairs with A's kind 10 duration,
    /// `mix autocheck`): A's overlap runs from 2500 ms before A's last end candidate (kind 28 field 4, the largest ms) to 2500 ms
    /// after it, or to A's end when that comes first; B enters at 0 and fades in to 2500 ms after its first start candidate
    /// (kind 28 field 3, the smallest ms), its own length; Fade (preset 1), bars 0, speeds 1. Which pairs Spotify beatmatches,
    /// and how, is not fitted yet, and the engine cannot stretch one voice (M4): <see cref="AutoBeatmatched"/> is where that fit
    /// plugs in, and until then every Auto pair takes the rule above.</para></summary>
    public static class MixPlanner
    {
        /// <summary>The rule's reach around a cue candidate: A's overlap spans this before and after A's last end candidate, B's
        /// fade-in runs this far past B's first start candidate.</summary>
        public const uint CueReachMs = 2_500;
        /// <summary>A missing candidate (kind 28 not analysed yet, a 502, or an answer without that list) stands as one 10 ms from
        /// its edge, the rule's commonest short answer (43 of the 156 reference pairs end on one): A's overlap is then the last
        /// 2510 ms of A, B's fade-in 2510 ms. The pair re-plans when the analysis lands (the follow key holds the cue versions).</summary>
        public const uint FallbackWindowMs = 2_510;
        public const uint MinTrackMs = 30_000;
        /// <summary>The local card's longest overlap and its Quick length (Mix.Card.cs, until the editor phase replaces the card).</summary>
        public const uint MaxOverlapMs = 16_000, ShortTargetMs = 2_500;

        /// <summary>The plan of the join from A to B. A saved recipe wins; otherwise Spotify's Auto. Spans are the tracks' kind-217
        /// grids in <see cref="BeatGrid"/> packing (may be empty), for <see cref="AutoBeatmatched"/>.</summary>
        public static MixTiming Plan(in PairFacts a, ReadOnlySpan<uint> beatsA, in PairFacts b, ReadOnlySpan<uint> beatsB,
            bool hasRecipe, in MixRecipe recipe)
        {
            if (a.IsLocal || b.IsLocal) return MixTiming.Refused(MixWhyNot.LocalFile);
            // Spotify's "no transition" (no preset id, a 1 ms overlap, no out-point): no join at all, never a 1 ms Fade at 0 ms.
            if (hasRecipe && recipe.IsNoTransition) return MixTiming.Refused(MixWhyNot.NoTransition);
            if (a.DurationMs < MinTrackMs || b.DurationMs <= 0) return MixTiming.Refused(MixWhyNot.TooShort);
            if (hasRecipe) return Saved(in recipe, in a, in b);
            return AutoBeatmatched(in a, beatsA, in b, beatsB, out var matched) ? matched : Auto(in a, in b);
        }

        /// <summary>THE SEAM for Spotify's beatmatched Auto (reference §7.2: which pairs it matches, 2 or 4 bars, the preset by
        /// score, B's speed). Not fitted yet, and the engine has no per-voice rate (M4), so it says no and the pair takes
        /// <see cref="Auto"/>. When the fit lands it returns true with a plan of <c>durationBars</c> 2 or 4, its preset and
        /// <see cref="MixTiming.SpeedB"/>.</summary>
        public static bool AutoBeatmatched(in PairFacts a, ReadOnlySpan<uint> beatsA, in PairFacts b, ReadOnlySpan<uint> beatsB, out MixTiming timing)
        {
            timing = default;
            return false;
        }

        /// <summary>Spotify's Auto for a pair it does not beatmatch (see the class summary). Every value is the rule's; a missing
        /// candidate takes <see cref="FallbackWindowMs"/> and says so in <see cref="MixTiming.Notes"/>.</summary>
        public static MixTiming Auto(in PairFacts a, in PairFacts b)
        {
            var notes = MixPlanNotes.None;
            long durA = a.DurationMs;
            long outStart = -1, outEnd = -1;
            if (a.Cues.HasLastEndCandidate)
            {
                long cue = a.Cues.LastEndCandidateMs;
                outStart = cue - CueReachMs;
                outEnd = Math.Min(cue + CueReachMs, durA);
            }
            // No candidate, or one the catalogue length cannot hold (the analysed audio can run longer than kind 10 says).
            if (outStart < 0 || outEnd - outStart < 1)
            {
                outEnd = durA;
                outStart = durA - FallbackWindowMs;
                notes |= MixPlanNotes.OutFallback;
            }
            long inDur = InLength(in b, ref notes);
            if (inDur > b.DurationMs) { inDur = Math.Max(1, b.DurationMs); notes |= MixPlanNotes.InClipped; }
            return MixTiming.Of(MixSource.Auto, (int)MixPresetId.Fade, default, 0, (uint)outStart, (uint)(outEnd - outStart), 0, (uint)inDur,
                1f, a.TempoX10 / 10f, notes);
        }

        /// <summary>A saved recipe: A over its own <c>startAMs</c> and <c>durationMs</c> (at most 35 s, inside A), B from
        /// <c>startBMs</c>, the preset with its style overrides at its <c>durationBars</c>. B's window: not beatmatched, the rule's
        /// (B's first start candidate + 2500; the official player published exactly that for its saved unmatched seams, protocol
        /// §6.3); beatmatched, the overlap, B unstretched until the engine has a per-voice rate (B's media time would then be
        /// <c>durationMs × speedB</c>; <see cref="MixPlanNotes.BeatmatchNotApplied"/>).</summary>
        static MixTiming Saved(in MixRecipe r, in PairFacts a, in PairFacts b)
        {
            var notes = r.CurveOverrides != 0 ? MixPlanNotes.CurveOverridesNotApplied : MixPlanNotes.None;
            long durA = a.DurationMs, durB = b.DurationMs;
            long overlap = Math.Max(1L, r.OverlapMs);
            if (overlap > MixStyles.MaxTransitionMs) { overlap = MixStyles.MaxTransitionMs; notes |= MixPlanNotes.Capped; }
            overlap = Math.Min(overlap, durA);
            long outStart = Math.Min(r.OutStartMs, durA - overlap);
            long inStart = r.InStartMs < durB ? r.InStartMs : 0;
            long inDur;
            if (r.IsBeatmatched) { inDur = overlap; notes |= MixPlanNotes.BeatmatchNotApplied; }
            else inDur = InLength(in b, ref notes);
            if (inStart + inDur > durB) { inDur = Math.Max(1, durB - inStart); notes |= MixPlanNotes.InClipped; }
            return MixTiming.Of(MixSource.Saved, r.Preset, r.StyleOverrides(), r.DurationBars, (uint)outStart, (uint)overlap, (uint)inStart, (uint)inDur,
                r.SpeedB > 0 ? r.SpeedB : 1f, r.BpmA > 0 ? r.BpmA : a.TempoX10 / 10f, notes);
        }

        /// <summary>B's fade-in length when not beatmatched: its first start candidate + <see cref="CueReachMs"/>, else
        /// <see cref="FallbackWindowMs"/>; at most 35 s.</summary>
        static long InLength(in PairFacts b, ref MixPlanNotes notes)
        {
            long len;
            if (b.Cues.HasFirstStartCandidate) len = (long)b.Cues.FirstStartCandidateMs + CueReachMs;
            else { len = FallbackWindowMs; notes |= MixPlanNotes.InFallback; }
            if (len > MixStyles.MaxTransitionMs) { len = MixStyles.MaxTransitionMs; notes |= MixPlanNotes.Capped; }
            return len;
        }

        /// <summary>The mean beat period over up to 8 beats around <paramref name="ms"/> (the local card's beat readout).</summary>
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
    }
}

/// <summary>#48 Mix: the seek rail's brighter end segment while a join is planned for the playing track (plan §3.10 "Player bar"): from
/// the mix-out to the audible end (the rail's end, the effective duration), a hairline over the theme's track. Pure decoration in the
/// rail's full-width slot; re-renders only when the plan or the duration changes.</summary>
internal sealed record MixRailProps(FluentGpu.Dsl.Element? Tip);

internal sealed class MixRailSegment : FluentGpu.Hooks.Component
{
    public override FluentGpu.Dsl.Element Render()
    {
        var tip = UseProps<MixRailProps>().Tip;
        float from = UseComputed(static () =>
        {
            _ = Playback.Mix.Changed.Value;
            long duration = Playback.DurationMs.Value;
            var t = Playback.Mix.Current;
            if (!t.Mix || duration <= 0 || t.OutStartMs >= duration) return -1f;
            return Math.Clamp(t.OutStartMs / (float)duration, 0f, 1f);
        }).Value;
        // The rail's tip host (chapters, a seek preview, a lyric line) rides the same slot: stacked over the segment, unchanged.
        FluentGpu.Dsl.Element segment = from < 0f ? new FluentGpu.Dsl.BoxEl { Key = "mix:rail:none", HitTestVisible = false } : Segment(from);
        return tip is null ? segment
            : new FluentGpu.Dsl.BoxEl { Key = "mix:rail:stack", ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false, Children = [segment, tip] };
    }

    static FluentGpu.Dsl.Element Segment(float from)
    {
        return new FluentGpu.Dsl.BoxEl
        {
            Key = "mix:rail", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FluentGpu.Foundation.FlexAlign.Center, HitTestVisible = false,
            Children =
            [
                new FluentGpu.Dsl.BoxEl { Grow = from, Basis = 0f, HitTestVisible = false },
                new FluentGpu.Dsl.BoxEl
                {
                    Key = "mix:rail:segment", Grow = 1f - from, Basis = 0f, Height = 2f, HitTestVisible = false,
                    Fill = FluentGpu.Dsl.Tok.TextPrimary with { A = 0.55f },
                },
            ],
        };
    }
}
