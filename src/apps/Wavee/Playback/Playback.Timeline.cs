// ── Playback/Playback.Timeline.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The playback timeline tap (#154 G2a): what is audible on THIS PC, anchored to the device clock, for anything that must follow
// the playhead off the UI thread (the LTC generator, the module observer fan-out, #48's mix planner).
//
// TWO HALVES, ONE READ. The engine publishes a straight line per session (`PcmAudioSession.TryReadTimeline`: the active voice's
// audible frames at a QPC instant, a rate, an advancing flag, an epoch). This file holds the other half, the IDENTITY: which
// track that line is, where it started on the session clock, its duration and any transition in flight. The pump publishes it
// at the sites that already change `s_id` / `s_activeStartMs` (`Playback.Audio`). `TryRead` composes the two: wait-free and
// allocation-free on any thread (the identity is an immutable record swapped whole; the anchor is a seqlock read).
//
// THE EPOCH. `Snapshot.Epoch` changes on every discontinuity a reader must relocate on: the engine's epoch (seek, pause, resume,
// rate, voice change, device rebuild) in the low 32 bits, the identity's generation (track, start, transition set or cleared) in
// the high 32. Same epoch = the same straight line, only re-anchored. `Changed` is raised on the pump when it moves (never per
// tick) and when the published duration changes with the epoch kept.
//
// Rules: no allocation on the read; a publish allocates one small record (a track change, a seek, never a tick). No UI.

using System.Threading;

using FluentGpu.Media;

namespace Wavee;

public static partial class Playback
{
    /// <summary>What is audible on THIS PC, for anything that must follow the playhead off the UI thread.</summary>
    public static class Timeline
    {
        [Flags] public enum Flags : byte { None = 0, Playing = 1, Transition = 2, Coarse = 4 }

        /// <summary>During a transition: which track the timecode names.</summary>
        public enum Master : byte { Outgoing, Incoming }

        /// <summary>One composed reading: the master track and its audible position at an instant.</summary>
        public readonly struct Snapshot
        {
            /// <summary>The master track (<c>default</c> when nothing plays locally).</summary>
            public readonly EntityId Track;
            /// <summary>The instant <see cref="PositionMicros"/> is true for (QPC, 100-ns units: read now with <see cref="NowQpc100ns"/>).</summary>
            public readonly long AnchorQpc100ns;
            /// <summary>Position inside <see cref="Track"/>, audible, at <see cref="AnchorQpc100ns"/>.</summary>
            public readonly long PositionMicros;
            /// <summary>0 when unknown. In a mixed playlist (#48) the track's effective end, not the file's length.</summary>
            public readonly long DurationMs;
            /// <summary>Content seconds per second: 1.0 for music; the episode speed.</summary>
            public readonly double Rate;
            public readonly Flags State;
            /// <summary>Changes on any discontinuity (engine epoch + identity generation).</summary>
            public readonly long Epoch;
            /// <summary>Valid with <see cref="Flags.Transition"/>.</summary>
            public readonly EntityId Incoming;
            public readonly long TransitionStartQpc100ns, TransitionEndQpc100ns;
            public readonly Master MasterVoice;

            internal Snapshot(EntityId track, long anchorQpc, long positionMicros, long durationMs, double rate, Flags state, long epoch,
                EntityId incoming, long transitionStart, long transitionEnd, Master master)
            {
                Track = track; AnchorQpc100ns = anchorQpc; PositionMicros = positionMicros; DurationMs = durationMs; Rate = rate;
                State = state; Epoch = epoch; Incoming = incoming; TransitionStartQpc100ns = transitionStart;
                TransitionEndQpc100ns = transitionEnd; MasterVoice = master;
            }

            public bool Playing => (State & Flags.Playing) != 0;

            /// <summary>The position at <paramref name="qpc100ns"/>: extrapolated while playing, held otherwise; never below 0 and
            /// NOT clamped to <see cref="DurationMs"/> (a mixed track still sounds past its mix-out).</summary>
            public long PositionMicrosAt(long qpc100ns)
            {
                if (!Playing) return PositionMicros;
                long p = PositionMicros + (long)((qpc100ns - AnchorQpc100ns) / 10.0 * Rate);
                return p < 0 ? 0 : p;
            }
        }

        /// <summary>A transition #48 (or a plain crossfade) scheduled on the session's mixer clock.</summary>
        readonly record struct Mark(EntityId Incoming, long StartMixFrame, long EndMixFrame, Master Master);

        /// <summary>The identity half, swapped whole (immutable): the session whose anchor it reads and how to turn that anchor
        /// into a position inside <see cref="Track"/>.</summary>
        sealed record Identity(PcmAudioSession? Session, EntityId Track, long StartMs, long DurationMs, bool SourceDomain, long Generation, Mark? Transition)
        {
            public static readonly Identity Empty = new(null, default, 0, 0, false, 0, null);
        }

        static Identity s_identity = Identity.Empty;
        static long s_generation;
        static readonly object s_publishGate = new();   // publishers only (the pump chain and the tick); readers never take it

        // `Changed` bookkeeping (s_publishGate)
        static long s_lastEpoch = long.MinValue, s_lastDurationMs = -1;
        static long s_changes;

        /// <summary>Raised on the pump chain when <see cref="Snapshot.Epoch"/> changes (track, seek, pause, resume, rate, transition
        /// set or cleared) and when the published duration changes with the epoch kept. Never per tick.</summary>
        public static event Action? Changed;

        /// <summary>Any thread, wait-free, allocation-free. False (and a default snapshot) when nothing plays on this PC: no
        /// local session (stopped, playing on another Connect device) or a session that has not published its first anchor.</summary>
        public static bool TryRead(out Snapshot snapshot) => TryRead(out snapshot, out _);

        /// <summary><see cref="TryRead(out Snapshot)"/> plus the raw engine anchor it was composed from (diagnostics).</summary>
        internal static bool TryRead(out Snapshot snapshot, out AudioTimelineAnchor anchor)
        {
            Identity id = Volatile.Read(ref s_identity);
            if (id.Session is not { } session || id.Track.IsEmpty || !session.TryReadTimeline(out anchor))
            {
                snapshot = default;
                anchor = default;
                return false;
            }
            int rate = Math.Max(1, anchor.MixRate);
            double frames = id.SourceDomain ? anchor.SourceFrame : anchor.ContentFrame;
            long micros = (long)(frames * 1e6 / rate) - (id.SourceDomain ? 0 : id.StartMs * 1000);
            Flags state = anchor.Advancing ? Flags.Playing : Flags.None;
            EntityId incoming = default;
            long tStart = 0, tEnd = 0;
            Master master = Master.Incoming;
            if (id.Transition is { } t && anchor.MixFrameAt(session.TimelineNowQpc100ns) < t.EndMixFrame)
            {
                state |= Flags.Transition;
                incoming = t.Incoming;
                tStart = anchor.QpcOfMixFrame(t.StartMixFrame);
                tEnd = anchor.QpcOfMixFrame(t.EndMixFrame);
                master = t.Master;
            }
            long epoch = (id.Generation << 32) | (anchor.Epoch & 0xFFFF_FFFFL);
            snapshot = new Snapshot(id.Track, anchor.Qpc100ns, Math.Max(0, micros), id.DurationMs, anchor.Rate, state, epoch,
                incoming, tStart, tEnd, master);
            return true;
        }

        /// <summary>Now, in the domain the snapshot's instants are stamped in: the machine's QPC in 100-ns units, or the clock of the
        /// endpoint the live session renders into when that is not the wall clock (the headless frame clock). Any thread.</summary>
        public static long NowQpc100ns() => Volatile.Read(ref s_identity).Session is { } s ? s.TimelineNowQpc100ns : AudioTimelineAnchor.NowQpc100ns();

        /// <summary>How many times <see cref="Changed"/> fired (diagnostics).</summary>
        internal static long ChangeCount => Interlocked.Read(ref s_changes);

        /// <summary>The live session the identity reads (diagnostics: its publisher counters and output latency).</summary>
        internal static PcmAudioSession? Session => Volatile.Read(ref s_identity).Session;

        /// <summary>#48's planner: a transition is audible between two session mix frames (<see cref="PcmAudioSession.SampleClock"/>
        /// domain; the start may lie slightly behind the audible edge or ahead of it); <paramref name="master"/> says which track
        /// the timecode names during it. Calling it again inside the window with the other master moves the flip (a midpoint flip
        /// is two calls). Pump chain.</summary>
        public static void SetTransition(EntityId incoming, long startMixFrame, long endMixFrame, Master master)
        {
            lock (s_publishGate)
            {
                Identity id = Volatile.Read(ref s_identity);
                if (id.Session is null) return;
                Volatile.Write(ref s_identity, id with { Generation = ++s_generation, Transition = new Mark(incoming, startMixFrame, endMixFrame, master) });
            }
            Poll();
        }

        /// <summary>The transition is over or cancelled. Pump chain.</summary>
        public static void ClearTransition()
        {
            lock (s_publishGate)
            {
                Identity id = Volatile.Read(ref s_identity);
                if (id.Transition is null) return;
                Volatile.Write(ref s_identity, id with { Generation = ++s_generation, Transition = null });
            }
            Poll();
        }

        /// <summary>Pump chain only: the identity of what <paramref name="session"/> plays, published where <c>Playback.Audio</c>
        /// changes <c>s_id</c> / <c>s_activeStartMs</c>. <paramref name="sourceDomain"/>: false = the position is the anchor's
        /// ContentFrame minus <paramref name="startMs"/> (tracks; <paramref name="startMs"/> is signed); true = the anchor's
        /// SourceFrame, <paramref name="startMs"/> ignored (episodes: required whenever the rate is not 1.0). A publish that changes
        /// only the duration keeps the generation (no relocation); anything else starts a new one and drops a transition.</summary>
        internal static void PublishIdentity(PcmAudioSession? session, EntityId track, long startMs, long durationMs, bool sourceDomain)
        {
            if (session is null) track = default;
            lock (s_publishGate)
            {
                Identity id = Volatile.Read(ref s_identity);
                bool same = ReferenceEquals(id.Session, session) && id.Track.Equals(track) && id.StartMs == startMs && id.SourceDomain == sourceDomain;
                if (same && id.DurationMs == durationMs) return;
                Identity next = same
                    ? id with { DurationMs = durationMs }
                    : new Identity(session, track, startMs, durationMs, sourceDomain, ++s_generation, null);
                Volatile.Write(ref s_identity, next);
            }
            Poll();
        }

        /// <summary>The pump's check, on every tick and after every publish: raise <see cref="Changed"/> when the epoch or the
        /// duration moved, and retire a crossfade's transition once its end is audible.</summary>
        internal static void Poll()
        {
            bool raise;
            lock (s_publishGate)
            {
                Identity id = Volatile.Read(ref s_identity);
                if (id.Transition is { } t && id.Session is { } s && s.TryReadTimeline(out AudioTimelineAnchor a)
                    && a.MixFrameAt(s.TimelineNowQpc100ns) >= t.EndMixFrame)
                    Volatile.Write(ref s_identity, id with { Generation = ++s_generation, Transition = null });
                long epoch = TryRead(out Snapshot snap) ? snap.Epoch : long.MinValue + 1;
                long duration = snap.DurationMs;
                raise = epoch != s_lastEpoch || duration != s_lastDurationMs;
                s_lastEpoch = epoch;
                s_lastDurationMs = duration;
                if (raise) s_changes++;
            }
            if (!raise) return;
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Warn("timecode", "timeline changed handler failed", ex); }
        }
    }
}
