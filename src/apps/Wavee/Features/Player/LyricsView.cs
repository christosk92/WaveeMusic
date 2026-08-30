using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using FluentGpu.Text;
using Wavee.Backend.Lyrics;
using Wavee.Core;

namespace Wavee;

// The lyrics depth-of-field: ONE look for everyone, no tiers, no flags. Each dimmed line, by its distance from the active
// line, gets a soft self-blur. The ladder is a LOOKUP measured off the Apple Music reference capture (lyrics-parity
// campaign, 2026-08-03 — edge energy 27 / 3.7 / ~0 across the first three rings: ring 1 is only mildly soft, ring 3 is
// already dissolved), NOT the old linear `5 * min(dist/5, 1)` ramp, which held ring 1 far too crisp and stopped short at
// the far end. 0 ⇒ no blur layer is emitted at all (SceneRecorder drops sigma ≤ 0.01).
// PER SURFACE, because the two surfaces are doing different jobs with the same ladder. The RAIL is a narrow panel
// beside a page: it shows a handful of lines and the reference treatment is right there. The STAGE is a READING
// surface — it shows ~20 lines at 36 DIP, and at the rail's far rungs (4 → 5.5 → 6.5 σ) everything outside the focal
// band dissolves into fog, which is what "the lyrics are nearly illegible" was describing. The stage keeps the same
// SHAPE — monotone, 0 on the focus, ring 1 barely soft — and flattens the tail so the verse around the line stays
// readable as shape rather than resolving to nothing.
static class LyricsFx
{
    public static float DofSigma(int dist, bool large) => large ? StageSigma(dist) : RailSigma(dist);

    /// <summary>The RAIL's ladder: the Apple Music reference capture, measured (lyrics-parity campaign 2026-08-03 —
    /// edge energy 27 / 3.7 / ~0 across the first three rings). Unchanged.</summary>
    static float RailSigma(int dist) => dist switch
    {
        <= 0 => 0f,
        1 => 1.25f,
        2 => 2.5f,
        3 => 4f,
        4 => 5.5f,
        _ => 6.5f,
    };

    /// <summary>The STAGE's ladder: same shape, flatter tail. Ring 1 is within a rounding step of the rail's (and of
    /// the design reference's 1.2), and the far end settles at 3 instead of 6.5 — recessed, not dissolved.</summary>
    static float StageSigma(int dist) => dist switch
    {
        <= 0 => 0f,
        1 => 1.2f,
        2 => 2.0f,
        3 => 2.6f,
        _ => 3.0f,
    };
}

enum LyricsFollowMode : byte { Following, DetachedActive, DetachedIdle, Resyncing }
enum FollowArmResult : byte { Unavailable, AtTarget, Armed }
enum FollowScrollIntent : byte { Normal, Resync }

sealed class LyricsView : Component
{
    readonly record struct TrackIdentity(string Id, string Artist);

    internal readonly record struct FrameDiagnostics(long NowMs, long AuthMs, int ActiveLine, int VoiceLine, bool ActiveChanged, bool VoiceChanged, bool ScrollSnapped, bool Playing, int LineCount);
    internal static FrameDiagnostics LastFrameDiagnostics { get; private set; }

    // ── Probe seam (WAVEE_LYRICS_ADVANCE_PROBE) ──────────────────────────────────────────────────────────────────────
    // The lyrics-advance probe drives the media clock SYNCHRONOUSLY (ProbeStep → OnFrame) so a line advance and the
    // RunFrame that records it are the SAME frame; the autonomous stepper is silenced by ProbeSyncMode (LyricsTicker
    // leaves LyricsFrameStepper unmounted). ProbeForceSnapped skips the one-time first-landing instant jump so every measured
    // advance takes the STEADY-STATE follow path — which since Wave D is the latch + per-line cascade, not a spring.
    // ProbeStep with an UNCHANGED clock still runs the whole core lane (DriveCascade included), so the probe can step
    // the cascade frame by frame on real wall dt while holding one handoff in isolation.
    internal static bool ProbeSyncMode;
    internal static LyricsView? ProbeActive;
    internal void ProbeStep(long nowMs) => OnFrame(forceVisual: true, probeNowMs: nowMs);
    internal void ProbeForceSnapped() => _scrollSnapped = true;
    internal NodeHandle ProbeViewport => _viewportNode;
    internal int ProbeActiveLine => _activeLine.Peek();
    internal int ProbeLineCount => _doc?.Lines.Count ?? 0;
    internal long ProbeLineStartMs(int i) => _doc is { } d && (uint)i < (uint)d.Lines.Count ? d.Lines[i].StartMs : 0L;
    internal NodeHandle ProbeLineNode(int i) => _lineNodes is { } ln && (uint)i < (uint)ln.Length ? ln[i] : default;
    internal NodeHandle ProbeGlowNode(int i) => _glowNodes is { } gn && (uint)i < (uint)gn.Length ? gn[i] : default;
    internal NodeHandle ProbeDofNode(int i) => _dofNodes is { } dn && (uint)i < (uint)dn.Length ? dn[i] : default;
    // Cascade internals the reworked advance probe asserts on: the compensating dy per line, its remaining stagger, and
    // whether ANY line is still in flight (the self-quiesce flag — the settle-wall-time stop condition).
    internal float ProbeCascadeComp(int i) => (uint)i < (uint)_casComp.Length ? _casComp[i] : 0f;
    internal float ProbeCascadeDelayMs(int i) => (uint)i < (uint)_casDelayLeftMs.Length ? _casDelayLeftMs[i] : 0f;
    internal bool ProbeCascadeActive => _cascadePending;
    internal bool ProbeScrollSnapped => _scrollSnapped;
    internal LyricsFollowMode ProbeFollowMode => _followMode.Peek();

    // NOT a cadence any more — the dt SEED for the first step of the two integrators (DriveDofRamp, DriveCascade),
    // which have no previous stamp to difference on the frame a document lands. See LyricsMotionCadence below for what
    // actually paces the motion now (the host frame clock) and why a wall-clock period was the wrong driver.
    internal const long KaraokeWipeIntervalMs = 16;

    // Eye-leads-voice anticipation: emphasis + scroll resolve this many ms AHEAD of the true clock so the line is rising
    // into focus as the first syllable lands (the karaoke wipe stays on true time). Inside the safe 100-500 ms karaoke
    // lead window; comfortably above one 16 ms frame.
    internal const long LeadMs = 140;

    // Dejittered media clock (see OnFrame): a free-running wall-clock base + an additive slew correction, instead of a
    // hard re-anchor on every laggy IPC snapshot. RebaseClock seeds all of these together.
    long _baseWall;                     // monotonic wall anchor (Environment.TickCount64)
    long _basePos;                      // playback position at _baseWall
    float _offset;                      // additive slew correction folded from IPC-snapshot disagreement
    bool _wasPlaying;                   // last frame's play state — rebase the clock on the paused→playing transition
    long _lastAuthMs = long.MinValue;   // last authoritative IPC PositionMs the ticker reacted to
    long _lastDisplay;                  // last displayed nowMs — monotonic-while-playing guard (no backward wipe/line retreat)
    long _lastWipeWallMs;
    int _lastWipeLine = -1;

    // Glow cross-fade (the halo must never hard-toggle): per-line alpha SIGNALS, bound as each row's glow-wrapper Opacity.
    // Bound (not a static element value) so a row re-render re-asserts the LIVE fade value instead of snapping it — the
    // reconciler skips paint writes for bound Opacity. OnFrame ramps the incoming voice line in and the outgoing one out
    // over GlowFadeMs; at rest no signal is written, so settled frames stay byte-identical (skip-submit intact).
    const float GlowFadeMs = 240f;
    const float GlowOutMs = 320f;           // end-of-line melt window (BetterLyrics ≈350 ms; clamped to line end on media clock)
    // Held-note glow (WaveeMusic LyricsAnimator "辉光（长音节）" / BetterLyrics): the halo blooms ONLY while a syllable of
    // at least this duration is being sung — a whole-line wash reads as noise; a swell on the held note reads as voice.
    const float HeldGlowMinMs = 700f;       // WaveeMusic LyricsGlowEffectLongSyllableDuration default
    const float HeldGlowRampMaxMs = 500f;   // swell-in cap; short-ish holds swell across half the note instead
    // Peak amplitude of the held-note bloom, as a fraction of the fully-swelled envelope. Strict-parity trim (campaign
    // 2026-08-03, -25%): the reference clip shows NO glow, but it contains no ≥HeldGlowMinMs syllable, so it can only
    // argue the bloom down, never refute it. Applied ONCE, at the single consumption point (ApplyVoiceGlowEnvelope), to
    // the finished envelope — so it scales the whole curve uniformly and leaves every TIMING (the 700 ms threshold, the
    // swell cap, GlowFadeMs/GlowOutMs) exactly where it was. The out-cross-fade inherits it for free: BeginGlowFades
    // starts from the LIVE alpha.
    const float HeldGlowPeakScale = 0.75f;
    const long ResyncIdleMs = 4000L;
    const int ResyncProgressSteps = 120;       // 30 Hz-equivalent ring updates across the four-second idle window
    FloatSignal[] _glowAlpha = Array.Empty<FloatSignal>();
    int _glowInLine = -1; long _glowInStart; float _glowInFrom;
    int _glowOutLine = -1; long _glowOutStart; float _glowOutFrom;
    readonly Signal<LyricsFollowMode> _followMode = new(LyricsFollowMode.Following);
    readonly FloatSignal _resyncProgress = new(1f);
    long _resyncDeadlineWallMs;

    bool _scrollSnapped;
    // The kernel's live Driven-chase Target isn't a readable SceneStore column (ScrollState.PendingTargetY is
    // deleted, kernel-internal only) — mirror the last POSTED Resync destination locally so ScrollActiveIntoView can
    // still tell "already chasing this exact target" from "a fresh destination" without re-latching the half-life on
    // every re-arm. Cleared the moment the body isn't a Programmatic Driven chase any more.
    float? _followProgrammaticTargetY;
    readonly Signal<int> _activeLine = new(-1);   // emphasis + scroll target (lead-shifted)
    readonly Signal<int> _voiceLine = new(-1);    // line currently being sung (true time) — owns the karaoke wipe/glow
    readonly FloatSignal _nowMs = new(0f);

    // Per-line PACKED emphasis (bucket 0..6 in bits 0-2, bit 3 UNUSED, PAST flag in bit 4). One VALUE-GATED Signal per line.
    // What earns the ARRAY is the per-LINE subscription, not the value gate: a Memo value-gates too (the push-pull core
    // resolves an equal recompute back to Clean without running its subscribers), but ONE shared memo over the active line
    // would have every realized row subscribed to that one node, so every bucket sweep would fan out to the whole realized
    // document. A signal PER LINE is the only shape under which a row re-renders solely on ITS OWN bucket/past change. As
    // `_activeLine` sweeps, PushEmphasis rewrites all lines but only the ~dozen crossing a bucket boundary actually notify
    // — the rest (already at bucket 6) are no-op writes. Sized in PrepareDocument alongside `_glowAlpha`, and REUSED IN
    // PLACE across a same-line-shape document upgrade: mounted rows froze these very signal objects at mount.
    Signal<int>[] _lineEmphasis = Array.Empty<Signal<int>>();
    readonly Signal<int> _emphasisFallback = new(6);   // bucket 6 (fully dim) — only used during a transient array-resize gap

    // Per-line READING-ORDER run length in DIP: the sum of the widths of the wrapped visual-line fragments, laid
    // end-to-end over inked glyph edges — which is EXACTLY the extent GlyphWipe.Split/Softness are fractions of. The
    // node width (AbsoluteRect.W) is NOT that extent: it is the wrap box, so a half-full last line would make the same
    // DIP feather resolve to two different fractions. Filled LAZILY (once per line per width epoch, never per frame)
    // by RunLengthOf through the text seam; NaN = unmeasured. Sized in PrepareDocument alongside `_glowAlpha`.
    float[] _lineRunLen = Array.Empty<float>();
    float _runLenWrapW = float.NaN;   // the wrap width every cached entry was measured at; a ≥0.5 DIP move invalidates ALL

    // ── LyricsMotionCadence ──────────────────────────────────────────────────────────────────────────────────────────
    // The lyrics motion is driven from the HOST FRAME CLOCK (LyricsFrameStepper), not from a wall-clock interval.
    //
    // It used to be a 16 ms UseInterval — the KARAOKE cadence, back when the only per-frame work was the glow fade.
    // Since the handoff became a per-line compensating cascade driven from OnFrame's core lane (DriveCascade), OnFrame
    // is the MOTION step for every doc kind, and a timer is the wrong driver for motion twice over: HostTimerQueue
    // re-arms from the FIRE stamp (16 ms + this frame's work + wake slack, so the period drifts past one refresh and
    // never divides the panel's), and it produces no latency-sensitive wake reason of its own, so it says nothing about
    // the rate the host should be producing at. Subscribing FrameClock.Tick instead steps the cascade, the σ ramp and
    // the wipe EXACTLY once per produced frame, at the panel's rate, in the same pre-flush phase window the timer drain
    // used (AppHost.Paint publishes the tick right after the timer drain), and asks the host for panel-rate frames
    // (WakeReasons.FrameClockPoller — which is latency-sensitive, so the ambient cap never paces the surface either).

    NodeHandle _viewportNode = NodeHandle.Null;
    NodeHandle[] _lineNodes = Array.Empty<NodeHandle>();
    NodeHandle[] _glowNodes = Array.Empty<NodeHandle>();
    NodeHandle[] _dofNodes = Array.Empty<NodeHandle>();

    // ── Directional depth-of-field σ model ───────────────────────────────────────────────────────────────────────────
    // `_dofCurrent[i]` is the σ line i is being DRIVEN to — the single source of truth for that node's BlurSigma. The
    // TARGET is whatever DofForLine (+ suppression) resolves to, and the two are NOT reached symmetrically: the reference
    // front-loads the outgoing line's softening (it is essentially fully blurred inside the first ~100 ms of its exit)
    // while the incoming line sharpens gradually THROUGH the flight. So an INCREASE snaps and a DECREASE eases over
    // ~200 ms (DriveDofRamp). NaN = never driven ⇒ adopt the target on the first visit (that is what the element mounted
    // with). Sized in PrepareDocument alongside `_glowAlpha`; the element reads it back through DofDeclaredFor so a
    // re-render mid-ramp re-asserts the live σ instead of snapping the node to the rest target.
    float[] _dofCurrent = Array.Empty<float>();
    bool _dofRampPending = true;   // a target may have moved (emphasis / suppression / realization) — run a ramp pass
    long _dofRampWallMs;           // wall stamp of the last ramp pass (dt source; 0 = none yet)

    // ── Staggered handoff cascade ────────────────────────────────────────────────────────────────────────────────────
    // A line handoff is NOT a rigid viewport scroll. In the reference the OUTGOING line starts moving first and each
    // successive line below starts ~50-70 ms later (displacements 44/30/9 px coexist mid-flight), every line travels the
    // SAME distance, and they CONVERGE to one synchronized settle ≈0.48 s after onset with strictly zero overshoot. That
    // is not one spring on the viewport — it is N springs on the LINES.
    //
    // So while Following, every handoff LATCHES the viewport instantly to the new target (LatchViewport — the mechanics
    // the first-landing branch already proves out: one offset write + one content transform + one restore latch + one
    // Mark, and never a RequestRerender) and the felt motion is a per-line COMPENSATING translate springing back to 0.
    //
    //   INVARIANT — "latch + comp = no visual motion on the latch frame":
    //   the scroll content transform is Translation(0, -offset) (ApplyScrollTransform), so raising the offset by `delta`
    //   moves every line UP the screen by `delta`. Adding +delta to each line's own translate puts it back EXACTLY where
    //   it was, so the jump itself is invisible. Hence ArmCascade's sign: comp[i] += (newOffset - oldOffset). comp then
    //   decays to 0, which IS the felt travel.
    //
    // ADDING (never assigning) is what makes a mid-cascade re-target velocity-continuous: a second handoff folds its jump
    // into whatever compensation is still in flight instead of teleporting and restarting.
    //
    // Cost note: this is strictly LESS engine work than the per-frame programmatic spring it replaces — ONE latch (one
    // LayoutDirty|VirtualRangeDirty reflow) per handoff instead of one per frame of the settle, plus a scalar float loop.
    float[] _casComp = Array.Empty<float>();          // per-line compensating dy in DIP (0 ⇒ settled: no state, no write)
    float[] _casVel = Array.Empty<float>();           // its velocity (DIP/s) — the ζ=1 closed form is position AND velocity
    float[] _casDelayLeftMs = Array.Empty<float>();   // stagger remaining before this line starts moving (it HOLDS until 0)
    float[] _casRate = Array.Empty<float>();          // per-line ζ=1 rate y, picked so every rank lands at the same time
    bool _cascadePending;                             // self-quiescing pass flag, exactly like _dofRampPending
    // The same fact as _cascadePending, mirrored into a SIGNAL for the ticker to gate on. The hot path reads the plain
    // bool (no signal traffic per frame); this is written only at the two transitions per handoff (Signal<T> coalesces
    // an equal write), so the ticker re-renders twice per line change and never per frame. It exists because a PAUSE
    // mid-flight would otherwise disable the interval (needsTicks = playing || detached) and freeze every line at its
    // compensated position — the whole document sitting up to one row off the focal band until playback resumed.
    readonly Signal<bool> _cascadeRunning = new(false);
    // QPC stamp of the last cascade pass (dt source; 0 = none yet). Deliberately NOT Environment.TickCount64 like the σ
    // ramp: GetTickCount64 advances at the SYSTEM TIMER PERIOD (~15.6 ms unless something on the box raised it), so at
    // 60/120 Hz its per-frame delta alternates 0 / 15.6 ms — which would quantize the one motion the eye is actually
    // watching. A σ ramp behind a 0.1 write gate survives that; a per-line travel does not.
    long _casQpc;

    // ── The secondary line (translation / romanization) ──────────────────────────────────────────────────────────────
    // The MODE lives in WaveeSettings.LyricsSecondaryLine and is read ONCE per view, in Render, under the
    // LyricsPrefs.Epoch subscription (the PlayerBarPrefs idiom). It is republished into THIS signal, which is what every
    // row subscribes to, for two reasons:
    //   • Component props FREEZE at mount (component-props-contract.md), so an `int secondaryMode` ctor arg would never
    //     reach an already-mounted LyricLineView — a toggle would silently do nothing until the doc remounted.
    //   • A shared signal read straight from LyricLineView.Render re-renders the WHOLE realized document exactly once
    //     per toggle. That fan-out is deliberate and correct here: the mode changes only on a user action (never per
    //     frame, never as the active line sweeps), and EVERY row's height changes when it flips, so there is nothing a
    //     per-row value gate could save — unlike _lineEmphasis, whose whole point is that a boundary moves only a dozen
    //     of a few hundred rows.
    readonly Signal<int> _secondary = new(LyricsPrefs.None);
    // Whether the CURRENT document carries either layer on ANY line — scanned once per doc in PrepareDocument (never per
    // frame) and published to LyricsPrefs.Available for the rail/immersive headers.
    internal bool HasTranslation { get; private set; }
    internal bool HasRomanization { get; private set; }

    LyricsDocument? _doc;
    LyricsDocument? _pendingUpgrade;

    // Bumped by PrepareDocument on every NON-same-shape document swap, and folded into each row's reconciler key.
    //
    // Without it the row key is the bare index, so swapping a 62-line document for a 35-line one reconciles rows
    // ll0..ll34 IN PLACE — same key, same component type — and component props FREEZE at mount, so each surviving row
    // keeps the `_lineEmphasis[i]` / `_glowAlpha[i]` signal objects and the `LyricLine` it captured under the OLD
    // document. PrepareDocument's !sameShape branch has meanwhile reallocated all of those arrays, so the rows are left
    // subscribed to orphaned signals and their scene handles are never re-reported (a row reports them ONCE, at
    // realization). The result is a document frozen at its last emphasis: every line stuck blurred, no line ever
    // becoming active, for the rest of the track.
    //
    // The !sameShape branch is written on the premise that a genuine document change rebuilds the rows; this is what
    // actually makes that true. The same-shape path deliberately does NOT bump — keeping those rows mounted (and their
    // subscriptions live) is the whole point of that branch.
    int _docEpoch;

    string RowKey(int index) => "ll" + _docEpoch + ":" + index;
    Loadable<LyricsDocument?>? _docLoadable;
    LyricsMeasuredLayout? _layout;
    PlaybackBridge? _b;
    Services? _svc;

    readonly bool _large;
    /// <summary>WHICH ink ladder this view paints with — theme rungs on the rail, on-media whites on the stage. See
    /// <see cref="LyricsInk"/>: it is a MODE, resolved at the point of consumption, so the rail still follows a live
    /// theme flip while the stage is theme-invariant by construction.</summary>
    readonly LyricsInk _ink;
    readonly Func<bool>? _visible;
    // The focal band as a fraction of the lyrics viewport height, and its ONE owner. The immersive surface sits its
    // active line slightly HIGHER in the taller viewport (0.38 vs the rail's 0.40) so more of the upcoming document is
    // visible below it — the reference framing. `_band` is the copy the measured layout is built against (published by
    // LyricsContent); the interlude-dots overlay is built in Render, BEFORE LyricsContent has run on the first pass, so
    // it anchors off the PROPERTY. Two readers, one definition.
    float FocalBand => _large ? 0.38f : 0.40f;
    float _band = 0.40f;

    // Opt-in lyrics-search debug surface (a corner button → a per-source "why no/which lyric" panel). Env-gated like the
    // FPS HUD so it never shows for normal users; set WAVEE_LYRICS_DEBUG=1 to enable.
    static readonly bool _lyricsDebug =
        Environment.GetEnvironmentVariable("WAVEE_LYRICS_DEBUG") is "1" or "true" or "TRUE";
    readonly Signal<bool> _debugOpen = new(false);
    public LyricsView(bool large = false, bool onMedia = false, Func<bool>? visible = null)
    {
        _large = large;
        _ink = onMedia ? LyricsInk.Media : LyricsInk.Theme;
        _visible = visible;
    }

    public override Element Render()
    {
        var ui = UseContext(ShellUi.Slot);
        var b = UseContext(PlaybackBridge.Slot);
        var svc = UseContext(Services.Slot);
        _b = b;
        _svc = svc;
        var post = UsePost();

        bool open = _visible is not null ? _visible() : (ui?.RailOpen.Value ?? false);
        UseEffect(() =>
        {
            if (!open) ResetFollowState(Context.Scene);
        }, DepKey.From(open));

        // ── Secondary line: ONE settings read per view, republished to the rows ──────────────────────────────────────
        // Subscribing the epoch (not the setting — there is nothing to subscribe to in a registry) is what makes both
        // the Settings picker and the two header toggles apply LIVE to an already-mounted surface. The write below is
        // value-gated by Signal<T>, so it is a no-op on every render except the ones that follow an actual toggle.
        _ = LyricsPrefs.Epoch.Value;
        int secondary = LyricsPrefs.Clamp(svc?.Settings.Get(WaveeSettings.LyricsSecondaryLine) ?? LyricsPrefs.None);
        _secondary.Value = secondary;
        // A mode flip changes EVERY row's height, which invalidates two things the measured layout owns: the extent
        // table and the follow target derived from it. Route the recovery through the SAME mechanics a document
        // hot-swap uses — re-arrange (the engine's measured pass 1 re-measures every realized row and feeds SetMeasured,
        // so the ExtentTable self-corrects; the whole doc is realized here, so that is ONE arrange, not a rolling
        // correction) and then re-latch.
        //
        // ResetScrollSnap is the whole recovery: it clears _scrollSnapped so the next OnFrame takes the first-landing
        // branch and HARD-LATCHES the active line onto the focal band from the freshly measured geometry, and it
        // ZeroCascades on the way. The cascade zeroing is REQUIRED, not incidental: (a) the first-landing latch's
        // contract is that every comp is already 0 (it does not compensate), and (b) any in-flight comp is a
        // compensation measured against the row heights that just changed, so decaying it to 0 would land the document
        // somewhere that no longer exists. Nothing else is disturbed — _lineEmphasis, _dofCurrent and the four cascade
        // ARRAYS all survive (same doc, same line count: PrepareDocument is not re-entered), so the DoF ladder and the
        // per-line emphasis buckets carry straight across the toggle.
        //
        // _lineRunLen is deliberately NOT invalidated. It is the reading-order extent of the MAIN text only, measured
        // at RowFontSize/RowLineHeight against the wrap width — none of which the secondary line touches (it is a
        // sibling INSIDE the same stretch column, so the main text's wrap box is unchanged). MeasureRunLength's
        // TextStyle therefore stays exactly the main-text style; if that ever stops being true, this comment is the
        // first thing to revisit.
        UseEffect(() =>
        {
            if (Context.Scene is { } sc && !_viewportNode.IsNull && sc.IsLive(_viewportNode))
                sc.Mark(_viewportNode, NodeFlags.LayoutDirty | NodeFlags.VirtualRangeDirty);
            ResetScrollSnap();
        }, DepKey.From(secondary));
        // Subscribe only to the bridge's atomic match identity. Metadata-only CurrentTrack refreshes no longer rebuild
        // this chrome host, and track/context cannot be observed in a transient mismatched pair.
        var live = b?.Identity.Value.Track;
        var identity = new TrackIdentity(
            live?.Id ?? "",
            live is { Artists.Count: > 0 } ? live.Artists[0].Name : "");
        // Peek, NEVER .Value: subscribing the lyrics view to the IPC position snapshot forces a full re-render every
        // tick. The position is consumed by the per-frame ticker (OnFrame, via Peek) and re-anchored there.
        long posNow = b?.PositionMs.Peek() ?? 0L;
        string trackId = identity.Id;
        UseSignalEffect(() =>
        {
            string currentTrackId = _b?.Identity.Value.Track?.Id ?? "";
            if (currentTrackId.Length == 0 || _svc?.Lyrics is not IUpgradingLyricsProvider up) return;

            var sub = up.LyricsUpgraded.Subscribe(new LyricsUpgradeObserver(upgrade =>
            {
                if (!StringComparer.Ordinal.Equals(upgrade.TrackId, currentTrackId)) return;
                post(() =>
                {
                    string liveTrackId = _b?.Identity.Peek().Track?.Id ?? "";
                    if (StringComparer.Ordinal.Equals(liveTrackId, upgrade.TrackId))
                        ReceiveUpgrade(upgrade);
                });
            }));
            Reactive.OnCleanup(() => sub.Dispose());
        });

        if (b is null || svc is null) return new BoxEl { Grow = 1f };
        if (trackId.Length == 0)
        {
            ClearDocument();
            return Message("Nothing playing");
        }

        if (_doc is not null && !StringComparer.Ordinal.Equals(_doc.TrackId, trackId))
            ClearDocument();

        // The document + Virtual.Custom live under a track-id keyed component boundary. Rail/debug/chrome rerenders now
        // reconcile this one ComponentEl in place instead of rebuilding the virtual element and remounting every line.
        Element body = Embed.Comp(() => new LyricsDocHost(this, trackId, identity.Artist, posNow))
            with { Key = "lyrics-doc:" + trackId };

        // The ticker self-no-ops until a timed document exists. Key it by track so its once-per-mount scroll-snap reset
        // still follows track identity now that parent chrome rerenders no longer remount the document subtree.
        Element? ticker = open
            ? Embed.Comp(() => new LyricsTicker { Owner = this }) with { Key = "lyrics-ticker:" + trackId }
            : null;
        Element resync = ResyncOverlay();
        Element dots = InterludeDots();
        Element unsynced = UnsyncedBanner(b);
        var stack = new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true,
            Children = ticker is null
                ? [body, dots, resync, unsynced]
                : [body, ticker, dots, resync, unsynced],
        };

        if (!_lyricsDebug) return stack;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true,
            Children = _debugOpen.Value ? [stack, DebugOverlay(trackId)] : [stack, DebugButton()],
        };
    }

    // ── Lyrics-search debug surface (WAVEE_LYRICS_DEBUG=1) ────────────────────────────────────────────────────────────
    // A corner pill opens a panel that shows, for the playing track, the request metadata the sources searched with and a
    // per-source row (outcome + timing + the breadcrumb "why") + the reranker's verdict. Data is LyricsDiagnostics, which
    // the AggregatingLyricsProvider publishes once per fetch (so the report is already there for the current track).

    /// <summary>The top-docked note shown while a video suppresses timed sync (<see cref="LyricsSyncGate"/>). It exists
    /// so the missing highlight reads as a deliberate state rather than as broken sync: the lines are all still here and
    /// still scrollable, they just are not being followed, because the video is a different edit of the song and these
    /// timings belong to the audio edit.
    /// <para>Same shape discipline as <see cref="ResyncOverlay"/>: the full-bleed positioner stays OUTSIDE
    /// <c>Flow.Show</c>, because a control-flow wrapper mirrors layout participation but NOT
    /// <c>HitTestPassThrough</c> — returning the positioner through it would leave a full-viewport hittable node over
    /// the list and silently kill wheel/touch scrolling.</para></summary>
    Element UnsyncedBanner(PlaybackBridge b) => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
        Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.M, _large ? 20f : 12f, Spacing.M, 0f),
        Children =
        [
            Flow.Show(
                () => LyricsSyncGate.SyncSuppressed(b.VideoActive()),
                new BoxEl
                {
                    Shrink = 0f, MinWidth = 0f,
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                    Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                    Corners = CornerRadius4.All(Radii.Control),
                    // Ink + plate come from LyricsInk, never from a raw Tok.*: this ONE view renders both the theme rail
                    // and the on-media stage, so a hardcoded theme ink is invisible on media (the seam LyricsInk exists
                    // to close, and what StageLayoutTests.TheLyricsReadingSurface_PaintsNoThemeInk enforces).
                    Fill = _ink.Plate,
                    HitTestVisible = false,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo))
                        {
                            Size = 12f, Weight = 600, MinWidth = 0f,
                            Color = _ink.Tertiary,   // meta caption, not lyric text
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                }),
        ],
    };

    Element ResyncOverlay()
    {
        string label = Loc.Get(Strings.Player.ResyncLyrics);
        return new BoxEl
        {
            // Keep the FULL-BLEED pass-through node OUTSIDE Flow.Show. Control-flow/component wrappers mirror layout
            // participation but not HitTestPassThrough; returning this positioner through Flow.Show would therefore
            // leave a full-viewport hittable wrapper above the list and silently kill wheel/touch scrolling.
            Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, 0f, 0f, _large ? 28f : 18f),
            Children =
            [
                Flow.Show(
                    () => _followMode.Value is LyricsFollowMode.DetachedActive or LyricsFollowMode.DetachedIdle,
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                        Padding = new Edges4(11f, 7f, 13f, 7f), Corners = CornerRadius4.All(16f),
                        Fill = _ink.Plate, BorderWidth = 1f, BorderColor = _ink.PlateStroke,
                        HoverFill = _ink.PlateHover, PressedFill = _ink.PlatePressed,
                        PressScale = WaveeMotion.ScaleSubtle.Press, Cursor = CursorId.Hand,
                        OnClick = () => BeginResync(Context.Scene),
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        Enter = new EnterExit(Dy: 4f, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Dy: 4f, Opacity: 0f, Active: true),
                        Layout = LayoutTransition.Fade,
                        Children =
                        [
                            ProgressRing.Create(_resyncProgress, size: 18f, foreground: _ink.RingFill,
                                track: _ink.RingTrack),
                            new TextEl(label) { Size = 12f, LineHeight = 16f, Weight = 600, Color = _ink.Primary },
                        ],
                    }),
            ],
        };
    }

    // ── The interlude: the finished line RETIRES, three dots keep time ───────────────────────────────────────────────
    // When a line is sung out and the next one is a real instrumental break away, the reference does NOT leave the
    // finished line sitting half-lit on the focal band (the old `_interlude` recede — scale 0.97 / opacity 0.55 / σ1 —
    // which read as a still-selected line that had broken). It RETIRES it: the line takes the ORDINARY past treatment
    // (past bit, its rung of the past opacity/σ ladder), focus moves on to the upcoming line, and the silence is marked
    // by three breathing dots just above it. There is therefore no "interlude look" for a lyric row at all any more —
    // the whole feature is (a) an early active-line advance and (b) an overlay. Bit 3 of the packed emphasis, which
    // carried the recede, is now unused.
    //
    // WHY THE ADVANCE ALONE DOES THE JOB. AdvancePastInterlude runs on the RESOLVED line (N), never on its own output,
    // so it is a pure function of the clock: it answers N+1 on every frame of the break and stops answering the moment
    // ResolveLine itself reaches N+1 (at nextStart − LeadMs). No sticky state, no release edge — and the value it
    // produces at that boundary is exactly the value the plain lead-shifted resolve produces, so the advance ends
    // without a second handoff. Because it lands in `active` BEFORE `activeChanged` is computed, the ONSET is a normal
    // handoff: PushEmphasis retires line N onto the past ladder, ScrollActiveIntoView latches the viewport and
    // ArmCascade flies the document, so line N+1 sits at the focal band in its unsung-gray-sharp state (dist 0, wipe
    // split 0 — nothing special needed, that is simply what a line that has not started looks like) for the whole
    // break. When the break ends, nothing moves at all: the line just starts filling.
    //
    // WHY THE DOTS ARE AN OVERLAY AND NOT A ROW. A pseudo-row would have to exist in the measured layout, which means
    // the virtual window, the ExtentTable, the per-line emphasis/σ/glow/cascade arrays and every `index → line` mapping
    // in this file would grow a conditional off-by-one for the duration of a gap — a large blast radius bought for
    // something that carries no reading-order meaning and is never scrolled to. Instead the dots are a sibling of the
    // scroll viewport in the ZStack, anchored at the SAME band fraction the follow targets. The scroll, virtualization
    // and cascade machinery is untouched; the only thing the two paths share is `FocalBand`.
    //
    // A11Y. The engine's element surface exposes a semantic Role but no automation NAME (names come from text
    // content), and the dots have no text to name them with. So rather than mint a role that would announce nothing,
    // the row is `HitTestVisible = false` — excluded from hit-testing and focus for its whole subtree, i.e. explicitly
    // decorative. Nothing is lost: the information it carries (a break is elapsing) is already implied by the lyric
    // rows themselves, which remain the focusable, seekable, readable surface throughout.

    // The gap that must open between a line's sung-out point and the next line's start before the break counts as an
    // interlude at all. ONE constant — it replaced the inline `gap >= 4000` recede test. Below it the handoff stays the
    // ordinary 140 ms-lead one and nothing appears.
    const long InterludeGapMs = 5000L;
    // The dots retire this long before the next line starts, so their exit never collides with its first syllable. It
    // is also the fill's finish line (DriveInterludeDots): the meter reads FULL exactly as the row leaves.
    const long InterludeDotsExitMs = 1000L;
    const float InterludeBreathMs = 2600f;      // one full breath — slow enough to read as breathing, not as blinking
    const float InterludeBreathScale = 0.06f;   // trough depth of the scale pulse (the peak is always exactly 1)
    const float InterludeBreathAlpha = 0.10f;   // …and of the alpha pulse, so a FULLY filled dot still peaks at exactly 1
    const float InterludeDotWriteEps = 0.004f;  // ≈1/255: below this an alpha write cannot change a pixel

    // ── The dots' RESERVED BAND ──────────────────────────────────────────────────────────────────────────────────────
    // The dots are still an overlay (the blast-radius reasoning above is unchanged — no pseudo-row, no index mapping
    // grows a conditional), but an overlay that draws over the lyrics is not free of layout: with no reserved space they
    // sat inside the retired line above (its glyph bottom is only rowPad clear of the anchor row) and, once the anchor
    // wrapped to three lines, inside the anchor's OWN text. So the ANCHOR ROW buys the space, conditionally, for exactly
    // as long as it is the anchor: `dot + 2·air` of extra TOP PAD.
    //
    // PAD, not MARGIN, and that is load-bearing: the measured-virtual seam feeds the extent table from
    // Measure(row) — the row's own BORDER box, which excludes its margin (FlexLayout.Measure adds MarginMain in the
    // PARENT's loop) — while pass 2 arranges into `slot − margin`. A top margin would therefore reserve nothing and
    // SQUASH the row by the same amount. Padding is inside the border box, so measure and arrange agree exactly.
    //
    // WHERE IT GOES. The follow centres the row's CONTENT box (row minus the reserve) on the focal band — see
    // ScrollActiveIntoView — so the sung line does not move at all when the reserve arms; the space opens ABOVE it and
    // the dots sit in it. Air is measured off the type size on both surfaces.
    // VERIFICATION is live, not gated, and deliberately so: this geometry is a LyricsView + measured-virtual-layout
    // interaction, and LyricsView is outside every test closure the repo has (Wavee.Tests source-includes only the
    // engine-free Backend; the VerticalSlice references Engine+Controls and never the app). Repro: play a word-by-word
    // track with a ≥5 s instrumental break, let the follow park through it on BOTH surfaces (rail + immersive), and
    // check the dots sit in their own band with clear air above and below — including when the anchor line wraps to
    // two or three lines, and while the handoff cascade is still settling.
    internal static float InterludeDotSize(bool large) => large ? 12f : 9f;
    internal static float InterludeDotAir(bool large) => large ? 10f : 8f;
    internal static float InterludeReserveDip(bool large) => InterludeDotSize(large) + 2f * InterludeDotAir(large);
    // How far the dots' BOTTOM edge sits above the focal band at REST (the build-time flex anchor; DriveInterludeDots
    // adds a dynamic lift for rows too tall for it). One definition, read by the anchor math and the per-frame lane.
    internal static float InterludeDotLift(bool large) => large ? 48f : 36f;

    // The line currently holding the reserve, or -1. Armed when the dots go up, released at the NEXT HANDOFF (never at
    // the dots' own exit: that lands one second before the next line starts, so the document would close the gap — and
    // jump — in the middle of the silence) and at follow detach. Both release edges coincide with a latch+cascade, which
    // is what absorbs the height change as motion instead of a snap.
    int _interludeReserveLine = -1;
    // Frames after a reserve edge during which the follow re-evaluates its target. The height change reaches the extent
    // table only on the NEXT arrange, so the frame that arms cannot see it; re-running ScrollActiveIntoView for a few
    // frames lets the ordinary handoff branch (delta > 0.5 ⇒ LatchViewport + ArmCascade) pick the correction up on
    // whichever frame the measure lands, and no-op on the others.
    const int ReserveRelatchFrames = 4;
    int _reserveRelatchFrames;

    // Whether the dots are mounted. Flipped exactly TWICE per interlude (Signal<T> coalesces the equal writes), so the
    // ShowEl re-renders twice per break and never per frame; the per-frame lane writes only the scene + the dot alphas.
    readonly Signal<bool> _dotsShown = new(false);
    // Per-dot alpha, bound as each dot's Opacity. Allocated ONCE for the life of the view (there are always three), so
    // the per-frame path only ever writes them — the per-line glow-alpha idiom, at fixed size.
    readonly FloatSignal[] _dotAlpha = [new FloatSignal(0f), new FloatSignal(0f), new FloatSignal(0f)];
    NodeHandle _dotsRowNode = NodeHandle.Null;   // the breath-scale target (see InterludeDots)

    Element InterludeDots()
    {
        float d = InterludeDotSize(_large);
        float gap = _large ? 10f : 8f;
        // How far the dots' BOTTOM edge sits above the focal band AT REST. The band centres the upcoming row's CONTENT
        // box, whose single-line height is RowLineHeight + 2·rowPad (47 rail / 64 immersive), so half of that is where
        // that box's TOP edge is: 36 and 48 leave ~12 and ~16 DIP of air above it, inside the band the anchor row now
        // reserves (InterludeReserveDip). A TALLER (wrapped) anchor pushes its own top edge past this constant, which is
        // what DriveInterludeDots' dynamic extra lift answers — the rest anchor stays a pure build-time constant.
        float lift = InterludeDotLift(_large);
        float band = FocalBand;
        // The anchor, in pure flex and with no geometry read at all: two EMPTY Grow spacers (basis 0) split the panel's
        // free space at exactly `band`, and the dots row's own bottom MARGIN buys back the difference. Solving
        // `dotsBottom = band·H − lift` for that margin gives m = (d + lift)/band − d — in which H cancels, so one
        // build-time constant holds the anchor exact at every viewport size (a translate would have to re-read
        // ViewportH every frame, and would then have to fight the row's own enter/exit for the transform channel).
        float m = (d + lift) / band - d;
        return new BoxEl
        {
            // Full-bleed positioner. HitTestVisible = false excludes the WHOLE subtree, which is what a decorative
            // meter wants: the lyrics underneath stay scrollable and clickable straight through it. (The resync pill
            // next door needs HitTestPassThrough instead precisely because its own child IS hittable.)
            Grow = 1f, MinHeight = 0f, HitTestVisible = false,
            Direction = 1, AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Grow = band, MinHeight = 0f },
                Flow.Show(() => _dotsShown.Value, new BoxEl
                {
                    // Enter/exit owner. The structural animation owns this node's transform + opacity channels, so the
                    // breath MUST live on a child (one transform owner per node). Reduced motion is the engine's call
                    // here and it already makes it: a non-Exempt token snaps the scale terminal and keeps the fade.
                    // RowSidePad, not a second copy of the literal: the dots line up with the lyric column's leading
                    // margin on both surfaces, and stay lined up if that gutter is ever retuned.
                    Direction = 0, Shrink = 0f,
                    Margin = new Edges4(RowSidePad, 0f, 0f, m),
                    Enter = new EnterExit(Sx: 0.72f, Sy: 0.72f, Opacity: 0f, Active: true),
                    Exit = new EnterExit(Sx: 0.72f, Sy: 0.72f, Opacity: 0f, Active: true),
                    Layout = LayoutTransition.Fade,
                    Children =
                    [
                        new BoxEl
                        {
                            // Breath owner. Declares no static transform and owns no animation channel, so
                            // DriveInterludeDots' direct LocalTransform write is the only thing that ever touches it —
                            // the WriteCascade rationale, verbatim. The transform origin defaults to the centre, so
                            // the pulse scales the trio about its middle.
                            Direction = 0, Shrink = 0f, Gap = gap, AlignItems = FlexAlign.Center,
                            OnRealized = h => _dotsRowNode = h,
                            Children = [Dot(0, d), Dot(1, d), Dot(2, d)],
                        },
                    ],
                }),
                new BoxEl { Grow = 1f - band, MinHeight = 0f },
            ],
        };

        // One dot: a flat primary-ink disc whose ALPHA is the bound per-dot signal, so the gap meter and the breath
        // are the same single number per dot — and no glow, no fill swap, nothing else to keep in sync. Driving the
        // node's OPACITY (rather than a colour) is what ties the two ends to the wipe's own vocabulary: a filled dot is
        // exactly the ink of a SUNG glyph, and an unfilled one is that colour at UnsungAlpha, the fraction the wipe dims
        // an unsung glyph by. (On the rail in light theme the theme token is itself 0.894 rather than opaque, so the dim
        // end lands a few percent under the unsung glyph; the lit end, which is the one the eye reads, is exact.)
        Element Dot(int k, float size) => new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            Corners = CornerRadius4.All(size * 0.5f),
            Fill = _ink.Primary,
            Opacity = (Prop<float>)_dotAlpha[k],
        };
    }

    // The point a line stops being SUNG: the last syllable's end for a word-by-word line, else the authored end — and,
    // absent that, the next line's start, which by construction leaves no gap. ONE definition, shared by OnFrame's
    // voice clear and the interlude test below.
    static long SungOutMs(LyricsDocument doc, int index)
    {
        var l = doc.Lines[index];
        if (l.IsWordByWord && l.Syllables.Count > 0) return l.Syllables[^1].EndMs;
        return l.EndMs ?? (index + 1 < doc.Lines.Count ? doc.Lines[index + 1].StartMs : long.MaxValue);
    }

    // The interlude advance: an already-resolved (lead-shifted) `active` steps ON to the next line for the whole of a
    // real instrumental break, and gapStart/gapEnd come back as that break's bounds (equal ⇒ not in one).
    //
    // The ADVANCE is word-by-word ONLY, and for a sharp reason: a line-synced document has no syllable timing, so
    // retiring a line early would light the NEXT line fully white (a line-synced row has no wipe with which to look
    // "unsung") seconds before it is sung. The last line is likewise never advanced past: there is nothing to move
    // focus to, so it simply stays lit.
    //
    // The GAP is reported either way. Those used to be the same decision because a line-synced doc could never show a
    // gap at all — DeriveEnds sets EndMs to the next line's start, so sungOut == nextStart by construction. That is no
    // longer true: LyricsClean folds a removed ♪/blank row's timestamp into the line above it, which gives a line-synced
    // document a genuine early sung-out point for the first time. Reporting the gap lights the dots through the break
    // (the instrumental cue those ♪ rows used to carry) while the focus stays put, so neither half borrows the other's
    // hazard.
    static int AdvancePastInterlude(LyricsDocument doc, int active, long nowMs, out long gapStart, out long gapEnd)
    {
        gapStart = 0L; gapEnd = 0L;
        if (active < 0 || active + 1 >= doc.Lines.Count) return active;
        var l = doc.Lines[active];
        bool wordSynced = l.IsWordByWord && l.Syllables.Count > 0;
        // Absent syllables, only an AUTHORED end earlier than the next line is a real sung-out point; without one
        // SungOutMs falls back to the next line's start and no gap can exist anyway.
        long nextStart = doc.Lines[active + 1].StartMs;
        if (!wordSynced && !(l.EndMs is { } e && e < nextStart)) return active;
        long sungOut = SungOutMs(doc, active);
        if (nowMs < sungOut || nextStart - sungOut < InterludeGapMs) return active;
        gapStart = sungOut; gapEnd = nextStart;
        return wordSynced ? active + 1 : active;
    }

    // The dots' per-frame lane: pure scalar math plus at most three value-gated signal writes and one transform write,
    // all against preallocated state — zero managed allocation, in the file's established idioms (.Peek() gates, direct
    // node paint writes, never a RequestRerender). BOTH channels are driven off the MEDIA clock rather than the wall
    // clock: the meter is information about the song, and reading `nowMs` makes it dt-deterministic, monotone, frozen
    // across a pause and correct after a scrub, for free.
    void DriveInterludeDots(SceneStore scene, long nowMs, long gapStart, long gapEnd)
    {
        // FILL — the karaoke gesture at gap scale: dot k brightens from the unsung alpha to full white across its third
        // of the break. The span ends at the dots' EXIT, not at the next line, so the meter reads full exactly as the
        // row leaves instead of being cut off two-thirds lit.
        float span = MathF.Max(1f, (gapEnd - InterludeDotsExitMs) - gapStart);
        float p = Math.Clamp((nowMs - gapStart) / span, 0f, 1f);

        // BREATH — the one decorative channel here, and so the one reduced motion drops; the fill keeps running because
        // it is information. Read as a VALUE at the point of consumption, never as an early return (see the Reduced
        // motion block). Both pulses are shaped to PEAK at exactly 1 so a fully filled dot is exactly white.
        float pulse = Motion.ReducedMotion
            ? 1f
            : 0.5f + 0.5f * MathF.Sin((nowMs - gapStart) / InterludeBreathMs * MathF.Tau);
        float breathA = 1f - InterludeBreathAlpha * (1f - pulse);
        float breathS = 1f - InterludeBreathScale * (1f - pulse);

        var alpha = _dotAlpha;
        for (int k = 0; k < alpha.Length; k++)
        {
            float fill = Math.Clamp(p * alpha.Length - k, 0f, 1f);
            float a = (LyricLineView.UnsungAlpha + (1f - LyricLineView.UnsungAlpha) * fill) * breathA;
            if (MathF.Abs(alpha[k].Peek() - a) >= InterludeDotWriteEps) alpha[k].Value = a;
        }

        // LIFT — the one geometry read in this lane. The flex anchor above is exact for a SINGLE-LINE anchor row; a
        // wrapped (2-3 line) one has a taller content box, so its top edge — and with it the reserved band the dots must
        // sit in — is further above the focal band than the build-time `lift` constant assumes. Solve for the dots'
        // bottom edge sitting `air` above that top edge: bottom = band − (H/2 + air) ⇒ extra = H/2 + air − lift, floored
        // at 0 so a short row keeps the rest anchor (and the dots never drift DOWN into the lyric). H is the row's
        // CONTENT height, i.e. the measured extent minus the reserve this same row is carrying.
        // On the one or two frames between arming the reserve and the arrange that measures it, the extent is still the
        // UNRESERVED height while the subtraction already applies, so H reads one reserve short and the extra lift comes
        // out a little low. That is nil for a single-line anchor (both values floor at 0) and a few DIP for a wrapped
        // one — and it lands during the dots' own scale+fade enter, on the same frames the handoff cascade is flying.
        float extraLift = 0f;
        int anchor = _activeLine.Peek();
        if (_layout is { } lay && anchor >= 0 && !_viewportNode.IsNull && scene.IsLive(_viewportNode) && scene.HasScroll(_viewportNode))
        {
            ref ScrollState sc = ref scene.ScrollRef(_viewportNode);
            float rowH = lay.ItemRect(anchor, sc.ViewportW).H - ReserveOf(anchor);
            extraLift = MathF.Max(0f, rowH * 0.5f + InterludeDotAir(_large) - InterludeDotLift(_large));
        }

        var h = _dotsRowNode;
        if (h.IsNull || !scene.IsLive(h)) return;
        ref NodePaint paint = ref scene.Paint(h);
        // Both channels live in ONE matrix (a node has a single transform owner), so the early-out must compare BOTH —
        // gating on the breath scale alone would swallow a lift change on any frame the pulse happened to be flat.
        float dy = -extraLift;   // the matrix translates DOWN-positive; the lift is upward
        if (MathF.Abs(paint.LocalTransform.M11 - breathS) < 0.002f && MathF.Abs(paint.LocalTransform.Dy - dy) < 0.05f) return;
        paint.LocalTransform = breathS >= 1f && dy == 0f
            ? Affine2D.Identity
            : new Affine2D(breathS, 0f, 0f, breathS, 0f, dy);
        scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    // The vertical space line `index` is currently reserving for the dots (0 unless it is the interlude anchor).
    float ReserveOf(int index) => index >= 0 && index == _interludeReserveLine ? InterludeReserveDip(_large) : 0f;

    // Arm/release the reserved band. One write per edge, through the SAME per-line emphasis signal the ladder already
    // uses (bit 3, free since the deleted recede), so exactly the one or two affected rows re-render — a shared
    // "interlude anchor" signal read by every row would re-render the whole realized document twice per break.
    void SetInterludeReserve(int line)
    {
        if (_interludeReserveLine == line) return;
        int prev = _interludeReserveLine;
        _interludeReserveLine = line;
        var em = _lineEmphasis;
        int active = _activeLine.Peek();
        if ((uint)prev < (uint)em.Length) em[prev].Value = PackEmphasis(prev, active, line);
        if ((uint)line < (uint)em.Length) em[line].Value = PackEmphasis(line, active, line);
        // A row height changed mid-document: the same recovery the secondary-line toggle uses, minus the hard re-latch.
        // Re-arranging is what feeds the corrected extent into the table; the follow then rides its ORDINARY handoff
        // branch (see _reserveRelatchFrames) so the document travels to the new target with the cascade, instead of
        // ResetScrollSnap's first-landing jump — which would also zero the cascade the interlude handoff just armed.
        if (Context.Scene is { } sc && !_viewportNode.IsNull && sc.IsLive(_viewportNode))
            sc.Mark(_viewportNode, NodeFlags.LayoutDirty | NodeFlags.VirtualRangeDirty);
        _reserveRelatchFrames = ReserveRelatchFrames;
    }

    // Retire the dots. The ShowEl unmount runs the Exit terminal; dropping the handle guarantees the per-frame lane can
    // never write to a node on its way out (the generation in the handle would catch a recycled index, but not writing
    // at all is both cheaper and clearer). After this the whole feature costs one bool Peek per frame.
    void HideInterludeDots()
    {
        _dotsShown.Value = false;
        _dotsRowNode = NodeHandle.Null;
    }

    sealed class LyricsDocHost(LyricsView owner, string trackId, string artist, long initialPositionMs) : Component
    {
        public override Element Render()
        {
            var svc = UseContext(Services.Slot);
            string fetchKey = trackId + "|" + artist;
            var docL = UseResource(
                ct => svc?.Lyrics is { } provider
                    ? provider.GetLyricsAsync(trackId, ct)
                    : Task.FromResult<LyricsDocument?>(null),
                (LyricsDocument?)null, fetchKey).Loadable;
            owner._docLoadable = docL;

            var doc = docL.Value.Value;
            if (doc is { Lines.Count: > 0 } ready)
                owner.PrepareDocument(ready, owner._b?.PositionMs.Peek() ?? initialPositionMs);

            return Skel.Region<LyricsDocument?>(
                docL,
                shimmerSource: () => LyricsShimmer(owner._large, owner._ink),
                content: d => d is { Lines.Count: > 0 } readyDoc ? owner.LyricsContent(readyDoc) : owner.Message("No lyrics available"),
                reveal: SkelReveal.FadeOnly,
                onFailed: () => owner.Message("No lyrics available"),
                isEmpty: d => d?.Lines is null || d.Lines.Count == 0,
                onEmpty: () => owner.Message("No lyrics available"),
                style: new SkeletonStyle(owner._ink.Skeleton, RowGap: owner._large ? 18f : 14f, BarRadius: 6f, TextRatio: 0.86f),
                smoothResize: false);
        }
    }

    // ink-scan: off ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Everything until the matching re-opening marker below is the ENV-GATED lyrics-search debug surface
    // (WAVEE_LYRICS_DEBUG=1). Those two marker lines are the ONLY places the token may appear — the scan counts them.
    // It is a developer panel on an opaque theme PLATE, not part of the reading surface, so it keeps the theme's text
    // rungs on purpose — white-on-media ink over a light solid plate would be the very invisibility the ink seam exists
    // to remove. StageLayoutTests scans the rest of this file for theme ink and skips exactly this span.
    Element DebugButton() => new BoxEl
    {
        // Full-bleed PASS-THROUGH positioner (its HitTestPassThrough is honoured — see FpsOverlay) pinning the pill
        // bottom-right; only the pill is hittable, so the lyrics underneath stay scrollable/clickable.
        Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
        Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
        Padding = new Edges4(0f, 0f, 12f, 12f),
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
                Fill = Tok.FillSolidBase with { A = 0.90f }, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Cursor = CursorId.Hand, OnClick = () => _debugOpen.Value = true,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                Children = [new TextEl("lyrics debug") { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary }],
            },
        ],
    };

    Element DebugOverlay(string trackId)
    {
        var report = LyricsDiagnostics.ForTrack(trackId);
        var rows = new List<Element>
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                Children =
                [
                    new TextEl("Lyrics search") { Size = 18f, LineHeight = 24f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f },
                    new BoxEl
                    {
                        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                        Cursor = CursorId.Hand, OnClick = () => _debugOpen.Value = false,
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        Children = [new TextEl("close ✕") { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary }],
                    },
                ],
            },
        };

        if (report is null)
        {
            rows.Add(new TextEl("No search recorded for this track yet — it may be a local/fake track, or the fetch is still in flight. Close and reopen to refresh.")
            { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        }
        else
        {
            rows.Add(new TextEl(report.Summary) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.AccentTextPrimary, Wrap = TextWrap.Wrap });
            rows.Add(new TextEl($"“{report.Title}” — {(report.Artist.Length > 0 ? report.Artist : "(no artist)")}")
            { Size = 12f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, LineHeight = 16f });
            rows.Add(new TextEl($"album: {(report.Album.Length > 0 ? report.Album : "—")}   ·   {report.DurationMs / 1000}s   ·   ISRC: {report.Isrc ?? "—"}")
            { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
            rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault });
            foreach (var t in report.Sources) rows.Add(SourceRow(t));
            if (report.Sources.Count == 0)
                rows.Add(new TextEl("(no sources ran)") { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary });
        }

        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, Direction = 1, Fill = Tok.FillSolidBase with { A = 0.97f },
            Children =
            [
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f,
                    Content = new BoxEl { Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.L), Children = rows.ToArray() },
                },
            ],
        };
    }

    static Element SourceRow(LyricsSourceTrace t)
    {
        ColorF dot = t.Outcome switch
        {
            LyricsOutcome.Hit => new ColorF(0.30f, 0.78f, 0.45f, 1f),     // green
            LyricsOutcome.Timeout => new ColorF(0.92f, 0.70f, 0.25f, 1f), // amber
            LyricsOutcome.Error => new ColorF(0.90f, 0.35f, 0.38f, 1f),   // red
            LyricsOutcome.Skipped => new ColorF(0.40f, 0.42f, 0.50f, 1f), // dim — didn't run (a faster match won)
            _ => new ColorF(0.55f, 0.57f, 0.62f, 1f),                     // grey (Miss)
        };

        var lines = new List<Element>
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children =
                [
                    new BoxEl { Width = 8f, Height = 8f, Corners = Radii.Circle(8f), Fill = dot },
                    new TextEl(t.SourceId) { Size = 14f, LineHeight = 20f, Weight = 600, Color = t.Winner ? Tok.AccentTextPrimary : Tok.TextPrimary },
                    new TextEl($"{t.Outcome.ToString().ToUpperInvariant()} · {t.ElapsedMs}ms{(t.Winner ? "  ★ winner" : "")}")
                    { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, Grow = 1f },
                ],
            },
        };
        if (t.Detail.Length > 0)
            lines.Add(new TextEl(t.Detail) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        if (t.Outcome == LyricsOutcome.Hit && t.RerankReason.Length > 0)
            lines.Add(new TextEl($"rerank score {t.Score:F2}  ·  {t.RerankReason}") { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });

        return new BoxEl { Direction = 1, Gap = Spacing.XS, Children = lines.ToArray() };
    }

    // ink-scan: on ──────────────────────────────────────────────────────────────────────────────────────────────────

    void PrepareDocument(LyricsDocument doc, long posMs)
    {
        if (ReferenceEquals(_doc, doc)) return;

        // A doc change retires the cascade BEFORE the handle/comp arrays are replaced — otherwise the outgoing doc's
        // rows (which survive a same-shape upgrade) would keep a stale compensating translate forever, with no handle
        // left to clear it through.
        ZeroCascade(Context.Scene);
        // Same reasoning for the dots: the overlay's node handle is about to be meaningless. The next OnFrame re-mounts
        // it from the new document's own timings if the position still lands inside a break.
        HideInterludeDots();

        var previous = _doc;
        // THE upgrade question, asked ONCE. SameLineShape guarantees same TrackId, same line count and identical
        // per-line text — which is exactly the condition under which the virtual list does NOT remount its rows (same
        // keys, same count), so every mounted LyricLineView survives the swap still holding the props it FROZE at
        // mount: its own `_lineEmphasis[i]` signal and `_glowAlpha[i]` signal (LyricLineView ctor). Reallocating those
        // arrays here therefore left every mounted row subscribed to an ORPHANED signal — the emphasis sweep and the
        // glow froze for the rest of the track after any same-shape upgrade (a disk-cached line-synced doc upgraded to
        // word-synced, a provider re-fetch). So a same-shape upgrade REUSES the per-line state in place, and only a
        // genuine document change rebuilds it. The rest of PrepareDocument runs identically on both paths.
        //
        // NOTE — ACCEPTED RESIDUAL (campaign decision; this is not an undiagnosed mystery). A row also freezes its
        // `LyricLine` at mount, and nothing reachable from here can replace it. So a same-shape upgrade that changes
        // only the WORD TIMINGS (line-synced → word-synced with identical text, a richer syllable split) does not reach
        // an already-mounted row's karaoke wipe data until those rows are rebuilt at the next track: post-fix such rows
        // are LIVE on emphasis + glow, but still wipe on the pre-upgrade timing. Closing that too would mean a
        // props-channel restructure of LyricLineView, which is deliberately out of scope.
        bool sameShape = previous is not null && SameLineShape(previous, doc);
        // A genuine document change must REMOUNT the rows, and only the key can force that (see RowKey). The
        // reallocation below is safe exactly when the rows are rebuilt to pick the new objects up.
        if (!sameShape) { _layout = null; _docEpoch++; }
        _doc = doc;
        ScanSecondaryLayers(doc);
        if (!sameShape)
        {
            _lineNodes = new NodeHandle[doc.Lines.Count];
            _glowNodes = new NodeHandle[doc.Lines.Count];
            _dofNodes = new NodeHandle[doc.Lines.Count];
            _glowAlpha = new FloatSignal[doc.Lines.Count];
            for (int i = 0; i < _glowAlpha.Length; i++) _glowAlpha[i] = new FloatSignal(0f);
            // Run lengths start unmeasured (NaN) and the width epoch starts unknown: the first RunLengthOf per line pays
            // one seam layout, every later call is an array read. A new doc for the SAME width still re-measures — the
            // strings changed, so the cached fragment sums are meaningless.
            _lineRunLen = new float[doc.Lines.Count];
            Array.Fill(_lineRunLen, float.NaN);
            _runLenWrapW = float.NaN;
            // σ starts undriven (NaN): the first ramp pass adopts each line's target, which is exactly the value the element
            // declared at mount, so a fresh doc lands on its ladder without a visible settle.
            _dofCurrent = new float[doc.Lines.Count];
            Array.Fill(_dofCurrent, float.NaN);
            // Cascade state starts at rest (0 = settled, nothing to write). Sized once per doc so DriveCascade never
            // allocates: the per-frame path only ever reads/writes these four preallocated float arrays.
            _casComp = new float[doc.Lines.Count];
            _casVel = new float[doc.Lines.Count];
            _casDelayLeftMs = new float[doc.Lines.Count];
            _casRate = new float[doc.Lines.Count];
        }
        else
        {
            // KEEP, by identity: `_lineNodes`/`_glowNodes`/`_dofNodes` (the surviving rows reported those handles ONCE,
            // at realization, and will never report them again — a fresh array would be permanently empty), `_glowAlpha`
            // and `_lineEmphasis` (the rows hold these very objects — keeping the arrays is what keeps their
            // subscriptions live, i.e. the whole point), `_lineRunLen`/`_runLenWrapW` (identical text at an unchanged
            // wrap width ⇒ the cached reading-order sums are still exact) and `_dofCurrent` (σ continuity: the ladder is
            // a function of DISTANCE, which the identical line set preserves, so re-arming it at NaN would re-snap every
            // ring mid-track for nothing).
            //
            // RESET, in place: the cascade is per-DOCUMENT motion state, not row-held, and the new document must start
            // at rest exactly like the fresh path. ZeroCascade above already zeroed comp/vel/delay THROUGH the live
            // handles (so the rows' transforms are back at identity); clearing here is the array-level equivalent of the
            // fresh allocation, plus `_casRate`, and costs no allocation.
            Array.Clear(_casComp);
            Array.Clear(_casVel);
            Array.Clear(_casDelayLeftMs);
            Array.Clear(_casRate);
            // Halo alphas are document state carried BY row-held signals: reset the VALUES (the fresh path's rows start
            // at 0) while the signal objects survive, so an in-flight cross-fade cannot freeze a lit halo on a line the
            // fade bookkeeping below is about to forget (`_glowInLine`/`_glowOutLine` = -1). Value-gated, and bound as
            // the rows' Opacity, so this writes at most the one or two lit lines and re-renders nothing. Leaving a
            // word-by-word row's glow σ un-rested is harmless — alpha 0 hides the layer (see FinishGlowOut), and the
            // live voice line re-drives both on the very next OnFrame.
            for (int i = 0; i < _glowAlpha.Length; i++) _glowAlpha[i].Value = 0f;
        }
        _dofRampPending = true;
        _dofRampWallMs = 0L;
        _cascadePending = false;
        _casQpc = 0L;
        // Seed each line at its REAL bucket for the doc's opening active line — NOT a constant. PrepareDocument runs
        // inside Render (LyricsDocHost), so the PushEmphasis below is a signal write during a render pass: seeding
        // "fully dim" made that write move every line whose true bucket wasn't 6, fanning the whole document out at
        // once on each new doc. Seeded with the computed value, PushEmphasis has nothing left to change and the write
        // pass is silent. The steady sweep is untouched — it still goes through PushEmphasis, the one chokepoint.
        bool timed = IsTimed(doc);
        // Seeded through the SAME interlude advance OnFrame applies, so a document that lands mid-break seeds the focal
        // line the first frame will resolve to — and PushEmphasis below stays the silent no-op it is designed to be.
        int seedActive = timed ? AdvancePastInterlude(doc, ResolveLine(doc.Lines, posMs), posMs, out _, out _) : -1;
        // Same-shape upgrade: the signals are KEPT (mounted rows hold them), so there is nothing to seed — PushEmphasis
        // below reaches the identical outcome through the value gate, writing only the lines whose bucket actually moved
        // (usually none: an upgrade lands at the same position on the same lines).
        if (!sameShape)
        {
            // A new line set: whatever line was holding the interlude band no longer means anything, and the freshly
            // minted signals must not carry bit 3 into it. (A SAME-shape upgrade keeps the signals AND the anchor —
            // same lines, same break, and the band is mid-flight.)
            _interludeReserveLine = -1;
            _lineEmphasis = new Signal<int>[doc.Lines.Count];
            for (int i = 0; i < _lineEmphasis.Length; i++) _lineEmphasis[i] = new Signal<int>(PackEmphasis(i, seedActive, -1));
        }
        _glowInLine = -1; _glowOutLine = -1;
        if (timed)
        {
            _activeLine.Value = seedActive;
        }
        else
        {
            _activeLine.Value = -1;
            _voiceLine.Value = -1;
        }
        PushEmphasis();   // per-line emphasis for the loaded doc (before OnFrame drives it): silent after the fresh-path seed
                          // above, and on the reuse path exactly the handful of lines whose bucket actually moved
        _nowMs.Value = posMs;
        _scrollSnapped = false;
        ResetWipeThrottle();
        RebaseClock(posMs);   // seed the dejittered clock anchor for the freshly loaded doc
    }

    // Which secondary layers this document carries, scanned ONCE per document (PrepareDocument) — never per frame and
    // never per row. LyricLine.Translation/Romanization are populated per line by the TTML parser (ruby / ttm:role), and
    // a partially-translated document is normal, so "has any line with data" is the right question for the toggle: the
    // lines without it simply render no second line.
    void ScanSecondaryLayers(LyricsDocument doc)
    {
        bool t = false, r = false;
        var lines = doc.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!t && l.Translation is { Length: > 0 }) t = true;
            if (!r && l.Romanization is { Length: > 0 }) r = true;
            if (t && r) break;
        }
        HasTranslation = t;
        HasRomanization = r;
        PublishSecondaryAvailability();
    }

    // Gated on a TIMED document, deliberately. The secondary line is rendered by LyricLineView, which only the timed
    // reading surface (LyricsContent) composes — UnsyncedLyricsContent is a plain scrolled block with no rows. Offering
    // the header toggle over an unsynced document would therefore be a control that visibly does nothing. In practice
    // the combination does not arise (the sources that carry ruby / ttm:role translations are all timed TTML), so this
    // is a guard, not a feature gate: HasTranslation/HasRomanization stay the honest answer about the document itself.
    void PublishSecondaryAvailability()
    {
        bool timed = _doc is { } d && IsTimed(d);
        LyricsPrefs.Available.Value = !timed ? 0
            : (HasTranslation ? LyricsPrefs.HasTranslation : 0) | (HasRomanization ? LyricsPrefs.HasRomanization : 0);
    }

    static bool SameLineShape(LyricsDocument a, LyricsDocument b)
    {
        if (!StringComparer.Ordinal.Equals(a.TrackId, b.TrackId) || a.Lines.Count != b.Lines.Count) return false;
        for (int i = 0; i < a.Lines.Count; i++)
            if (!StringComparer.Ordinal.Equals(a.Lines[i].Text, b.Lines[i].Text)) return false;
        return true;
    }

    // Packed per-line emphasis: bucket (distance from active, clamped 0..6) in bits 0-2, INTERLUDE RESERVE in bit 3 (it
    // once carried the deleted recede and was free; the past bit deliberately stays at bit 4 so the ladder's
    // `& 16` test and every packed value above bucket 6 are unchanged), PAST flag in bit 4. Clamp 6 is exact for the
    // look — the DoF ladder saturates at ring 5 and the glyph-mount `near`
    // threshold is dist ≤ 2, so any line ≥6 away is visually identical; clamping lets far lines share bucket 6 and skip
    // the re-render as active sweeps.
    //
    // Bit 4 (past) is the reference's past/future ASYMMETRY: a line the song has already passed settles dimmer than an
    // upcoming line the same distance away (measured 118 vs 133 luma), so the two directions ride two different opacity
    // ladders (LyricLineView.OpacityOf). It is deliberately NOT set at the saturated bucket 6: both ladders bottom out at
    // the same 0.10/σ 6.5 there, so tagging far lines would be visually identical while breaking the far-line no-op —
    // every line above the active one would re-render on a seek instead of sitting silent at bucket 6.
    // Bit 3 (reserve) is the interlude anchor's extra TOP PAD — the band the breathing dots occupy (see the reserve
    // block above). It rides the emphasis signal rather than a signal of its own because that signal is already
    // per-line and value-gated: arming the reserve then re-renders exactly the anchor row, and releasing it exactly the
    // row that had it. It carries no ladder meaning — OpacityOf ignores it, `dist`/`past` are unchanged — so a line's
    // brightness, scale and σ are identical whether or not it is holding the band.
    static int PackEmphasis(int index, int active, int reserveLine)
    {
        int bucket = active < 0 ? 6 : Math.Min(Math.Abs(index - active), 6);
        int e = bucket;
        if (index == reserveLine) e |= 8;                           // holds the interlude dots' reserved band
        if (active >= 0 && index < active && bucket < 6) e |= 16;   // already sung ⇒ the dimmer of the two ladders
        return e;
    }

    // Rewrite every line's emphasis signal from the current active line. Value-gated: only lines whose PACKED value
    // actually changes notify their subscriber, so a boundary re-renders ~a dozen rows instead of the whole realized
    // document. The past bit rides along for free: the one line that crosses future→past at a handoff is already a
    // bucket-crosser (dist 0 → 1), so it re-renders exactly once — and takes the past ladder as it does.
    void PushEmphasis()
    {
        var em = _lineEmphasis;
        if (em.Length == 0) return;
        int active = _activeLine.Peek();
        int reserve = _interludeReserveLine;
        for (int i = 0; i < em.Length; i++) em[i].Value = PackEmphasis(i, active, reserve);
    }

    void ClearDocument()
    {
        ResetFollowState(Context.Scene);
        // Retire the secondary-layer capability BEFORE the early-out below: with no document there is nothing to
        // translate, so the header toggle must go away rather than linger over the next track's lyrics-less state.
        HasTranslation = false;
        HasRomanization = false;
        PublishSecondaryAvailability();
        _layout = null;
        _viewportNode = NodeHandle.Null;
        if (_doc is null && _lineNodes.Length == 0)
        {
            _pendingUpgrade = null;
            return;
        }
        _doc = null;
        _pendingUpgrade = null;
        _lineNodes = Array.Empty<NodeHandle>();
        _glowNodes = Array.Empty<NodeHandle>();
        _dofNodes = Array.Empty<NodeHandle>();
        _glowAlpha = Array.Empty<FloatSignal>();
        _lineEmphasis = Array.Empty<Signal<int>>();
        _lineRunLen = Array.Empty<float>();
        _runLenWrapW = float.NaN;
        _dofCurrent = Array.Empty<float>();
        _dofRampPending = true;
        _dofRampWallMs = 0L;
        // ResetFollowState above already zeroed + cleared the cascade through the live handles; drop the arrays with the
        // rest of the per-doc state.
        _casComp = Array.Empty<float>();
        _casVel = Array.Empty<float>();
        _casDelayLeftMs = Array.Empty<float>();
        _casRate = Array.Empty<float>();
        _cascadePending = false;
        _casQpc = 0L;
        _glowInLine = -1; _glowOutLine = -1;
        _activeLine.Value = -1;
        _voiceLine.Value = -1;
        HideInterludeDots();
        // The emphasis signals are gone with the document, so this is a plain field reset — SetInterludeReserve would
        // have nothing to write to (and ResetFollowState above no-ops when the mode was already Following).
        _interludeReserveLine = -1;
        _reserveRelatchFrames = 0;
        _nowMs.Value = 0f;
        _scrollSnapped = false;
        ResetWipeThrottle();
        _lastAuthMs = long.MinValue;   // reset so a freshly loaded doc re-anchors on the next snapshot
        _lastDisplay = 0L;
    }

    void ReceiveUpgrade(LyricsDocument upgrade)
    {
        if (upgrade.Lines.Count == 0) return;
        var current = _docLoadable?.Value.Peek() ?? _doc;
        if (current is not null && !StringComparer.Ordinal.Equals(current.TrackId, upgrade.TrackId)) return;
        if (current is not null && !IsRicherLyrics(upgrade, current)) return;

        bool playing = _b?.IsPlaying.Peek() == true;
        if (!playing || _doc is null || _activeLine.Peek() < 0)
            ApplyLyricsUpgrade(upgrade, _b?.PositionMs.Peek() ?? 0L);
        else
            _pendingUpgrade = upgrade;
    }

    void ApplyLyricsUpgrade(LyricsDocument upgrade, long posMs)
    {
        _pendingUpgrade = null;
        _docLoadable?.SetReady(upgrade);
        PrepareDocument(upgrade, posMs);
    }

    static bool IsRicherLyrics(LyricsDocument next, LyricsDocument current)
    {
        int nr = Richness(next), cr = Richness(current);
        if (nr != cr) return nr > cr;
        if (nr < 3) return false;
        return SyllableCount(next) > SyllableCount(current);
    }

    static int Richness(LyricsDocument doc)
    {
        foreach (var l in doc.Lines)
            if (l.IsWordByWord && l.Syllables.Count > 0)
                return 3;
        return doc.Sync switch
        {
            LyricsSyncKind.Syllable => 3,
            LyricsSyncKind.Line => 2,
            LyricsSyncKind.Unsynced => 1,
            _ => 0,
        };
    }

    static int SyllableCount(LyricsDocument doc)
    {
        int n = 0;
        foreach (var l in doc.Lines) n += l.Syllables.Count;
        return n;
    }

    // Seed every dejittered-clock field from an authoritative position so all re-anchor sites (doc load, click-seek) agree.
    // Sets _lastDisplay so the monotonic guard adopts the new (possibly backward) position immediately, and _lastAuthMs so
    // the next OnFrame doesn't re-treat the same value as a snapshot disagreement. Does NOT touch _wasPlaying — the
    // paused→playing transition still rebases on resume so a pause gap never leaks into the wall delta.
    void RebaseClock(long pos)
    {
        _baseWall = Environment.TickCount64;
        _basePos = pos;
        _offset = 0f;
        _lastAuthMs = pos;
        _lastDisplay = pos;
    }

    // The TIMED-row type metrics, as properties rather than LyricsContent locals: RunLengthOf has to rebuild the exact
    // TextStyle a row's TextEl renders with (and SoftnessOfLine has to know the side padding to derive the wrap width),
    // and it runs outside the content pass — a second copy of the ladder here is precisely the drift that would make the
    // measured run length disagree with the rendered wrap.
    float RowFontSize => _large ? 36f : 26f;
    float RowLineHeight => _large ? 46f : 33f;   // ~1.27x (was 1.4x) — denser block
    // Immersive gets a generous 64 DIP gutter inside its ~700 DIP measured column (ImmersiveLyricsSurface); the rail
    // keeps 22 so the narrow panel never reads cramped.
    float RowSidePad => _large ? 64f : 22f;

    /// <summary>Whether timed sync is suppressed right now, read so the CALLER SUBSCRIBES — this runs in Render, and the
    /// subscription is what re-renders the surface (swapping the timed presentation for the unsynced one) the moment a
    /// video starts or stops. <c>OnFrame</c> deliberately peeks the same state instead; see the note there.</summary>
    bool SyncSuppressedNow() => _b is { } b && LyricsSyncGate.SyncSuppressed(PlacementCore.IsActive(b.VideoSurface.Value));

    Element LyricsContent(LyricsDocument doc)
    {
        // A timed document whose sync is SUPPRESSED renders exactly like an untimed one. This is the whole presentation
        // change, and it matters: leaving it on the timed path meant every line sat in the INACTIVE treatment — the big
        // karaoke type at the dimmed far-from-active opacity — which reads as broken lyrics rather than as a document
        // you are meant to read. There is no active line to be far from, so there is no reason to render as if there were.
        if (!IsTimed(doc) || SyncSuppressedNow()) return UnsyncedLyricsContent(doc);

        var lines = doc.Lines;
        // Bigger type (rail 20 -> 26) and a tighter rhythm. Rows are CONTENT-FIT (variable height) via the measured
        // layout below, so a one-line lyric is short and a two-line lyric is tall — no dead space, no mid-word clipping.
        float fontSz = RowFontSize;
        float lineHt = RowLineHeight;
        float rowPad = _large ? 9f : 7f;            // vertical padding per row; inter-line gap = 2*rowPad
        float sidePad = RowSidePad;
        float rowEst = lineHt + 2f * rowPad;        // measured-layout seed = a single-line row's height
        _band = FocalBand;   // publish the one definition into the copy the measured layout is built against

        if (_layout is null || MathF.Abs(_layout.Band - _band) > 0.001f || MathF.Abs(_layout.Estimate - rowEst) > 0.5f)
            _layout = new LyricsMeasuredLayout(rowEst, _band);

        var layout = _layout;
        return Virtual.Custom(
            lines.Count,
            layout,
            i =>
            {
                var idx = i;
                return Embed.Comp(() => new LyricLineView(
                    idx, lines[idx],
                    (uint)idx < (uint)_lineEmphasis.Length ? _lineEmphasis[idx] : _emphasisFallback, _nowMs, _followMode,
                    idx < _glowAlpha.Length ? _glowAlpha[idx] : null,
                    // The SIGNAL, not its value: the row reads it every render, so a toggle reaches rows that mounted
                    // long before it (component props freeze at mount — see the _secondary field block).
                    _secondary,
                    fontSz, lineHt, rowPad, sidePad, _large, _ink,
                    ReportLineNode, ReportGlowNode, ReportDofNode, SoftnessOfLine, DofDeclaredFor, () => SeekToLine(idx))) with { Key = RowKey(idx) };
            },
            keyOf: RowKey,
            // Realize the WHOLE document (a lyrics doc is at most a few hundred cheap rows): with a 4-5 row overscan,
            // lines cold-mounted mid-auto-scroll — text shaping + DoF layer popping in as the spring passed them (the
            // "future lines flicker in" report). Realized-but-offscreen lines cost nothing per frame (clip-culled).
            overscan: Math.Min(lines.Count, 400)) with
        {
            Grow = 1f,
            MinHeight = 0f,
            // E4 normally mounts visible-only and warms overscan at 12 rows/frame. Lyrics deliberately request the
            // entire bounded document: every row must be measured before follow geometry is trusted, and no upcoming
            // line may materialize seconds later as the budget catches up.
            RealizeOverscanImmediately = true,
            AutoEdgeFade = true,
            SuppressScrollBar = true,
            OnScrollGeometryChanged = (
                static g => g.UserScrollActive ? 1L : 0L,
                g => OnLyricsScrollActivity(g.UserScrollActive, Environment.TickCount64)),
            OnRealized = h => _viewportNode = h,
        };
    }

    Element UnsyncedLyricsContent(LyricsDocument doc)
    {
        // Same LEFT-ALIGNED, WRAPPED treatment as the timed path, but at SMALLER type. The timed metrics are sized for
        // one line at a time sweeping through a focal band — they are display type, and a whole document set in them is
        // a wall. Unsynced lyrics are read as a block, top to bottom, so they take reading type instead (this is also how
        // Spotify presents its unsynced lyrics). Superseded rationale: this used to deliberately reuse the timed metrics
        // so the surface "would not switch typographic systems"; in practice the switch is the point — nothing is being
        // followed, so nothing needs to be legible from across the room.
        float fontSz = _large ? 28f : 19f;
        float lineHt = _large ? 36f : 26f;
        float rowPad = _large ? 7f : 5f;
        float sidePad = RowSidePad;
        var rows = new Element[doc.Lines.Count];

        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new BoxEl
            {
                Direction = 1,
                Shrink = 0f,
                Padding = new Edges4(sidePad, rowPad, sidePad, rowPad),
                AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new TextEl(doc.Lines[i].Text)
                    {
                        Size = fontSz,
                        Weight = 700,
                        Wrap = TextWrap.Wrap,
                        LineHeight = lineHt,
                        Color = _ink.Primary with { A = 0.88f },
                        MaxLines = 0,
                        Trim = TextTrim.None,
                    },
                ],
            };
        }

        return new ScrollEl
        {
            Grow = 1f,
            MinHeight = 0f,
            AutoEdgeFade = true,
            SuppressScrollBar = true,
            ScrollKey = "lyrics:unsynced:" + doc.TrackId,
            Content = new BoxEl
            {
                Direction = 1,
                Padding = new Edges4(0f, _large ? 44f : 26f, 0f, _large ? 44f : 26f),
                Children = rows,
            },
        };
    }

    // The shimmer takes the surface's INK seam, not a theme fill: the stage's ground is its own, so a theme fill
    // here is a light bar on a dark surface (and, once the stage flips, the wrong polarity outright).
    static Element LyricsShimmer(bool large, LyricsInk ink)
    {
        float padX = large ? 64f : 22f;   // matches RowSidePad so the bars sit exactly where the first lines will land
        float padTop = large ? 150f : 110f;
        float rowH = large ? 32f : 22f;
        float gap = large ? 24f : 18f;
        float[] widths = large ? [0.82f, 0.66f, 0.74f, 0.58f, 0.70f, 0.50f] : [0.86f, 0.72f, 0.80f, 0.62f, 0.76f, 0.58f];
        var rows = new Element[widths.Length];

        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new BoxEl
            {
                Width = large ? 520f * widths[i] : 255f * widths[i],
                Height = rowH,
                Corners = CornerRadius4.All(6f),
                Fill = ink.Skeleton,
                AlignSelf = FlexAlign.Start,   // both surfaces are left-aligned now (the centered fullscreen was refuted)
            };
        }

        return new BoxEl
        {
            Grow = 1f,
            MinHeight = 0f,
            Direction = 1,
            Gap = gap,
            Padding = new Edges4(padX, padTop, padX, 0f),
            Children = rows,
        };
    }

    void ReportLineNode(int index, NodeHandle h)
    {
        if ((uint)index < (uint)_lineNodes.Length) _lineNodes[index] = h;
    }

    void ReportGlowNode(int index, NodeHandle h)
    {
        if ((uint)index < (uint)_glowNodes.Length) _glowNodes[index] = h;
    }

    void ReportDofNode(int index, NodeHandle h)
    {
        if ((uint)index < (uint)_dofNodes.Length) _dofNodes[index] = h;
        _dofRampPending = true;   // a freshly realized row may still be NaN in the σ model — let one pass adopt its target
        // REALIZE-MID-CASCADE, fixed at the source: a row that mounts (or re-mounts after leaving the virtual window)
        // while its compensating translate is still non-zero must NOT render one frame at the un-compensated position —
        // that is a one-frame pop into place, exactly the artifact the cascade exists to remove. Seed it HERE, at
        // realization, rather than waiting for the next DriveCascade tick.
        float c = (uint)index < (uint)_casComp.Length ? _casComp[index] : 0f;
        if (c != 0f && Context.Scene is { } scene) WriteCascade(scene, index, c, landed: false);
    }

    // ── Wipe feather geometry (DIP → the engine's reading-order fraction) ─────────────────────────────────────────────
    // GlyphWipe.Softness is a FRACTION of the run's reading-order length, but the feather is a fixed on-screen band, so
    // the DIP constant has to be divided by that length PER LINE. Everything below serves that one conversion.

    // The width every timed row's text lays out into: the scroll viewport less the row's own side padding (rows are
    // Stretch-aligned, so this IS the wrap box the seam must be queried with). One width for the whole document ⇒ one
    // epoch. NaN until the viewport exists (first frames / rail closed) — RunLengthOf treats that as "not measurable yet"
    // and returns a conservative estimate; OnFrame's self-heal replaces the resulting seed once geometry lands.
    float CurrentWrapWidth()
    {
        var scene = Context.Scene;
        var viewport = _viewportNode;
        if (scene is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport)) return float.NaN;
        float w = scene.ScrollRef(viewport).ViewportW - 2f * RowSidePad;
        return w > 1f ? w : float.NaN;
    }

    // The seeded/self-healed Softness for one line. Clamped: under 0.01 the band collapses to a hard per-pixel cut on a
    // long line, over 0.10 a very short line returns to the mushy full-word wash the frame evidence refuted.
    float SoftnessOfLine(int index)
    {
        float runLen = RunLengthOf(index, CurrentWrapWidth());
        return Math.Clamp(LyricLineView.WipeSoftnessDipFor(_large) / runLen,
            LyricLineView.WipeSoftnessMin, LyricLineView.WipeSoftnessMax);
    }

    // Lazily measured reading-order run length for one line, cached per line per width epoch. The lazy fill costs one
    // text-seam layout per line per resize and NOTHING thereafter, so the per-frame callers (OnFrame's self-heal) only
    // ever read the array. A wrap-width move of ≥0.5 DIP retires every entry at once — the rail is fixed-width, but the
    // fullscreen surface resizes with the window.
    float RunLengthOf(int index, float wrapWidth)
    {
        var runLen = _lineRunLen;
        if (float.IsNaN(wrapWidth))
        {
            // No trustworthy wrap width yet (pre-layout, or the rail is closed): answer from the last live epoch, else a
            // plausible ~12-em line. Deliberately does NOT poison the cache — the seed this produces is exactly what
            // OnFrame's self-heal replaces the moment real geometry lands.
            float last = _runLenWrapW;
            return last > 1f ? last : RowFontSize * 12f;
        }
        if (float.IsNaN(_runLenWrapW) || MathF.Abs(_runLenWrapW - wrapWidth) >= 0.5f)
        {
            _runLenWrapW = wrapWidth;
            Array.Fill(runLen, float.NaN);
        }
        if ((uint)index >= (uint)runLen.Length) return wrapWidth;
        float cached = runLen[index];
        if (!float.IsNaN(cached)) return cached;
        float measured = MeasureRunLength(index, wrapWidth);
        runLen[index] = measured;
        return measured;
    }

    // One seam query per line per epoch: GetRangeRects over the WHOLE string returns one rect per wrapped visual-line
    // fragment, and the sum of their widths is the reading-order extent the wipe sweeps (the replay lays those same
    // fragments end-to-end). Queried under the TextStyle LyricLineView.LineText builds, through the SAME layout pipeline
    // that renders it, so the fragments are the rendered wrap — not an approximation of it.
    float MeasureRunLength(int index, float wrapWidth)
    {
        var doc = _doc;
        if (doc is null || (uint)index >= (uint)doc.Lines.Count) return wrapWidth;
        string text = doc.Lines[index].Text;
        if (text.Length == 0 || TextSeam.Default is not { } fonts) return wrapWidth;

        // MUST mirror LyricLineView.LineText EXACTLY (both surfaces: wrapped, untrimmed, unbounded line count) — the
        // feather is a fraction of the extent measured here, so any disagreement re-scales every wipe boundary.
        var style = new TextStyle(default, RowFontSize, 700,
            TextWrap.Wrap, TextTrim.None, 0,
            CharSpacing: 0f, LineHeight: RowLineHeight);
        // A wrapped lyric line is 1-3 visual lines; 8 is generous headroom and the seam DROPS the excess rather than
        // failing, so a pathological line would under-sum. Treat a full span as possibly truncated and fall back to the
        // wrap-box estimate for those fragments — an over-long run only tightens the feather, never widens it.
        Span<RectF> fragments = stackalloc RectF[8];
        int n = fonts.GetRangeRects(text, in style, wrapWidth, 0, text.Length, fragments);
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += fragments[i].W;
        if (n == fragments.Length) sum = MathF.Max(sum, n * wrapWidth);
        return sum > 1f ? sum : wrapWidth;
    }

    internal LyricsFollowMode FollowModeValue => _followMode.Value;   // LyricsTicker-only subscription; parent Render never reads it

    static bool SuppressesDof(LyricsFollowMode mode) => mode != LyricsFollowMode.Following;

    float DofForLine(int index) => DofSigmaFor(index, _activeLine.Peek(), _large);

    // The σ ladder rung for one line against a given active line. Split out so the per-frame ramp can hoist the signal
    // Peek out of its whole-document loop without forking the ladder into a second definition.
    static float DofSigmaFor(int index, int active, bool large)
    {
        // NO ACTIVE LINE ⇒ NO BLUR. This used to return DofSigma(6) — the ladder's MAXIMUM — which blurred the ENTIRE
        // document at full σ before the first line landed, and for the whole life of any document whose clock never
        // resolves. "There is no focus yet" is not "every line is six rings from the focus"; an unfocused document
        // should be crisp, which is also what SuppressesDof already does for every non-Following mode.
        if (active < 0) return 0f;
        return LyricsFx.DofSigma(Math.Min(Math.Abs(index - active), 6), large);
    }

    // Suppression is a TARGET move, not a write of its own: DriveDofRamp owns every σ the nodes ever see, so engaging
    // suppression (FollowMode leaves Following) EASES the ladder out — each line's target drops to 0, a decrease — and
    // releasing it SNAPS the ladder back (an increase), through the exact directional model the emphasis ladder uses.
    // The snap-back on release is deliberate and shares the front-loaded-recede rationale; if it reads harsh in the
    // feel-test, that is a tuning call on DriveDofRamp's direction test, not a special case here.
    void ApplyDofSuppression(SceneStore? scene)
    {
        _dofRampPending = true;
        // Resolve it now if a scene is at hand (the mode can flip from a scroll callback, outside the ticker); otherwise
        // the next OnFrame picks it up — LyricsTicker keeps ticking for the whole detached/resyncing window.
        if (scene is not null) DriveDofRamp(scene, Environment.TickCount64);
    }

    // Exponential time constant for a σ DECREASE: ~95% of the way in 200 ms (1 - e^(-200/65) = 0.954), matching the
    // reference's incoming line, which de-blurs progressively DURING the move and is crisp as it lands.
    const float DofRampTauMs = 65f;
    // σ write gate. The recorder buckets σ at 0.5 in the blur pin key, so a finer gate mints no extra pins but still
    // dirties paint every single frame of the ramp; 0.1 keeps the ramp visually smooth (~15 writes across 200 ms, ≤3 pin
    // buckets crossed) and bounded. The LANDING write is exempt so the node ends EXACTLY on the target, never a hair off.
    const float DofWriteEps = 0.1f;

    void DriveDofRamp(SceneStore scene, long wallMs)
    {
        var cur = _dofCurrent;
        float dt = _dofRampWallMs == 0L ? KaraokeWipeIntervalMs : Math.Clamp((float)(wallMs - _dofRampWallMs), 0f, 100f);
        _dofRampWallMs = wallMs;
        if (!_dofRampPending || cur.Length == 0) return;

        bool suppress = SuppressesDof(_followMode.Peek());
        int active = _activeLine.Peek();
        float k = 1f - MathF.Exp(-dt / DofRampTauMs);
        bool moving = false;
        for (int i = 0; i < cur.Length; i++)
        {
            float target = suppress ? 0f : DofSigmaFor(i, active, _large);
            float c = cur[i];
            bool landed = true;
            if (float.IsNaN(c) || target >= c)
            {
                c = target;                              // first visit, or an INCREASE ⇒ snap (front-loaded recede)
            }
            else
            {
                c += (target - c) * k;                   // DECREASE ⇒ ease in (incoming line sharpening)
                if (c - target <= 0.01f) c = target;     // land exactly — no asymptote residue
                else { landed = false; moving = true; }
            }
            cur[i] = c;

            var h = (uint)i < (uint)_dofNodes.Length ? _dofNodes[i] : NodeHandle.Null;
            if (h.IsNull || !scene.IsLive(h)) continue;
            ref NodePaint p = ref scene.Paint(h);
            if (MathF.Abs(p.BlurSigma - c) < (landed ? 0.001f : DofWriteEps)) continue;
            p.BlurSigma = c;
            scene.Mark(h, NodeFlags.PaintDirty);
        }
        _dofRampPending = moving;
    }

    // The σ the ELEMENT declares for line i (LyricLineView's `Blur`): the ramp model's LIVE value, so a re-render landing
    // mid-ramp re-asserts the in-flight σ instead of stomping the node back to the rest target — the same element-vs-
    // driver agreement the scale/opacity springs document at the bottom of LyricLineView.Render. NaN (never driven) falls
    // back to the rest target, which is what an undriven node should mount at.
    float DofDeclaredFor(int index)
    {
        float cur = (uint)index < (uint)_dofCurrent.Length ? _dofCurrent[index] : float.NaN;
        if (!float.IsNaN(cur)) return cur;
        return SuppressesDof(_followMode.Peek()) ? 0f : DofForLine(index);
    }

    // ── Reduced motion ───────────────────────────────────────────────────────────────────────────────────────────────
    // The engine exposes the OS preference as a VALUE — `FluentGpu.Dsl.Motion.ReducedMotion`, a plain static bool the
    // Win32 PAL publishes from SPI_GETCLIENTAREAANIMATION at startup and re-reads on WM_SETTINGCHANGE
    // (Win32Platform.ReadReducedMotion). It is NOT a signal, so there is no subscription to get wrong: read it where the
    // affected constant is CONSUMED, exactly like the engine does at AnimScheduler.Structural.cs:99-102 and like the rest
    // of the app (MediaCard, ContentFilterChips, WaveeShell). Never an early-return in a render/hook path — the flag is a
    // mutable global a resize grip can flip mid-life, and a conditional return would shift hook slots.
    //
    // The four consumption points, and what each does when the preference is on:
    //   • ArmCascade            — every stagger delay drops to 0, so every line shares the one rate: the document makes
    //                             ONE rigid, gently-settling translate instead of a top-first waterfall.
    //   • LyricLineView.WipeLiftFor — Lift = 0 on BOTH wipe layers (main + glow): no per-word rise.
    //   • LyricLineView.Render  — scale flat 1.0 (no 0.98 breathing), folded into the emphasis spring's DepKey so the
    //                             first re-render after a settings flip retargets instead of holding the old scale.
    //   • DriveInterludeDots    — the dots hold still (pulse pinned to its peak) but keep FILLING across the gap.
    // What deliberately STAYS: the karaoke wipe, the opacity ladder, the DoF blur ladder and the interlude dots' fill.
    // Those are INFORMATION — they say which words have been sung, which line is live and how much of the instrumental
    // break is left — not decoration, and dropping them would break the view.
    // (Wave F hook: the immersive surface's drifting cover backdrop must read this too and hold still.)

    // ── Staggered handoff cascade: tuning ────────────────────────────────────────────────────────────────────────────
    // Measured off the reference capture: ~50-70 ms of onset lag per successive line BELOW the outgoing one. Rank 0 is
    // the outgoing line and everything above it — those move IMMEDIATELY. The rank cap keeps the tail bounded: past ~4
    // ranks a line is already blurred to nothing, and a longer tail would still be settling when the NEXT line lands.
    const float CascadeStaggerMs = 60f;
    const int CascadeMaxRank = 4;
    // Every rank lands at the SAME wall time — 0.48 s after onset (measured 0.42-0.56 s, converging). A critically
    // damped (ζ=1) settle is essentially complete once y·t ≈ 7: the closed form's envelope (1+u)·e^(−u) is 0.7 % of the
    // travel at u = 7, i.e. well inside the 0.5 DIP landing gate for a line-sized step. So a rank that starts `delay`
    // late simply gets the FASTER rate 7/(0.48 − delay) and catches up exactly at the synchronized settle.
    const float CascadeTotalS = 0.48f;
    const float CascadeSettleY = 7f;
    const float CascadeLandDip = 0.5f;    // land EXACTLY (comp = 0, vel = 0, identity write) inside this residual…
    const float CascadeLandVel = 20f;     // …but only while it is genuinely settling — never mid-flight through zero
    const float CascadeWriteEps = 0.1f;   // transform write gate (the LANDING write is exempt: it must be exact)
    const float CascadeDtMaxMs = 100f;    // ticker-gap clamp: a minimized/parked window resumes without a teleport
    // WRITE BAND. The cascade used to arm — and therefore write + Mark — EVERY line in the document, so a 150-line
    // lyric cost 150 transform writes and 150 dirty marks on every frame of a 0.48 s settle, for a viewport that shows
    // at most ~15 lines. Only lines within this many rows of the new active line take part; everything further out is
    // RETIRED at arm time (comp/vel/delay zeroed and one identity write, itself a no-op unless the node was actually
    // displaced), which is exactly where it belongs — a line 25+ rows away is at its true scroll position, hundreds of
    // DIP off-screen, and its whole travel would be invisible. Retiring rather than "integrate but skip the write" is
    // what keeps it CORRECT: a line that leaves the band mid-flight would otherwise keep a stale non-zero transform
    // forever (the document is realized whole, so ReportDofNode never re-fires to re-seed it), and it makes
    // DriveCascade's existing settled test (comp == 0 && delay <= 0 ⇒ continue) skip out-of-band lines for free.
    // SIZING, honestly: 24 single-line rows is ~1130 DIP in the rail (47 DIP/row) and ~1540 in the immersive surface
    // (64), and the active line sits at FocalBand (38-40 % down), so the band covers the whole viewport for any lyrics
    // surface up to ~1800 DIP tall — every real window, and taller still as soon as one lyric wraps. Past that the
    // bottom-most rows would SNAP with the latch instead of easing, which is exactly what a skip-the-write band would
    // do there too; it is a bound on the effect, not a correctness cliff.
    const int CascadeWriteBand = 24;

    // Arm one handoff. `delta` is the viewport jump that just happened (newOffset − oldOffset); `newActive` is the line
    // that just took focus. See the INVARIANT on the _casComp field block for the sign.
    void ArmCascade(SceneStore scene, float delta, int newActive)
    {
        var comp = _casComp;
        if (comp.Length == 0 || delta == 0f) return;
        var vel = _casVel; var delay = _casDelayLeftMs; var rate = _casRate;
        int outgoing = newActive - 1;   // rank 0 — the line that just lost focus, and everything above it
        // Reduced motion, read ONCE as a value and hoisted out of the loop (see the Reduced motion block above). The
        // cascade still RUNS — the lines have to end up where the latched viewport put them, and a compensation that
        // never decays would leave the document permanently off its focal band — but the WATERFALL goes: with every
        // delay at 0, the rate below resolves to the same CascadeSettleY/CascadeTotalS for every line, so the whole
        // document performs one rigid, critically-damped, zero-overshoot translate over the same 0.48 s.
        bool reduce = Motion.ReducedMotion;
        // The band this handoff owns (see CascadeWriteBand). Everything outside it is retired to its true position in
        // the same pass, so the per-frame cost of the settle is bounded by the band and not by the document length.
        int lo = Math.Max(0, newActive - CascadeWriteBand);
        int hi = Math.Min(comp.Length - 1, newActive + CascadeWriteBand);
        for (int i = 0; i < comp.Length; i++)
        {
            if (i < lo || i > hi)
            {
                // Out of band. Drop any compensation it still carries and put it back on its true scroll position —
                // the identity write is skipped inside WriteCascade unless the node really is displaced.
                if (comp[i] != 0f || vel[i] != 0f || delay[i] != 0f)
                {
                    comp[i] = 0f; vel[i] = 0f; delay[i] = 0f;
                    WriteCascade(scene, i, 0f, landed: true);
                }
                continue;
            }
            float c = comp[i] + delta;   // ADD, never assign: a mid-cascade re-target folds into the in-flight comp
            // Re-arm the stagger for EVERY line on every handoff: a re-target restarts the wave from the NEW active
            // line, so a line that was already flying can be asked to hold again — that is the new wave passing it.
            float delayMs = reduce ? 0f : CascadeStaggerMs * Math.Clamp(i - outgoing, 0, CascadeMaxRank);
            float y = CascadeSettleY / MathF.Max(0.05f, CascadeTotalS - delayMs * 0.001f);
            // ZERO OVERSHOOT, by construction. x(t) = e^(−y·t)·(c + j1·t) with j1 = v + c·y crosses zero iff j1 has the
            // OPPOSITE sign to c. A same-direction re-arm provably cannot (while decaying, |v| < y·|c|, and the added
            // delta only grows |c|); a REVERSING one can, so clamp j1 to 0 there. That degrades exactly those lines to
            // a pure e^(−y·t) decay from their current position — still continuous, still monotone, never an overshoot.
            float v = vel[i];
            if ((v + c * y) * c < 0f) v = -c * y;
            comp[i] = c; vel[i] = v; delay[i] = delayMs; rate[i] = y;
            // Compensate on the LATCH FRAME ITSELF, not one tick later. DriveCascade runs later in this same OnFrame,
            // but this keeps the invariant true independently of that ordering (and of a row realized in between).
            WriteCascade(scene, i, c, landed: false);
        }
        SetCascadePending(true);
    }

    // Step every in-flight line. Core lane of OnFrame, right beside DriveDofRamp and under the same clamped-dt
    // discipline: the wall stamp is refreshed on EVERY call (even when nothing is pending) so a long ticker gap can
    // never be spent as one giant integration step. "In-flight" is now at most the CascadeWriteBand window around the
    // active line: ArmCascade retires everything outside it, so the settled test below skips those lines for free and
    // the per-frame write count is bounded by the band rather than by the document length.
    void DriveCascade(SceneStore scene)
    {
        var comp = _casComp;
        long qpc = Stopwatch.GetTimestamp();
        float dtMs = _casQpc == 0L
            ? KaraokeWipeIntervalMs
            : Math.Clamp((float)((qpc - _casQpc) * 1000.0 / Stopwatch.Frequency), 0f, CascadeDtMaxMs);
        _casQpc = qpc;
        if (!_cascadePending || comp.Length == 0) return;

        var vel = _casVel; var delay = _casDelayLeftMs; var rate = _casRate;
        float dtS = dtMs * 0.001f;
        bool moving = false;
        for (int i = 0; i < comp.Length; i++)
        {
            float c = comp[i], d = delay[i];
            if (c == 0f && d <= 0f) continue;   // settled — no state to integrate, no write to make
            if (d > 0f)
            {
                delay[i] = d - dtMs;            // HOLD at the old screen position — this IS the stagger
                moving = true;
                continue;                       // already written at arm time; nothing has moved
            }
            // The exact ζ=1 closed-form step (position AND velocity) — the same math the scroll kernel's own
            // ChaseStep uses (ported from the old ScrollIntegrator.cs:521-527; this per-line DoF cascade is its own
            // bespoke stepper, not a ScrollState/kernel read — it never touches scroll v3's Scroll/ namespace). It is
            // dt-deterministic, so the cascade lands at the same WALL time whatever the frame rate, and a mid-flight
            // re-arm stays velocity-continuous.
            float y = rate[i] > 0f ? rate[i] : CascadeSettleY / CascadeTotalS;
            float v = vel[i];
            float j1 = v + c * y;
            float e = MathF.Exp(-y * dtS);
            c = e * (c + j1 * dtS);
            v = e * (v - j1 * y * dtS);
            bool landed = MathF.Abs(c) < CascadeLandDip && MathF.Abs(v) < CascadeLandVel;
            if (landed) { c = 0f; v = 0f; }
            else moving = true;
            comp[i] = c; vel[i] = v;
            WriteCascade(scene, i, c, landed);
        }
        SetCascadePending(moving);
    }

    // The ONE place a line's compensating translate reaches the scene.
    //
    // `dofContent` is the verified-safe transform target. It declares no static transform and owns no animation channel
    // (LyricLineView's UseSpring binds ScaleX/ScaleY/Opacity to the ROW's host node — RenderContext.UseSpring targets
    // HostNode — not this inner wrapper), and Reconciler.cs:3671-3682 writes LocalTransform on a re-render ONLY for the
    // declared-static → declared-identity transition, which a node that never declares one cannot make. So nothing else
    // ever stomps this write, and an emphasis re-render mid-cascade leaves the in-flight translate alone.
    //
    // A PURE TRANSLATION of a blurred node is a blur-pin cache HIT: BlurPinKey is position-independent by construction
    // (BlurPinKey.cs:7-16 — σ + integer layer size + every op's position REBASED to the layer origin), so the DoF layer
    // is not re-Gaussian'd for any frame of the cascade.
    void WriteCascade(SceneStore scene, int index, float comp, bool landed)
    {
        var h = (uint)index < (uint)_dofNodes.Length ? _dofNodes[index] : NodeHandle.Null;
        // Unrealized / recycled-out line: skip the write, but the comp keeps integrating in the array so the line is at
        // the RIGHT place whenever it does realize — ReportDofNode seeds the transform at that moment.
        if (h.IsNull || !scene.IsLive(h)) return;
        ref NodePaint p = ref scene.Paint(h);
        if (MathF.Abs(p.LocalTransform.Dy - comp) < (landed ? 0.0005f : CascadeWriteEps)) return;
        p.LocalTransform = comp == 0f ? Affine2D.Identity : Affine2D.Translation(0f, comp);
        scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    // Cancel the cascade and put every line back on its true scroll position (identity transform for every line whose
    // comp is non-zero, then all four arrays zeroed). Called wherever the compensation would otherwise fight something
    // else for the lines' positions: a USER detach (the transform must never fight the user's own scroll), a seek or a
    // >250 ms clock snap, a doc change, rail close / ResetFollowState, and any follow-mode transition out of Following.
    // Keep the plain hot-path flag and its ticker-facing signal in lockstep. Signal<T> coalesces an equal write, so the
    // per-frame `SetCascadePending(moving)` notifies exactly once — on the frame the last line lands.
    void SetCascadePending(bool pending)
    {
        _cascadePending = pending;
        _cascadeRunning.Value = pending;
    }

    // Ticker-only subscription (like FollowModeValue): keeps the 16 ms interval armed across a PAUSE that lands inside a
    // handoff, so the cascade always finishes instead of freezing the document off its focal band.
    internal bool CascadeRunningValue => _cascadeRunning.Value;

    // The non-zero-comp gate below is still exactly right under the write band: a line only ever holds a written,
    // non-zero transform while its comp is non-zero. In band, WriteCascade's landing write is exact (0.0005 DIP) so
    // comp == 0 and the node's identity transform become true together; out of band, ArmCascade already zeroed both.
    void ZeroCascade(SceneStore? scene)
    {
        var comp = _casComp;
        SetCascadePending(false);
        for (int i = 0; i < comp.Length; i++)
        {
            if (comp[i] != 0f && scene is not null) WriteCascade(scene, i, 0f, landed: true);
            comp[i] = 0f; _casVel[i] = 0f; _casDelayLeftMs[i] = 0f;
        }
    }

    void SetFollowMode(LyricsFollowMode next, SceneStore? scene)
    {
        var previous = _followMode.Peek();
        if (previous == next) return;
        bool wasSuppressed = SuppressesDof(previous);
        bool nowSuppressed = SuppressesDof(next);
        _followMode.Value = next;
        // Leaving Following (detach or resync) retires the cascade: the follow no longer owns the lines' positions, so a
        // lingering compensating translate would fight the user's scroll / the resync glide. This is the ONE chokepoint
        // every mode transition goes through, so detach + resync are both covered here. The interlude dots go with it
        // and for the same reason — they are a follow-owned overlay — retired HERE so the ShowEl exit animation runs at
        // the detach EDGE rather than one frame later in OnFrame's gate.
        // …and the reserved band goes with them: it exists only to hold space for the dots, so leaving it armed would
        // park a 25-32 DIP hole above a line the user has scrolled away from. This is the OTHER release edge (the first
        // is the next handoff); the dots' own exit, one second before the next line, deliberately is not.
        if (next != LyricsFollowMode.Following) { ZeroCascade(scene); HideInterludeDots(); SetInterludeReserve(-1); }
        if (wasSuppressed != nowSuppressed) ApplyDofSuppression(scene);
    }

    void ResetFollowState(SceneStore? scene)
    {
        _resyncDeadlineWallMs = 0L;
        _resyncProgress.Value = 1f;
        ZeroCascade(scene);   // rail close / track change / lyric click: no in-flight compensation survives it
        SetFollowMode(LyricsFollowMode.Following, scene);
    }

    void OnLyricsScrollActivity(bool userScrollActive, long wallMs)
    {
        if (userScrollActive)
        {
            _resyncDeadlineWallMs = 0L;
            _resyncProgress.Value = 1f;
            ZeroCascade(Context.Scene);   // the user owns the viewport now — drop the compensation before it can fight
            SetFollowMode(LyricsFollowMode.DetachedActive, Context.Scene);
            return;
        }

        if (_followMode.Peek() != LyricsFollowMode.DetachedActive) return;
        _resyncDeadlineWallMs = wallMs + ResyncIdleMs;
        _resyncProgress.Value = 1f;
        SetFollowMode(LyricsFollowMode.DetachedIdle, Context.Scene);
    }

    void TickFollowState(SceneStore scene, long wallMs)
    {
        var mode = _followMode.Peek();
        if (mode == LyricsFollowMode.DetachedIdle)
        {
            float left = Math.Clamp((_resyncDeadlineWallMs - wallMs) / (float)ResyncIdleMs, 0f, 1f);
            float shown = MathF.Ceiling(left * ResyncProgressSteps) / ResyncProgressSteps;
            if (MathF.Abs(_resyncProgress.Peek() - shown) > 0.0001f) _resyncProgress.Value = shown;
            if (left <= 0f)
            {
                BeginResync(scene);
                return;
            }
        }

        if (mode == LyricsFollowMode.Resyncing) DriveResync(scene);
    }

    void BeginResync(SceneStore? scene)
    {
        _resyncDeadlineWallMs = 0L;
        _resyncProgress.Value = 1f;
        SetFollowMode(LyricsFollowMode.Resyncing, scene);
        if (scene is not null) DriveResync(scene);
    }

    void DriveResync(SceneStore scene)
    {
        int active = _activeLine.Peek();
        if (active < 0)
        {
            CompleteResync(scene);
            return;
        }

        if (ScrollActiveIntoView(scene, active, FollowScrollIntent.Resync) == FollowArmResult.AtTarget)
            CompleteResync(scene);
    }

    void CompleteResync(SceneStore scene)
    {
        _resyncDeadlineWallMs = 0L;
        _resyncProgress.Value = 1f;
        SetFollowMode(LyricsFollowMode.Following, scene);   // DoF returns only after the programmatic spring has landed
    }

    void SeekToLine(int index)
    {
        var b = _b; var doc = _doc;
        if (b is null || doc is null || (uint)index >= (uint)doc.Lines.Count) return;
        ResetFollowState(Context.Scene);   // a deliberate lyric click returns to live before the new active index resolves
        long ms = doc.Lines[index].StartMs;
        b.CommitSeek(ms);   // arms the latch, optimistically publishes PositionMs, issues the accurate seek — a line tap is a commit, never a scrub preview
        RebaseClock(ms);    // seed all clock fields; _lastAuthMs=ms keeps OnFrame from re-treating our own jump as a seek
        _scrollSnapped = false;   // the next follow is the HARD first-landing jump, with the cascade left at rest
        ZeroCascade(Context.Scene);
        ResetWipeThrottle();
    }

    internal void OnFrame(bool forceVisual = false, long probeNowMs = long.MinValue)
    {
        var b = _b; var doc = _doc;
        if (b is null || doc is null || doc.Lines.Count == 0) return;
        if (!IsTimed(doc)) return;
        // A VIDEO is a different edit of the song, so these line timings — which belong to the AUDIO edit — no longer
        // describe what is being heard (LyricsSyncGate). Suppressing sync here, at the ONE driver, is what makes the
        // whole timed apparatus stop together: no active/voice resolve, no emphasis rewrite, no follow scroll, no wipe.
        // Doing it per-consumer instead would leave some of them ticking against a clock that means nothing.
        // The document itself is untouched and stays freely scrollable — the rows simply render in their resting ink,
        // which is exactly what an active index of -1 already means everywhere downstream.
        // Peek, not .Value: OnFrame is a frame callback, not a Render scope, so a subscribing read here would either be
        // dropped or attach to whatever computation happened to be tracking. Same reason PlaybackBridge.ShouldPlayAsVideo
        // peeks. The BANNER in Render subscribes, which is what re-renders the view when video starts or stops.
        if (LyricsSyncGate.SyncSuppressed(PlacementCore.IsActive(b.VideoSurface.Peek())))
        {
            if (_activeLine.Peek() != -1) _activeLine.Value = -1;
            if (_voiceLine.Peek() != -1) _voiceLine.Value = -1;
            ResetFollowState(Context.Scene);   // drop any armed follow so the view does not keep chasing a stale target
            return;
        }

        // Dejittered media clock. The authoritative IPC PositionMs is itself a coarse ~1 Hz extrapolation; the old code
        // HARD re-anchored on every snapshot, so a delayed/corrected one snapped nowMs — and since BOTH the active-line
        // resolve and the karaoke wipe read nowMs, the line jumped (even backward) and the fill lurched. Instead: DEADBAND
        // tiny disagreements (IPC jitter), gently SLEW small ones into an additive offset (no visible jump), SNAP only a
        // true seek, plus a MONOTONIC-while-playing guard so the wipe/line never tick backward except on a real seek. Peek
        // only (no .Value subscribe ⇒ no re-render ⇒ no all-lines-pulse); pure scalar math, zero per-frame alloc. With the
        // deadband, steady-state extrapolation stays byte-identical to before, so the swap timing + skip-submit are intact.
        long auth = b.PositionMs.Peek();
        long wallMs = Environment.TickCount64;
        bool playing = b.IsPlaying.Peek();
        long nowMs;
        if (probeNowMs != long.MinValue)
        {
            // Probe sync-advance (WAVEE_LYRICS_ADVANCE_PROBE): the probe owns the media clock, so a line advance and the
            // RunFrame that records the resulting scroll SETTLE are the same frame (the async 16 ms Timer is silenced by
            // ProbeSyncMode). Deterministic + free of the ticker decoupling — the basis of the trustworthy re-probe.
            nowMs = probeNowMs; auth = probeNowMs; playing = true;
            _baseWall = wallMs; _basePos = probeNowMs; _offset = 0f; _lastAuthMs = probeNowMs; _lastDisplay = probeNowMs;
        }
        else if (!playing)
        {
            // Paused: the snapshot is the truth. Pin the base to it so a later RESUME doesn't leak the pause gap into the
            // wall delta, and a paused scrub follows immediately.
            nowMs = auth;
            _baseWall = wallMs; _basePos = auth; _offset = 0f; _lastAuthMs = auth; _lastDisplay = auth;
        }
        else
        {
            if (!_wasPlaying)
            {
                // Just resumed: rebase clean so the (untimed) pause duration doesn't appear as a forward jump.
                _baseWall = wallMs; _basePos = auth; _offset = 0f; _lastAuthMs = auth; _lastDisplay = auth;
            }
            else if (auth != _lastAuthMs)
            {
                _lastAuthMs = auth;
                long predicted = _basePos + (wallMs - _baseWall);
                long err = auth - (long)(predicted + _offset);
                long ae = err < 0 ? -err : err;
                if (ae <= 12) { /* deadband: ignore IPC jitter < ~12 ms */ }
                else if (ae <= 250) { _offset += err * 0.5f; }   // slew: absorb ~half per snapshot (closes in 1-2)
                else                                              // snap: a real seek / device transfer
                {
                    _baseWall = wallMs; _basePos = auth; _offset = 0f;
                    _lastDisplay = auth;        // bypass the monotonic guard for a legitimate (possibly backward) seek
                    _scrollSnapped = false;     // next ScrollActiveIntoView does the INSTANT-jump latch, not an ease across the song
                    ZeroCascade(Context.Scene); // …and a song-length jump is NOT a handoff: no cascade rides across it
                    ResetWipeThrottle();        // re-evaluate the wipe at the new position
                }
            }
            long nowRaw = (long)(_basePos + (wallMs - _baseWall) + _offset);
            nowMs = Math.Max(nowRaw, _lastDisplay);   // monotonic while playing
            _lastDisplay = nowMs;
        }
        _wasPlaying = playing;

        // Eye-leads-voice: emphasis + scroll resolve against a LEAD-shifted clock (~140 ms early) so the line is rising
        // into focus as the first syllable lands, while the karaoke wipe/glow stay on the TRUE audio clock (voiceLine +
        // raw nowMs) so the fill matches the voice. Two indices — one must NOT drive the other (lead-shifting a single
        // index would retarget the wipe to the not-yet-singing line, killing the fill on the line you are hearing).
        int active = ResolveLine(doc.Lines, nowMs + LeadMs);   // emphasis + scroll (anticipates)
        int voiceLine = ResolveLine(doc.Lines, nowMs);          // wipe + glow (on true time)
        // A line stops being the VOICE at its own SUNG-OUT point, not when the next line starts: ResolveLine keeps
        // returning the previous line through the whole inter-line gap, which left it fully lit + glowing NEXT TO the
        // already-risen (lead) new active line — the "previous line is still fully active on the next line" double
        // emphasis. Between sung-out and the next start nothing is being sung ⇒ no voice.
        if (voiceLine >= 0 && nowMs >= SungOutMs(doc, voiceLine)) voiceLine = -1;
        // …and once a line IS sung out, a real instrumental break RETIRES it rather than parking focus on it: the active
        // index steps to the upcoming line for the whole gap (see the interlude block), so the finished line takes the
        // ordinary past treatment and the dots overlay marks the silence. Applied HERE, before `activeChanged` is
        // computed, so the onset rides the normal handoff — emphasis rewrite, viewport latch and cascade.
        active = AdvancePastInterlude(doc, active, nowMs, out long gapStart, out long gapEnd);
        bool inInterlude = gapEnd > gapStart;
        bool activeChanged = active != _activeLine.Peek();
        if (activeChanged) _activeLine.Value = active;
        int prevVoiceLine = _voiceLine.Peek();
        bool voiceChanged = voiceLine != prevVoiceLine;
        if (voiceChanged) _voiceLine.Value = voiceLine;
        _nowMs.Value = nowMs;
        if (activeChanged && _pendingUpgrade is { } upgrade)
        {
            ApplyLyricsUpgrade(upgrade, nowMs);
            return;
        }

        // Main-content scroll tightens the shared GPU budget — defer the lyrics GLOW only (its per-frame σ/split writes
        // invalidate the scroll blur-pin and force re-Gaussians). The READABLE karaoke wipe is a cheap single-line
        // gradient write and keeps tracking the voice: freezing it visibly desynced the fill during any page scroll,
        // then lurched it forward on scroll end. (Core lane below is never gated.)
        bool deferHeavy = Context.PeekMainScrollBusy?.Invoke() == true;
        bool runGlow = !deferHeavy || activeChanged || voiceChanged || forceVisual;

        var scene = Context.Scene;
        if (scene is null) return;
        TickFollowState(scene, wallMs);

        // ── Core lane (always): the reserved band, the programmatic scroll follow, then the interlude dots ──
        // The dots are mounted for the break MINUS its last second, so they are gone before the next line's first
        // syllable lands and that handoff stays the clean one it always was. Outside a break this is one bool Peek.
        // FOLLOW-GATED like every other follow-owned visual: once the user has scrolled away, the dots would be a
        // filling, pulsing overlay floating over whatever lyrics they are reading, next to the resync pill. They come
        // back with the follow (SetFollowMode runs the exit at the detach edge so the retirement is animated, not a
        // frame-boundary disappearance).
        bool dotsUp = _followMode.Peek() == LyricsFollowMode.Following && inInterlude && nowMs < gapEnd - InterludeDotsExitMs;
        // The band's two edges, decided BEFORE the follow runs so an arm and its first latch can share a frame. RELEASE
        // rides the next handoff (a height change is only ever paid for while the document is already travelling);
        // ARMING is the dots' own onset, which IS a handoff (AdvancePastInterlude lands in `active` before
        // `activeChanged` is computed). Ordered release-then-arm so back-to-back breaks hand the band over cleanly.
        if (activeChanged && _interludeReserveLine >= 0 && _interludeReserveLine != active) SetInterludeReserve(-1);
        if (dotsUp && _interludeReserveLine != active) SetInterludeReserve(active);
        // A reserve edge changes a row height that this frame's extent table has not seen yet — keep the follow live for
        // a few frames so the arrange that measures it is followed by an ordinary latch+cascade onto the new target.
        bool reserveRelatch = _reserveRelatchFrames > 0;
        if (reserveRelatch) _reserveRelatchFrames--;
        if (active >= 0 && (uint)active < (uint)doc.Lines.Count && (!_scrollSnapped || activeChanged || reserveRelatch || forceVisual))
            ScrollActiveIntoView(scene, active, FollowScrollIntent.Normal);
        if (dotsUp)
        {
            _dotsShown.Value = true;
            DriveInterludeDots(scene, nowMs, gapStart, gapEnd);
        }
        else if (_dotsShown.Peek()) HideInterludeDots();
        if (activeChanged) { PushEmphasis(); _dofRampPending = true; }
        // Directional DoF ramp — core lane, never budget-deferred: it is bounded (it self-quiesces the pass after every
        // line lands) and the ladder is INFORMATION, not decoration. It must run AFTER PushEmphasis so the rows that are
        // about to re-render this frame read a σ model that already reflects the new active line.
        DriveDofRamp(scene, wallMs);
        // Staggered handoff cascade — core lane for the same reasons: it is bounded (it self-quiesces the pass once
        // every line lands) and it IS the follow motion now, so budget-deferring it would freeze the lyrics mid-flight.
        // Runs AFTER the ScrollActiveIntoView above so an arm and its first integration step share one frame.
        DriveCascade(scene);
        LastFrameDiagnostics = new(nowMs, auth, active, voiceLine, activeChanged, voiceChanged, _scrollSnapped, playing, doc.Lines.Count);

        // ── Visual lane: glow cross-fade (budget-deferred during a main scroll) + karaoke wipe (never deferred) ──
        if (runGlow)
        {
            // Voice handoff: CROSS-FADE the halos (incoming line ramps in, outgoing ramps out) instead of a hard clear —
            // the old instant σ/content toggle popped the glow on and off in one frame at every line change.
            if (voiceChanged) BeginGlowFades(scene, prevVoiceLine, voiceLine, wallMs);
            DriveGlowFades(scene, wallMs);
            // Voice-line glow envelope (FIX B): monotone min(inFade, outFade) on the media clock — no hard gate against the
            // in-cross-fade, so short chorus lines compress to a smooth triangle instead of snapping. Authoritative for the
            // live voice row; the cross-fade above only handles the OUTGOING line (and seek handoffs routed through ease()).
            if (voiceLine >= 0 && (uint)voiceLine < (uint)doc.Lines.Count)
                ApplyVoiceGlowEnvelope(scene, doc, voiceLine, nowMs);
        }

        // The karaoke wipe/glow live on the VOICE line (true time), trailing the emphasis line during the lead window. Drive
        // the READABLE main text wipe (the visible reveal) as the primary; the glow wipe (sung-only bloom) rides the same
        // split. Gate on the MAIN node's wipe — it is present on every word-by-word line (line-synced main has no wipe, so
        // this whole block correctly no-ops for line-synced, whose glow is a static child-blur, not an OnFrame-driven node).
        if ((uint)voiceLine >= (uint)_lineNodes.Length) return;
        var mainNode = _lineNodes[voiceLine];
        var glowNode = (uint)voiceLine < (uint)_glowNodes.Length ? _glowNodes[voiceLine] : NodeHandle.Null;
        if (mainNode.IsNull || !scene.IsLive(mainNode)) return;

        if (scene.TryGetGlyphWipe(mainNode, out var mw))
        {
            float split = LyricLineView.ComputeSplit(doc.Lines[voiceLine], nowMs);
            if (split > 0f && split < 1f) split = Math.Clamp(split + LyricLineView.WipeLeadFrac, 0f, 1f);
            // Pixel-quantize the boundary so sub-pixel ticks produce byte-identical gradient bytes ⇒ the host skip-submit hash
            // elides them (and the blur pin-cache HITS). main and glow share this run width (same text/size/wrap). The
            // SETTLED endpoints are exempt: they must stay EXACTLY 0/1, because that is the condition the replay's
            // settled-split fast path tests to drop the run into the cheap plain glyph batch — and a fractional runW
            // would land a rounded 1 at 1.0017 (Round(295.5)/295.5) instead. ComputeSplit already returns exact 1 on a
            // sung-out line (played == total) and exact 0 before the first syllable, and the lead-clamp above preserves
            // both (it is skipped at 0, and Clamp caps at exactly 1), so the endpoints really are exact here.
            float runW = scene.AbsoluteRect(mainNode).W;
            if (runW > 1f && split > 0f && split < 1f) split = MathF.Round(split * runW) / runW;
            // NO wall-clock rate gate here. There used to be one — `wallMs - _lastWipeWallMs < KaraokeWipeIntervalMs`
            // — from when an over-eager timer could call OnFrame faster than the wipe needed. It was measuring a 16 ms
            // threshold on Environment.TickCount64, whose granularity IS the system timer tick (~15.6 ms by default):
            // consecutive OnFrame calls one refresh apart read a delta of 15, which is < 16, so roughly every other
            // call returned here without advancing the fill. The wipe therefore stepped at ~20-30 Hz however fast the
            // host was producing — the karaoke reveal reading as a slideshow on a 120 Hz panel. The frame IS the rate
            // limiter now (one OnFrame per produced frame), and both writes below are already VALUE-gated and
            // pixel-quantized, so a frame that moves the split by less than a device pixel still writes nothing.
            _lastWipeWallMs = wallMs;
            _lastWipeLine = voiceLine;

            // The feather the seed baked in can be STALE — the seed for a line realized before the viewport had geometry
            // (or before a fullscreen resize) resolved against a fallback run length. Self-heal it into the same write
            // instead of a second one: at the ≥0.002 fraction gate this fires at most once per line per width epoch.
            float softness = SoftnessOfLine(voiceLine);

            // SETTLED ⇒ STOP WRITING. The wipe's Split/Softness/Lift fold verbatim into the recorder's BlurPinKey, so
            // re-writing a line that is ALREADY fully sung misses the blur pin on exactly the frames the outgoing line is
            // being blurred+dimmed away (it stays the voice line for the whole ~LeadMs window after focus has moved, and
            // WipeLeadFrac drives Split to 1 a few % before that). Freezing the byte-identical value keeps the pin
            // cache hitting AND keeps the run in the replay's cheap plain glyph batch instead of the gradient batch.
            bool settled = (split >= 1f && mw.Split >= 1f) || (split <= 0f && mw.Split <= 0f);

            // Readable main wipe — the karaoke reveal the user sees (S2).
            if (!settled && (MathF.Abs(split - mw.Split) > 0.0008f || MathF.Abs(softness - mw.Softness) > 0.002f))
            {
                scene.SetGlyphWipe(mainNode, mw with { Split = split, Softness = softness });
                scene.Mark(mainNode, NodeFlags.PaintDirty);
            }

            // Glow bloom — same split; σ co-decays with the bound glow alpha (FIX B melt) so the halo tightens as it
            // dims. Deferred with the glow lane during a main scroll: a per-frame split/σ write here would invalidate
            // the scroll blur-pin every frame — the halo holds its last frame instead (barely visible under the fill).
            if (runGlow && !glowNode.IsNull && scene.IsLive(glowNode) && scene.TryGetGlyphWipe(glowNode, out var w))
            {
                // The halo must track the main layer's geometry EXACTLY (same split, same feather, same lift) or the
                // bloom drifts out from under the crisp text; it gets the same settled write-stop for the same reason.
                bool glowSettled = (split >= 1f && w.Split >= 1f) || (split <= 0f && w.Split <= 0f);
                bool glowDirty = !glowSettled && (MathF.Abs(split - w.Split) > 0.0008f || MathF.Abs(softness - w.Softness) > 0.002f);
                if (glowDirty) scene.SetGlyphWipe(glowNode, w with { Split = split, Softness = softness });
                // Held-note bloom σ, TRIMMED for strict parity (was 6 large / 4 rail — campaign 2026-08-03): the
                // reference capture shows no glow anywhere, but it also contains no ≥HeldGlowMinMs held syllable, so it
                // can only argue the bloom smaller, never away. Apple genuinely blooms held notes, so the MECHANISM
                // stays and only its amplitude comes down — here and at HeldGlowPeakScale.
                float baseSigma = _large ? 4.5f : 3f;
                float glowA = GlowAlphaOf(voiceLine);
                // KNOWN COST, not a correctness bug (and deliberately not "fixed" here): while this row carries a
                // depth-of-field σ, a halo σ makes a blur layer NESTED inside a blur layer, and a nested PushLayer is
                // exactly what BlurPinKey.TryCompute refuses — so dofContent is pin-INELIGIBLE for the voice row through
                // the whole ~140 ms lead window and the ~240 ms out-fade after it, i.e. at every line handoff. It
                // re-Gaussians instead of pin-hitting. The LINE-SYNCED branch of LyricLineView.Render dodges this by
                // putting σ on the focal row only; the two ways out here both cost a VISUAL: zero the halo σ (the bloom
                // becomes a crisp duplicate under the main text at full GlowOpacity) or zero the row's DoF σ (~380 ms of
                // two in-focus rows per handoff, weakening the ladder). Neither is obviously right, so the cost stands.
                // What this used to ALSO cost — the panel flashing crisp+full-alpha at a handoff during any page scroll —
                // is gone: a blur-hold miss now blurs the frame instead of publishing the subtree inline
                // (D3D12Device's hold arms).
                float sigma = baseSigma * glowA;
                ref var gp = ref scene.Paint(glowNode);
                if (MathF.Abs(gp.BlurSigma - sigma) > 0.01f) { gp.BlurSigma = sigma; glowDirty = true; }
                if (glowDirty) scene.Mark(glowNode, NodeFlags.PaintDirty);
            }
        }
    }

    // Arm the halo cross-fade at a voice handoff. Each fade remembers its FROM alpha so a handoff landing mid-fade (rapid
    // line runs, or scrubbing back onto a fading line) continues from the current value instead of jumping to 0/1. If a
    // THIRD line's out-fade is still in flight, finish it instantly — at most two halos ever animate.
    void BeginGlowFades(SceneStore scene, int prev, int next, long wallMs)
    {
        if (_glowOutLine >= 0 && _glowOutLine != prev && _glowOutLine != next) FinishGlowOut(scene, _glowOutLine);
        _glowOutLine = prev;
        _glowOutStart = wallMs;
        // From the LIVE alpha (not an assumed 1): the end-of-line pre-fade usually already took it near 0, so a normal
        // handoff finishes the out-fade almost instantly instead of re-airing a 240 ms halo tail over the new line.
        _glowOutFrom = GlowAlphaOf(prev);
        _glowInLine = next;
        _glowInStart = wallMs;
        _glowInFrom = GlowAlphaOf(next);
    }

    // Step halo cross-fades (called every OnFrame tick). Sine-Out eased (FIX B). The live voice line's envelope is
    // ApplyVoiceGlowEnvelope — this handles OUTGOING lines only; the in-ramp is the envelope's min(in,out) form.
    void DriveGlowFades(SceneStore scene, long wallMs)
    {
        if (_glowOutLine >= 0)
        {
            float t = EaseOutSine(Math.Clamp((wallMs - _glowOutStart) / GlowFadeMs, 0f, 1f));
            float a = _glowOutFrom * (1f - t);
            if (a <= 0f) { FinishGlowOut(scene, _glowOutLine); _glowOutLine = -1; }
            else SetGlowAlpha(_glowOutLine, a);
        }
    }

    // Monotone voice-line glow envelope: min(eased in-ramp, eased out-ramp) on the media clock + optional bloom taper.
    void ApplyVoiceGlowEnvelope(SceneStore scene, LyricsDocument doc, int voiceLine, long nowMs)
    {
        var line = doc.Lines[voiceLine];
        long lineStart = line.StartMs;
        long lineEnd = line.IsWordByWord && line.Syllables.Count > 0 ? line.Syllables[^1].EndMs
            : line.EndMs ?? (voiceLine + 1 < doc.Lines.Count ? doc.Lines[voiceLine + 1].StartMs : long.MaxValue);
        float alphaOut = EaseOutSine(Math.Clamp((lineEnd - nowMs) / GlowOutMs, 0f, 1f));
        float alpha;
        if (line.IsWordByWord && line.Syllables.Count > 0)
        {
            // WaveeMusic/BetterLyrics rule (LyricsAnimator "辉光（长音节）", scope = LongDurationSyllable): the halo
            // blooms ONLY while a ≥ HeldGlowMinMs syllable is being HELD — swell in across the hold, melt out into its
            // end. Short syllables get no glow at all; the old whole-line wash is gone.
            // HeldGlowPeakScale is the strict-parity amplitude trim, applied HERE (once, to the finished monotone
            // envelope) so the shape/timing of the swell and the melt are untouched — only how bright it ever gets.
            alpha = HeldGlowPeakScale * MathF.Min(HeldSyllableGlow(line, nowMs), alphaOut);
        }
        else
        {
            // Line-synced (no syllable timing): keep the gentle whole-line envelope — there is no "held note" signal.
            float inFade = EaseOutSine(Math.Clamp((nowMs - lineStart) / GlowFadeMs, 0f, 1f));
            alpha = MathF.Min(inFade, alphaOut);
        }
        alpha = MathF.Max(0f, alpha);
        SetGlowAlpha(voiceLine, alpha);
        // Envelope owns the live voice row — retire any redundant in-cross-fade arm.
        if (_glowInLine == voiceLine) _glowInLine = -1;
    }

    // The held-note bloom: 0 unless nowMs sits inside a syllable of at least HeldGlowMinMs; inside one, swell in over
    // min(HeldGlowRampMaxMs, half the note) and melt over GlowOutMs into the note's end — min(up, down) is the same
    // monotone triangle form the line envelope used, so a short-held note compresses smoothly instead of snapping.
    static float HeldSyllableGlow(LyricLine line, long nowMs)
    {
        var syls = line.Syllables;
        for (int i = 0; i < syls.Count; i++)
        {
            var s = syls[i];
            if (nowMs < s.StartMs) break;        // syllables are time-ordered — nothing later can contain nowMs
            if (nowMs >= s.EndMs) continue;
            long dur = s.EndMs - s.StartMs;
            if (dur < HeldGlowMinMs) return 0f;  // short syllable: never glows
            float rampIn = MathF.Min(HeldGlowRampMaxMs, dur * 0.5f);
            float up = EaseOutSine(Math.Clamp((nowMs - s.StartMs) / rampIn, 0f, 1f));
            float down = EaseOutSine(Math.Clamp((s.EndMs - nowMs) / GlowOutMs, 0f, 1f));
            return MathF.Min(up, down);
        }
        return 0f;
    }

    static float EaseOutSine(float t) => MathF.Sin(t * MathF.PI * 0.5f);

    static float ComputeSplit(LyricLine line, long nowMs) => LyricLineView.ComputeSplit(line, nowMs);

    float GlowAlphaOf(int line) => (uint)line < (uint)_glowAlpha.Length ? _glowAlpha[line].Peek() : 0f;

    void SetGlowAlpha(int line, float a)
    {
        if ((uint)line < (uint)_glowAlpha.Length) _glowAlpha[line].Value = a;
    }

    void FinishGlowOut(SceneStore scene, int line)
    {
        SetGlowAlpha(line, 0f);
        // Word-by-word glow σ is paint-driven (element Blur = 0) — return it to rest once invisible so the halo layer
        // costs nothing. Line-synced glow σ is a constant element Blur: leave it (alpha 0 hides it; settled bytes pin-hit).
        if (_doc is { } d && (uint)line < (uint)d.Lines.Count && d.Lines[line].IsWordByWord)
            ClearGlowNode(scene, line);
    }

    void ClearGlowNode(SceneStore scene, int line)
    {
        if ((uint)line >= (uint)_glowNodes.Length) return;
        var g = _glowNodes[line];
        if (g.IsNull || !scene.IsLive(g)) return;
        ref var gp = ref scene.Paint(g);
        bool dirty = false;
        if (gp.BlurSigma != 0f) { gp.BlurSigma = 0f; dirty = true; }
        if (gp.BlurCachePolicy != BlurCachePolicy.Normal) { gp.BlurCachePolicy = BlurCachePolicy.Normal; dirty = true; }
        if (dirty) scene.Mark(g, NodeFlags.PaintDirty);
    }

    FollowArmResult ScrollActiveIntoView(SceneStore scene, int active, FollowScrollIntent intent)
    {
        var viewport = _viewportNode;
        var layout = _layout;
        if (layout is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport))
            return FollowArmResult.Unavailable;

        ref ScrollState sc = ref scene.ScrollRef(viewport);
        if (sc.ViewportH <= 0.5f || sc.ContentH <= 0.5f) return FollowArmResult.Unavailable;

        if (intent == FollowScrollIntent.Normal)
        {
            if (_followMode.Peek() != LyricsFollowMode.Following) return FollowArmResult.Unavailable;
            if (sc.UserScrollActive)
            {
                OnLyricsScrollActivity(true, Environment.TickCount64);
                return FollowArmResult.Unavailable;
            }
        }

        // ArrangeVirtualMeasured now owns the engine's SetViewport-before-geometry contract. Refresh it here too because
        // this target calculation runs outside layout and should use the newest published viewport immediately.
        layout.SetViewport(sc.ViewportH, sc.ViewportW);

        RectF item = layout.ItemRect(active, sc.ViewportW);
        // The band centres the row's CONTENT box, not its outer box. They differ by exactly one thing: the interlude
        // reserve, a top pad the anchor row carries while the dots are up (see the reserve block). Centring the padded
        // outer box would drop the sung line by half the reserve for the whole break and lift it back at the handoff —
        // a focal line that drifts is precisely what the follow exists to prevent. Skipping the reserve instead opens
        // the whole band ABOVE the line and leaves the line itself exactly where it always sits.
        float reserve = MathF.Min(ReserveOf(active), item.H);
        float target = item.Y + reserve + (item.H - reserve) * 0.5f - sc.ViewportH * _band;
        target = Math.Clamp(target, 0f, MathF.Max(0f, sc.ContentH - sc.ViewportH));

        if (!_scrollSnapped && intent == FollowScrollIntent.Normal)
        {
            // FIRST LANDING for this doc/seek: a hard jump with the cascade left at rest (comps are already 0 here — the
            // paths that clear _scrollSnapped all ZeroCascade). There is nothing to compensate: the user has not seen a
            // previous position to travel from.
            _scrollSnapped = true;
            LatchViewport(viewport, target);
            return FollowArmResult.AtTarget;
        }
        if (intent == FollowScrollIntent.Resync) _scrollSnapped = true;   // Resync is always a spring, never the open latch

        if (intent == FollowScrollIntent.Normal)
        {
            // ── The handoff: ONE instant viewport latch + the per-line compensating cascade ─────────────────────────
            // Reached only in the steady state (this branch is gated Following by the intent check at the top, and
            // _scrollSnapped by the first-landing branch above), which is exactly the cascade's arming condition.
            float delta = target - sc.OffsetY;
            if (MathF.Abs(delta) <= 0.5f) return FollowArmResult.AtTarget;
            LatchViewport(viewport, target);
            ArmCascade(scene, delta, active);
            return FollowArmResult.AtTarget;
        }

        // ── Resync only, below: the kernel's Driven chase, flying the viewport back after a user scroll ──
        // Velocity-continuous re-target: only zero the carried spring velocity on the FIRST entry into a Programmatic
        // Driven chase. A re-target while ALREADY easing KEEPS the velocity — the kernel retargets a live Driven chase
        // for the same node in place (plan §2.2 "Driven"), so the spring chains smoothly to the new target instead of
        // restarting a decelerating chase.
        bool alreadyProgrammatic = sc.Activity == ScrollActivity.Driven
                                  && (sc.ActivityFlags & ScrollActivityFlags.Programmatic) != 0;
        if (!alreadyProgrammatic) _followProgrammaticTargetY = null;
        if (alreadyProgrammatic && _followProgrammaticTargetY is { } lastTarget && MathF.Abs(lastTarget - target) <= 0.5f)
            return FollowArmResult.Armed;
        if (!alreadyProgrammatic && MathF.Abs(sc.OffsetY - target) <= 0.5f)
            return FollowArmResult.AtTarget;

        // ZERO OVERSHOOT, here too. The old AMLL tune (ζ=0.833/ω0=10) selected the UNDERDAMPED closed form, which by
        // definition crosses the target — the frame evidence refutes any overshoot in this motion. zeta=0/omega=0
        // selects the kernel's ζ=1 halflife branch instead. 110 ms half-life ⇒ a ~0.5 s settle, the same felt duration
        // as before, monotone. The 4 DIP/s landing gate keeps the global 16 DIP/s wheel threshold from truncating the
        // soft tail.
        _followProgrammaticTargetY = target;
        ScrollIntoView.ScrollTo(Context, viewport, target, halflifeMs: 110f, zeta: 0f, omega: 0f, settleVel: 4f);
        // ScrollIntoView.ScrollTo wakes the frame WITHOUT re-rendering THIS component (RequestFrame, not
        // RequestRerender): the kernel re-realizes the virtual window on each offset move (reuseOverlap), while the
        // active-line emphasis re-renders the line components IN PLACE via the _activeLine signal (same node ⇒
        // springs retarget by rebase). A full re-render here would rebuild the virtual window and remount every line,
        // re-seeding its springs from default paint — every line would flash "active" for a frame on each line change
        // (the reported swap-flash).
        return FollowArmResult.Armed;
    }

    // The INSTANT viewport latch — the one mechanism the follow uses in Following mode, for both the first landing and
    // every subsequent handoff. Posts an immediate ScrollTo through the kernel — no in-flight Driven chase survives it
    // (a fresh ScrollTo always supersedes), the kernel's own Reclamp re-realizes the virtual window (reuseOverlap —
    // existing rows kept).
    //
    // ScrollIntoView.ScrollTo wakes the frame WITHOUT re-rendering (RequestFrame, not RequestRerender) — re-rendering
    // here would re-run the Skel.Region content delegate, rebuild the VirtualListEl and remount every line node,
    // re-seeding each line's springs from default paint (1.0) — the "all lines flash active for a frame" bug. This is
    // the proven path; the cascade rides on top of it.
    void LatchViewport(NodeHandle viewport, float target)
    {
        _followProgrammaticTargetY = null;   // an instant latch supersedes any live Resync chase
        ScrollIntoView.ScrollTo(Context, viewport, target, animate: false);
    }

    internal void ResetScrollSnap()
    {
        _scrollSnapped = false;
        // Every path that clears _scrollSnapped also retires the cascade — the first-landing branch in
        // ScrollActiveIntoView relies on that: a hard jump with a non-zero comp left over would displace the document.
        ZeroCascade(Context.Scene);
        ResetWipeThrottle();
    }

    void ResetWipeThrottle()
    {
        _lastWipeWallMs = 0L;
        _lastWipeLine = -1;
    }

    static int ResolveLine(IReadOnlyList<LyricLine> lines, long nowMs)
    {
        if (lines.Count == 0 || nowMs < lines[0].StartMs) return -1;
        int lo = 0, hi = lines.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].StartMs <= nowMs) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    static bool IsTimed(LyricsDocument doc) => doc.Sync is LyricsSyncKind.Line or LyricsSyncKind.Syllable;

    Element Message(string msg) => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.XXL, 0f, Spacing.XXL, 0f),
        Children = [new TextEl(msg) { Size = 14f, LineHeight = 20f, Color = _ink.Secondary, Wrap = TextWrap.Wrap }],
    };
}

// Variable-height (measured) virtual layout for the lyrics list. Each row is CONTENT-FIT — a one-line lyric is short, a
// two-line lyric is tall — so there is no dead space and nothing clips/ellipsizes; a top/bottom focal pad still lets the
// FIRST and LAST lines scroll to the focal band. Implements the engine's measured seam (estimate-then-correct over an
// ExtentTable; the engine measures each realized row and feeds SetMeasured) AND the viewport seam (for the focal pad).
// The app also pushes the viewport (LyricsView.ScrollActiveIntoView) because the engine's measured arrange path, unlike
// the fixed-geometry path, does not call SetViewport.
sealed class LyricsMeasuredLayout : IMeasuredVirtualLayout, IViewportVirtualLayout
{
    public readonly float Estimate;   // per-row height seed for not-yet-measured rows (a single-line row)
    public readonly float Band;
    float _viewport;
    ExtentTable? _table;

    public LyricsMeasuredLayout(float estimate, float band)
    {
        Estimate = MathF.Max(1f, estimate);
        Band = Math.Clamp(band, 0.05f, 0.95f);
    }

    ExtentTable Ensure(int n)
    {
        if (_table is null) _table = new ExtentTable(n, Estimate);
        else if (_table.Count != n) _table.Reset(n, Estimate);
        return _table;
    }

    // Pad so a centered active line can sit at the focal band even at the very top/bottom of the list. Estimate*0.5 is the
    // half-height correction (the band centers a row's MIDDLE); exact enough since the active row is always measured.
    float TopPad => _viewport <= 0f ? 0f : MathF.Max(0f, _viewport * Band - Estimate * 0.5f);
    float BottomPad => _viewport <= 0f ? 0f : MathF.Max(0f, _viewport * (1f - Band) - Estimate * 0.5f);

    public void SetViewport(float mainExtent, float crossSize) => _viewport = MathF.Max(0f, mainExtent);

    public float ContentExtent(int itemCount, float crossSize)
        => itemCount <= 0 ? 0f : TopPad + (float)Ensure(itemCount).Total + BottomPad;

    public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan, out int first, out int last)
    {
        if (itemCount <= 0) { first = last = 0; return; }
        var t = Ensure(itemCount);
        float o = scrollOffset - TopPad;
        first = Math.Max(0, t.IndexAt(MathF.Max(0f, o)) - overscan);
        last = Math.Min(itemCount, t.IndexAt(MathF.Max(0f, o + viewportExtent)) + 1 + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int index, float crossSize)
    {
        float pos = _table?.OffsetOf(index) ?? index * Estimate;
        float ext = _table?.ExtentAt(index) ?? Estimate;
        return new RectF(0f, TopPad + pos, crossSize, ext);
    }

    public void SetMeasured(int index, float mainExtent, float crossSize) => _table?.SetExtent(index, mainExtent);
    public float OffsetOf(int index, float crossSize) => TopPad + (_table?.OffsetOf(index) ?? index * Estimate);
    public int IndexAt(float offset, float crossSize) => _table?.IndexAt(MathF.Max(0f, offset - TopPad)) ?? 0;
}

sealed class LyricLineView : Component
{
    readonly int _index;
    readonly LyricLine _line;
    readonly Signal<int> _emphasis;   // packed per-line emphasis (bucket + past bit) — value-gated by LyricsView
    readonly FloatSignal _nowMs;
    readonly Signal<LyricsFollowMode> _followMode; // stable parent signal; Peek only so a mode flip never fans out row renders
    readonly FloatSignal? _glowFade;   // per-line halo alpha (owned + ramped by LyricsView); bound as the glow wrapper's Opacity
    // The secondary-line mode (LyricsPrefs.None/Translation/Romanization), owned by LyricsView and READ (not frozen)
    // here — see the _secondary field block on LyricsView for why it is a signal and why a whole-document re-render on
    // a toggle is the right fan-out.
    readonly Signal<int> _secondary;
    readonly float _fontSz;
    readonly float _lineHt;
    readonly float _rowPad;
    readonly float _sidePad;
    // NOTE — there is deliberately no _wipeLift field. The per-word rise depends on Motion.ReducedMotion, a mutable
    // global the OS can flip mid-session, and component props FREEZE at mount: rows are mounted once for the whole
    // document (overscan 400) and never remount, so a frozen lift would have kept the pre-flip value until the next
    // track change. Render calls WipeLiftFor(_large) at each GlyphWipe instead — see WipeLiftFor.
    // WHICH SURFACE this row belongs to (immersive fullscreen vs the 340 DIP rail). It is NOT a "centered" flag any
    // more: the frame evidence refutes centred/NoWrap/ellipsised fullscreen lyrics — BOTH surfaces are left-aligned,
    // wrapped and untrimmed, and BOTH anchor their emphasis scale at the left margin (TransformOriginX 0). All that is
    // left of the old distinction is the line-synced halo σ, which scales with the type size.
    readonly bool _large;
    /// <summary>The ink ladder this row paints with, handed down from the view (theme rungs on the rail, on-media
    /// whites on the stage). Safe to freeze at mount even though a theme can flip mid-session: it is a MODE, not a
    /// colour — every rung below resolves its token at the point of consumption. See <see cref="LyricsInk"/>.</summary>
    readonly LyricsInk _ink;
    readonly Action<int, NodeHandle> _reportNode;
    readonly Action<int, NodeHandle> _reportGlow;
    readonly Action<int, NodeHandle> _reportDof;
    readonly Func<int, float> _softnessOf;   // DIP feather → this line's reading-order fraction (LyricsView.SoftnessOfLine)
    readonly Func<int, float> _dofSigmaOf;   // the σ ramp model's LIVE value for this line (LyricsView.DofDeclaredFor)
    readonly Action _onSeek;

    // The packed emphasis this row last rendered with, -1 before its first render. The ONLY thing it exists for is the
    // DIRECTION of the opacity spring (see Render): a row re-renders exactly when its packed emphasis changes, so the
    // previous value is a free, exact proxy for "which way is this row's opacity about to travel?".
    int _prevEmphasis = -1;

    public LyricLineView(int index, LyricLine line, Signal<int> emphasis, FloatSignal nowMs, Signal<LyricsFollowMode> followMode,
        FloatSignal? glowFade, Signal<int> secondary,
        float fontSz, float lineHt, float rowPad, float sidePad, bool large, LyricsInk ink,
        Action<int, NodeHandle> reportNode,
        Action<int, NodeHandle> reportGlow, Action<int, NodeHandle> reportDof, Func<int, float> softnessOf,
        Func<int, float> dofSigmaOf, Action onSeek)
    {
        _index = index; _line = line; _emphasis = emphasis; _nowMs = nowMs;
        _followMode = followMode; _glowFade = glowFade; _secondary = secondary;
        _fontSz = fontSz; _lineHt = lineHt; _rowPad = rowPad; _sidePad = sidePad; _large = large; _ink = ink;
        _reportNode = reportNode; _reportGlow = reportGlow; _reportDof = reportDof; _softnessOf = softnessOf;
        _dofSigmaOf = dofSigmaOf; _onSeek = onSeek;
    }

    // The halo wrapper's opacity: BOUND to the per-line fade signal so a row re-render re-asserts the live fade value
    // (the reconciler skips bound Opacity) — a static value here would snap the halo at exactly the re-render moments
    // (active/voice flips) the fade exists to smooth.
    // The glow's VISIBILITY, damped by the ink seam's bloom weight. A near-white halo on a near-white ground cannot
    // carry what a white halo carries on black, so the light stage damps it rather than pretending the effect
    // transfers. The dark arm's scale is exactly 1, and it takes the original un-wrapped binding — so nothing about the
    // shipped path changes shape.
    Prop<float> GlowOpacity()
    {
        if (_glowFade is not { } s) return 0f;
        float k = _ink.BloomScale;
        return k >= 1f ? (Prop<float>)s : Prop.Of(() => s.Value * k);
    }

    public override Element Render()
    {
        // Read ONLY this line's packed emphasis — a value-gated signal LyricsView rewrites as the active line moves, so
        // reading `.Value` here re-renders the row solely when ITS OWN bucket/past class changes (not on every
        // boundary).
        int e = _emphasis.Value;
        int dist = e & 7;                        // bucket 0..6 (clamped distance from the active line)
        bool past = (e & 16) != 0;               // already sung — rides the dimmer of the two opacity ladders
        bool isActive = dist == 0;               // bucket 0 ⇔ this is the active line
        // Bit 3: this row is the interlude anchor and owes the breathing dots a band of real space above its text (the
        // overlay reserves nothing on its own — it used to draw straight over the retired line and, on a wrapped anchor,
        // over its own lyric). Purely additive top PAD: it changes no ladder value, so the row's colour, scale, opacity
        // and σ are byte-identical with and without it — the only thing that moves is where its text sits.
        float reserve = (e & 8) != 0 ? LyricsView.InterludeReserveDip(_large) : 0f;

        // Emphasis targets. Active line: full focus (scale 1 / opacity 1 / crisp). A row has NO interlude state of its
        // own any more — an instrumental break retires the finished line onto the past ladder and advances focus (see
        // the interlude block in LyricsView), so every row is only ever "active", "past" or "upcoming". In the
        // reference the DISTANCE hierarchy is carried almost entirely by OPACITY + DoF BLUR: scale is a flat,
        // barely-there 0.98 on
        // every inactive row (the old 1 - 0.25*f ramp shrank far rows to 0.75, which the frame evidence refutes). The
        // shrink is LEFT-anchored (TransformOriginX 0 below, on BOTH surfaces), so rows stay flush to the
        // margin instead of breathing about their middle. Voice only drives the karaoke wipe and glow during the lead
        // split, so depth never disagrees with emphasis.
        //
        // Reduced motion flattens scale to 1.0 on every row (read as a VALUE — see the Reduced motion block in
        // LyricsView): the 0.98 breathing is the one purely decorative channel here, since opacity + DoF carry the
        // whole distance hierarchy on their own, and a scale change on every visible row at every handoff is exactly the
        // field-wide motion the preference asks us to drop. `reduce` is folded into `key` below so the spring RETARGETS
        // on the first re-render after an OS-settings flip rather than holding the pre-flip target.
        bool reduce = Motion.ReducedMotion;
        float scale = reduce || isActive ? 1f : 0.98f;
        // Row emphasis follows ACTIVE only — voice keeps the karaoke wipe/glow but must not hold full brightness once
        // focus moves (the lead window used to leave the previous line white for its entire sung tail).
        float opacity = OpacityOf(e);
        // DoF σ comes from LyricsView's ramp model, not from `dist` directly: the model owns the in-flight value and
        // agrees with this ladder at rest (see LyricsView.DofDeclaredFor / DriveDofRamp).
        float blur = _followMode.Peek() == LyricsFollowMode.Following ? _dofSigmaOf(_index) : 0f;

        // AMLL scale in BOTH directions; opacity is critical/no-bounce and DIRECTIONAL.
        // Cold mounts still begin at the element rest targets below, so the soft inactive spring cannot flash a new row.
        var key = DepKey.From(dist, (isActive ? 2 : 0) | (past ? 4 : 0) | (reduce ? 8 : 0));
        var scaleSpring = new SpringParams(100f, 25f, 2f);             // AMLL m=2,d=25,k=100
        // Front-loaded outgoing dim: measured, the exiting line falls 252 → 180 luma inside the first ~100 ms of its
        // flight, while the incoming line brightens across the WHOLE handoff. So the opacity spring is ~3× faster when
        // the row is DIMMING. The component cannot see the slab's live value, so direction is read off the emphasis
        // TRANSITION — this row re-renders exactly when its packed emphasis changes, so "was the previous packed value's
        // opacity higher than this one's?" is that same question, asked where the answer is free. (Both the target and
        // the response are pure functions of `e`, and `key` carries all of `e`, so the retarget and the params change
        // together in the one UseSpring re-arm.)
        int prevPacked = _prevEmphasis;
        _prevEmphasis = e;
        bool dimming = prevPacked >= 0 && opacity < OpacityOf(prevPacked) - 0.0005f;
        var opacitySpring = SpringParams.FromResponse(dimming ? 0.30f : 0.889f, 1.0f);
        UseSpring(AnimChannel.Opacity, opacity, opacitySpring, key);
        UseSpring(AnimChannel.ScaleX, scale, scaleSpring, key);
        UseSpring(AnimChannel.ScaleY, scale, scaleSpring, key);

        Element textEl;

        // The karaoke wipe sub-tree renders on the active line AND the voice line — during the ~140 ms lead the voice line
        // (still being sung) trails the emphasis line, but its fill must keep running. Emphasis (scale/opacity) follows
        // `active`; the wipe split follows true time via _nowMs.
        // Word-by-word line: ALWAYS a two-child ZStack [glow, main], in EVERY state (active / voice / dimmed). The two
        // nodes mount ONCE and only their PROPERTIES toggle, so a line LEAVING the voice slot (the row just above active
        // during the ~140 ms lead) is an in-place update — NOT the BoxEl↔TextEl child-type swap that forced a Remove+Mount,
        // re-shaped the glyph run + missed the blur cache = the one-frame flicker on the lines above active. The main
        // text's glyphs never re-shape on that transition (its string is unchanged), and because both nodes persist,
        // OnRealized (which fires only on mount) keeps the wipe/glow node reports (_reportNode/_reportGlow, read by
        // OnFrame) valid across every transition WITHOUT a remount. (Line-synced lines use the same persistent ZStack below.)
        if (_line.IsWordByWord && _line.Syllables.Count > 0)
        {
            // Karaoke split for THIS line on the true clock: 0 = upcoming (unsung), advancing = being sung, 1 = passed.
            // Apply the SAME small lead the OnFrame driver uses so the reconcile re-render seeds a value consistent with the
            // per-frame writer (kills the ~4% boundary snap-back on the handoff frame — S3-4).
            float split = ComputeSplit(_line, (long)_nowMs.Peek());
            if (split > 0f && split < 1f) split = Math.Clamp(split + WipeLeadFrac, 0f, 1f);
            // The feather is authored in DIP but the engine takes it as a fraction of THIS line's reading-order length,
            // so LyricsView converts per line (and OnFrame re-heals the value if the line was seeded before the viewport
            // had geometry). main and glow MUST share it — see the glow comment below.
            float softness = _softnessOf(_index);
            // MAIN = the readable foreground, and it CARRIES THE WIPE — this is the progressive reveal the user SEES:
            // sung glyphs full-bright Primary (Before), unsung glyphs dim-but-readable (After = Primary @ UnsungAlpha),
            // sitting _wipeLift DIP low and rising as the feather sweeps them. The row group opacity spring (active-only
            // emphasis) dims the whole row once focus moves; this wipe does sung/unsung.
            ColorF sung = _ink.Primary;
            Element main = LineText(_line.Text, sung) with
            {
                Wipe = new GlyphWipe(Before: sung, After: sung with { A = UnsungAlpha },
                    Split: split, Softness: softness, Lift: WipeLiftFor(_large)),
                OnRealized = h => _reportNode(_index, h),
            };
            // GLOW = a soft blurred bloom UNDER the main, on the SUNG glyphs only (After.A = 0 ⇒ unsung glyphs fully
            // transparent). Its glyphs mount once the row is NEAR the focus (dist ≤ 2 — still dim + blurred, so the
            // content swap itself can never pop on the focal row); a peripheral line pays no second glyph run. OnFrame
            // drives its split (same value as main) + its constant σ; its VISIBILITY is the cross-fade wrapper below.
            // Softness AND Lift match the main layer exactly: the halo is the same glyphs at the same geometry, so any
            // disagreement would let the bloom float out from under the crisp text at the boundary.
            bool near = dist <= 2;
            // The halo paints the BLOOM colour rather than the sung ink. On a dark stage the two are the same white,
            // so this is byte-identical to what shipped; on a LIGHT stage blurred near-black glyphs under near-black
            // glyphs would subtract luminance — a smudge, not a halo — so the light arm blooms in the veil instead.
            var bloom = _ink.Bloom;
            Element glowText = (near
                ? LineText(_line.Text, bloom) with
                  {
                      Wipe = new GlyphWipe(Before: bloom, After: bloom with { A = 0f },
                          Split: split, Softness: softness, Lift: WipeLiftFor(_large)),
                  }
                : LineText("", bloom)) with { OnRealized = h => _reportGlow(_index, h) };
            // The wrapper's bound opacity is the per-line glow-fade signal: OnFrame ramps it in over ~240 ms as this line
            // becomes the voice and out as it leaves — the halo never appears/vanishes in one frame (the old handoff pop).
            Element glow = new BoxEl { Opacity = GlowOpacity(), HitTestVisible = false, Children = [glowText] };
            textEl = new BoxEl { ZStack = true, Children = [glow, main] };
        }
        else
        {
            // Line-level lyrics (no syllables ⇒ no karaoke wipe / no held-note bloom). PERSISTENT 2-child ZStack in EVERY
            // state — same shape whether karaoke-live or dimmed — so a line handoff is an in-place property toggle, NOT the
            // BoxEl↔TextEl child-TYPE swap that forced a Remove+Mount (glyph reshape + blur-cache miss + OnRealized re-fire)
            // on the text subtree every activation (L1 / the line-synced handoff flicker). Text color tracks active emphasis;
            // voice-only rows keep the glow cross-fade but recede to Secondary so the eye stays on the rising active line.
            bool lit = isActive;
            bool near = dist <= 2;
            Element glow = new BoxEl
            {
                // Constant σ halo while NEAR the focus (dist ≤ 2); its VISIBILITY is the bound glow-fade signal, ramped by
                // OnFrame at the voice handoff — the halo cross-fades instead of the old one-frame σ 0↔halo + text swap
                // pop. Glyphs + the blur layer mount/step while the row is still dim + blurred (never on the focal row),
                // and a peripheral line pays neither a second glyph run nor a blur layer.
                // TRIMMED for strict parity with the word-by-word bloom above (was 13 large / 9 rail): a line-synced doc
                // has no held-note signal, so this whole-line wash is the softest claim of the two — it comes down by the
                // same ~25% spirit as HeldGlowPeakScale + the retuned baseSigma.
                // σ ON THE ACTIVE ROW ONLY (dist == 0), NOT on every near row: at dist ≥ 1 the parent dofContent already
                // carries a depth-of-field σ (LyricsFx.DofSigma), so a σ here would be a blur layer NESTED inside a blur
                // layer — and a nested PushLayer is exactly what BlurPinKey.TryCompute bails on, making dofContent
                // uncacheable for every near row. At dist == 0 the DoF σ is 0, so the halo is never nested and both
                // layers stay pin-eligible. The glyph mount below stays on `near` — the anti-pop contract is unchanged.
                Blur = isActive ? (_large ? 10f : 7f) : 0f,
                // Scroll motion translates this stationary glyph subtree every frame. Reuse its retained blur when it
                // exists; otherwise render crisp for the moving frame and rebuild the full halo after settling.
                BlurCachePolicy = BlurCachePolicy.HoldIfCached,
                Opacity = GlowOpacity(),
                HitTestVisible = false,
                Children = [LineText(near ? _line.Text : "", _ink.Bloom with { A = 0.4f })],
            };
            Element main = LineText(_line.Text, lit ? _ink.Primary : _ink.Secondary) with
            {
                // ~150 ms brush cross-fade so the Primary↔Secondary color flip at the handoff never snaps in one frame.
                BrushTransitionMs = WaveeMotion.Fast,
                OnRealized = h => _reportNode(_index, h),
            };
            textEl = new BoxEl { ZStack = true, Children = [glow, main] };
        }

        // ── The SECONDARY line (translation / romanization) ──────────────────────────────────────────────────────────
        // Read from the shared mode signal — never a frozen ctor int (component props freeze at mount, so a toggle would
        // never reach a mounted row). A line the source did not translate/romanize simply renders none, which is why a
        // partially-covered document is fine and why the mode is not gated on per-line data.
        string? secondaryText = _secondary.Value switch
        {
            LyricsPrefs.Translation => NonEmpty(_line.Translation),
            LyricsPrefs.Romanization => NonEmpty(_line.Romanization),
            _ => null,
        };

        // Own DoF on a persistent INNER content wrapper, separate from the outer scale/opacity track owner. Ancestor
        // scale still composes normally (the text should scale); this separation removes the row-padding blur layer and
        // lets LyricsView suppress/restore static σ by direct node write without touching the line component.
        Element dofContent = new BoxEl
        {
            Direction = 1,
            // The σ ramp model's live value (LyricsView owns the in-flight σ by direct node write): declaring the REST
            // target here instead would stomp an in-flight incoming de-blur back to crisp on the very re-render that
            // starts it. At settle the two agree, exactly like the scale/opacity springs' rest targets below.
            Blur = blur,
            // The depth-of-field bands are static at rest but scroll with the lyrics viewport. Holding a compatible
            // cached blur avoids a fresh Gaussian for every scroll submit; a cache miss stays crisp until the viewport
            // settles, when the normal recorder path refreshes it.
            BlurCachePolicy = BlurCachePolicy.HoldIfCached,
            OnRealized = h => _reportDof(_index, h),
            // INSIDE dofContent, deliberately: the secondary line must inherit EVERYTHING the lyric it belongs to gets —
            // this wrapper's depth-of-field σ, the cascade's compensating translate (LyricsView.WriteCascade targets this
            // very node), and, through the row host above, the emphasis opacity + left-anchored scale springs. A sibling
            // of dofContent would sit crisp and unmoved beside a blurred, travelling lyric.
            Children = secondaryText is null ? [textEl] : [textEl, SecondaryText(secondaryText)],
        };

        return new BoxEl
        {
            Direction = 1,
            // Element-side values are the scale/opacity springs' REST targets. The slab owns in-flight values; at settle
            // both agree, so a later emphasis render cannot snap the row or flash a newly realised overscan item.
            ScaleX = scale,
            ScaleY = scale,
            Opacity = opacity,   // element rest target — reconciler re-asserts the dim value (not 1), not bound (no mount flash)
            // No fixed Height — the row sizes to its text (1 line short, 2 lines tall); the measured layout reads that
            // natural height so there is no dead space. Vertical padding (_rowPad) is the inter-line gap.
            Shrink = 0f,
            Padding = new Edges4(_sidePad, _rowPad + reserve, _sidePad, _rowPad),
            Justify = FlexJustify.Center,
            AlignItems = FlexAlign.Stretch,
            // LEFT-anchored emphasis scale on BOTH surfaces: rows breathe about their leading margin instead of their
            // middle, so the left edge of the column is rock-solid as the active line grows (the reference behaviour).
            TransformOriginX = 0f,
            TransformOriginY = 0.5f,
            Cursor = CursorId.Hand,
            OnClick = _onSeek,
            Role = AutomationRole.Button,
            Focusable = true,
            AllowFocusOnInteraction = false,
            Children = [dofContent],
        };

        // BOTH surfaces: wrapped, unbounded line count, no ellipsis. A lyric line is never allowed to be truncated —
        // "lif.." is unreadable — and the fullscreen single-line/ellipsis arm the old `_centered` flag selected is
        // exactly what the reference capture refutes (its wrapped rows fill row-by-row with a vertical boundary).
        // LyricsView.MeasureRunLength rebuilds this style verbatim; the two must be changed together.
        TextEl LineText(string text, ColorF color) => new(text)
        {
            Size = _fontSz,
            Weight = 700,
            Wrap = TextWrap.Wrap,
            LineHeight = _lineHt,
            Color = color,
            MaxLines = 0,
            Trim = TextTrim.None,
        };

        // The secondary layer, as Apple renders it: a smaller, lighter, DIMMER line directly under the lyric, sharing
        // its left margin and its wrap box. Deliberately plain —
        //   • NO GlyphWipe: the karaoke reveal is the record of what is being SUNG, and a translation is not sung. A
        //     wiped second line would also double the gradient-run count on the one line that is mid-sweep, which is
        //     exactly the budget Wave A's settled-split fast path exists to protect.
        //   • NO glow layer and NO Lift: both are per-word texture belonging to the sung glyphs.
        // Everything else it needs (blur, opacity, scale, cascade) it inherits from dofContent and the row host, so this
        // is a leaf TextEl and nothing more.
        TextEl SecondaryText(string text) => new(text)
        {
            Size = _fontSz * SecondaryFontRatio,
            Weight = 600,
            Wrap = TextWrap.Wrap,
            LineHeight = _lineHt * SecondaryFontRatio,
            Color = _ink.Secondary,
            MaxLines = 0,
            Trim = TextTrim.None,
            // The one piece of spacing: a hair of air under the lyric so the two read as a pair, not as one run-on
            // block. Top margin only — the row's own _rowPad still owns the gap to the NEXT line.
            Margin = new Edges4(0f, SecondaryGapDip, 0f, 0f),
        };
    }

    // ── Secondary line (translation / romanization) ──────────────────────────────────────────────────────────────────
    // 0.62x the lyric type — the same source-px→DIP ratio the rest of this campaign's constants are measured at, and it
    // lands on ≈16 DIP under the rail's 26 and ≈22 under the immersive 36: clearly subordinate, still comfortably
    // readable at the focal band. The line height rides the same ratio so the pair keeps the lyric's 1.27x rhythm.
    internal const float SecondaryFontRatio = 0.62f;
    internal const float SecondaryGapDip = 3f;

    static string? NonEmpty(string? s) => s is { Length: > 0 } ? s : null;

    // ── Emphasis ladder ──────────────────────────────────────────────────────────────────────────────────────────────
    // The row-group opacity for a PACKED emphasis value — the one place the ladder lives, so the direction test above can
    // ask it the same question about the previous packed value. A switch, not a table: no allocation, no indirection.
    //
    // Measured off the reference capture: peak luma per ring is 252 (active) / 133 / 113 / 101 / 89 / 72, and a line the
    // song has ALREADY passed settles DIMMER than an upcoming line the same distance away (118 vs 133) — the past/future
    // asymmetry the old single `max(0.16, 0.55*(1-f))` ramp could not express. The past rows sit far lower in ALPHA than
    // that luma gap suggests because their TEXT is the sung white (Primary at full alpha behind a settled wipe) where an
    // upcoming row's is the unsung gray: the same row alpha would read markedly brighter on a past line.
    //
    // Bit 3 of the packed value is unused (it carried the deleted interlude recede — a flat 0.55 that was neither
    // ladder's rung and read as a half-lit, still-selected line). A sung-out line in an instrumental break is now just
    // a PAST line here, on the past ladder, exactly like every other line the song has gone by.
    internal static float OpacityOf(int packed)
    {
        int dist = packed & 7;
        if (dist == 0) return 1f;                         // active line: full focus
        return (packed & 16) != 0
            ? dist switch { 1 => 0.19f, 2 => 0.13f, _ => 0.10f }                                 // past (sung, white-base)
            : dist switch { 1 => 0.45f, 2 => 0.24f, 3 => 0.14f, 4 => 0.11f, _ => 0.10f };        // future (unsung, gray-base)
    }

    // ── Wipe texture ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Every constant below is measured off the frame-by-frame Apple Music reference capture (lyrics-parity campaign,
    // 2026-08-03; the video is 480 px wide at cap-height ≈30 px, so source px × 0.62 ⇒ rail DIP at font 26).

    // Small POSITIVE wipe lead: nudge the bright boundary a hair ahead of the strictly-played fraction so the edge reads
    // as anticipating the voice. Shared by the element seed (LyricLineView.Render) and the per-frame driver (OnFrame) so
    // the reconcile re-render and the OnFrame writer agree on the boundary (no snap-back — S3-4).
    //
    // 2%, HALVED from the 0.04 that shipped alongside the old Softness-0.14 wash. A 4-9x too-wide feather HID a 4% lead
    // inside its own gradient; against the narrow 5 DIP band the reference actually shows, the same 4% put the bright
    // edge visibly ahead of the syllable being sung — eager, whereas in the capture the boundary TRACKS the voice. The
    // TIME-domain anticipation is a separate knob and is deliberately untouched: LeadMs = 140 (emphasis + scroll only).
    internal const float WipeLeadFrac = 0.02f;

    // Unsung glyph alpha (GlyphWipe.After). Reference: crossed text holds 250-255 luma while unsung sits at 183-192 over
    // a ~90 background ⇒ α ≈ 0.59 of the sung white. The 0.45 shipped before read a whole step too dim/too far away.
    internal const float UnsungAlpha = 0.58f;

    // Feather width of the sung/unsung boundary, IN DIP. Reference: a narrow 5-12 source-px band ≈ 2-3% of the run width
    // — the fixed Softness 0.14 shipped before was 4-9× too wide (a mushy word-sized wash, not a defined edge). DIP is
    // the authored unit because GlyphWipe.Softness is a FRACTION of the run's READING-ORDER length, so the same on-screen
    // band is a different fraction on every line; LyricsView.SoftnessOfLine does the per-line division.
    internal const float WipeSoftnessDip = 5f;
    internal const float WipeSoftnessDipLarge = 7f;
    internal static float WipeSoftnessDipFor(bool large) => large ? WipeSoftnessDipLarge : WipeSoftnessDip;
    // Clamps on the converted fraction: below 0.01 the band collapses to a hard per-pixel cut on a long line; above 0.10
    // a very short line would be back in the refuted wash.
    internal const float WipeSoftnessMin = 0.01f;
    internal const float WipeSoftnessMax = 0.10f;

    // Per-word vertical rise (GlyphWipe.Lift — dy only, no scale, since the engine's char pop was refuted and removed).
    // Reference: unsung words sit ~2 source px low at cap-height 30 px ⇒ ≈0.048 em ⇒ 1.25 DIP at font 26.
    internal const float WipeLiftDip = 1.25f;
    internal const float WipeLiftDipLarge = 1.75f;
    // Reduced motion zeroes the lift. Render calls this at BOTH GlyphWipe construction sites (main + glow) rather than
    // freezing one value into the row: they are two reads of the same static bool in one straight-line block, so they
    // cannot disagree (and they must not, or the bloom floats out from under the text), while a row that mounted before
    // the OS flipped the preference still picks the new value up on its next render. Freezing it as a ctor prop was the
    // bug — rows mount once per document and never remount, so the flip could not reach them until a track change.
    // The per-word rise is pure decoration: the sung/unsung colour split alone says which words have been sung. Read as
    // a VALUE, never an early-return — see the Reduced motion block in LyricsView.
    internal static float WipeLiftFor(bool large) => Motion.ReducedMotion ? 0f : large ? WipeLiftDipLarge : WipeLiftDip;

    internal static float ComputeSplit(LyricLine line, long now)
    {
        var syl = line.Syllables;
        int total = 0;
        for (int i = 0; i < syl.Count; i++) total += Math.Max(1, syl[i].Text.Length);
        if (total == 0) return 0f;
        float played = 0f;
        for (int i = 0; i < syl.Count; i++)
        {
            int len = Math.Max(1, syl[i].Text.Length);
            long s = syl[i].StartMs, e = syl[i].EndMs;
            if (now >= e) { played += len; continue; }
            if (now >= s) played += len * Math.Clamp((float)(now - s) / Math.Max(1L, e - s), 0f, 1f);
            break;
        }
        return Math.Clamp(played / total, 0f, 1f);
    }

}

/// <summary>Cross-surface LYRICS preferences: the epoch every lyrics surface re-reads its persisted flags under (the
/// <c>PlayerBarPrefs</c> / <c>PlaybackPrefs</c> idiom — three writers, one signal), plus the secondary-line CAPABILITY
/// of the document currently on screen.
///
/// <para>The mode itself is NOT cached here: it lives in <see cref="WaveeSettings.LyricsSecondaryLine"/> and every
/// consumer reads it under <see cref="Epoch"/>, exactly like <c>PlayerBarShowRemaining</c>. <see cref="LyricsView"/>
/// then republishes that one read into a single per-view signal its rows subscribe to — a per-ROW settings read would
/// be one registry hit per line per render.</para>
///
/// <para><see cref="Available"/> is published by <see cref="LyricsView"/> (once per document, in
/// <c>PrepareDocument</c>) and read by the rail header + the immersive top bar: the toggle is not rendered at all for a
/// document with neither layer, and it CYCLES only through the layers that document actually has.</para></summary>
static class LyricsPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

    // The three states of WaveeSettings.LyricsSecondaryLine.
    public const int None = 0;
    public const int Translation = 1;
    public const int Romanization = 2;

    // …and the two capability bits of Available (deliberately 1 << (mode - 1), so BitFor below is arithmetic, not a map).
    public const int HasTranslation = 1;
    public const int HasRomanization = 2;

    /// <summary>Which secondary layers the document on screen carries (bit set of <see cref="HasTranslation"/> /
    /// <see cref="HasRomanization"/>). 0 ⇒ no toggle is offered. Both lyrics surfaces prepare the SAME document, so
    /// their writes agree and <c>Signal&lt;T&gt;</c> coalesces the second one.</summary>
    public static readonly Signal<int> Available = new(0);

    /// <summary>Coerce a persisted/hand-edited value into a real mode — a stray 7 must show the original, not nothing.</summary>
    public static int Clamp(int mode) => (uint)mode <= Romanization ? mode : None;

    public static int BitFor(int mode) => mode is Translation or Romanization ? 1 << (mode - 1) : 0;

    /// <summary>The next state of the cycling header toggle: none → translation → romanization → none, SKIPPING any
    /// layer this document does not have. "None" is always reachable, so the cycle can never trap the user in a layer.</summary>
    public static int Next(int mode, int available)
    {
        for (int step = 1; step <= 3; step++)
        {
            int next = (Clamp(mode) + step) % 3;
            if (next == None || (available & BitFor(next)) != 0) return next;
        }
        return None;
    }

    /// <summary>The toggle's tooltip: it names the state the view is in NOW (a cycling control whose tooltip named its
    /// next state would read as a lie the moment the user hovered it after clicking).</summary>
    public static string Tooltip(int mode) => Clamp(mode) switch
    {
        Translation => Loc.Get(Strings.Player.LyricsSecondaryTranslation),
        Romanization => Loc.Get(Strings.Player.LyricsSecondaryRomanization),
        _ => Loc.Get(Strings.Player.LyricsSecondaryOff),
    };

    /// <summary>The ONE writer both the Settings picker and the two header toggles go through: persist, then bump so
    /// every mounted lyrics surface re-reads it on the same frame.</summary>
    public static void Set(IAppSettings? settings, int mode)
    {
        settings?.Set(WaveeSettings.LyricsSecondaryLine, Clamp(mode));
        Bump();
    }
}

sealed class LyricsUpgradeObserver(Action<LyricsDocument> onNext) : IObserver<LyricsDocument>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(LyricsDocument value) => onNext(value);
}

sealed class LyricsTicker : Component
{
    public required LyricsView Owner;

    public override Element Render()
    {
        var bridge = UseContextSignal(PlaybackBridge.Slot);

        // Once per mount (re-keyed on a track change): reset scroll-snap + mark this the probe-active instance.
        UseEffect(() =>
        {
            Owner.ResetScrollSnap();
            LyricsView.ProbeActive = Owner;   // probe hook (harmless otherwise): the live instance the advance-probe drives
        }, DepKey.Empty);

        var b = bridge.Value;                                  // subscribe → re-render when the bridge arrives
        bool playing = b is not null && b.IsPlaying.Value;     // subscribe IsPlaying → re-gate the interval on play/pause
        var followMode = Owner.FollowModeValue;                 // isolated subscription: never re-renders LyricsView/rows
        bool cascading = Owner.CascadeRunningValue;             // ditto — flips twice per handoff, never per frame

        // Play-start edge → one immediate advance (matches the old dueTime:0 ticker); paused → subscribe PositionMs so a
        // scrub while paused re-wipes to the new spot. Re-runs on any bridge/IsPlaying change.
        UseSignalEffect(() =>
        {
            var bb = bridge.Value;
            if (bb is null) return;
            if (bb.IsPlaying.Value)
            {
                if (!LyricsView.ProbeSyncMode) Owner.OnFrame();
            }
            else
            {
                _ = bb.PositionMs.Value;
                Owner.OnFrame(forceVisual: true);
            }
        });

        // While anything is in motion, mount the per-frame stepper (the SeekTicker idiom) so OnFrame runs ONCE PER
        // PRODUCED FRAME at the panel's rate. It replaces a 16 ms UseInterval, which paced the motion off a wall clock
        // that neither divides the refresh nor tells the host to keep producing — see LyricsView.WipeIntervalMs.
        // Mounting it CONDITIONALLY (rather than gating inside it) is what lets the loop idle again: an unmounted
        // stepper is not a FrameClock.Tick subscriber, so it contributes no wake reason at all.
        // Detached/resync work must continue while playback is paused (countdown + programmatic settle), and so must an
        // in-flight handoff cascade — a pause landing inside the ~0.48 s flight would otherwise freeze every line at its
        // compensated position. Following + paused + settled remains completely quiescent and still wakes only for
        // PositionMs changes through the effect above. ProbeSyncMode drives OnFrame synchronously (ProbeStep), so the
        // stepper stays unmounted under the probe.
        bool needsTicks = playing || cascading || followMode != LyricsFollowMode.Following;
        Element? stepper = needsTicks && !LyricsView.ProbeSyncMode
            ? Embed.Comp(() => new LyricsFrameStepper { Owner = Owner })
            : null;
        return new BoxEl
        {
            HitTestVisible = false, Width = 0f, Height = 0f,
            Children = stepper is null ? [] : [stepper],
        };
    }
}

/// <summary>Per-frame stepper for <see cref="LyricsView"/> (the <c>SeekTicker</c> idiom): mounted only while the
/// surface actually has motion in flight, it subscribes to the host frame clock and runs one <c>OnFrame</c> per
/// produced frame, so the karaoke wipe, the σ ramp and the handoff cascade all advance at the panel's rate instead of
/// at a wall-clock interval's. Its subscription is also the request FOR those frames
/// (<c>WakeReasons.FrameClockPoller</c>); unmounting it lets the loop idle again. It NEVER re-renders the owner — the
/// step writes scene side-tables and a handful of signals the rows bind.</summary>
sealed class LyricsFrameStepper : Component
{
    public required LyricsView Owner;

    public override Element Render()
    {
        var tick = UseContextSignal(FrameClock.Tick);
        UseSignalEffect(() =>
        {
            _ = tick.Value;
            Owner.OnFrame();
        });
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
