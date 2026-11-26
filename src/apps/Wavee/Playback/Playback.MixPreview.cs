// ── Playback/Playback.MixPreview.cs — #48 Mix M5, "Hear it" (full-support plan §2.3.4, §2.4.1, §2.4.2; protocol §5) ──────────
//
// ONE PREVIEW, THREE INTAKES:
//
//   the editor's Hear it (Start: a list pair, an unsaved recipe)      ┐   this device plays (Previewing):
//   a Connect `automix-preview` from another device's editor (Receive) ├─▶   A from (mix-out − 3 s) → the overlap (the pump's own join,
//                                                                      │    MixRender.Join / Arm, the plan forced through Mix.Force) → B
//   Hear it while another device owns playback ─────────────────────── ┴─▶ sent to it instead: pause, then `automix-preview` (Send)
//
// The deck plays the pair as the official receiver does: context `spotify:mixing:preview`, rows `preview_1` / `preview_2`, no play log
// and no registration (Playback.Host.Context.cs Report). The state machine (Follow, after every drain):
//
//   Idle ──Start/Receive──▶ Previewing ── B played its window + 3 s ─┬─ loop: A again
//     ▲                         │  Stop / stop-automix-preview         └─ else: Restore
//     │                         │  a controller's pause / resume ───────────▶ Restore (paused / playing)
//     │                         └─ the deck left the preview (a play elsewhere, a load) ─▶ Abandon (nothing restored)
//     └──── Restoring: the queue as it was, the deck row at its position, playing or paused as it was (or as the stop says)
//
// The timeline (start, overlap, B, restore, restored) is kept for the inspector's `mix show=preview`. UI thread throughout.

using FluentGpu.Signals;

using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;

namespace Wavee;

public static partial class Playback
{
    /// <summary>#48 M5: the transition preview (the file header). The editor calls <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public static class MixPreview
    {
        public enum Phase : byte { Idle, Previewing, Restoring }

        /// <summary>The preview's phase: the editor's Hear it button reads it (playing ↔ stop).</summary>
        public static readonly Signal<Phase> State = new(Phase.Idle) { DebugName = "mix.preview" };

        /// <summary>The context a preview plays, as the official receiver names it (protocol §5).</summary>
        public const string ContextUri = "spotify:mixing:preview";
        /// <summary>A plays this long before its mix-out (the editor's <c>startPositionMs</c>, 3000 in every captured preview).</summary>
        public const int PreRollMs = (int)Spotify.Decode.AutomixPreRollMs;
        /// <summary>B plays on this long after its fade-in.</summary>
        public const int PostRollMs = 3_000;

        /// <summary>Who asked: this PC's editor, or another device's editor over Connect.</summary>
        public enum Origin : byte { Local, Connect }

        public readonly record struct Event(long AtMs, string What, string Track, long PositionMs, string Deck);

        static readonly List<Event> s_timeline = new();
        /// <summary>The last preview's events, oldest first (the inspector's <c>mix show=preview</c>).</summary>
        public static IReadOnlyList<Event> Timeline => s_timeline;

        /// <summary>What a restore puts back. The queue is copied whole; the deck row by its queue index.</summary>
        sealed record Saved(bool HasCurrent, EntityRef Row, EntityId Id, EntityId Context, int Cursor, int PositionMs, bool Paused,
            PlayableKind Kind, int[] Packed, QueueEdge[] Rows, int ContextStart);

        static Saved? s_saved;
        static EntityRef s_a, s_b;
        static MixTiming s_timing;
        static bool s_loop, s_sawOverlap, s_sawB, s_sawA, s_verifyRestore, s_handedOff;
        /// <summary>A still on the deck this long past its audible end: the join did not run.</summary>
        const int HandOffSlackMs = 500;
        static long s_bAtMs, s_startMs;
        static int s_fromMs;
        static Origin s_origin;
        static EntityId s_context;
        /// <summary>A preview sent to another device (Send): its id, so Stop sends the stop to the same device.</summary>
        static string s_remoteTarget = "";
        static bool s_remoteWasPlaying;

        public static bool Active => State.Peek() != Phase.Idle || s_remoteTarget.Length > 0;
        public static MixTiming Timing => s_timing;
        public static Origin From => s_origin;

        /// <summary>Is <paramref name="context"/> the preview's own context.</summary>
        public static bool IsPreviewContext(EntityId context) => !context.IsEmpty && context.Kind == EntityKind.Unknown && context.Text == ContextUri;

        // ── the editor's API ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>HEAR IT: preview the transition from row <paramref name="fromIndex"/> of the playlist at <paramref name="playlistSlot"/>
        /// into row <paramref name="toIndex"/> (−1: the next row). <paramref name="recipe"/> is an edit not saved yet (null: what plays now,
        /// a saved recipe or Auto), <paramref name="raw"/> the stored bytes it patches (kept for a Connect preview's recipe). While another
        /// device owns playback the preview goes to it over Connect (pause, then <c>automix-preview</c>). A new preview replaces the
        /// running one and keeps its restore point. False when the pair cannot be previewed (a local file, an episode, the last row).
        /// UI thread.</summary>
        public static bool Start(int playlistSlot, int fromIndex, int toIndex = -1, MixRecipe? recipe = null, byte[]? raw = null, bool loop = false)
        {
            if (Entities.Current is null) return false;
            var p = new global::Wavee.Playlist(playlistSlot);
            if (!p.IsValid) return false;
            if (toIndex < 0) toIndex = fromIndex + 1;
            var edges = Entities.Current.Edges;
            var targets = edges.PlaylistTracks.Targets(playlistSlot);
            var payload = edges.PlaylistTracks.Payload(playlistSlot);
            if ((uint)fromIndex >= (uint)targets.Length || (uint)toIndex >= (uint)targets.Length) return false;
            if (payload[fromIndex].Kind != PlaylistItemKind.Track || payload[toIndex].Kind != PlaylistItemKind.Track) return false;
            var a = new global::Wavee.Track(targets[fromIndex]);
            var b = new global::Wavee.Track(targets[toIndex]);
            if (!a.IsValid || !b.IsValid || a.IsLocal || b.IsLocal) return false;
            var fa = Mix.Facts(a, edges);
            var fb = Mix.Facts(b, edges);
            MixTiming timing = recipe is { } edit
                ? MixPlanner.Plan(in fa, BeatmatchFacts.Live(in fa, a.Slot, edges), in fb, BeatmatchFacts.Live(in fb, b.Slot, edges), true, in edit)
                : Mix.PlanListPair(playlistSlot, fromIndex, toIndex);
            if (ForwardsPlays) return Send(p, fromIndex, toIndex, a, b, recipe, raw);
            return Play(new EntityRef(EntityKind.Track, a.Slot), new EntityRef(EntityKind.Track, b.Slot), in timing, Origin.Local, loop, -1);
        }

        /// <summary>Stop the preview: restore what was playing (<paramref name="resume"/>: playing or paused; null: as it was). A preview sent
        /// to another device sends it <c>stop-automix-preview</c> (or <c>resume</c> when it does not offer the stop). UI thread.</summary>
        public static void Stop(bool? resume = null)
        {
            if (s_remoteTarget.Length > 0) { SendStop(resume ?? s_remoteWasPlaying); return; }
            if (State.Peek() != Phase.Previewing) return;
            Restore(resume, "stop");
        }

        // ── the Connect receiver ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A Connect <c>automix-preview</c> (this device is the active one, another device's editor sends it): play it, replacing
        /// a running preview; the first one keeps the restore point (the editor paused us first). UI thread.</summary>
        public static void Receive(Spotify.Decode.AutomixPreview preview)
        {
            if (Entities.Current is null) return;
            EntityId aId = EntityId.Parse(preview.A.AsSpan()), bId = EntityId.Parse(preview.B.AsSpan());
            if (aId.Kind != EntityKind.Track || bId.Kind != EntityKind.Track) { Log.Warn("mix", "automix-preview names no two tracks: dropped"); return; }
            EntityRef a = Entities.Ref(aId), b = Entities.Ref(bId);
            if (a.IsNone || b.IsNone) return;
            var edges = Entities.Current.Edges;
            var ta = new global::Wavee.Track(a.Slot);
            var tb = new global::Wavee.Track(b.Slot);
            bool hasRecipe = MixRecipe.TryDecode(preview.Recipe, out var recipe);
            // A pair this PC has not fetched yet: the recipe's own timing stands in for the lengths the planner checks.
            var fa = Mix.Facts(ta, edges);
            var fb = Mix.Facts(tb, edges);
            if (fa.DurationMs <= 0) fa = fa with { DurationMs = Math.Max(MixPlanner.MinTrackMs, (long)recipe.OutStartMs + recipe.OverlapMs) };
            if (fb.DurationMs <= 0) fb = fb with { DurationMs = (long)recipe.InStartMs + MixStyles.MaxTransitionMs + PostRollMs };
            var timing = MixPlanner.Plan(in fa, BeatmatchFacts.Live(in fa, a.Slot, edges), in fb, BeatmatchFacts.Live(in fb, b.Slot, edges), hasRecipe, in recipe);
            int preRoll = (int)Math.Clamp(preview.StartPositionMs, 0, 60_000);
            Log.Info("mix", "automix-preview received: preset=" + timing.Preset + " out=" + timing.OutStartMs + " overlap=" + timing.OutDurationMs
                + " preRoll=" + preRoll + (hasRecipe ? "" : " (no recipe: Auto)"));
            Play(a, b, in timing, Origin.Connect, loop: false, preview.RelativeStart || preview.StartPositionMs == Spotify.Decode.AutomixPreRollMs ? preRoll : -1);
        }

        /// <summary>A Connect <c>stop-automix-preview</c>: restore, resumed when its payload said "true". UI thread.</summary>
        public static void ReceiveStop(bool resume)
        {
            if (State.Peek() != Phase.Previewing) return;
            Log.Info("mix", "stop-automix-preview received (resume=" + resume + ")");
            Restore(resume, "stop-automix-preview");
        }

        /// <summary>Inside the drain, before a controller's verb folds: the editor wraps its previews in <c>pause</c> … <c>resume</c>
        /// (protocol §5): a pause or resume while a Connect preview plays ends it, the deck put back paused or playing as the verb says
        /// (the verb then folds on the restored deck). A skip ends it without a restore.</summary>
        internal static void OnRemoteCommand(RemoteCmd kind)
        {
            if (State.Peek() != Phase.Previewing) return;
            switch (kind)
            {
                case RemoteCmd.Pause: Restore(false, "pause"); break;
                case RemoteCmd.Resume: Restore(true, "resume"); break;
                case RemoteCmd.SkipNext or RemoteCmd.SkipPrev or RemoteCmd.SeekTo: Restore(null, kind.ToString()); break;
            }
        }

        /// <summary>A new load (a controller's play or transfer) replaces the preview: nothing is restored. UI thread.</summary>
        internal static void Abandon(string why)
        {
            if (State.Peek() == Phase.Idle) return;
            Note("abandoned (" + why + ")", default, 0);
            End();
        }

        // ── the local machine ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Play the pair <paramref name="a"/> → <paramref name="b"/> with <paramref name="timing"/>: A from
        /// <paramref name="preRollMs"/> (−1: <see cref="PreRollMs"/>) before the mix-out ("none": before A's end), the plan forced on the
        /// pair, the queue [A, B] in the preview context.</summary>
        static bool Play(EntityRef a, EntityRef b, in MixTiming timing, Origin origin, bool loop, int preRollMs)
        {
            if (!timing.Mix && timing.WhyNot != MixWhyNot.NoTransition)
            {
                Log.Info("mix", "preview refused: the pair does not mix (" + timing.WhyNot + ")");
                return false;
            }
            if (s_state.Owner == Owner.Foreign) return false;
            if (State.Peek() == Phase.Idle)
            {
                s_saved = Capture();
                s_timeline.Clear();
                s_startMs = FrameNowMs();
            }
            s_a = a;
            s_b = b;
            s_timing = timing;
            s_origin = origin;
            s_loop = loop;
            int pre = preRollMs >= 0 ? preRollMs : PreRollMs;
            long outAt = timing.Mix ? timing.OutStartMs : new global::Wavee.Track(a.Slot).DurationMs;
            s_fromMs = (int)Math.Max(0, outAt - pre);
            s_context = EntityId.Parse(ContextUri.AsSpan());
            Mix.Force(a.Id, b.Id, in timing);
            Note("start " + (timing.Mix ? MixSeams.ChipText(in timing) + " out=" + timing.OutStartMs + " overlap=" + timing.OutDurationMs
                + " in=" + timing.InStartMs + " inDur=" + timing.InDurationMs + (timing.SpeedB != 1f ? " speedB=" + timing.SpeedB.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) : "")
                : "no transition") + " (" + origin + ")", a.Id, s_fromMs);
            PlayPair();
            State.Value = Phase.Previewing;
            return true;
        }

        /// <summary>Lay [A, B] as the preview context and play A at the pre-roll.</summary>
        static void PlayPair()
        {
            s_sawA = s_sawOverlap = s_sawB = s_handedOff = false;
            Span<int> packed = stackalloc int[2];
            Span<EntityRef> refs = [s_a, s_b];
            Queue.Pack(refs, packed);
            Span<QueueEdge> rows =
            [
                new QueueEdge(s_uids.ItemIdOf("preview_1"u8), (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying),
                new QueueEdge(s_uids.ItemIdOf("preview_2"u8), (byte)QueueProvider.Context, (byte)QueueBucket.NextUp),
            ];
            Queue.Replace(packed, rows);
            Entities.Publish();
            Post(Input.PlayFrom(s_a, s_a.Id, s_context, Queue.CursorOf(0), PlayableKind.Audio, s_fromMs, FrameNowMs(),
                ClaimCause.UserPlay, paused: false, explicitPosition: true));
        }

        /// <summary>After every drain (Mix.Follow): the preview's timeline, its end and the restore's check.</summary>
        internal static void Follow(in State s)
        {
            if (s_verifyRestore && s_saved is { } back && s.CurrentId == back.Id && s.Phase != Playback.Phase.Loading)
            {
                s_verifyRestore = false;
                Note("restored " + (s.Phase == Playback.Phase.Paused ? "paused" : s.Phase.ToString().ToLowerInvariant()), s.CurrentId, s.Position(FrameNowMs()));
                s_saved = null;
            }
            if (State.Peek() != Phase.Previewing) return;
            long now = FrameNowMs();
            if (!IsPreviewContext(s.Context))
            {
                // The Play has not landed yet (its own drain), or something else took the deck (a play elsewhere): the second ends it.
                if (s_sawA || now - s_startMs > 5_000) Abandon("the deck left the preview");
                return;
            }
            int pos = s.Position(now);
            if (s.CurrentId == s_a.Id)
            {
                if (!s_sawA && s.Phase == Playback.Phase.Playing) { s_sawA = true; Note("A playing", s.CurrentId, pos); }
                if (!s_sawOverlap && s_timing.Mix && pos >= s_timing.OutStartMs) { s_sawOverlap = true; Note("overlap", s.CurrentId, pos); }
                // The join did not run (the pump refused it as late, or a --fake deck that prepares none): A is past its audible end, so B
                // comes in by a plain hand-off where the join would have left it, and the preview still reaches B.
                if (s_timing.Mix && !s_handedOff && s.Phase == Playback.Phase.Playing && pos >= s_timing.AudibleEndMs + HandOffSlackMs)
                {
                    s_handedOff = true;
                    int at = (int)(s_timing.InStartMs + Math.Round(s_timing.OutDurationMs * (double)s_timing.SpeedB));
                    Note("no join: B by a plain hand-off", s_b.Id, at);
                    Post(Input.PlayFrom(s_b, s_b.Id, s_context, Queue.CursorOf(1), PlayableKind.Audio, at, now, ClaimCause.UserPlay,
                        paused: false, explicitPosition: true));
                }
                return;
            }
            if (s.CurrentId != s_b.Id) return;
            if (!s_sawB) { s_sawB = true; s_bAtMs = now; Note("B current", s.CurrentId, pos); }
            // B played its fade-in and the post-roll: what is left of its window after the flip (D14 (b): B arrives the overlap in).
            long window = s_timing.Mix ? Math.Max(0, (long)s_timing.InDurationMs - (long)Math.Round(s_timing.OutDurationMs * (double)s_timing.SpeedB)) : 0;
            if (now - s_bAtMs < window + PostRollMs && s.Phase != Playback.Phase.Ended) return;
            if (s_loop) { Note("loop", s.CurrentId, pos); PlayPair(); return; }
            Restore(null, "end");
        }

        /// <summary>The deck as it is now: what a restore puts back.</summary>
        static Saved Capture()
        {
            int n = Queue.Count;
            var packed = Queue.PackedRefs.ToArray();
            var rows = Queue.Rows.ToArray();
            var s = s_state;
            var saved = new Saved(s.HasCurrent, s.Current, s.CurrentId, s.Context, s.Cursor.Index, s.Position(FrameNowMs()),
                s.Phase != Playback.Phase.Playing && s.Phase != Playback.Phase.Loading, s.Kind, packed, rows, Queue.ContextStart);
            Log.Info("mix", "preview: kept the deck (" + (saved.HasCurrent ? "a row at " + saved.PositionMs + " ms, " + (saved.Paused ? "paused" : "playing") : "nothing")
                + ", queue " + n + " rows)");
            return saved;
        }

        /// <summary>Put the deck back: the queue as it was, the row at its position, paused or playing (<paramref name="resume"/>, else as
        /// it was). With nothing playing before, the deck stops.</summary>
        static void Restore(bool? resume, string why)
        {
            var back = s_saved;
            State.Value = Phase.Restoring;
            Mix.Unforce();
            if (back is null) { End(); return; }
            bool paused = resume is { } r ? !r : back.Paused;
            Note("restore (" + why + ")", back.Id, back.PositionMs);
            if (!back.HasCurrent)
            {
                Queue.Replace(back.Packed, back.Rows, back.ContextStart);
                Entities.Publish();
                Post(Input.Stop(StopReason.None));
                s_saved = null;
                End();
                return;
            }
            Queue.Replace(back.Packed, back.Rows, back.ContextStart);
            Entities.Publish();
            long now = FrameNowMs();
            Post(Input.PlayFrom(back.Row, back.Id, back.Context, Queue.CursorOf(back.Cursor), back.Kind, back.PositionMs, now,
                ClaimCause.UserPlay, paused, explicitPosition: true));
            Post(Input.ContextPages(back.Context, s_pageUrl.Length > 0, now));
            s_verifyRestore = true;
            State.Value = Phase.Idle;
            s_remoteTarget = "";
        }

        static void End()
        {
            Mix.Unforce();
            State.Value = Phase.Idle;
            if (!s_verifyRestore) s_saved = null;
        }

        static void Note(string what, EntityId track, long positionMs)
        {
            long at = FrameNowMs() - s_startMs;
            string name = track == s_a.Id && !track.IsEmpty ? "A" : track == s_b.Id && !track.IsEmpty ? "B" : track.IsEmpty ? "-" : track.Text;
            string deck = s_state.HasCurrent ? (s_state.CurrentId == s_a.Id ? "A" : s_state.CurrentId == s_b.Id ? "B" : s_state.CurrentId.Text) : "-";
            if (s_timeline.Count < 64) s_timeline.Add(new Event(at, what, name, positionMs, deck));
            Log.Info("mix", "preview " + what + " track=" + name + " pos=" + positionMs);
        }

        // ── the editor's side over Connect (another device owns playback) ─────────────────────────────────────────────

        /// <summary>Send the preview to the owner, as the captured editor does: <c>pause</c> before the first one, then
        /// <c>automix-preview</c> with the pair, the pre-roll relative to the out-point and the recipe (an unsaved edit, the stored recipe,
        /// or the pair's Auto as a recipe), the playable uris, the context and the item speeds 1.0.</summary>
        static bool Send(global::Wavee.Playlist list, int fromIndex, int toIndex, global::Wavee.Track a, global::Wavee.Track b, MixRecipe? edit, byte[]? raw)
        {
            string target = Devices.IdOf(s_state.Own.Device);
            if (target.Length == 0) return false;
            string parameters = SendParameters(list, fromIndex, toIndex, a, b, edit, raw, out int recipeBytes);
            bool first = s_remoteTarget.Length == 0;
            if (first) s_remoteWasPlaying = IsPlaying.Peek();
            s_remoteTarget = target;
            LastSent = parameters;
            State.Value = Phase.Previewing;
            Log.Info("mix", "preview sent to the owner (" + (first ? "pause, then " : "") + "automix-preview, " + recipeBytes + " recipe bytes)");
            Spotify.Api.Run(() =>
            {
                if (first && s_remoteWasPlaying) Spotify.Connect.Pause(target, CancellationToken.None);
                Spotify.Connect.MixPreviewSignal(target, Spotify.Decode.MixPreviewSignal.Preview, parameters, CancellationToken.None);
            });
            return true;
        }

        /// <summary>The <c>automix-preview</c> parameters for a list pair (base64 <c>AutomixPreview</c>): the pair, the pre-roll relative to the
        /// out-point, the recipe with its item ids (an unsaved edit over its stored bytes, else the stored recipe, else the pair's Auto
        /// written as a recipe), the playable uris, the context, item speeds 1.0. Send and the inspector's envelope call it alike.</summary>
        internal static string SendParameters(global::Wavee.Playlist list, int fromIndex, int toIndex, global::Wavee.Track a, global::Wavee.Track b,
            MixRecipe? edit, byte[]? raw, out int recipeBytes)
        {
            var pair = MixEdit.Read(list.Slot, fromIndex);
            MixRecipe recipe;
            byte[] bytes = raw ?? [];
            if (edit is { } e) recipe = e;
            else if (pair.Valid && toIndex == fromIndex + 1)
            {
                recipe = MixEdit.ReplacedRecipe(in pair, list.Slot, out bytes);
                if (bytes.Length == 0 && MixEdit.AutoBaseline(in pair) is { } auto) recipe = auto;
            }
            else recipe = default;
            if (pair.Valid) { recipe.FromItemId = pair.ItemA; recipe.ToItemId = pair.ItemB; }
            byte[] encoded = recipe.IsEmpty && bytes.Length == 0 ? [] : MixRecipe.EncodeToArray(in recipe, bytes);
            recipeBytes = encoded.Length;
            var preview = new Spotify.Decode.AutomixPreview(a.Id.Text, b.Id.Text, "auto", "", Spotify.Decode.AutomixPreRollMs, true, 0, encoded,
                a.Id.Text, b.Id.Text, list.Id.Text, 1.0, 1.0);
            return Convert.ToBase64String(Spotify.Decode.EncodeAutomixPreview(preview));
        }

        /// <summary>The last <c>automix-preview</c> parameters this editor sent (diagnostics).</summary>
        public static string LastSent { get; private set; } = "";

        /// <summary>End a preview sent to the owner: <c>stop-automix-preview</c> with "true" / "false" when it offers the stop, else
        /// <c>resume</c> when it was playing (the captured editor closed with a resume).</summary>
        static void SendStop(bool resume)
        {
            string target = s_remoteTarget;
            s_remoteTarget = "";
            State.Value = Phase.Idle;
            bool offersStop = (Mix.Remote.Offered & 2) != 0;
            Log.Info("mix", "preview stop sent to the owner (" + (offersStop ? "stop-automix-preview " + (resume ? "true" : "false") : resume ? "resume" : "nothing to resume") + ")");
            Spotify.Api.Run(() =>
            {
                if (offersStop) Spotify.Connect.MixPreviewSignal(target, Spotify.Decode.MixPreviewSignal.Stop, resume ? "true" : "false", CancellationToken.None);
                else if (resume) Spotify.Connect.Resume(target, CancellationToken.None);
            });
        }
    }
}
