// ── Playback/Playback.Mix.Wire.cs — #48 Mix M7, Connect as the official player speaks it (protocol §6, full-support §2.4.4) ──────
//
// WHAT A PLAYING MIXED LIST PUBLISHES (WireNow, built once per plan and queue):
//
//   current row      the seam it came in by (fade-in keys, CurrentIn) and the seam it leaves by (fade-out keys: the pump's own plan, Out)
//   next_tracks[k]   both of its seams: in from the row before it, out to the row after it (cuepoint position 0 on an upcoming row);
//                    a "none" seam, a row queued from elsewhere, autoplay or a local file carries no key on that side
//
// THE MOMENT THE WIRE FLIPS (OnWire, decision D14 as the full-support plan revises it): the official player publishes f11 = the
// mix-out (`audio.fade_out_start_time`) while a transition is planned, and makes B current from the moment B starts playing (B at its
// in-point, A in prev_tracks, f9 = B's speed). A phone controlling Wavee reads those, so the PUT follows the official player. What
// Wavee's OWN bar shows during the overlap stays D14 (b) (the outgoing track runs to its audible end, "Mixing into …"): the owner's
// decision for M7 is not taken in the plan, so the bar keeps today's behaviour. The overlap is read off the deck's position against
// the planned join (a join the pump refused, too late or cross-kind, publishes nothing of it: Audio.MixRefusedFor).
//
// AS A CONTROLLER (NoteRemote): the owner's published fades (the current row's fade-out, next_tracks[0]'s fade-in) drive the queue's
// line and the bar's label instead of Wavee's own plan.
//
// A LIST PLAYED FROM ELSEWHERE (EnsureListMix): a remote play carries no lens (protocol §7), so the deck's playlist is read once when
// its rows are not held; a list Spotify has mixed then owes its lens read (Entities.CommitMix) and the planner follows.

using FluentGpu.Signals;

namespace Wavee;

public static partial class Playback
{
    public static partial class Mix
    {
        static MixWire? s_wire;
        static (int Plan, EntityId Context, uint Flags, bool Without, uint Queue, int Cursor, bool Shuffle, int Data, int Overrides, uint List) s_wireKey;

        static MixWire? s_djWire;
        static (int Plan, EntityId Context, uint Queue) s_djWireKey;

        /// <summary>The PUT's Mix half for the deck's context, rebuilt only when the plan, the context, its flags, the queue or the list's
        /// mix data change. Null outside a mixed playlist and outside an AI DJ seam that joins. UI thread (the capture).</summary>
        internal static MixWire? WireNow(in State s)
        {
            if (Dj.Of(s.Context) is not null)
            {
                // AI DJ: the seam's fade keys (Dj.WireOf: A's on the current item, B's on the row it joins), rebuilt when the plan moved.
                var djKey = (Changed.Peek(), s.Context, Queue.Version);
                if (djKey == s_djWireKey) return s_djWire;
                s_djWireKey = djKey;
                return s_djWire = Dj.WireOf(in s_djSeam);
            }
            if (s.Context.Kind != EntityKind.Playlist || !Entities.Current.Playlists.TryGetSlot(s.Context, out int slot)) return null;
            var p = new global::Wavee.Playlist(slot);
            bool without = MixOptOut.Is(p);
            var key = (Changed.Peek(), s.Context, (uint)p.MixFlags, without, Queue.Version, s.Cursor.Index, s.Shuffle && !MixPrefs.WhenShuffling,
                MixData.Version, MixOverrides.Version, Entities.Current.Edges.PlaylistTracks.Version(slot));
            if (key == s_wireKey) return s_wire;
            s_wireKey = key;
            if (!p.MixOn || without) return s_wire = null;
            var t = Current;
            var outMeta = new List<KeyValuePair<string, string>>(16);
            var inMeta = new List<KeyValuePair<string, string>>(16);
            FadeMeta(in t, outMeta, inMeta);
            var rows = Upcoming(in s, slot, in t, inMeta, out var currentIn);
            return s_wire = new MixWire(true, p.MixFlags, CurrentNext, outMeta.ToArray(), currentIn, rows);
        }

        /// <summary>Every upcoming row's two seams, in queue order from the deck (the reducer's walk), and the seam the deck row came in by.
        /// A pair is planned as the playing pair is (<see cref="PlanListPair"/>: a saved recipe on an adjacent pair, else Auto; the
        /// shuffle rule; "none" and refusals publish nothing); the deck's own next seam is the pump's plan (<paramref name="live"/>).</summary>
        static MixRowWire[] Upcoming(in State s, int slot, in MixTiming live, List<KeyValuePair<string, string>> liveIn,
            out KeyValuePair<string, string>[] currentIn)
        {
            currentIn = [];
            ReadOnlySpan<QueueEdge> q = Queue.Rows;
            int cursor = s.Cursor.Index;
            if ((uint)cursor >= (uint)q.Length || (s.Shuffle && !MixPrefs.WhenShuffling)) return [];
            var payload = Entities.Current.Edges.PlaylistTracks.Payload(slot);
            var listIndex = new Dictionary<ulong, int>(payload.Length);
            for (int i = 0; i < payload.Length; i++)
            {
                ulong item = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i].ItemId));
                if (item != 0) listIndex.TryAdd(item, i);
            }
            int ListIndexAt(int k, ReadOnlySpan<QueueEdge> rows)
                => (uint)k < (uint)rows.Length && rows[k].Provider == (byte)QueueProvider.Context && Queue.RefAt(k).Kind == EntityKind.Track
                   && listIndex.TryGetValue(rows[k].ItemId, out int i) ? i : -1;
            MixTiming PairAt(int k, ReadOnlySpan<QueueEdge> rows)
            {
                int a = ListIndexAt(k, rows), b = ListIndexAt(k + 1, rows);
                return a < 0 || b < 0 ? MixTiming.Refused(MixWhyNot.UserQueued) : PlanListPair(slot, a, b);
            }

            var scratchOut = new List<KeyValuePair<string, string>>(16);
            var scratchIn = new List<KeyValuePair<string, string>>(16);
            if (cursor > 0)
            {
                var into = PairAt(cursor - 1, q);
                FadeMeta(in into, scratchOut, scratchIn);
                currentIn = scratchIn.ToArray();
            }
            int end = Math.Min(q.Length, cursor + 1 + WireWindow.MaxNext);
            var rowsOut = new List<MixRowWire>(Math.Max(0, end - cursor - 1));
            bool liveHolds = live.Mix && cursor + 1 < q.Length && Queue.RefAt(cursor + 1).Id == CurrentNext;
            for (int k = cursor + 1; k < end; k++)
            {
                KeyValuePair<string, string>[] inKeys;
                if (k == cursor + 1 && liveHolds) inKeys = liveIn.ToArray();
                else
                {
                    scratchOut.Clear(); scratchIn.Clear();
                    var into = k == cursor + 1 ? MixTiming.Refused(MixWhyNot.NotMixed) : PairAt(k - 1, q);
                    FadeMeta(in into, scratchOut, scratchIn, upcoming: true);
                    inKeys = scratchIn.ToArray();
                }
                scratchOut.Clear(); scratchIn.Clear();
                var outOf = k + 1 < q.Length ? PairAt(k, q) : MixTiming.Refused(MixWhyNot.NotMixed);
                FadeMeta(in outOf, scratchOut, scratchIn, upcoming: true);
                // A row with only its fade-in (its next seam is not a mix) still names the mode, as the official rows do.
                if (scratchOut.Count == 0 && inKeys.Length > 0) scratchOut.Add(new("automix.mode", "auto"));
                if (inKeys.Length == 0 && scratchOut.Count == 0) continue;
                rowsOut.Add(new MixRowWire(q[k].ItemId, Queue.RefAt(k).Id, inKeys, scratchOut.ToArray()));
            }
            return rowsOut.ToArray();
        }

        /// <summary>Is the deck inside its planned overlap, as the wire tells it: a join is planned for the deck row and the pump has not
        /// refused it, and the position is past the mix-out. UI thread.</summary>
        public static bool InOverlap(in State s, long frameNowMs)
        {
            var t = Current;
            if (!t.IsPlaylistMix || CurrentNext.IsEmpty || !s.HasCurrent || Audio.MixRefusedFor(s.CurrentId)) return false;
            long pos = s.Position(frameNowMs);
            return pos >= t.OutStartMs && pos < t.AudibleEndMs;
        }

        /// <summary>The PUT as the official player states a mixed list (the file header): f11 = the mix-out while a join is planned and
        /// armed; inside the overlap B is current at its in-point (plus what it covered at its speed), A is the newest prev row, f9 = B's
        /// speed, B's own fade keys on it. Anything else passes through unchanged. UI thread (the capture).</summary>
        internal static Snapshot OnWire(in Snapshot snap, in State s, long frameNowMs)
        {
            var t = Current;
            // A playlist join only: an AI DJ join keeps its own current item (the reducer's narration semantics, Playback.Dj.TalkOver.cs).
            if (!snap.IsActive || !snap.HasTrack || !t.IsPlaylistMix || snap.Wire.Mix is not { } mix || Audio.MixRefusedFor(s.CurrentId)
                || !snap.Track.Equals(s.CurrentId)) return snap;
            long pos = s.Position(frameNowMs);
            var window = snap.Wire.Window;
            bool overlap = pos >= t.OutStartMs && pos < t.AudibleEndMs && window is { Next.Length: > 0 } && window.Next[0].Id == CurrentNext;
            if (!overlap)
                return new Snapshot(in snap.Device, snap.IsActive, snap.Reason, snap.MessageId, snap.Volume, snap.ClientTimestampMs,
                    snap.StartedPlayingAtMs, snap.HasBeenPlayingForMs, snap.HasTrack, snap.Track, snap.Context, snap.Uid, snap.PositionAsOfMs,
                    snap.TimestampMs, t.OutStartMs, snap.IsPlaying, snap.IsPaused, snap.IsBuffering, snap.Shuffling, snap.Repeat, snap.Kind,
                    snap.HasVideo, snap.LastCommandMessageId, snap.VideoGid, in snap.Wire);

            WireRow b = window!.Next[0];
            WireRow a = window.Current;
            var prev = new WireRow[window.Prev.Length + 1];
            window.Prev.CopyTo(prev);
            prev[^1] = new WireRow(snap.Track, a.ItemId, a.UidText.Length > 0 ? a.UidText : snap.Uid, a.Provider, a.Title, a.ArtistName, a.AlbumTitle,
                a.Image, a.AlbumId, a.ArtistId, a.DecisionId, a.QueuedBy, a.LocalUri);
            var shifted = new WireWindow(in b, prev, window.Next[1..],
                window.ContextIndex >= 0 && b.Provider != QueueProvider.Queue ? window.ContextIndex + 1 : window.ContextIndex, window.AutoplayStation);
            var bRow = mix.RowOf(b.ItemId, b.Id);
            var outKeys = bRow?.Out ?? [];
            // B's duration on the wire: its own mix-out when it mixes on, else its file length.
            long bDuration = 0;
            foreach (var e in outKeys)
                if (e.Key == "audio.fade_out_start_time" && long.TryParse(e.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v)) bDuration = v;
            if (bDuration <= 0 && Entities.Current.Tracks.TryGetSlot(b.Id, out int bSlot)) bDuration = new global::Wavee.Track(bSlot).DurationMs;
            // Out keys on B are an UPCOMING row's (cuepoint 0); B is the playing row now: its cuepoint is its mix-out's end.
            var current = new KeyValuePair<string, string>[outKeys.Length];
            long outDur = 0;
            foreach (var e in outKeys) if (e.Key == "audio.fade_out_duration") long.TryParse(e.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out outDur);
            for (int i = 0; i < outKeys.Length; i++)
                current[i] = outKeys[i].Key == "automix.fade_out_cuepoint.position" && bDuration > 0
                    ? new(outKeys[i].Key, (bDuration + outDur).ToString(System.Globalization.CultureInfo.InvariantCulture)) : outKeys[i];
            var viewMix = mix with { Incoming = shifted.Next.Length > 0 ? shifted.Next[0].Id : default, Out = current, CurrentIn = bRow?.In ?? [] };
            float speed = t.SpeedB > 0 && float.IsFinite(t.SpeedB) ? t.SpeedB : 1f;
            var w = snap.Wire;
            var wire = new WireExtras(shifted, w.QueueRevision, in w.Ids, w.PrivateSession, w.OutputDevice, w.CommandSender, speed, w.Origin,
                w.OutputType, w.Audiobook, w.LikedPlaylist, w.LikedOwner, viewMix, w.LocalFilesTracks, w.Quality);
            long bPos = t.InStartMs + (long)Math.Round((pos - t.OutStartMs) * (double)speed);
            string uid = b.UidText.Length > 0 ? b.UidText : b.ItemId != 0 ? b.ItemId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) : "";
            return new Snapshot(in snap.Device, snap.IsActive, snap.Reason, snap.MessageId, snap.Volume, snap.ClientTimestampMs,
                snap.StartedPlayingAtMs, snap.HasBeenPlayingForMs, true, b.Id, snap.Context, uid, bPos, snap.TimestampMs, bDuration,
                snap.IsPlaying, snap.IsPaused, snap.IsBuffering, snap.Shuffling, snap.Repeat, PlayableKind.Audio, false,
                snap.LastCommandMessageId, null, in wire);
        }

        // ── a list played from elsewhere ──────────────────────────────────────────────────────────────────────────────

        static EntityId s_listEnsured;

        /// <summary>The deck plays a Spotify playlist whose rows this PC does not hold (a remote play, a link): read them once, so a list
        /// Spotify has mixed is known as mixed and read through its lens (protocol §7: the receiver decides from the playlist).</summary>
        static void EnsureListMix(in State s)
        {
            if (s.Context.Kind != EntityKind.Playlist || s.Context.Provider != EntityProvider.Spotify || s.Context == s_listEnsured) return;
            s_listEnsured = s.Context;
            EntityRef list = Entities.Ref(s.Context);
            if (list.IsNone || Entities.Current.Edges.PlaylistTracks.State(list.Slot) != EdgeState.Unknown) return;
            Log.Info("mix", "the deck's playlist is not held here: reading it for its mix state");
            Entities.EnsureEdge(FetchEdge.PlaylistTracks, list.Slot, priority: FetchPriority.Prefetch);
        }

        // ── as a controller ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The owner's published transition: its current track's mix-out and the next track's fade-in, the next title, and the
        /// Mix preview signals it offers (1 = automix-preview, 2 = stop-automix-preview).</summary>
        public readonly record struct RemoteFade(bool Active, EntityId Track, long OutStartMs, long OutDurationMs, long InStartMs, long InDurationMs,
            string NextTitle, byte Offered);

        static RemoteFade s_remote;
        static ulong s_remoteTitleHash;

        /// <summary>What the owner offers and publishes, for diagnostics and the preview's send.</summary>
        public static RemoteFade Remote => s_remote;

        /// <summary>A foreign owner's cluster (Playback.Host.cs FoldCluster): note its published fades and preview signals. Strings are
        /// made only when they moved. UI thread.</summary>
        internal static void NoteRemote(Spotify.Decode.ClusterBuffer buffer, in Spotify.Decode.ClusterDelta d, EntityId track)
        {
            bool active = d.HasTrack && d.FadeOutStartMs >= 0 && d.FadeOutDurationMs > 0 && d.NextCount > 0;
            string title = s_remote.NextTitle ?? "";
            if (active)
            {
                var nextTitle = buffer.Utf8(buffer.Tracks(d.NextStart, 1)[0].Title);
                ulong hash = 14695981039346656037UL;
                foreach (byte b in nextTitle) { hash ^= b; hash *= 1099511628211UL; }
                if (hash != s_remoteTitleHash) { s_remoteTitleHash = hash; title = System.Text.Encoding.UTF8.GetString(nextTitle); }
            }
            var next = new RemoteFade(active, track, d.FadeOutStartMs, d.FadeOutDurationMs, d.FadeInStartMs, d.FadeInDurationMs,
                active ? title : "", d.MixOffered);
            if (next == s_remote) return;
            bool moved = next.Active != s_remote.Active || next.OutStartMs != s_remote.OutStartMs || next.OutDurationMs != s_remote.OutDurationMs
                || next.NextTitle != s_remote.NextTitle;
            s_remote = next;
            if (moved)
            {
                s_remoteText = null;
                if (active) Log.Info("mix", "the owner publishes a transition: out=" + d.FadeOutStartMs + " outDur=" + d.FadeOutDurationMs
                    + " in=" + d.FadeInStartMs + " inDur=" + d.FadeInDurationMs);
                Changed.Value = Changed.Peek() + 1;
            }
        }

        /// <summary>We are no longer a controller of a foreign owner. UI thread.</summary>
        internal static void ForgetRemote()
        {
            if (s_remote == default) return;
            bool was = s_remote.Active;
            s_remote = default;
            if (was) { s_remoteText = null; Changed.Value = Changed.Peek() + 1; }
        }

        /// <summary>The queue's line for the owner's published transition, "Mixing · 4.2 s"; null when none is published.</summary>
        static string? RemoteLine()
        {
            if (!s_remote.Active) return null;
            double s = s_remote.OutDurationMs / 1000.0;
            string seconds = s.ToString(s_remote.OutDurationMs % 1000 < 50 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
            return Strings.Mix.QueueLine(Strings.Mix.ReadoutPlain(seconds));
        }

        /// <summary>"Mixing into &lt;the owner's next title&gt;" while the owner publishes a transition; null otherwise.</summary>
        static string? RemoteLabel()
        {
            if (!s_remote.Active) return null;
            return s_remoteText ??= Strings.Mix.MixingInto(s_remote.NextTitle);
        }

        static string? s_remoteText;
    }
}
