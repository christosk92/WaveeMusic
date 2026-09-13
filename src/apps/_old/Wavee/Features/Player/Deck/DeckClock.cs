using System;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Player.Deck.Model;

namespace Wavee;

/// <summary>
/// The ONE timer in the Now Playing hero slot — one per mounted deck, renders a 0x0 box.
///
/// <para><b>What it is.</b> Every tick it folds <c>PlaybackBridge</c> into a <see cref="DeckInput"/>, hands that to
/// the deck's <see cref="IDeckModel"/>, and diff-writes the resulting <see cref="DeckFrame"/> into
/// <see cref="DeckSignals"/> inside ONE <c>Runtime.Batch</c>. The face never re-renders: its leaf nodes bound those
/// signals at mount, so a tick costs one compositor pass and (when nothing crossed a quantum) not even that.</para>
///
/// <para><b>Why the ticker is a separate component.</b> Interval hooks belong to the component that owns them, and
/// <c>UseInterval</c> auto-pauses on <c>UseIsActive</c> (minimized, power-suspended, <c>Flow.KeepAlive</c>-parked).
/// Hanging it off the host would make every enable/disable edge re-render the host — and therefore re-BUILD the
/// face, whose bind thunks must be captured exactly once.</para>
///
/// <para><b>The four things that stop it:</b> reduced motion (a VALUE, never a hook branch — the state-change effect
/// below becomes the clock instead), the rail being closed, the deck being settled with no play intent, and the
/// window being inactive (folded in by <c>UseInterval</c>). All four are gates on ONE bool.</para>
/// </summary>
sealed class DeckClock : Component
{
    /// <summary>Mirrors <c>AmbientPowerPolicy.PluggedLoopHz</c> (the plugged-in default loop rate). Motion IS presents
    /// here (there is no partial repaint), so this rate is the deck's present rate; the value-gated writes below are
    /// what keep most of those elided. Kept as a self-paced <c>UseInterval</c> (not a slab <c>Cadence</c> row): the
    /// deck's per-tick fold (position, bands, peaks) is host state, not an animatable channel, and this component has
    /// no <c>AppHost</c> access to read <c>DefaultLoopHz</c> live — so the literal is a deliberate mirror of the
    /// constant, not a derivation from it.</summary>
    public const float TickMs = 1000f / AmbientPowerPolicy.PluggedLoopHz;

    /// <summary>A reported position further than this from what the interpolation expected, with no committed seek
    /// of our own, IS a seek — somebody else moved the playhead (a Connect device, a media key, a lock-screen
    /// scrub). Without this the arm would slew across the record as if the song had suddenly sped up.</summary>
    public const long RemoteJumpMs = 2_500;

    /// <summary>How close the estimate has to land to a synthesized remote jump before we stop forcing it.</summary>
    const long SyntheticSeekSettleMs = 500;

    /// <summary>Longest a committed seek may stay latched before the deck stops trusting it (the bridge's own
    /// latch is about a second; this is the deck's belt to that brace).</summary>
    const long SeekLatchMs = 2_000;
    long _seekCommittedAtMs;

    public required PlaybackBridge Bridge;
    public required IDeckModel Model;
    public required DeckSignals Out;
    public required bool UsesLevels;

    /// <summary>The deck's current turntable speed in rpm, re-read per tick (the option can flip while mounted).</summary>
    public required Func<float> Rpm;

    /// <summary>Degrees of rotation that move the disc's RIM by one pixel — the angle write's quantum. A 324-DIP
    /// deck yields ~0.5 deg, so a slow platter writes a new angle only when a pixel would actually move.</summary>
    public required Func<float> AngleQuantumDeg;

    /// <summary>Raised on the needle-drop edge (one tick). The host forwards it to the face's one-shot thump.</summary>
    public Action? ThumpRequested;

    /// <summary>The host's headshell/click-wheel gesture, if any: the ticker is also its preview pump, so a drag does
    /// not need a second timer (<c>SeekBar</c> mounts a <c>ScrubPreviewTicker</c> for exactly this job).</summary>
    public DeckGesture? Gesture;

    readonly Signal<bool> _settled = new(true);
    readonly Action _writeCb;

    PositionInterpolator _pos;
    bool _anchored;
    long _lastTickMs, _expectedPosMs;
    string? _uri, _albumUri;
    long _prevPos, _prevDur;
    PlaybackPhase _prevPhase;
    DeckBoundary _pendingBoundary;
    long? _syntheticSeek;
    DeckFrame _pending;
    float _quantum = 0.5f;

    public DeckClock() => _writeCb = WriteCore;

    public override Element Render()
    {
        var b = Bridge;
        var ui = UseContext(ShellUi.Slot);

        // ── the ~1 Hz anchor ────────────────────────────────────────────────────────────────────────────────────
        long posMs = b.PositionMs.Value;
        UseEffect(() =>
        {
            long now = FrameTime.NowMs;
            // A jump nobody here asked for: somebody else moved the playhead. Synthesize a seek target so the model
            // sees an EDGE (lift, swing, lower) instead of a groove that teleported.
            if (_syntheticSeek is null && b.SeekTargetMs.Peek() is null && _prevDur > 0
                && Math.Abs(posMs - _expectedPosMs) > RemoteJumpMs)
                _syntheticSeek = posMs;
            _pos.Anchor(now, posMs);
            _anchored = true;
        }, posMs);

        // ── the track-boundary edge ─────────────────────────────────────────────────────────────────────────────
        var track = b.CurrentTrack.Value;
        UseEffect(() =>
        {
            _pendingBoundary = DeckBoundaryRules.Classify(_uri, _albumUri, _prevPos, _prevDur, _prevPhase,
                track?.Uri, track?.Album.Uri, b.PositionMs.Peek(), b.Repeat.Peek() == RepeatMode.Track);
            _uri = track?.Uri;
            _albumUri = track?.Album.Uri;
            Tick();   // fold the edge NOW — a skip must not wait up to 33 ms, and it must not wait at all when paused
        }, track?.Uri ?? "");

        // ── the run gate ────────────────────────────────────────────────────────────────────────────────────────
        bool playing = b.IsPlaying.Value;
        bool pwr = playing;            // 0.2.9's bridge publishes ONE play fact: IsPlaying is the accepted intent
        bool buffering = b.IsBuffering.Value;
        bool err = b.Error.Value is not null;
        long durNow = b.DurationMs.Value;
        bool ended = PhaseOf(playing, buffering, err, posMs, durNow) == PlaybackPhase.Ended;
        _ = b.SeekTargetMs.Value;      // subscribe: a committed seek must re-fold even while paused-and-settled
        _ = b.ScrubTargetMs.Value;     // subscribe: a drag starting on a still deck must wake it
        _ = b.Repeat.Value;
        _ = NpvPlayerPrefs.Epoch.Value; // an option flip (33 -> 45) changes Rpm() under the running model
        bool reduced = Motion.ReducedMotion;
        bool settled = _settled.Value;
        bool railOpen = ui?.RailOpen.Value ?? true;
        bool run = !reduced && railOpen && (playing || pwr || buffering || !settled);
        UseInterval(Tick, TickMs, enabled: run);
        var active = UseIsActive();
        UseEffect(() =>
        {
            var source = b.LevelSource.Value;
            bool visible = active.Value;
            bool needsLevels = UsesLevels && visible && !Motion.ReducedMotion
                && (ui?.RailOpen.Value ?? true)
                && (b.IsPlaying.Value || b.IsBuffering.Value || !_settled.Value);
            if (!needsLevels || source is null) return null;
            var lease = source.AcquireLevels();
            return lease.Dispose;
        });

        // Reduced motion / rail closed / settled: there is no ticker, so a STATE CHANGE is the clock. Same hook, same
        // order, every render — only the dep changes (the canon rule: reduced motion is a value, not a branch).
        UseEffect(() => { if (!run) Tick(); },
            HashCode.Combine(posMs, playing, pwr, buffering, err, ended, reduced, settled));

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }

    /// <summary>
    /// THE fold: <c>PlaybackBridge</c> -> <see cref="DeckInput"/>. Static and shared with <see cref="Seed"/> so a
    /// deck mounted mid-song sees byte-identical inputs to one that has been ticking — which is what lets
    /// <c>TonearmMachine.Seed</c> answer "the record was already playing" instead of replaying a cue sequence.
    /// Peek-only: this runs from a timer, never from a render, so it must subscribe nothing.
    /// </summary>
    public static DeckInput Fold(PlaybackBridge b, ref PositionInterpolator pos, long nowMs, float rpm,
                                 DeckBoundary boundary, long? syntheticSeek)
    {
        bool playing = b.IsPlaying.Peek();
        bool buffering = b.IsBuffering.Peek();
        bool err = b.Error.Peek() is not null;
        bool advancing = playing && !buffering;
        long dur = b.DurationMs.Peek();
        long? seek = b.SeekTargetMs.Peek() ?? syntheticSeek;
        long position = pos.Estimate(nowMs, advancing, seek, null, dur);
        var phase = PhaseOf(playing, buffering, err, position, dur);
        return new DeckInput(
            nowMs,
            b.CurrentTrack.Peek() is not null,
            phase,
            playing,
            advancing,
            buffering,
            err,
            phase == PlaybackPhase.Ended,
            boundary,
            seek,
            b.ScrubTargetMs.Peek(),
            position,
            dur,
            b.Repeat.Peek() == RepeatMode.Track,
            rpm,
            Motion.ReducedMotion);
    }

    /// <summary>The playhead this close to the end while NOT playing is the track having run out — the queue end the
    /// tonearm rides into its locked groove on. Same window <c>DeckBoundaryRules.NaturalEndWindowMs</c> uses.</summary>
    const long EndedWindowMs = 1_500;

    /// <summary>Derive the deck's transport phase from what the 0.2.9 bridge actually publishes (Peek-free: the caller
    /// hands in values it already read, so this is usable from Render and from the ticker alike).</summary>
    static PlaybackPhase PhaseOf(bool playing, bool buffering, bool error, long positionMs, long durationMs)
    {
        if (error) return PlaybackPhase.Failed;
        if (buffering) return PlaybackPhase.Buffering;
        if (playing) return PlaybackPhase.Playing;
        if (durationMs > 0 && positionMs >= durationMs - EndedWindowMs) return PlaybackPhase.Ended;
        return PlaybackPhase.Paused;
    }

    /// <summary>The input a deck model is CONSTRUCTED from (see <see cref="Fold"/>): the transport exactly as it is
    /// right now, with a fresh interpolator anchored to the reported position.</summary>
    public static DeckInput Seed(PlaybackBridge b, float rpm)
    {
        long now = FrameTime.NowMs;
        var interp = default(PositionInterpolator);
        interp.Anchor(now, b.PositionMs.Peek());
        return Fold(b, ref interp, now, rpm, DeckBoundary.None, null);
    }

    void Tick()
    {
        Gesture?.Drain();   // the drag's 100 ms audible-preview pump rides this timer

        long now = FrameTime.NowMs;
        // An UNANCHORED interpolator extrapolates machine uptime (PositionInterpolatorTests pins why), and the
        // boundary effect can reach a first Tick before the position effect has anchored. Belt and braces.
        if (!_anchored) { _pos.Anchor(now, Bridge.PositionMs.Peek()); _anchored = true; }
        // Clamped: a resumed-from-sleep tick must not integrate a ten-minute dt into the platter.
        float dt = _lastTickMs == 0L ? TickMs / 1000f : Math.Clamp((now - _lastTickMs) / 1000f, 0.001f, 0.040f);
        _lastTickMs = now;

        var input = Fold(Bridge, ref _pos, now, Rpm(), _pendingBoundary, _syntheticSeek);
        _pendingBoundary = DeckBoundary.None;   // an EDGE: exactly one fold sees it
        _expectedPosMs = input.PositionMs;
        if (_syntheticSeek is { } s && Math.Abs(input.PositionMs - s) < SyntheticSeekSettleMs) _syntheticSeek = null;
        // The deck's own committed seek: CommitSeek publishes PositionMs optimistically, so the reported position
        // converges within a tick or two; release the latch then (or after the bridge's own latch window, whichever
        // comes first) so a stale target can never pin the arm.
        if (Bridge.SeekTargetMs.Peek() is { } committed
            && (Math.Abs(Bridge.PositionMs.Peek() - committed) < SyntheticSeekSettleMs || now - _seekCommittedAtMs > SeekLatchMs))
            Bridge.SeekTargetMs.Value = null;
        if (Bridge.SeekTargetMs.Peek() is not null && _seekCommittedAtMs == 0L) _seekCommittedAtMs = now;
        if (Bridge.SeekTargetMs.Peek() is null) _seekCommittedAtMs = 0L;
        _prevPos = input.PositionMs;
        _prevDur = input.DurationMs;
        _prevPhase = input.Phase;

        _pending = Model.Tick(in input, dt);
        float aq = AngleQuantumDeg();
        _quantum = aq > 0f ? aq : 0.5f;
        // ONE batch -> ONE FrameRequested. Without it a 24-band analyser would wake the frame loop 48 times a tick.
        if (Context.Runtime is { } rt) rt.Batch(_writeCb); else WriteCore();
        if (_pending.Thump) ThumpRequested?.Invoke();
    }

    // Every write is value-gated at a PERCEPTUAL quantum (a rim pixel, a 1/1024 of the bar, a 1/64 of a band): a tick
    // that moves nothing writes nothing, the DrawList stays byte-identical, and the host's skip-submit elides the
    // Present entirely. This is what makes a 30 Hz deck cost nothing while a long track creeps.
    void WriteCore()
    {
        var o = Out;
        var f = _pending;
        float aq = _quantum;
        Set(o.Frac, Q(f.Frac, 1f / 1024f));
        Set(o.Angle0, Q(f.Angle0, aq));
        Set(o.Angle1, Q(f.Angle1, aq * 0.5f));
        Set(o.Lift, Q(f.Lift, 0.01f));
        Set(o.Slide, Q(f.Slide, 0.005f));
        Set(o.Aux0, Q(f.Aux0, 0.005f));
        Set(o.Aux1, Q(f.Aux1, 0.005f));
        var bands = Model.Bands;
        for (int i = 0; i < bands.Length && i < o.Bands.Length; i++) Set(o.Bands[i], Q(bands[i], 1f / 64f));
        var peaks = Model.Peaks;
        for (int i = 0; i < peaks.Length && i < o.Peaks.Length; i++) Set(o.Peaks[i], Q(peaks[i], 1f / 64f));
        if (o.CoverGen.Peek() != f.CoverGen) o.CoverGen.Value = f.CoverGen;
        if (o.Phase.Peek() != f.Phase) o.Phase.Value = f.Phase;
        bool settled = Model.IsSettled;
        if (_settled.Peek() != settled) _settled.Value = settled;
    }

    static void Set(FloatSignal s, float v)
    {
        if (v != s.Peek()) s.Value = v;
    }

    static float Q(float v, float q) => q > 0f ? MathF.Round(v / q) * q : v;
}
