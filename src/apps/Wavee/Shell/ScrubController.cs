// ── Shell/ScrubController.cs ───────────────────────────────────────────────────────────────────────────────────────
// the scrub of a seek rail as one core controller (themes plan C2, X2/X3): the pointer gesture (Begin / Update / Commit /
// Cancel) with its pixel dwell and its SEEK-GENERATION HOLD, the bar-wide keyboard ladder (KeyStep) and the ±N s step (SeekBy).
// A theme's rail only maps a pointer to a FRACTION and draws the signals this exposes.
//
// Role: CORE
// Spec: ch 20 §6, V-PA12, V-PA22, D4; the arithmetic is `Shell.SeekRail` (`Shell.PlayerBar.cs`)
//
// Before this, all of it lived in `Shell.PlayerBar.UI.cs` (`BarSeekRail`: the scrub signals, `Recompute`, the commit hold)
// and `Shell.PlayerBar.Podcast.UI.cs` (the ladder as static mutable state). Three things are NOT moved, because they are
// view: the readout bubble, the compositor binds, and the chrome-activity callbacks (the controller reports a gesture's edges
// through ONE callback and the view fans it out).
//
// The model is reached only through `IScrubSource`: production is `PlaybackScrubSource` (the `Playback` signals and posts);
// `ScrubControllerTests` drives the same controller and ladder with a fake source and a fake clock, so the dwell, the
// generation hold and the key ladder are unit facts with no reducer and no engine.

using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    // ══ THE PORT ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The model facts that gate a scrub: whether a rail may be dragged (<see cref="SeekRail.Enabled"/>) and whether
    /// the playhead advances on its own (<see cref="SeekRail.Advances"/>).</summary>
    public readonly record struct ScrubGate(bool HasCurrent, Playback.Fault Error, Playback.Phase Phase, bool Buffering,
        bool CanSeek)
    {
        public bool Enabled => SeekRail.Enabled(HasCurrent, Error, Phase, CanSeek);
        public bool Advances => SeekRail.Advances(HasCurrent, Error, Phase, Buffering);
    }

    /// <summary>What the scrub reads from and posts to the playback model. Every read takes <c>track</c> where a REACTIVE
    /// caller needs the subscription (a render, a computed) and is a peek otherwise; the controller's own work (an event
    /// handler, a tick, an effect body) is always peeks.</summary>
    public interface IScrubSource
    {
        /// <summary>The host's frame clock, in ms.</summary>
        long NowMs();

        ScrubGate Gate(bool track);

        Playback.LiveWindow Live(bool track);

        long DurationMs(bool track);

        bool IsBehindLive();

        /// <summary>The playhead extrapolated to <paramref name="nowMs"/> from the model's TIMESTAMPED sample.</summary>
        long PlayheadMs(long nowMs);

        /// <summary>The last REPORTED position (not extrapolated).</summary>
        long ReportedPositionMs();

        EntityId CurrentId();

        /// <summary>The seek generation (<see cref="Playback.SeekGen"/>).</summary>
        uint SeekGen();

        /// <summary>The generation of the last seek that LANDED (<see cref="Playback.LastSeekLandedGen"/>).</summary>
        uint LastSeekLandedGen();

        void ScrubBegin(int ms);
        void ScrubMove(int ms);
        void ScrubEnd(int ms);
        void ScrubCancel();
        void SeekTo(int ms);
    }

    /// <summary>The production port: the <see cref="Playback"/> signals and posts.</summary>
    public sealed class PlaybackScrubSource : IScrubSource
    {
        public static readonly PlaybackScrubSource Instance = new();

        public long NowMs() => Playback.FrameNowMs();

        public ScrubGate Gate(bool track)
            => track
                ? new ScrubGate(!Playback.CurrentId.Value.IsEmpty, Playback.Error.Value, Playback.PhaseSignal.Value,
                    Playback.Buffering.Value, Playback.CanSeek.Value)
                : new ScrubGate(!Playback.CurrentId.Peek().IsEmpty, Playback.Error.Peek(), Playback.PhaseSignal.Peek(),
                    Playback.Buffering.Peek(), Playback.CanSeek.Peek());

        public Playback.LiveWindow Live(bool track) => track ? Playback.Live.Value : Playback.Live.Peek();

        public long DurationMs(bool track) => track ? Playback.DurationMs.Value : Playback.DurationMs.Peek();

        public bool IsBehindLive() => Playback.IsBehindLive.Peek();

        public long PlayheadMs(long nowMs) => Playback.Snap().Position(nowMs);

        public long ReportedPositionMs() => Playback.PositionMs.Peek();

        public EntityId CurrentId() => Playback.CurrentId.Peek();

        public uint SeekGen() => Playback.SeekGen.Peek();

        public uint LastSeekLandedGen() => Playback.LastSeekLandedGen.Peek();

        public void ScrubBegin(int ms) => Playback.ScrubBegin(ms);
        public void ScrubMove(int ms) => Playback.ScrubMove(ms);
        public void ScrubEnd(int ms) => Playback.ScrubEnd(ms);
        public void ScrubCancel() => Playback.ScrubCancel();
        public void SeekTo(int ms) => Playback.SeekTo(ms);
    }

    // ══ THE KEYBOARD LADDER (bar-wide) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The keyboard scrub ladder (D4, V-PA22). Left / Right step a VISUAL target (5 s, then 15 s, then 30 s while
    /// the key is held, Shift = 1 s) and ONE seek commits once the key has been quiet for
    /// <see cref="KeyboardScrubLadder.GraceMs"/> (there is no key-up). It is BAR-WIDE state: the bar's key handler and the
    /// fullscreen stage's call the same <see cref="Step"/>, the commit tick is mounted by every seek rail (so it runs wherever
    /// a rail does: bar, stage, on-media), and every rail and time label paints <see cref="PreviewMs"/> until the seek lands.
    /// UI thread only.</summary>
    public sealed class KeyboardScrub
    {
        /// <summary>The one ladder the app shares.</summary>
        public static readonly KeyboardScrub Shared = new(PlaybackScrubSource.Instance);

        /// <summary>The commit tick's period while a ladder is live.</summary>
        public const float TickMs = 50f;

        readonly IScrubSource _src;
        KeyboardScrubLadder _ladder;
        long _baseMs;            // the ladder works in [0, span]; a commit seeks to BaseMs + target (a DVR window's start)
        EntityId _track;         // the row the ladder began on: a load that replaced it drops the ladder
        long _commitAtMs;        // the commit's frame stamp; 0 = still stepping
        uint _commitGen;         // the SeekGen sampled BEFORE the commit's post
        int _tickMounts;         // how many ticks are mounted: the last one leaving drops a live ladder

        public KeyboardScrub(IScrubSource source) => _src = source;

        /// <summary>The target the ladder paints, in ms (absolute, the rail's own coordinates); −1 = no ladder live. Read by
        /// the seek rails (the thumb) and the time labels. It stays set after the commit until the seek LANDS (or
        /// <see cref="SeekRail.CommitHoldMs"/> passes), so the thumb and the label never step back to the pre-seek position in
        /// the frame between the post and its drain.</summary>
        public Signal<long> PreviewMs { get; } = new(-1L);

        /// <summary>The ladder has something to tick for: stepping, or committed and waiting for its seek to land. Gates the
        /// 50 ms interval.</summary>
        public Signal<bool> Active { get; } = new(false);

        /// <summary>One Left / Right press (or OS repeat): step the ladder.</summary>
        public void Step(int direction, bool fine)
        {
            if (!_src.Gate(false).Enabled) { Reset(); return; }
            var live = _src.Live(false);
            long now = _src.NowMs();
            long position = live.HasWindow ? live.PositionMs : _src.PlayheadMs(now);
            var (baseMs, spanMs, fromMs) = SeekRail.KeySpace(in live, _src.DurationMs(false), position);
            if (spanMs <= 0) { Reset(); return; }    // no seekable span (live without a window): a step must never commit a seek to 0
            _commitAtMs = 0;                         // a new press supersedes a commit still waiting to land
            if (!_ladder.Active) _track = _src.CurrentId();
            int target = _ladder.Step(direction, fine, fromMs, spanMs, now);
            _baseMs = baseMs;
            PreviewMs.Value = baseMs + target;
            Active.Value = true;
        }

        /// <summary>The 50 ms tick (mounted while <see cref="Active"/>): commit ONCE when the key went quiet, then hold the
        /// painted target until the seek lands. The seek generation is sampled BEFORE the post (V-PA12): the drain that bumps
        /// it runs later, or inline under a synchronous marshaller.</summary>
        public void Tick()
        {
            long now = _src.NowMs();
            if (!_track.Equals(_src.CurrentId()) || !_src.Gate(false).Enabled) { Reset(); return; }   // the row changed under it
            if (_commitAtMs == 0L)
            {
                int target = _ladder.Tick(now);
                if (target < 0) return;                       // still stepping
                _commitGen = _src.SeekGen();                  // BEFORE the post
                _commitAtMs = Math.Max(1L, now);
                _src.SeekTo((int)Math.Clamp(_baseMs + target, 0L, int.MaxValue));
                return;
            }
            if (SeekRail.HoldsDrop(_commitAtMs, now) && !Playback.SeekLandedAfter(_commitGen, _src.LastSeekLandedGen())) return;
            Reset();                                          // landed (or the hold timed out): the model paints the position again
        }

        /// <summary>A commit tick mounted. Several rails ticking is harmless: the first to see the quiet key commits, the
        /// rest find the ladder already committed.</summary>
        public void TickMounted() => _tickMounts++;

        /// <summary>A commit tick unmounted. Nobody left to commit a live ladder (the only rail unmounted inside the grace):
        /// drop it, or the labels would hold its target forever.</summary>
        public void TickUnmounted()
        {
            if (--_tickMounts == 0) Reset();
        }

        void Reset()
        {
            _ladder = default;
            _commitAtMs = 0L;
            if (PreviewMs.Peek() >= 0L) PreviewMs.Value = -1L;
            if (Active.Peek()) Active.Value = false;
        }
    }

    // ══ THE POINTER GESTURE (one per rail) ═══════════════════════════════════════════════════════════════════════════

    /// <summary>One seek rail's scrub. The gesture is Begin → Update* → Commit (ONE seek) | Cancel; between a Commit and the
    /// seek's LANDING the rail keeps showing the DROP point (the seek-generation hold, V-PA12), so a ticker frame between the
    /// release and the drain never repaints the OLD position for one step. The signals are what a rail draws.</summary>
    public sealed class ScrubController
    {
        readonly IScrubSource _src;
        readonly KeyboardScrub _keys;
        readonly Action<bool>? _onGesture;

        long _committedAtMs;
        // V-PA12: the seek generation the rail SAW when it committed, sampled BEFORE the commit is posted (the drain that bumps
        // the generation runs later), so the hold ends on the first landing past it: the commit's own seek or a newer one's.
        uint _committedGen;

        /// <summary>A pointer gesture is live (the finger is down). While it is, the drawn fraction IGNORES the model.</summary>
        public Signal<bool> Scrubbing { get; } = new(false);

        /// <summary>The fraction under the pointer (0..1), valid while <see cref="Scrubbing"/>.</summary>
        public FloatSignal Fraction { get; } = new(0f);

        /// <summary>THE drawn fraction: the pixel-quantised position (or the scrub fraction while scrubbing or holding the drop
        /// point). A rail binds its fill and thumb to this one cell.</summary>
        public FloatSignal Display { get; } = new(0f);

        /// <summary>The rail's width in px, as the view measured it.</summary>
        public FloatSignal RailPx { get; } = new(0f);

        /// <param name="source">The model port.</param>
        /// <param name="keys">The bar-wide keyboard ladder, whose target the rail paints while it is live.</param>
        /// <param name="onGesture">Called with <c>true</c> when a gesture begins and <c>false</c> when it commits or cancels
        /// (the view's chrome-activity fan-out). Null for none.</param>
        public ScrubController(IScrubSource source, KeyboardScrub keys, Action<bool>? onGesture = null)
        {
            _src = source;
            _keys = keys;
            _onGesture = onGesture;
        }

        /// <summary>A controller over the real playback model and the shared ladder.</summary>
        public static ScrubController ForPlayback(Action<bool>? onGesture = null)
            => new(PlaybackScrubSource.Instance, KeyboardScrub.Shared, onGesture);

        // ── the static doors (bar-wide) ─────────────────────────────────────────────────────────────────────────────

        /// <summary>One Left / Right press on the shared ladder (the bar's key map and the stage's call this).</summary>
        public static void KeyStep(int direction, bool fine) => KeyboardScrub.Shared.Step(direction, fine);

        /// <summary>The ladder's painted target (−1 = none): the time labels read it.</summary>
        public static Signal<long> KeyPreviewMs => KeyboardScrub.Shared.PreviewMs;

        /// <summary>Where a ±<paramref name="deltaMs"/> step button lands, straight from the reported position: ONE seek, no
        /// accumulator (the reducer paints the drop point at the post, so a second press reads it). A rail that may not be
        /// dragged does nothing.</summary>
        public static void SeekBy(IScrubSource source, int deltaMs)
        {
            if (!source.Gate(false).Enabled) return;
            var live = source.Live(false);
            long position = live.HasWindow ? live.PositionMs : source.PlayheadMs(source.NowMs());
            long target = SeekRail.StepTargetMs(position, deltaMs, source.DurationMs(false), in live);
            source.SeekTo((int)Math.Clamp(target, 0L, int.MaxValue));
        }

        /// <summary><see cref="SeekBy(IScrubSource, int)"/> on the playback model.</summary>
        public static void SeekBy(int deltaMs) => SeekBy(PlaybackScrubSource.Instance, deltaMs);

        // ── what a render reads (reactive) ──────────────────────────────────────────────────────────────────────────

        /// <summary>May the rail be dragged right now? (a peek)</summary>
        public bool Enabled => _src.Gate(false).Enabled;

        /// <summary>The rail's gate, with the render's subscriptions.</summary>
        public ScrubGate Gate() => _src.Gate(true);

        /// <summary>The three-way SHAPE of the rail (reactive: a memo over it re-renders only on a shape change).</summary>
        public SeekRailMode Mode() => SeekRail.ModeOf(_src.Live(true));

        /// <summary>How long the playhead dwells on one pixel (reactive over the live window, the duration and the rail
        /// width): <c>clamp(span / railPx, 33, 250)</c> ms.</summary>
        public float DwellMs() => SeekRail.DwellMs(SeekRail.SpanMs(_src.Live(true), _src.DurationMs(true)), RailPx.Value);

        // ── the gesture ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The fraction under a local x on the measured rail.</summary>
        public float FractionAt(float x) => SeekRail.FractionAt(x, RailPx.Peek());

        /// <summary>The position a given pointer fraction commits to, in ms (the rail's own commit target: a track through the
        /// reducer's seek clamp, a DVR window into its edges), so what the scrub arms carry, what the labels show and what the
        /// release commits are one number (U-8).</summary>
        public long TargetMsAt(float fraction)
            => SeekRail.CommitTargetMs(fraction, _src.DurationMs(false), _src.Live(false));

        int TargetMs() => (int)Math.Clamp(TargetMsAt(Fraction.Peek()), 0L, int.MaxValue);

        /// <summary>Does a pointer gesture want a time readout? Only on a plain track (a DVR window's labels say how far
        /// behind live), not on an episode (its chapter timeline already names the chapter under the pointer).</summary>
        public bool ReadoutWanted
            => SeekRail.ModeOf(_src.Live(false)) == SeekRailMode.Track && _src.CurrentId().Kind != EntityKind.Episode;

        /// <summary>Pointer down at <paramref name="fraction"/>: jump to the press point and tell the reducer, which decides
        /// grains or a visual-only scrub. False (and nothing happens) when the rail may not be dragged.</summary>
        public bool Begin(float fraction)
        {
            if (!Enabled) return false;
            Scrubbing.Value = true;
            _onGesture?.Invoke(true);
            Fraction.Value = fraction;
            _src.ScrubBegin(TargetMs());
            Recompute();
            return true;
        }

        /// <summary>Pointer moved to <paramref name="fraction"/>. False when the rail may not be dragged.</summary>
        public bool Update(float fraction)
        {
            if (!Enabled) return false;
            Fraction.Value = fraction;
            _src.ScrubMove(TargetMs());
            Recompute();
            return true;
        }

        /// <summary>The drag-END edge: the gesture's ONE seek (the reducer: an audible scrub's release, else an ordinary
        /// seek), then the drop point is held until it lands. When nothing can be committed (the rail is disabled, there is no
        /// duration and no window) the gate is still RELEASED (<see cref="Cancel"/>; bailing with it set would freeze the fill at
        /// the abandoned finger position) and this returns false.</summary>
        public bool Commit()
        {
            if (!Enabled) { Cancel(); return false; }
            var live = _src.Live(false);
            long duration = _src.DurationMs(false);
            if (!SeekRail.CanCommit(live, duration)) { Cancel(); return false; }
            _committedGen = _src.SeekGen();      // BEFORE the post: a synchronous marshaller drains (and bumps it) inside ScrubEnd
            _src.ScrubEnd(TargetMs());           // the gesture's ONE seek
            _committedAtMs = Math.Max(1L, _src.NowMs());
            Scrubbing.Value = false;
            _onGesture?.Invoke(false);
            Recompute();
            return true;
        }

        /// <summary>Capture lost / Escape: idempotent (a gesture the reducer never began is nothing).</summary>
        public void Cancel()
        {
            _src.ScrubCancel();
            Scrubbing.Value = false;
            _onGesture?.Invoke(false);
            _committedAtMs = 0L;
            Recompute();
        }

        /// <summary>The rail was unmounted mid-gesture (the stage closing, a tier remount, a theme swap): a live scrub must not
        /// leave the pump holding the main voice behind a grain voice nobody will ever release. Tells the reducer and returns
        /// true iff a gesture was live (so the view closes its readout); touches no signal.</summary>
        public bool AbortIfScrubbing()
        {
            if (!Scrubbing.Peek()) return false;
            _src.ScrubCancel();
            return true;
        }

        // ── the model side ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A model report landed (position, duration, window, edge, phase, a seek landing, the ladder's target):
        /// release the drop-point hold on a LANDING past the generation the commit saw (V-PA12), not on the first report after
        /// it (the committing drain's own publish is a report, and releasing there repaints the PRE-seek position for the
        /// whole flight of the seek), then re-derive the resting fraction. No landing (a foreign owner, a parked deck) →
        /// <see cref="SeekRail.CommitHoldMs"/> ends it.</summary>
        public void OnModelReport()
        {
            if (_committedAtMs > 0L && !Scrubbing.Peek()
                && Playback.SeekLandedAfter(_committedGen, _src.LastSeekLandedGen())) _committedAtMs = 0L;
            Recompute();
        }

        /// <summary>The measured rail width changed (a change under half a pixel is noise).</summary>
        public void SetRailWidth(float widthPx)
        {
            if (widthPx > 0f && MathF.Abs(widthPx - RailPx.Peek()) > 0.5f)
            {
                RailPx.Value = widthPx;
                Recompute();
            }
        }

        /// <summary>Re-derive the ONE value the binds read. Peeks only: it runs from the ticker and from effects.</summary>
        public void Recompute()
        {
            var live = _src.Live(false);
            var mode = SeekRail.ModeOf(live);
            long now = _src.NowMs();
            bool holding = SeekRail.HoldsDrop(_committedAtMs, now);
            float model = 0f;
            if (!Scrubbing.Peek() && !holding)
            {
                bool advancing = _src.Gate(false).Advances;
                // THE CLOCK: the model's timestamped sample, extrapolated on the clock it was stamped with, or, while the
                // keyboard ladder is live (stepping, or committed and waiting for its seek to land), the ladder's target.
                long key = _keys.PreviewMs.Peek();
                long position = key >= 0L ? key
                    : advancing ? _src.PlayheadMs(now)
                    : mode == SeekRailMode.Dvr ? live.PositionMs : _src.ReportedPositionMs();
                model = SeekRail.ModelFraction(mode, position, _src.DurationMs(false), live, _src.IsBehindLive());
            }
            float shown = SeekRail.Displayed(Scrubbing.Peek() || holding, Fraction.Peek(), model);
            Display.SetIfChanged(SeekRail.Quantize(shown, RailPx.Peek()));   // an unmoved pixel writes nothing
        }
    }
}
