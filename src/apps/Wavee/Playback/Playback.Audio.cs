// ── Playback/Playback.Audio.cs ─────────────────────────────────────────────────────────────────────────────────────
// pump over FluentGpu.Media, AudioSource ×5, gapless/crossfade; SilentSink for --fake (ch 31, +50)
//
// Role: SHELL
// Owner: H
// Wave: 3
// Budget: 4,250 lines (2250 + 220 for the FLAC adapter + 510 for the Vorbis adapter over the CORE decoder, its pure
//         granule clock, `IRandomAccessBytes` and `RingSource` — Vorbis plan §6.1 estimated +260; over it because the
//         clock and the landing peek are their own public type, so the frame accounting, the gapless trim and the seek
//         hand-off are unit facts rather than prose, and about a third of the rest is doc comments — + 30 for the
//         connected silent session, + 550 for headless plan §3.5: `Metrics`, the paced silent endpoint, the silent
//         transport and the seek interrupt, + 680 for gap batch B4: the starve rule, cancellable loads and prepares, the
//         backend boot off the UI thread, the exact join, the decoder pool, the podcast arm and §22's reducer signals)
// Spec: plan §4.9 + ch 31 §9.4 + FLAC plan §4 + Vorbis plan §5.5, §6 + headless plan §3.5 + gap register §2.5 (B4)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS FILE IS. The audio pump: one serialized queue of load work, one long-lived WASAPI session, a decoder per
// format over the engine's `IAudioDecoder` seam, the gapless/crossfade hand-off, the level tap, the output-device
// roster, and the host API the reducer's `Execute` calls. Nothing here writes a table or a signal that the reducer
// owns: every result goes back as a posted `Input` stamped with the epoch it was started for (C1/C4).
//
// THE FIVE RULES THAT MAY NOT BE SIMPLIFIED, each of which is a shipped bug the current shape fixed:
//
//   1. ONE SERIALIZED CHAIN, AND AN EPOCH ON EVERYTHING. Ten `Next` clicks are ten reducer Steps and ONE Load effect
//      (C3), but the ten CDN opens are this file's problem: they run in order on one chain, each carrying the epoch
//      it was started for, and a superseded one disposes its product at the next epoch check instead of publishing
//      it. `_clockStale` is the companion guard: the engine's position keeps ticking the OUTGOING track until the
//      new session opens, so every reader in that window would report the previous track's position for the track
//      that is starting (observed: a track restarting at 0:00 announced at 190,488 ms).
//
//   2. A ZERO-BYTE READ IS PERMANENT EOF TO EVERY CODEC. That is why the byte seam (§9a `RingSource`, §16
//      `Prefetching`) is a bounded WAIT and not a short read, and why a wait that runs out is `Starved` and the reader
//      waits AGAIN (D5): a slow-but-alive CDN must never silently end a track. The pump owns the budget — "Reconnecting"
//      after 1.5 s, `Fault.Network` after 90 s (`StarvePolicy`) — and a Stop or a superseding Load closes the bytes, which
//      is the only thing that ends a starving read. The 90 s is for a link that is SLOW; a mirror set that refused every
//      range has answered, and waiting out an answer is the one thing that cannot work, so it fails after 6 s instead.
//
//   3. NEVER ROUTE A 0 ms CROSSFADE THROUGH THE CROSSFADE PATH. `GainEnvelope.Fade(…, 0 frames)` folds to
//      `Constant`, which is two voices at unity for the whole tail — not a butt-join. `EffectiveFadeMs` is the ONLY
//      selector between the two hand-offs, and 0 means gapless.
//
//   4. EVERY JOIN FRAME IS "CLOCK NOW + FRAMES STILL TO PLAY", NEVER "FRAMES FROM TRACK START". A seek rebases the
//      position clock but not the sample clock, and a device-format reload builds a NEW session at clock 0 — so a
//      track-absolute join frame computed once at open scheduled the join hundreds of seconds into the future.
//      `Playback.GaplessJoinClock` (G's, CORE) is the one place that arithmetic lives.
//
//   5. A PRIMED VOICE IS ONLY SPLICEABLE INTO A MIXER RUNNING AT THE RATE IT WAS RESAMPLED FOR. Every splice site
//      re-checks `GaplessJoinClock.PrimedSlotMatches`; a 48 kHz prime joined into a 44.1 kHz session played at ~92 %
//      speed for the rest of the track.
//
// BIT DEPTH AND RATE, ONCE (FLAC plan §4.1). A decoder's output is `float32` at the SOURCE rate; 16-bit and 24-bit
// differ only in the scale constant. The device opens ONCE at its own rate and is never reopened for a codec-rate
// change, so 44.1 → 48 kHz is a linear resample at the decode edge — the engine's existing floor for Vorbis too, and
// the one place lossless is not lossless on the way out. The honest badge is therefore the SOURCE format
// ("FLAC 24/44.1"), never a claim about the output.
//
// Rules: threads and async are allowed (SHELL), but this file never writes a table and never writes a signal the
// reducer owns; the pump is a named 200 ms timer and the position sample 1 s (P10); no unbounded queue (C8).

using System.Collections.Concurrent;
using System.Text;
using System.Threading;

using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Signals;
using FluentGpu.Windows.Wasapi;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The audio pump. One session, one chain, one ticker.</summary>
    public static partial class Audio
    {
        // ── 1. constants, every one of them a decision ───────────────────────────────────────────────────────────────

        /// <summary>The ONE audio timer (P10). Everything the pump decides — the arm snapshot, the commit, the join
        /// going live, the hand-off close, the xrun drain, the fault poll — is decided here, so there is no second
        /// clock to disagree with.</summary>
        const int TickMs = 200;

        /// <summary>The position sample the reducer folds (P10). Deliberately coarser than the pump: a scrub bar
        /// interpolates from `PosMs + (now − PosQpc)` and only needs the truth once a second.</summary>
        const int PositionSampleMs = 1_000;

        /// <summary>A 0 ms hand-off commits inside the last 1.5 s — several ticks before the boundary, so a missed
        /// tick still leaves room.</summary>
        const int GaplessCommitLeadMs = 1_500;

        /// <summary>`Ended` is HELD for up to ~4 s while a prepare is still filling. A degraded join beats a hard
        /// cut, and 20 ticks is the longest a listener tolerates before the silence reads as a bug instead.</summary>
        const int EndedHoldMaxTicks = 20;

        /// <summary>The crossfade ceiling, in two places and the same number in both (the settings side migrates a
        /// persisted value beyond it once, because older builds offered 30 s).</summary>
        public const int MaxCrossfadeMs = 12_000;

        /// <summary>How long before the end the arm snapshot is taken. `max(fade, 2 s)`: a 0 ms join still needs the
        /// diagnostic line, and the line is what tells a log reader WHY a boundary was a hard cut.</summary>
        public static int ArmLeadMs(int fadeMs) => Math.Max(fadeMs, 2_000);

        /// <summary>The window in which an unprepared endgame is worth nudging the reducer about — once per track. A
        /// track SHORTER than the margin is its own window: a 6 s interlude must ask on its first tick or never.</summary>
        public static long EndingSoonMs(int fadeMs, long durMs)
        {
            long margin = fadeMs + 8_000L;
            return durMs > 0 && durMs < margin ? durMs : margin;
        }

        /// <summary>Which hand-off a boundary gets, if any. The ONLY selector between the two is the effective fade
        /// (rule 3): a 0 ms "crossfade" routed through the crossfade path folds to two voices at unity for the whole
        /// tail, which is not a butt-join. Pure, so every row of the table is one assertion.</summary>
        public enum HandOff : byte
        {
            /// <summary>Not yet, or nothing to hand over: the boundary will be whatever the reducer does next.</summary>
            None,
            /// <summary>Arm B at A's natural-end frame. A is never faded and never truncated.</summary>
            Gapless,
            /// <summary>Fade A out and B in over the same frames, one equal-power curve.</summary>
            Crossfade,
        }

        /// <inheritdoc cref="HandOff"/>
        public static HandOff HandOffAt(long positionMs, long durationMs, int fadeMs, bool prepared, bool overlapAllowed,
            bool handOffInFlight)
        {
            if (handOffInFlight || !prepared || !overlapAllowed || durationMs <= 0) return HandOff.None;
            if (fadeMs > 0) return positionMs >= durationMs - fadeMs ? HandOff.Crossfade : HandOff.None;
            return positionMs >= durationMs - GaplessCommitLeadMs ? HandOff.Gapless : HandOff.None;
        }

        /// <summary>The crossfade's effective length, clamped once and in one place. A persisted value beyond the
        /// ceiling (older builds offered 30 s) is honoured as the ceiling rather than refused.</summary>
        public static int EffectiveFadeMsFor(bool enabled, int requestedMs)
        {
            int clamped = Math.Clamp(requestedMs, 0, MaxCrossfadeMs);
            return enabled && clamped > 0 ? clamped : 0;
        }

        /// <summary>The engine's own per-voice normalization, ALWAYS off (D6, G-104). The decoders own the gain — the
        /// catalogue's or the Ogg header's track gain, capped by the track peak, folded into their sample conversion
        /// (<see cref="NormalizationFactor"/>). The engine's <c>NormMode.Track</c> over a voice with no ReplayGain tags is
        /// <c>ScalarLinear(default, Track, −14)</c> = +4 dB on EVERY voice on top of that, which is how every track played
        /// about 4 dB too loud. The user's normalization setting gates the decoders' factor, never this.</summary>
        public const NormMode EngineNormalization = NormMode.Off;

        /// <summary>What a starving read means to the pump (D5): nothing while bytes flow or the wait is short, the
        /// "Reconnecting" band once it has waited <see cref="StallReportMs"/>, and a network fault — never the end of the
        /// track — once it has waited <see cref="StallFailMs"/>. PURE; the tick folds it once per 200 ms.
        ///
        /// <para>D5's patience has ONE fact it never had: whether the bytes are late or REFUSED. Ninety seconds is the
        /// right budget for a link that is slow or flapping, because bytes are coming — just not yet. It is the wrong
        /// budget for a mirror set that answered a non-2xx to every range, because that is an answer, and an answer does
        /// not change by being waited on. A refused body therefore fails fast (<see cref="RefusedFailMs"/>) where a slow
        /// one keeps the full 90 s.</para></summary>
        public static class StarvePolicy
        {
            /// <summary>Longer than a far seek's probe on a healthy link, shorter than a listener's patience.</summary>
            public const int StallReportMs = 1_500;

            /// <summary>0.2.9's blocking read budget.</summary>
            public const int StallFailMs = 90_000;

            /// <summary>And the budget for a mirror set that REFUSED: one re-resolve cycle
            /// (<see cref="Spotify.Audio.Body.ResolveBackoffMs"/>) plus its round trip. That is exactly long enough for
            /// the body to have asked storage-resolve again and been handed a FRESH url set — if the stall survives
            /// that, the new urls refused too and eighty-four more seconds of silence buy nothing.</summary>
            public const int RefusedFailMs = Spotify.Audio.Body.ResolveBackoffMs + 1_000;

            public enum Verdict : byte { Flowing, Recovering, Failed }

            /// <summary>The verdict for one read's wait. PURE.</summary>
            /// <param name="stallMs">How long the live body's read has waited without a byte.</param>
            /// <param name="refused">The live body's mirrors are refusing RIGHT NOW
            /// (<see cref="Spotify.Audio.Body.Refusing"/>) rather than merely being slow.</param>
            public static Verdict Decide(long stallMs, bool refused = false)
                => stallMs >= (refused ? RefusedFailMs : StallFailMs) ? Verdict.Failed
                 : stallMs >= StallReportMs ? Verdict.Recovering
                 : Verdict.Flowing;
        }

        /// <summary>A superseding-token holder (G-117): <see cref="Next"/> cancels the work started for the previous token
        /// and answers a fresh one, so ten loads leave nine cancelled and one live. Cancellation is REQUESTED at once and
        /// its callbacks (a socket, a byte source's close) run off the caller's thread — the UI thread calls this.</summary>
        public sealed class Supersede
        {
            readonly Lock _gate = new();
            CancellationTokenSource _live = new();

            /// <summary>Cancel what the last token started, and answer the token the next piece of work runs under.</summary>
            public CancellationToken Next()
            {
                CancellationTokenSource old, fresh = new();
                lock (_gate) { old = _live; _live = fresh; }
                _ = old.CancelAsync();
                return fresh.Token;
            }

            /// <summary>Cancel what the last token started; the next <see cref="Next"/> starts clean.</summary>
            public void Cancel() => Next();
        }

        const int WorkLogPeriodMs = 30_000;

        // ── 2. published state (ch 20 §7, ch 21 §7) ──────────────────────────────────────────────────────────────────

        /// <summary>The output endpoints the picker offers. See <see cref="EnumerateEndpoints"/> for why this is one
        /// row on a stock build.</summary>
        public static readonly Signal<LocalAudioDevice[]> Devices = new([]);

        /// <summary>The explicitly chosen endpoint id, or null for "follow the system default".</summary>
        public static readonly Signal<string?> SelectedOutputId = new(null);

        /// <summary>Can this process play audio locally at all? False when the WASAPI leaf never opened — a Connect
        /// viewer session, a machine with no render endpoint, a `--fake` run.</summary>
        public static readonly Signal<bool> Supported = new(false);

        /// <summary>The local output's mute, separate from volume: the player bar's speaker glyph reads it and a
        /// muted session still reports its real volume.</summary>
        public static readonly Signal<bool> Muted = new(false);

        /// <summary>The level meter the deck faces' VU reads. DEMAND-GATED: the tap does no work until somebody holds
        /// a lease from <see cref="AcquireLevels"/>, so a closed rail costs nothing.</summary>
        public static IReadSignal<VisualizerFrame> Levels => s_effects.Visualizer;

        /// <summary>Take a level-meter lease. The caller disposes it; the tap goes quiet again when the last lease
        /// does.</summary>
        public static IDisposable AcquireLevels() => s_effects.AcquireVisualizer();

        /// <summary>Take a SPECTRUM lease (the engine's FFT tier; it implies the level tap). Dispose to release — the RT
        /// stops filling the ring and the clock thread stops analysing the moment the last lease goes.</summary>
        public static IDisposable AcquireSpectrum() => s_effects.AcquireSpectrum();

        /// <summary>Copy the latest band magnitudes (dB) tear-free. 0 ⇒ nothing published yet (no lease, not playing,
        /// or the window is not yet latency-aligned).</summary>
        public static int CopySpectrum(Span<float> into, out SpectrumInfo info) => s_effects.CopySpectrum(into, out info);

        /// <summary>Mirror of <c>Prefs.Stage.SyncOffsetMs</c> onto the analysis window (positive = read earlier).</summary>
        public static void SetSpectrumOffsetMs(float ms) => s_effects.SpectrumOffsetMs = ms;

        /// <summary>Diagnostics: the live session's endpoint queue depth (frames, diagnostic only — §2.7) and spectrum publish
        /// count; (0, 0) without one.</summary>
        public static (long DelayFrames, long Publishes) SpectrumDiagnostics()
        {
            lock (s_gate) { return s_session is { } s ? (s.OutputDelayFrames, s.SpectrumPublishes) : (0L, 0L); }
        }

        /// <summary>One local render endpoint, as the picker renders it.</summary>
        /// <param name="Id">The endpoint id, or "" for the system default row.</param>
        /// <param name="Name">The short display name — `Playback.AudioDeviceNaming.Shorten`'s answer.</param>
        /// <param name="Kind">The form factor, folded to the picker's glyph vocabulary.</param>
        public readonly record struct LocalAudioDevice(string Id, string Name, byte Kind, bool IsDefault);

        public static LocalAudioDevice? CurrentEndpoint
        {
            get
            {
                lock (s_gate)
                {
                    if (s_session?.Sink is not WasapiAudioDevice { EndpointInfo: { } endpoint }) return null;
                    const byte type = 0; // Form factor does not establish transport (a speaker may be Bluetooth).
                    return new LocalAudioDevice(endpoint.Id, endpoint.Name, type, true);
                }
            }
        }


        /// <summary>How the endpoint roster is read. A SEAM with a one-row default, and the reason is a verified
        /// engine fact rather than laziness: the engine's WASAPI leaf opens `GetDefaultAudioEndpoint` and FOLLOWS the
        /// default — there is no per-endpoint open anywhere in it, so a picker that let the user choose an endpoint
        /// could not honour the choice. 0.2.9 shipped exactly this shape for exactly this reason
        /// (`OutputDeviceRouter` decides, `SetOutputDevice` was a documented no-op). Per-endpoint output is an engine
        /// change (`WasapiAudioDevice` must accept a device id); when it lands, this seam is where the real
        /// enumeration attaches and `SelectAsync` starts routing instead of only persisting.</summary>
        public static Func<LocalAudioDevice[]>? EnumerateEndpoints { get; set; }

        /// <summary>Refresh the roster. Called at boot and on the engine's default-device change.</summary>
        public static void RefreshDevices()
        {
            LocalAudioDevice[] rows;
            try { rows = EnumerateEndpoints?.Invoke() ?? []; }
            catch (Exception ex) { Log.Warn("audio", "endpoint enumeration failed", ex); rows = []; }
            ToUi(() => Devices.Value = rows);
        }

        /// <summary>Persist and apply an output choice. The ORDER is the whole content: route first so the first local
        /// audio lands on the just-chosen endpoint, and only then pull playback home from a foreign device.</summary>
        public static void Select(string? deviceId)
        {
            string? id = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            string name = "";
            foreach (LocalAudioDevice d in Devices.Peek())
            {
                if (string.Equals(d.Id, id ?? "", StringComparison.OrdinalIgnoreCase)) { name = d.Name; break; }
            }
            Platform.Settings.Set(Platform.Keys.OutputDeviceId, id ?? "");
            Platform.Settings.Set(Platform.Keys.OutputDeviceName, name);
            ToUi(() => SelectedOutputId.Value = id);
            // The route itself waits on the engine change above; the intent round-trips so the picker is honest about
            // what it remembered.
            Log.Info("audio", $"output device intent id={id ?? "<default>"} name={name}");
        }

        public static void SetMuted(bool muted)
        {
            MediaPlayer? p = Volatile.Read(ref s_player);
            try { p?.SetMuted(muted); } catch { }
            try { LiveSilentSession()?.SetMuted(muted); } catch { }
            ToUi(() => Muted.Value = muted);
        }

        // ── 3. the volume taper (ch 20 §8) ───────────────────────────────────────────────────────────────────────────

        /// <summary>Slider position → amplitude. CUBIC, and the boundary matters: the UI slider stays LINEAR (a
        /// half-way thumb must look half-way) and the taper is applied here, at the amplitude edge, so the perceived
        /// loudness curve is right without the control lying about its own position.</summary>
        public static class VolumeTaper
        {
            public static float Amplitude(float position)
            {
                float p = Math.Clamp(position, 0f, 1f);
                return p * p * p;
            }
        }

        // ── 4. the session and its state ─────────────────────────────────────────────────────────────────────────────

        static readonly object s_gate = new();
        static readonly AudioEffects s_effects = new();
        static readonly ConcurrentQueue<IDisposable> s_toDispose = new();

        static PcmAudioPlayer? s_backend;
        static MediaPlayer? s_player;
        static PcmAudioSession? s_session;
        static Timer? s_ticker;
        static Task s_tail = Task.CompletedTask;
        static int s_booted, s_chainDepth;
        static bool s_seeded, s_disposed;

        // G-117: every load and every prepare runs under a token the next one cancels.
        static readonly Supersede s_loads = new(), s_prepares = new();

        // The decoder the engine's factory answers for THIS open or prepare. Flows with the async context into the engine's
        // open, so a prepare running beside a load can never take the load's decoder (or the other way round).
        static readonly AsyncLocal<IAudioDecoder?> s_decoderForOpen = new();
        // `UseSilentEndpoint`: every session renders into the paced silent endpoint and no WASAPI client is ever made.
        static volatile bool s_forceSilent;

        // the epoch pair (C4)
        static uint s_loadEpoch;                   // the reducer's LoadEpoch the live session belongs to
        static EpochGate s_chain;                  // this file's own supersede counter
        static volatile bool s_clockStale = true;  // see rule 1

        // the live track
        static IMediaByteSource? s_bytes;
        static IAudioDecoder? s_activeDecoder;      // the decoder the live voice reads (G-113: a late Ogg tail's exact end)
        static Opened s_opened;
        static EntityId s_id;
        static PlayableKind s_activeKind;
        static long s_activeDurMs, s_activeStartMs, s_activeJoinFrame;
        static long s_anchorClock, s_anchorPlayheadMs;   // the session clock at which the active voice was at a known playhead
        static long s_activePrimaryId;
        static bool s_playIntent, s_errorReported, s_startedAnnounced, s_recovering, s_pendingSeekQueued;
        // `--fake` drives its own session and the `MediaPlayer` facade never opened one, so the facade's Idle state
        // and zero position must NOT be read: everything comes off the session itself.
        static bool s_silent;
        static int s_pendingSeekMs = -1;
        // V-PA2: the reducer's seek generation the parked/last seek belongs to. Seeded by every `Load` (the reducer is already
        // on `seekGen` when it emits the load) and stamped by `Seek`; every Position/Seeked the pump posts carries it. Written
        // under s_gate, read by the tick.
        static uint s_pendingSeekGen;
        // V-PA28: the SEEK MAILBOX. Last write wins: `Seek` stores (stamped load epoch << 32 | target ms) and at most ONE
        // `SeekCoreAsync` is queued on the chain for a whole run of seeks — it takes the newest. −1 = empty. The generation, the
        // start stamp and the stream counters of the newest write ride with it. All written and taken under s_gate.
        static long s_seekMailbox = -1;
        static uint s_seekMailboxGen;
        static long s_seekT0;
        static Spotify.Audio.Stream.Stats s_seekBefore;
        // The in-flight design-A prepare (a second decoder opening at the seek target). A newer seek, a load, a stop all cancel
        // it. Volatile.Read/Write everywhere (V-PA28).
        static CancellationTokenSource? s_seekPrepare;
        // The stale timer faded the live voice to silence (V-PE14: the prepare was late) and nothing has replaced it yet, so a
        // seek that resolves on that SAME voice (the ring jump, the in-place fallback) must give it its level back. s_gate.
        static bool s_activeSilenced;
        // A seek dropped a committed gapless join (S-5): the reducer is owed `Input.PrepareLost` once the seek lands — across a
        // cancelled seek too, which is why it is a flag and not a local. s_gate.
        static bool s_joinLostBySeek;
        // voice id → the linear factor its decoder folded into the PCM (V-PA15; <see cref="IGainFolding"/>) and the figures behind
        // it: what a live normalization change ramps FROM (`now / baked`, `now` from THAT voice's figures — a crossfade has two
        // tracks live). Written at open, at a join, at a crossfade and at a seek swap. s_gate.
        static readonly Dictionary<long, BakedVoice> s_bakedFactor = new();
        // H-11: the 200 ms timer re-enters when a tick overruns (a log write, a prepare commit, a long drain); a second tick on
        // top of the first would fold the stall twice and post the state twice. 0 = idle, 1 = a tick is running.
        static int s_tickBusy;
        // D1: the per-session glitch ledger. Written on the tick thread (`DrainXruns`, `FoldStall`), read by `Metrics.Read()` and
        // the health card, folded and reset by `RetireXruns` — one lock inside, no engine type in it.
        static readonly GlitchLedger s_ledger = new();
        static PlaybackState s_lastState = PlaybackState.Idle;
        static long s_lastPositionPostMs, s_lastWorkLogMs;
        static long s_nextVoiceId;
        static int s_endedHold;
        // The endgame's per-track state (EndgamePlan, G-112): `s_armLogged` gates the once-per-track `[gapless] arm`
        // diagnostic; `s_lastEndgameAskMs`/`s_endgameAskCount` drive the re-asking nudge (a sentinel of -1 means "never
        // asked this track"). All three reset wherever a new track becomes active or a seek re-arms the endgame.
        static bool s_armLogged;
        static long s_lastEndgameAskMs = -1;
        static int s_endgameAskCount;

        // the prepared slot (B)
        static IPreparedItem? s_prepItem;
        static IMediaByteSource? s_prepBytes;
        static IAudioDecoder? s_prepDecoder;
        static Opened s_prepOpened, s_joinOpened;
        static EntityId s_prepId, s_prepBuildingId;
        static long s_prepDurMs;
        static bool s_prepOverlap;
        static int s_prepInFlight;

        // the pending hand-off (B armed into the live mixer, not yet audible)
        static bool s_joinPending, s_crossfadeInFlight;
        static long s_joinFrame, s_joinVoiceId, s_joinDurMs;
        static IAudioSource? s_joinVoice;
        static IAudioDecoder? s_joinDecoder;
        static long s_joinTotalFrames;
        static IMediaByteSource? s_joinBytes, s_retiringBytes;
        static EntityId s_joinId;

        // crossfade settings
        static bool s_crossfadeEnabled;
        static int s_crossfadeMs;
        static float s_volume = 1f;

        static int EffectiveFadeMs => EffectiveFadeMsFor(s_crossfadeEnabled, s_crossfadeMs);

        /// <summary>The crossfade as the pump will actually apply it: 0 means gapless.</summary>
        public static int CrossfadeMs => EffectiveFadeMs;

        /// <summary>What the pump is actually playing right now, for the diagnostics page and the log lines. The
        /// reducer's own `State.CurrentId` is the authority for everything else; this is the SHELL's view of it, and
        /// the two disagreeing is exactly the class of bug the epoch exists to make visible.</summary>
        public static EntityId PlayingId => s_id;

        /// <inheritdoc cref="Opened"/>
        public static Opened PlayingOpened => s_opened;

        // ── 5. boot and shutdown ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Build the backend and the player ONCE — ON THE PUMP CHAIN, never on the caller's thread (G-124): the
        /// WASAPI leaf probes the default endpoint's format by OPENING it, which is seconds on a cold Bluetooth or USB
        /// device, and the reducer's <c>Execute</c> calls this pump from the UI thread. The first REAL load boots it as its
        /// first step (a `--fake` run, a unit test and a Connect-viewer session boot the reducer with no WASAPI client
        /// anywhere, which is exactly what `Playback.Host`'s boot contract promises); <see cref="Warm"/> boots it ahead of
        /// the first play. A fake load, a volume change and a prepare only <see cref="SeedOnce"/>: none needs a device.
        /// After <see cref="UseSilentEndpoint"/> the backend renders into <see cref="PacedSilentEndpoint"/> and WASAPI is
        /// never probed at all. Returns immediately.</summary>
        public static void Boot()
        {
            SeedOnce();
            Settings.ApplyNormalization ??= static () => SetNormalization();   // G-132: the settings rows' seam (toggle, mode, album); V-PA27: an Action
            if (Volatile.Read(ref s_booted) == 0) Enqueue(static () => { EnsureBackend(); return Task.CompletedTask; });
        }

        /// <summary>Boot the backend ahead of the first play — the composition root calls it once the startup quiet period
        /// is over, so the endpoint probe happens while nobody is waiting for sound. Idempotent; returns immediately.</summary>
        public static void Warm()
        {
            if (Volatile.Read(ref s_booted) == 0 && !s_disposed) Boot();
        }

        /// <summary>The backend, built on the first call (pump chain only). True when local playback is possible.</summary>
        static bool EnsureBackend()
        {
            if (Interlocked.Exchange(ref s_booted, 1) != 0) return Volatile.Read(ref s_player) is not null;
            // G-127: the engine's device diagnostics — the negotiated format, an open that failed and why — are always-on log
            // lines, wired before the first open. A host that set its own (the headless arm) keeps them.
            WasapiAudioDevice.FormatSink ??= static line => Log.Info("audio", "device format " + line);
            WasapiAudioDevice.DiagSink ??= static line => Log.Info("audio", line);
            try
            {
                // The factory's only argument is the mix format; it answers the decoder the pump chose for the open or
                // prepare that is asking (FLAC plan §4.2) — `s_decoderForOpen`, which flows with that open's async context.
                PcmAudioPlayer backend = s_forceSilent ? CreateSilentBackend() : WasapiPcm.CreateBackend(s_effects, decoderFactory: TakePendingDecoder);
                s_backend = backend;
                Volatile.Write(ref s_player, MediaPlayer.Build().WithBackend(MediaKind.PcmAudio, backend).Build());
                ToUi(static () => Supported.Value = true);
                if (s_forceSilent) Log.Info("audio", $"audio backend: silent endpoint {SilentFormat.SampleRate} Hz, paced to the wall clock");
            }
            catch (Exception ex)
            {
                // No render endpoint, or a machine the WASAPI leaf refused. Local playback is off; Connect still works
                // and every surface renders the foreign device instead of an error.
                Log.Warn("audio", "audio backend unavailable — local playback is off this session", ex);
                ToUi(static () => Supported.Value = false);
            }
            RefreshDevices();
            string persisted = Platform.Settings.Get(Platform.Keys.OutputDeviceId);
            if (persisted.Length > 0) ToUi(() => SelectedOutputId.Value = persisted);
            return Volatile.Read(ref s_player) is not null;
        }

        /// <summary>The persisted DSP preferences, once — what a session needs before its first block, and all a fake load,
        /// a volume change or a prepare needs. The caller's thread (the UI thread's <c>Execute</c>): these are signal writes.</summary>
        static void SeedOnce()
        {
            if (s_seeded) return;
            s_seeded = true;
            SeedFromSettings();
        }

        /// <summary>The mix format of every silent session (the paced endpoint has no device rate to follow).</summary>
        public static readonly MixFormat SilentFormat = new(48_000, 2);

        /// <summary>Render every session into the paced silent endpoint instead of a WASAPI device: a real track still
        /// streams, decodes and runs the whole graph, at wall-clock speed, into nothing — the headless `--silent` run
        /// (headless plan §3.5; the decision was to fix the pacing here, not in the engine, §8 Q4). Call it BEFORE the
        /// first load: a backend that already opened a device is kept, and the call says so in the log.</summary>
        public static void UseSilentEndpoint()
        {
            s_forceSilent = true;
            if (Volatile.Read(ref s_booted) != 0 && s_backend is not null)
                Log.Warn("audio", "UseSilentEndpoint after the audio backend was built — this session keeps its device");
        }

        /// <summary>True once <see cref="UseSilentEndpoint"/> was called.</summary>
        public static bool SilentEndpoint => s_forceSilent;

        /// <summary>The silent backend: the same `PcmAudioPlayer` and RT feed shape `WasapiPcm.CreateBackend` builds, over
        /// <see cref="PacedSilentEndpoint"/> and a plain (non-MMCSS) feed thread, with no device watcher — there is no
        /// device to lose.</summary>
        static PcmAudioPlayer CreateSilentBackend()
            => new(SilentFormat,
                endpointFactory: static fmt => new PacedSilentEndpoint(fmt),
                effects: s_effects,
                maxBlock: 1024,
                driveWithOwnThread: false,
                onSessionCreated: static session =>
                    new AudioFeedThread(session, sampleRate: session.Format.SampleRate).Start(),
                decoderFactory: TakePendingDecoder);

        /// <summary>Read the persisted DSP preferences. Seeded BEFORE the first open, so a session never renders one
        /// block with a flat EQ and then ramps.</summary>
        public static void SeedFromSettings()
        {
            SetCrossfade(Platform.Settings.Get(Platform.Keys.CrossfadeEnabled),
                Platform.Settings.Get(Platform.Keys.CrossfadeMs));
            SetEqualizer(Platform.Settings.Get(Platform.Keys.EqualizerEnabled),
                ParseEqGains(Platform.Settings.Get(Platform.Keys.EqualizerGains)));
            // D6: the decoders own normalization (`GainLinear` reads the setting per track); the engine's must never add to it.
            s_effects.Normalization.Value = EngineNormalization;
            // The PERSISTED number is the slider's own position (that is what `Playback.Host` writes back), so the
            // taper is applied here exactly as it is on a live change — otherwise a restart would be audibly louder.
            float saved = Platform.Settings.Get(Platform.Keys.RememberVolume)
                ? Platform.Settings.Get(Platform.Keys.SavedVolume) : 1f;
            s_volume = VolumeTaper.Amplitude(saved);
            // The stage's analysis-window offset (Prefs.Stage.SyncOffsetMs; written live by the gallery/Appearance sliders).
            s_effects.SpectrumOffsetMs = Math.Clamp(Platform.Settings.Get(Platform.Keys.StageSyncOffsetMs), -500, 500);
        }

        public static void Shutdown()
        {
            s_disposed = true;
            StopTicker();
            try { s_ticker?.Dispose(); } catch { }
            s_ticker = null;
            Enqueue(static () => DisposeSessionAsync());
            try { s_tail.Wait(5_000); } catch { }
            try { s_player?.DisposeAsync().AsTask().Wait(5_000); } catch { }
            DrainDisposals();
        }

        // ── 6. the host API the reducer's Execute calls ──────────────────────────────────────────────────────────────

        /// <summary>Load <paramref name="id"/> for reducer epoch <paramref name="epoch"/>, starting at
        /// <paramref name="fromMs"/>. Returns immediately. The epoch bumps HERE, synchronously, so a superseded load
        /// stops being ours before this call returns — and `_clockStale` goes up with it, so nothing reports the
        /// outgoing track's position for the track that is starting. <paramref name="seekGen"/> is the reducer's seek
        /// generation at the load (V-PA2): it seeds <c>s_pendingSeekGen</c>, so a position or a parked seek reported by this
        /// load's own session carries the generation the reducer is already on and is not dropped as stale.</summary>
        public static void Load(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs, uint seekGen)
        {
            // A fake playable has no bytes and plays through the silent voice: it needs the DSP seed, never a device. A real
            // one boots the backend as the load's own first step, on the chain (G-124).
            SeedOnce();
            Interlocked.Exchange(ref s_loadStartedTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            s_clockStale = true;
            // Play intent is the CALLER's, decided here and synchronously: a `Load` followed by `Pause` in the same Execute
            // pass (a paused restore, a paused reload) must open silently paused, so the load itself only reads it.
            lock (s_gate) s_playIntent = true;
            long chain = BumpChain();
            // G-117: the load this one supersedes stops being worth its round trips NOW — its token is cancelled (its open
            // returns at the next request boundary instead of running four 20 s deadlines to completion), and so is any
            // prepare built for the track that is being replaced.
            CancellationToken token = s_loads.Next();
            s_prepares.Cancel();
            CancelSeekPrepare();                // a seek's second decoder opening on the OUTGOING track is not worth finishing
            ScrubDrop();                        // a drag on the outgoing track is orphaned (the reducer sends no cancel)
            ReleaseBlockedChain();
            Enqueue(() => LoadCoreAsync(row, id, epoch, fromMs, kind, seekGen, chain, token));
        }

        /// <summary>Prepare the NEXT playable so the boundary can be a hand-off instead of a hard cut. Best effort by
        /// construction: no prepared slot simply means the next boundary reloads, which is what 0.2.9 did every time
        /// the reducer had not asked in time. Nothing to hand off from means nothing to build, so no device is opened
        /// here: a live session already booted the backend.
        /// <para>OFF THE PUMP CHAIN (its CDN open blocks, and a seek or a pause must not queue behind it) and under its own
        /// token. The SAME id already prepared or being prepared is a no-op — the reducer re-emits the effect whenever the
        /// next row might have changed (G-112) — and a DIFFERENT id replaces the slot. <paramref name="kind"/> is the next
        /// playable's: a boundary across kinds is never spliced (<see cref="MediaSwitch.AllowCrossfade"/>).</para></summary>
        public static void Prepare(EntityRef row, EntityId id, PlayableKind kind = PlayableKind.Audio)
        {
            SeedOnce();
            lock (s_gate)
            {
                bool slotHolds = s_prepId == id && s_prepItem is not null;
                bool building = s_prepBuildingId == id && Volatile.Read(ref s_prepInFlight) > 0;
                bool joined = s_joinPending && s_joinId == id;
                if (!id.IsEmpty && (slotHolds || building || joined)) return;
                s_prepBuildingId = id;
            }
            CancellationToken token = s_prepares.Next();
            DisposePreparedSlot();
            if (id.IsEmpty) return;
            Interlocked.Increment(ref s_prepInFlight);
            _ = Task.Run(() => PrepareCoreAsync(row, id, kind, token));
        }

        /// <summary>The next row changed and nothing replaces it (the reducer's <c>CancelPrepared</c>, G-112): drop the
        /// prepared slot, cancel a prepare in flight, and abandon a join that is armed but not yet audible — a join into a
        /// track that is no longer next is a wrong-track join.</summary>
        public static void CancelPrepared()
        {
            lock (s_gate) s_prepBuildingId = default;
            s_prepares.Cancel();
            PcmAudioSession? session;
            lock (s_gate) session = s_session;
            AbandonPendingJoin(session, "next-changed");
            DisposePreparedSlot();
        }

        /// <summary>Warm the next playable's metadata, head, mirrors and key without opening it — what the reducer asks for
        /// at a load, so the endgame's <see cref="Prepare"/> costs the first range and the tail (G-112). Non-blocking.</summary>
        public static void Prefetch(EntityId id)
        {
            if (id.Provider != EntityProvider.Spotify || id.IsEmpty) return;
            try { Spotify.Audio.Prefetch(id.Text); }
            catch (Exception ex) { Log.Warn("audio", "prefetch failed", ex); }
        }

        /// <summary>Seek the live track — through the MAILBOX (V-PA28, S-4): the target is stamped with the load epoch and the
        /// reducer's seek generation <paramref name="gen"/>, the previous in-flight prepare is cancelled, and ONE
        /// <see cref="SeekCoreAsync"/> is queued for a whole run of seeks (it takes the newest target; the older ones never
        /// reach the engine). The stream counters are sampled here (the seek's kind is their delta, <see cref="SeekKindOf"/>).
        /// Until the seek lands the pump REPORTS the target as the position (<see cref="ActivePositionMs"/>), stamped with
        /// <paramref name="gen"/>, so the bar never steps back. Nothing touches the live producer here: the ring jump and the
        /// voice swap leave it alone, and only the in-place fallback interrupts its byte wait (V-PA19).
        /// <para>A seek for an OLDER load than the pump's is dropped. A seek for a load that is still in flight (the user
        /// dragged the bar the instant the track changed: its epoch is AHEAD of <c>s_loadEpoch</c> until the load op runs) is
        /// kept — the chain runs it after that load, exactly as the chain always ordered it.</para></summary>
        public static void Seek(int ms, uint epoch = 0, uint gen = 0)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Spotify.Audio.Stream.Stats before = Spotify.Audio.Stream.Stats.Read();
            int target = Math.Max(0, ms);
            bool queued;
            lock (s_gate)
            {
                if (epoch != 0 && epoch < s_loadEpoch) return;                       // a superseded load's seek
                uint stamp = epoch == 0 ? s_loadEpoch : epoch;
                s_pendingSeekMs = target;
                s_pendingSeekGen = gen;
                s_pendingSeekQueued = true;               // this seek IS the queued work: the tick's Ready arm must not apply it again
                s_seekMailboxGen = gen;
                s_seekT0 = t0;
                s_seekBefore = before;
                queued = Interlocked.Exchange(ref s_seekMailbox, ((long)stamp << 32) | (uint)target) >= 0;
            }
            CancelSeekPrepare();                                                     // a newer seek supersedes an in-flight prepare
            if (!queued) Enqueue(SeekCoreAsync);                                     // ONE chain op reads the newest target
        }

        /// <summary>Cancel the in-flight design-A prepare, if any. A cancel racing the prepare's own teardown is harmless.</summary>
        static void CancelSeekPrepare()
        {
            try { Volatile.Read(ref s_seekPrepare)?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public static void Pause()
        {
            // The intent flag drops synchronously, before the physical pause is enqueued: a timer callback can still
            // read the engine's old Playing state while the pause is queued, and no later tick corrects it once the
            // ticker stops.
            lock (s_gate) s_playIntent = false;
            StopTicker();
            Enqueue(static async () =>
            {
                // A silent voice's session is this file's, not the facade's: the facade has no session to pause.
                if (LiveSilentSession() is { } silent) { try { await silent.PauseAsync().ConfigureAwait(false); } catch { } return; }
                MediaPlayer? p = Volatile.Read(ref s_player);
                if (p is not null) { try { await p.PauseAsync().ConfigureAwait(false); } catch { } }
            });
        }

        public static void Resume()
        {
            lock (s_gate) s_playIntent = true;
            Enqueue(static async () =>
            {
                if (LiveSilentSession() is { } silent) { try { await silent.PlayAsync().ConfigureAwait(false); } catch { } return; }
                MediaPlayer? p = Volatile.Read(ref s_player);
                if (p is not null) { try { await p.PlayAsync().ConfigureAwait(false); } catch { } }
            });
            StartTicker();
        }

        /// <summary>The live session when it is a silent VOICE session (a fake playable), else null.</summary>
        static PcmAudioSession? LiveSilentSession()
        {
            lock (s_gate) return s_silent ? s_session : null;
        }

        public static void Stop()
        {
            s_clockStale = true;
            BumpChain();
            StopTicker();
            s_loads.Cancel();
            s_prepares.Cancel();
            CancelSeekPrepare();
            ScrubDrop();
            ReleaseBlockedChain();
            Enqueue(static () => DisposeSessionAsync());
        }

        /// <summary>A Stop or a superseding Load while the chain is still busy: the op in front of it may be a seek or an
        /// open blocked in a starving read (which never ends on its own, D5), so the live bytes are closed HERE, off the
        /// chain — the blocked read answers −1 at once, the op returns, and the queued dispose runs. An idle chain disposes
        /// in order and needs none of this.</summary>
        static void ReleaseBlockedChain()
        {
            if (Volatile.Read(ref s_chainDepth) == 0) return;
            IMediaByteSource? live;
            lock (s_gate) live = s_silent ? null : s_bytes;
            if (live is null) return;
            try { live.Close(); } catch { }
        }

        /// <summary>Set the output amplitude. The taper is applied HERE (§3) so the slider stays linear.</summary>
        public static void SetVolume(float position)
        {
            // A volume change needs no device: the amplitude is applied to whatever session is live and seeded into the
            // next one at open.
            SeedOnce();
            float amplitude = VolumeTaper.Amplitude(position);
            lock (s_gate) s_volume = amplitude;
            MediaPlayer? p = Volatile.Read(ref s_player);
            try { p?.SetVolume(amplitude); } catch { }
            try { LiveSilentSession()?.SetVolume(amplitude); } catch { }
            // The PERSIST is `Playback.Host`'s (it already writes `SavedVolume` on the volume effect); doing it here
            // too would be two writers of one key disagreeing about whether it holds a position or an amplitude.
        }

        public static void SetCrossfade(bool enabled, int durationMs)
        {
            lock (s_gate)
            {
                s_crossfadeMs = Math.Clamp(durationMs, 0, MaxCrossfadeMs);
                s_crossfadeEnabled = enabled && s_crossfadeMs > 0;
            }
            // 0 on the engine's own knob means GAPLESS; the manual splice below is what actually fades, and the engine
            // reads this for its queue path. Both must agree or a boundary fades twice.
            s_effects.CrossfadeMs.Value = s_crossfadeEnabled ? s_crossfadeMs : 0f;
            s_effects.CrossfadeCurve.Value = CrossCurve.EqualPower;
        }

        /// <summary>A 10-band graphic EQ at the classic ISO centres, clamped to ±12 dB. A gain-only change ramps in
        /// the live graph; toggling `enabled` changes the topology.</summary>
        public static void SetEqualizer(bool enabled, ReadOnlySpan<float> gainsDb)
        {
            Equalizer eq = s_effects.Equalizer;
            if (eq.Bands.Length != EqBands.Length)
                eq.Apply(new EqPreset(EqBands, new float[EqBands.Length]));
            for (int i = 0; i < eq.Bands.Length && i < gainsDb.Length; i++)
                eq.Bands[i].GainDb.Value = Math.Clamp(gainsDb[i], -12f, 12f);
            eq.Enabled.Value = enabled;
        }

        static readonly float[] EqBands = [31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

        static float[] ParseEqGains(string csv)
        {
            var gains = new float[EqBands.Length];
            int i = 0;
            foreach (Range r in csv.AsSpan().Split(','))
            {
                if (i >= gains.Length) break;
                gains[i++] = float.TryParse(csv.AsSpan()[r].Trim(), out float g) ? Math.Clamp(g, -12f, 12f) : 0f;
            }
            return gains;
        }

        // ── 7. the serialized chain ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>One `Task` chain under one lock. Everything physical goes through it, so `Load → Seek → Prepare`
        /// are strictly ordered and the native device is never opened twice concurrently. The catch is the last line
        /// of defence: a failed attach must produce a typed fault, never a silently dead host.</summary>
        static void Enqueue(Func<Task> op)
        {
            lock (s_gate)
            {
                Interlocked.Increment(ref s_chainDepth);
                s_tail = s_tail.ContinueWith(async _ =>
                {
                    try { await op().ConfigureAwait(false); }
                    catch (OperationCanceledException) { }              // a superseded load or a stopped seek: nothing to report
                    catch (Exception ex)
                    {
                        Log.Warn("audio", "pump op failed", ex);
                        PostFault(Fault.Unknown);
                    }
                    finally { Interlocked.Decrement(ref s_chainDepth); }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        static long BumpChain() { lock (s_gate) return s_chain.Bump(); }

        static bool IsStale(long chain) { lock (s_gate) return s_chain.IsStale(chain); }

        /// <summary>The supersede rule, as a value (C4). Ten `Next` clicks bump it ten times; the nine loads already
        /// in flight each discover at their next await boundary that the work is no longer theirs and dispose what
        /// they opened instead of publishing it. Its own type so the rule is pinned by a test rather than by reading
        /// the pump — and so "stale" can never accidentally mean "equal".</summary>
        public struct EpochGate
        {
            long _current;

            /// <summary>The epoch a load started for.</summary>
            public long Current => _current;

            /// <summary>Supersede everything in flight and answer the new epoch.</summary>
            public long Bump() => ++_current;

            /// <summary>Is work started for <paramref name="startedFor"/> no longer ours?</summary>
            public readonly bool IsStale(long startedFor) => _current != startedFor;
        }

        // ── 8. the load ──────────────────────────────────────────────────────────────────────────────────────────────

        static async Task LoadCoreAsync(EntityRef row, EntityId id, uint epoch, int fromMs, PlayableKind kind, uint seekGen,
            long chain, CancellationToken token)
        {
            await DisposeSessionAsync().ConfigureAwait(false);
            if (IsStale(chain) || token.IsCancellationRequested) return;

            lock (s_gate)
            {
                // A seek parked for the OUTGOING track must never land on this one — mailbox included (V-PA2, V-PA28). The one
                // exception is a seek the reducer posted for THIS load while it was still in flight (stamped with this load's
                // epoch): it is the load's own newest intent. It keeps the mailbox AND the pending target/generation it set, so the
                // tick still reports the target and its landing still matches the reducer's generation.
                long box = Volatile.Read(ref s_seekMailbox);
                if (box >= 0 && (uint)(box >> 32) == epoch)
                {
                    // keep: `Seek` already wrote s_pendingSeekMs / s_pendingSeekGen / s_pendingSeekQueued for it
                }
                else
                {
                    Interlocked.Exchange(ref s_seekMailbox, -1);
                    s_seekMailboxGen = seekGen;
                    s_pendingSeekMs = -1;
                    s_pendingSeekQueued = false;
                    s_pendingSeekGen = seekGen;    // V-PA2: seeded from EVERY load, so the first report is current
                }
                s_activeSilenced = false;
                s_joinLostBySeek = false;
                s_bakedFactor.Clear();
                s_errorReported = false;
                s_startedAnnounced = false;
                s_recovering = false;
                s_lastState = PlaybackState.Idle;
                s_loadEpoch = epoch;
                s_id = id;
                s_activeKind = kind;
                s_armLogged = false;
                s_lastEndgameAskMs = -1;
                s_endgameAskCount = 0;
                s_endedHold = 0;
            }

            // A `--fake` load has no bytes at all: the silent voice runs the real graph for the declared duration.
            if (id.Provider == EntityProvider.Fake)
            {
                await OpenSilentAsync(row, epoch, fromMs, chain).ConfigureAwait(false);
                return;
            }

            // G-124: the device is probed HERE, on the chain, the first time a real track loads.
            if (!EnsureBackend()) { PostFault(Fault.RuntimeMissing, epoch); return; }

            IMediaByteSource? bytes = Open(id, token, out Opened opened, out Fault fault);
            if (IsStale(chain) || token.IsCancellationRequested) { bytes?.Close(); return; }
            if (bytes is null)
            {
                if (fault != Fault.None) PostFault(fault, epoch);
                return;
            }

            lock (s_gate) { s_opened = opened; s_activeDurMs = opened.DurationMs; }
            await OpenSessionAsync(bytes, opened, row, epoch, fromMs, seekGen, chain, token, autoResume: true).ConfigureAwait(false);
        }

        /// <summary>How often an open still in progress looks at its body's stall (only while opening; a healthy open
        /// finishes well inside the first look).</summary>
        const int OpenWatchMs = 1_000;

        /// <summary>Await the engine's open of <paramref name="bytes"/> WITHOUT letting a dead link hold the chain forever: a
        /// read in the decoder's header parse waits as long as the link starves (D5), so the open is watched — a superseding
        /// load or a stop closes the bytes (the read answers −1 and the open fails at once), and a stall past
        /// <see cref="StarvePolicy.StallFailMs"/> closes them and answers <see cref="Fault.Network"/>. The tick is not running
        /// yet, which is why this watch exists at all.</summary>
        static async Task<Fault> AwaitOpenAsync(Task open, IMediaByteSource bytes, CancellationToken token)
        {
            using CancellationTokenRegistration closeOnCancel = token.Register(static b => ((IMediaByteSource)b!).Close(), bytes);
            while (true)
            {
                Task done = await Task.WhenAny(open, Task.Delay(OpenWatchMs)).ConfigureAwait(false);
                if (ReferenceEquals(done, open)) break;
                if (StarvePolicy.Decide(StallOf(bytes), RefusedOf(bytes)) != StarvePolicy.Verdict.Failed) continue;
                Log.Warn("audio", $"open starved for {StallOf(bytes)} ms reason={(RefusedOf(bytes) ? "refused" : "slow")}"
                                  + " — failing the load");
                try { bytes.Close(); } catch { }
                try { await open.ConfigureAwait(false); } catch { }
                return Fault.Network;
            }
            try { await open.ConfigureAwait(false); }
            catch (OperationCanceledException) { return Fault.None; }
            catch (Exception ex) { Log.Warn("audio", "open failed", ex); return Fault.DecodeFailed; }
            return Fault.None;
        }

        /// <summary>How long a live Spotify body's read has waited without a byte; 0 for any other source.</summary>
        static long StallOf(IMediaByteSource? bytes) => bytes is RingSource ring ? ring.Body.StallMs : 0;

        /// <summary>Is a live Spotify body's mirror set refusing right now (the last thing that happened to it was a
        /// range every url said no to)? False for a local file, a module stream or a podcast enclosure.</summary>
        static bool RefusedOf(IMediaByteSource? bytes) => bytes is RingSource ring && ring.Body.Refusing;

        static async Task OpenSessionAsync(IMediaByteSource bytes, Opened opened, EntityRef row, uint epoch,
            int fromMs, uint seekGen, long chain, CancellationToken token, bool autoResume)
        {
            MediaPlayer? player = Volatile.Read(ref s_player);
            if (player is null) { bytes.Close(); PostFault(Fault.RuntimeMissing, epoch); return; }

            IAudioDecoder decoder = CreateDecoderFor(opened);
            s_decoderForOpen.Value = decoder;                 // flows into the engine's open; its factory takes it
            MediaSource source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);

            if (PlayIntentGate.ShouldAnnounceBuffering(s_playIntent)) PostSignal(AudioSignal.Buffering, epoch);
            long openStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Fault openFault = await AwaitOpenAsync(player.OpenAsync(source).AsTask(), bytes, token).ConfigureAwait(false);
            s_decoderForOpen.Value = null;

            // ALWAYS-ON, one line per load: which way the backend open went. The two silent exits below (a stale chain,
            // a cancelled token) and a backend that never created a session left NOTHING in the log — 2026-09-22, the
            // hand-back from a closed video window: `audio.open` logged, then no device open, no first frames, no
            // warning, and the transport clock ran with no sound.
            Log.Info("audio", "audio.session.open fault=" + openFault + " stale=" + (IsStale(chain) ? 1 : 0)
                + " cancelled=" + (token.IsCancellationRequested ? 1 : 0)
                + " session=" + (player.Session is PcmAudioSession ? 1 : 0)
                + " error=" + (player.Error.Peek() is { } e0 ? e0.ToString() : "-")
                + " ms=" + (long)((System.Diagnostics.Stopwatch.GetTimestamp() - openStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));

            if (IsStale(chain) || token.IsCancellationRequested) { bytes.Close(); return; }
            if (openFault != Fault.None) { bytes.Close(); PostFault(openFault, epoch); return; }
            if (player.Error.Peek() is { } err) { bytes.Close(); PostFault(MapError(err), epoch); return; }

            var pcm = player.Session as PcmAudioSession;
            lock (s_gate)
            {
                s_bytes = bytes;
                s_activeDecoder = decoder;
                s_session = pcm;
                s_silent = false;
                s_activeStartMs = 0;
                s_activeDurMs = opened.DurationMs;
                s_crossfadeInFlight = false;
                s_joinPending = false;
                s_activeJoinFrame = 0;
                s_clockStale = false;                       // the new session owns the clock from here
                if (pcm is not null)
                {
                    s_activePrimaryId = pcm.PrimaryVoiceIdValue;
                    NoteBakedFactor(s_activePrimaryId, decoder, in opened);
                    s_activeJoinFrame = GaplessJoinClock.JoinFrameFor(pcm.SampleClock, opened.DurationMs, 0, pcm.Format.SampleRate);
                    s_anchorClock = pcm.SampleClock;
                    s_anchorPlayheadMs = 0;
                    pcm.DeviceFormatChanged += OnDeviceFormatChanged;
                    pcm.DeviceRebuilt += OnDeviceRebuilt;
                }
            }

            try { player.SetVolume(s_volume); player.SetMuted(Muted.Peek()); player.SetRate(RateFor(s_id)); } catch { }

            // The label and the real duration are facts about the OPENED file, so they are published from here rather
            // than guessed from the catalogue.
            PostOpenedFacts(in opened, epoch);

            // SeekGate: the engine's seek BLOCKS until the decoder has PCM at the target, and on one serialized chain
            // a seek past the clear head would wait for the body attach that is behind it in the same queue. A deferred
            // seek parks instead, and pauses if there was play intent so the user does not hear the intro of a track
            // they resumed at 3:45.
            if (fromMs > 0)
            {
                bool canServe = bytes.Caps.Seekable;
                if (SeekGate.Decide(hasSession: true, sourceCanServeBeyondHead: canServe) == SeekAdmission.ApplyNow)
                    await ApplySeekAsync(fromMs, epoch, seekGen).ConfigureAwait(false);
                else
                {
                    // Parked for the tick's Ready arm — unless a newer seek of the user's already owns the pending target.
                    lock (s_gate) { if (s_pendingSeekGen == seekGen) s_pendingSeekMs = fromMs; }
                    if (s_playIntent) { try { await player.PauseAsync().ConfigureAwait(false); } catch { } }
                }
            }

            if (s_playIntent && autoResume)
            {
                try { await player.PlayAsync().ConfigureAwait(false); } catch (Exception ex) { Log.Warn("audio", "play failed", ex); }
                StartTicker();
            }
            _ = row;
        }

        /// <summary>The opened file's duration and format badge, posted under <paramref name="epoch"/> — at open, and again
        /// when a hand-off's adoption moves the live voice to a new epoch (the reducer clears both when it puts the joined
        /// row on the deck). The badge is INTERNED ON THE UI THREAD (G-125): <c>Entities.Strings</c> has one writer, and this
        /// runs on the pump's pool thread.</summary>
        static void PostOpenedFacts(in Opened opened, uint epoch)
        {
            if (opened.DurationMs > 0) Post(Input.Duration((int)Math.Min(opened.DurationMs, int.MaxValue), epoch));
            if (opened.Label.Length == 0) return;
            string label = opened.Label;
            ToUi(() => Post(Input.Format(Entities.Strings.Intern(label), epoch)));
        }

        /// <summary>`--fake`'s arm (ch 31 GAP 6). The silent VOICE runs the real graph — mixer, DSP chain, level tap,
        /// the RT feed — so what the demo exercises is the shipping path with the device leaf swapped for the paced
        /// silent endpoint, and `Ended` arrives at the declared duration on the wall clock.</summary>
        static async Task OpenSilentAsync(EntityRef row, uint epoch, int fromMs, long chain)
        {
            long durMs = row.Kind == EntityKind.Track && row.Slot > 0 ? new Track(row.Slot).DurationMs : 0;
            if (durMs <= 0) durMs = 180_000;
            SilentStart start = SilentStart.For(durMs, fromMs);
            PcmAudioSession session = OpenSilentSession(SilentFormat, start.VoiceMs, s_effects, s_volume);
            if (IsStale(chain)) { await session.DisposeAsync().ConfigureAwait(false); return; }

            lock (s_gate)
            {
                s_session = session;
                s_silent = true;
                s_bytes = null;
                s_opened = new Opened(Spotify.Audio.Format.Unknown, durMs, 0f, "", 0, false);
                s_activeDurMs = durMs;
                s_activeStartMs = -start.PositionOffsetMs;   // the position reads from where the load asked (SilentStart)
                s_activePrimaryId = session.PrimaryVoiceIdValue;
                s_clockStale = false;
            }
            Post(Input.Duration((int)Math.Min(durMs, int.MaxValue), epoch));
            bool play;
            lock (s_gate) play = s_playIntent;
            if (!play) return;                                  // a paused load stays paused; Resume starts it
            await session.PlayAsync().ConfigureAwait(false);
            StartTicker();
        }

        /// <summary>The silent session, built OUTSIDE the `MediaPlayer` facade and therefore CONNECTED here. The engine
        /// starts a session's own feeder only inside `ConnectSignals` (`PcmAudioPlayer.cs:1079-1087`) and its `Advance`
        /// returns at once while no signal sink is connected, so an unconnected session never left `Idle`: `PlayAsync`
        /// only set a flag, the tick never posted `Started`, and `--fake` sat in `Loading` forever (headless plan §1.1).
        /// The core behind the sink is private and unobserved — the pump reads <c>CurrentState</c> and
        /// <c>PlayedFrames</c> off the session itself, so no signal write here reaches the UI thread's graph.
        /// <para>PACED, and FED. The endpoint is <see cref="PacedSilentEndpoint"/>, whose hardware consumes frames at the
        /// wall clock's rate, so a three-minute voice ends in three minutes (the engine's headless endpoint accepts every
        /// frame and ran the clock as fast as the feeder spun — headless plan §8 Q4, fixed here). The session is driven
        /// by an <see cref="AudioFeedThread"/> exactly as a device session is, not by the engine's single-thread feeder:
        /// on a feed-less session every transport command (the pause fade, the resume fade-in) drains the mixer queue
        /// INLINE on the caller's thread while that feeder renders, and routing Pause/Resume here would race it.</para></summary>
        public static PcmAudioSession OpenSilentSession(MixFormat format, long voiceMs, IAudioEffects? effects, float volume)
        {
            IAudioEndpoint endpoint = SilentSink(format);
            var session = new PcmAudioSession(format, endpoint.Sink, endpoint.Clock, 1024, driveWithOwnThread: false, endpoint);
            session.Configure(PcmAudioPlayer.BuildGraphSpec(effects, format));
            if (effects is not null) session.BindEffects(effects);
            var feed = new AudioFeedThread(session, sampleRate: format.SampleRate);   // attaches itself; the session disposes it
            IAudioSource voice = SilentVoice(format, voiceMs);
            long frames = Math.Max(1, voiceMs * format.SampleRate / 1000);
            session.SetVoice(voice, TimeSpan.FromMilliseconds(voiceMs), frames, NormMode.Off, -18f, volume);
            session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
            feed.Start();
            return session;
        }

        /// <summary>Where a silent load starts. The silent voice is not seekable — the engine's `SeekAsync` accepts only a
        /// decoder, trimming or memory source and throws for a signal generator — so a load at <c>fromMs</c> is not a seek:
        /// the voice holds only what is left (never zero frames) and the reported position is offset to where the load
        /// asked. PURE.</summary>
        public readonly record struct SilentStart(long VoiceMs, long PositionOffsetMs)
        {
            public static SilentStart For(long durationMs, long fromMs)
            {
                long duration = Math.Max(0, durationMs);
                long from = Math.Clamp(fromMs, 0, duration);
                return new SilentStart(Math.Max(1, duration - from), from);
            }
        }

        static async Task DisposeSessionAsync()
        {
            CancelSeekPrepare();                // a second decoder opening for a seek on the session going away has no purpose left
            PcmAudioSession? session;
            IMediaByteSource? bytes, retiring, join, prep;
            IPreparedItem? prepared;
            bool wasSilent;
            GlitchLedger.Snapshot summary = default;
            EntityId summaryTrack = default;
            lock (s_gate)
            {
                session = s_session;
                wasSilent = s_silent;
                bytes = s_bytes;
                retiring = s_retiringBytes;
                join = s_joinBytes;
                prep = s_prepBytes;
                prepared = s_prepItem;
                s_session = null;
                s_silent = false;
                s_bytes = null;
                s_activeDecoder = null;
                s_retiringBytes = null;
                s_joinBytes = null;
                s_joinDecoder = null;
                s_joinVoice = null;
                s_prepBytes = null;
                s_prepItem = null;
                s_prepDecoder = null;
                s_prepId = default;
                s_recovering = false;
                s_joinPending = false;
                s_crossfadeInFlight = false;
                s_prepOverlap = false;
                s_activePrimaryId = 0;
                s_activeJoinFrame = 0;
                s_clockStale = true;
                s_lastState = PlaybackState.Idle;
                if (session is not null) { summary = RetireXruns(session); summaryTrack = s_id; }
            }
            if (session is not null)
            {
                LogSessionSummary(in summary, summaryTrack);
                session.DeviceFormatChanged -= OnDeviceFormatChanged;
                session.DeviceRebuilt -= OnDeviceRebuilt;
            }
            if (prepared is not null) { try { await prepared.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (wasSilent && session is not null)
            {
                // The `--fake` session is built here, not by the engine's facade, so nobody else will dispose it.
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            foreach (IMediaByteSource? s in new[] { bytes, retiring, join, prep })
            {
                if (s is not null) { try { s.Close(); } catch { } }
            }
            // The session is the engine's; the player outlives it and re-opens. Nothing is disposed here that a later
            // open needs.
            DrainDisposals();
        }

        static void DrainDisposals()
        {
            while (s_toDispose.TryDequeue(out IDisposable? d)) { try { d.Dispose(); } catch { } }
        }

        // ── 9. the decoder factory (FLAC plan §4.2) ──────────────────────────────────────────────────────────────────

        /// <summary>The engine calls this ONCE per open with the negotiated mix format; it answers the decoder the pump
        /// already chose. A null pending decoder is a bug, not a fallback — but it must not crash the RT open, so the
        /// engine's own WAV decoder is the floor and the log line says what happened.</summary>
        static IAudioDecoder TakePendingDecoder(MixFormat mix)
        {
            // The open or prepare that is asking set it in its own async context, so two running side by side (a load and
            // an off-chain prepare) each get their own. Taken once: a second call in the same flow is a bug, not a codec.
            IAudioDecoder? chosen = s_decoderForOpen.Value;
            s_decoderForOpen.Value = null;
            if (chosen is not null) return chosen;
            Log.Warn("audio", "decoder factory called with no pending decoder — falling back to PCM");
            return new WavAudioDecoder();
        }

        /// <summary>Format → decoder. The routing table is this switch, exactly like `Open`'s (plan §5.10), and it is
        /// public because "which codec does a Flac24 rung get" is a promise worth a test rather than a comment.</summary>
        public static IAudioDecoder CreateDecoderFor(in Opened opened) => opened.Format switch
        {
            Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24 => new FlacAudioDecoder(opened.GainDb, opened.Peak, opened.AlbumGainDb, opened.AlbumPeak),
            Spotify.Audio.Format.Mp3 => new Mp3AudioDecoder(opened.GainDb, opened.Peak),
            Spotify.Audio.Format.Aac => new Modules.AacAudioDecoder(opened.GainDb, opened.Peak),
            _ => new VorbisAudioDecoder(opened.GainDb, opened.DurationMs, opened.Peak, opened.AlbumGainDb, opened.AlbumPeak),
        };

        // ── 8a. normalization (D5, D6): modes, album mode, the ONE factor, the live ramp ────────────────────────────────

        /// <summary>The loudness the user asks for: the pregain applied on top of every figure (Spotify's own dial, −23/−14/−11 LUFS
        /// against the −14 reference the figures are in). The values are PERSISTED ints (<c>Platform.Keys.NormalizationMode</c>) —
        /// append only.</summary>
        public enum NormalizationMode : byte { Quiet = 0, Normal = 1, Loud = 2 }

        /// <summary>The limiter's ceiling (−1.5 dB), shared with the engine's <c>LimiterSpec.Default</c>: every normalization cap lands
        /// here (N-1), so a peak the cap admits is never touched by the limiter.</summary>
        public const float LimiterCeilingLinear = 0.84139514f;

        /// <summary>ReplayGain tags are referenced to −18 LUFS; the −14 frame everything else uses is +4 dB (V-PA18).</summary>
        public const float ReplayGainToSpotifyDb = 4f;

        /// <summary>A mode's pregain in dB: Quiet −9 (→ −23 LUFS), Normal 0 (→ −14), Loud +3 (→ −11). PURE.</summary>
        public static float PregainDb(NormalizationMode mode) => mode switch { NormalizationMode.Quiet => -9f, NormalizationMode.Loud => 3f, _ => 0f };

        /// <summary>The persisted mode int, coerced: anything outside 0..2 is Normal (an old or hand-edited profile never picks a mode
        /// that does not exist). PURE.</summary>
        public static NormalizationMode ModeOf(int persisted) => persisted is >= 0 and <= 2 ? (NormalizationMode)persisted : NormalizationMode.Normal;

        /// <summary>The normalization gain as a linear multiplier, folded into the conversion that happens anyway — one multiply per
        /// sample and no extra pass. The setting (<c>NormalizationEnabled</c>, <c>NormalizationMode</c>, <c>NormalizationAlbum</c>)
        /// is read HERE, per open (a live change re-ramps the playing voice instead — <see cref="SetNormalization"/>); off ⇒ 1. The
        /// engine's own <c>ReplayGainInfo</c> stays unity for the primary voice so the two do not compound.
        /// <paramref name="peak"/> is the track's linear true peak (<c>Opened.Peak</c>), which caps a boost.</summary>
        internal static float GainLinear(float gainDb, float peak = 0f, float albumGainDb = 0f, float albumPeak = 0f)
            => NormalizationFactor(Platform.Settings.Get(Platform.Keys.NormalizationEnabled),
                ModeOf(Platform.Settings.Get(Platform.Keys.NormalizationMode)), Platform.Settings.Get(Platform.Keys.NormalizationAlbum),
                gainDb, peak, albumGainDb, albumPeak);

        /// <summary>The largest boost or cut a gain figure may ask for. Spotify's track gains sit within ±15 dB; the Ogg
        /// figure is four bytes of a header (byte 144), and a garbage float there must never become a deafening
        /// multiply.</summary>
        public const float MaxGainDb = 30f;

        /// <summary>The two-argument form: Normal mode, the track pair — what every caller before the modes meant. PURE.</summary>
        public static float NormalizationFactor(bool enabled, float gainDb, float peak = 0f)
            => NormalizationFactor(enabled, NormalizationMode.Normal, false, gainDb, peak);

        /// <summary>The normalization gain as ONE linear factor (D5, Vorbis plan §6.3): the chosen pair (the album's when
        /// <paramref name="album"/> is asked AND the source carried one, else the track's) plus the mode's pregain, <c>10^(dB/20)</c>
        /// clamped to ±<see cref="MaxGainDb"/>, and — when the peak is known — capped so <c>factor × peak ≤ </c>
        /// <see cref="LimiterCeilingLinear"/> (the limiter's ceiling, N-1): the cut that keeps a boosted peak under the limiter.
        /// <see cref="NormalizationMode.Loud"/> lifts the cap to 1.0 and lets the (pre-volume) limiter do the rest, as Spotify's own
        /// Loud does. Off, or a non-finite figure, ⇒ exactly 1. The mode is applied HERE and nowhere else (V-PA14: the lossless
        /// catalogue figure stays at the −14 reference). PURE; the decoders fold the answer into the interleave multiply (Vorbis) or
        /// the int→float scale constant (FLAC).</summary>
        public static float NormalizationFactor(bool enabled, NormalizationMode mode, bool album, float gainDb, float peak,
            float albumGainDb = 0f, float albumPeak = 0f)
        {
            if (!enabled) return 1f;
            bool useAlbum = album && float.IsFinite(albumGainDb) && albumGainDb != 0f;
            float g = useAlbum ? albumGainDb : gainDb, p = useAlbum ? albumPeak : peak;
            if (!float.IsFinite(g)) return 1f;
            float factor = MathF.Pow(10f, Math.Clamp(g + PregainDb(mode), -MaxGainDb, MaxGainDb) / 20f);
            float cap = mode == NormalizationMode.Loud ? 1f : LimiterCeilingLinear;
            if (p > 0f && float.IsFinite(p) && factor * p > cap) factor = cap / p;
            return factor;
        }

        /// <summary>The four normalization figures of one file, in the −14 LUFS frame (a ReplayGain tag is already +4 dB): what a decoder
        /// folded, and — kept per voice — what a live mode switch recomputes the factor from (V-PA15). <c>AlbumGainDb == 0</c> means "no
        /// album figure" and album mode then uses the track pair.</summary>
        public readonly record struct NormalizationFigures(float GainDb, float Peak, float AlbumGainDb = 0f, float AlbumPeak = 0f)
        {
            /// <summary>The factor these figures give under the CURRENT settings values passed in. PURE.</summary>
            public float Factor(bool enabled, NormalizationMode mode, bool album)
                => NormalizationFactor(enabled, mode, album, GainDb, Peak, AlbumGainDb, AlbumPeak);

            /// <summary>The catalogue's / header's figures when it carried a track gain (they win — Spotify's own measurement), else the
            /// file's ReplayGain tags at +4 dB (N-3, V-PA18): the track pair from the track tags (the album tags when only those exist),
            /// the album pair from the album tags only. No tags ⇒ <paramref name="source"/> unchanged. PURE.</summary>
            public static NormalizationFigures WithReplayGain(in NormalizationFigures source, in ReplayGainTags tags)
            {
                if (source.GainDb != 0f || !tags.Any) return source;
                float albumGain = tags.HasAlbumGain ? tags.AlbumGainDb + ReplayGainToSpotifyDb : 0f;
                return tags.HasTrackGain
                    ? new NormalizationFigures(tags.TrackGainDb + ReplayGainToSpotifyDb, tags.TrackPeak, albumGain, tags.HasAlbumGain ? tags.AlbumPeak : 0f)
                    : new NormalizationFigures(albumGain, tags.AlbumPeak, albumGain, tags.AlbumPeak);
            }
        }

        /// <summary>The four ReplayGain tags of a local file (<c>REPLAYGAIN_TRACK_GAIN</c>/<c>_TRACK_PEAK</c>/<c>_ALBUM_GAIN</c>/<c>_ALBUM_PEAK</c>), in
        /// the tag's own −18 LUFS frame: gains in dB, peaks LINEAR. <see cref="HasTrackGain"/>/<see cref="HasAlbumGain"/> say whether the gain tag was
        /// present and believable; a missing peak is 0 (unknown). The parsers are PURE over spans (ASCII, case-insensitive keys).</summary>
        public readonly record struct ReplayGainTags(float TrackGainDb, float TrackPeak, float AlbumGainDb, float AlbumPeak,
            bool HasTrackGain, bool HasAlbumGain)
        {
            /// <summary>Any gain tag at all.</summary>
            public bool Any => HasTrackGain || HasAlbumGain;

            /// <summary>The four tag VALUES (the text after the <c>=</c>), each empty when the tag is absent.</summary>
            public static ReplayGainTags Of(ReadOnlySpan<byte> trackGain, ReadOnlySpan<byte> trackPeak,
                ReadOnlySpan<byte> albumGain, ReadOnlySpan<byte> albumPeak)
            {
                bool hasTrack = TryParseGainDb(trackGain, out float tg), hasAlbum = TryParseGainDb(albumGain, out float ag);
                TryParsePeak(trackPeak, out float tp);
                TryParsePeak(albumPeak, out float ap);
                return new ReplayGainTags(hasTrack ? tg : 0f, tp, hasAlbum ? ag : 0f, ap, hasTrack, hasAlbum);
            }

            /// <summary>A gain tag: <c>-6.54 dB</c>, <c>+3.2dB</c>, <c> -6.54 </c> (the unit optional, any case). False when there is no number,
            /// it is not finite, or it is beyond ±<see cref="MaxGainDb"/> (a garbage tag is no gain).</summary>
            public static bool TryParseGainDb(ReadOnlySpan<byte> text, out float db)
            {
                db = 0f;
                text = TrimAscii(text);
                if (text.Length >= 2 && (text[^2] | 0x20) == (byte)'d' && (text[^1] | 0x20) == (byte)'b') text = TrimAscii(text[..^2]);
                if (text.IsEmpty
                    || !float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
                    || !float.IsFinite(value) || Math.Abs(value) > MaxGainDb) return false;
                db = value;
                return true;
            }

            /// <summary>A peak tag: a LINEAR amplitude (<c>0.988553</c>). False (0) when it is not a believable peak (<see cref="Spotify.Audio.SanePeak"/>).</summary>
            public static bool TryParsePeak(ReadOnlySpan<byte> text, out float peak)
            {
                peak = 0f;
                text = TrimAscii(text);
                if (text.IsEmpty
                    || !float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)) return false;
                peak = Spotify.Audio.SanePeak(value);
                return peak > 0f;
            }

            /// <summary>An Ogg Vorbis COMMENT HEADER packet (type 3, <c>"vorbis"</c>, then the comment block) — what the adapter already
            /// reads at open. Nothing (<c>default</c>) for a packet that is not one, or a block that is cut short.</summary>
            public static ReplayGainTags FromVorbisComment(ReadOnlySpan<byte> packet)
                => packet.Length >= 7 && packet[0] == 3 ? FromCommentBlock(packet[7..]) : default;

            /// <summary>A Vorbis comment BLOCK (vendor string, count, then length-prefixed <c>KEY=value</c> fields, all little-endian) — the body of a
            /// Vorbis comment header after its 7-byte prefix, and of a FLAC VORBIS_COMMENT block. The last of a repeated key wins.</summary>
            public static ReplayGainTags FromCommentBlock(ReadOnlySpan<byte> block)
            {
                int pos = 0;
                if (!ReadLe(block, ref pos, out uint vendor) || vendor > (uint)(block.Length - pos)) return default;
                pos += (int)vendor;
                if (!ReadLe(block, ref pos, out uint count)) return default;
                ReadOnlySpan<byte> trackGain = default, trackPeak = default, albumGain = default, albumPeak = default;
                for (uint i = 0; i < count; i++)
                {
                    if (!ReadLe(block, ref pos, out uint len) || len > (uint)(block.Length - pos)) break;
                    ReadOnlySpan<byte> field = block.Slice(pos, (int)len);
                    pos += (int)len;
                    int eq = field.IndexOf((byte)'=');
                    if (eq <= 0) continue;
                    ReadOnlySpan<byte> key = field[..eq], value = field[(eq + 1)..];
                    if (System.Text.Ascii.EqualsIgnoreCase(key, "REPLAYGAIN_TRACK_GAIN"u8)) trackGain = value;
                    else if (System.Text.Ascii.EqualsIgnoreCase(key, "REPLAYGAIN_TRACK_PEAK"u8)) trackPeak = value;
                    else if (System.Text.Ascii.EqualsIgnoreCase(key, "REPLAYGAIN_ALBUM_GAIN"u8)) albumGain = value;
                    else if (System.Text.Ascii.EqualsIgnoreCase(key, "REPLAYGAIN_ALBUM_PEAK"u8)) albumPeak = value;
                }
                return Of(trackGain, trackPeak, albumGain, albumPeak);
            }

            static bool ReadLe(ReadOnlySpan<byte> b, ref int pos, out uint v)
            {
                v = 0;
                if (pos < 0 || pos + 4 > b.Length) return false;
                v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
                pos += 4;
                return true;
            }

            static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> s)
            {
                int start = 0, end = s.Length;
                while (start < end && s[start] <= (byte)' ') start++;
                while (end > start && s[end - 1] <= (byte)' ') end--;
                return s[start..end];
            }
        }

        /// <summary>V-PA15, live (D5): re-ramp every live voice's gain slot to the factor the CURRENT settings give its figures — each by
        /// <c>now / baked</c> for THAT voice (<see cref="SetVoiceGainRatio"/>), so the decoders keep their folded factor and nothing reopens.
        /// Called by the settings rows (the toggle, the mode, the album switch) via <see cref="Settings.ApplyNormalization"/>; with nothing
        /// playing there is nothing to ramp, and the next open reads the setting itself (<see cref="GainLinear"/>).</summary>
        public static void SetNormalization()
        {
            bool enabled = Platform.Settings.Get(Platform.Keys.NormalizationEnabled);
            NormalizationMode mode = ModeOf(Platform.Settings.Get(Platform.Keys.NormalizationMode));
            bool album = Platform.Settings.Get(Platform.Keys.NormalizationAlbum);
            PcmAudioSession? sess;
            KeyValuePair<long, BakedVoice>[] voices;
            long activeId;
            lock (s_gate) { sess = s_session; voices = s_bakedFactor.ToArray(); activeId = s_activePrimaryId; }
            int ramped = 0;
            float shown = 1f;
            if (sess is not null)
                foreach (var (voiceId, voice) in voices)
                {
                    float now = voice.Figures.Factor(enabled, mode, album);
                    if (voiceId == activeId) shown = now;
                    if (SetVoiceGainRatio(sess, voiceId, now, voice.Factor)) ramped++;
                }
            Log.Info("audio", $"normalization enabled={enabled} mode={mode} album={album} voices={ramped}/{voices.Length} factor={shown:0.###}");
        }

        /// <summary>One voice's live ramp: <c>SetVoiceGain(id, now / baked, 50 ms)</c> — ABSOLUTE relative to what its decoder folded in, so
        /// a repeated call is idempotent. False for a voice the session no longer knows, an unusable baked factor, or a full command queue
        /// (the previous factor stays in force until the next change). PURE ratio, one engine call.</summary>
        static bool SetVoiceGainRatio(PcmAudioSession sess, long voiceId, float now, float baked)
            => baked > 0f && float.IsFinite(baked) && sess.SetVoiceGain(voiceId, now / baked, rampMs: 50);

        // ── 9a. Ogg Vorbis: the engine-facing adapter over the CORE decoder (Vorbis plan §6) ─────────────────────────
        //
        // The CORE — the Ogg page reader, the page index, the seek planner (`Playback.Audio.Ogg.cs`) and the Vorbis I
        // decoder (`Playback.Audio.Vorbis.cs`), owner V — is pure over spans. THIS is the I/O around it, in the FLAC
        // adapter's shape: a byte window over the byte seam, the CORE over the window, the engine's resampler at the
        // decode edge. Three facts from the decoder's side shape it:
        //   · OUTPUT IS ALWAYS INTERLEAVED STEREO (mono duplicated, 3+ channels downmixed), so the mix format reported
        //     is `Vorbis.OutputChannels`, never the file's channel count.
        //   · `Reader.Reposition(WindowOffset + Cursor)` IS A REFILL and keeps a packet spanning the boundary; any other
        //     offset is a seek. A landing always restarts from nothing (`VorbisClock.Restart`), so the peek and the
        //     decode walk the same packets.
        //   · A PAGE'S GRANULE IS THE END of the last packet completed on it (`SeekPlan.OffsetGranule` included), so a
        //     position is always `granuleAtEnd − frames`, never the resume page's granule itself.

        /// <summary>THE ADAPTER'S SAMPLE CLOCK over the Ogg granules (Vorbis I §A.2), pure so a test can drive it with
        /// a real file's packet sequence. Every packet is placed from the page that ends it; the end of the stream is
        /// trimmed at the EOS granule; a seek's landing drops the frames before its target. Granule domain throughout:
        /// the ORIGIN is the granule of the first frame a decode from the first audio page produces — 0 for a
        /// libvorbis file, negative when the first page claims fewer samples than its packets make (a lead-in the
        /// engine's `TrimmingSource` skips), positive for a stream cut out of a longer one.</summary>
        public struct VorbisClock
        {
            /// <summary>No position yet: a landing whose peek could not reach a page granule.</summary>
            public const long Unknown = long.MinValue;

            /// <summary>The granule of the next frame the decoder produces — the end of the last admitted packet.</summary>
            public long Position;

            /// <summary>After a seek: frames before this granule are dropped. <see cref="Unknown"/> when there is none.</summary>
            public long Target;

            /// <summary>The most silence one hole is ever padded with, in granules (~12 s at 44.1 kHz). A larger gap is not "a lost
            /// page", it is a wrong pin: the clock re-pins and pads nothing.</summary>
            public const long MaxPad = 1 << 19;

            bool _hole;     // S-7: a hole was crossed; the next page pin names the span that is missing
            int _owed;      // frames of silence owed to the next run that hands frames out

            public static VorbisClock At(long position) => new() { Position = position, Target = Unknown };

            /// <summary>S-7: packets were lost here (a page-sequence hole, an undecodable packet) — the decoder re-primed and the
            /// running count is now SHORT by exactly what was lost. The next page granule that pins a start names it
            /// (<see cref="Admit"/>) and the run that follows carries it as <see cref="Run.Pad"/>. Ignored while a seek's landing
            /// is still dropping frames to <see cref="Target"/>: nothing before the target is heard.</summary>
            public void Hole()
            {
                if (Target == Unknown) _hole = true;
            }

            /// <summary>What to hand out of one decoded packet: <see cref="Pad"/> frames of SILENCE first (what a hole cost,
            /// S-7), then skip <see cref="Skip"/> frames of its output and hand out <see cref="Count"/>.
            /// <see cref="Start"/> is the packet's first granule, <see cref="Unknown"/> when it could not be placed.</summary>
            public readonly record struct Run(int Skip, int Count, long Start, int Pad = 0);

            /// <summary>Place one decoded packet. <paramref name="granuleAtEnd"/> is the page granule when the packet
            /// is the last one completed on its page (else −1); <paramref name="eos"/> says that page is the stream's
            /// last, whose granule TRUNCATES rather than ends (libvorbis block.c:864-936).</summary>
            public Run Admit(int frames, long granuleAtEnd, bool eos)
            {
                if (frames < 0) frames = 0;
                long start;
                bool pinned = granuleAtEnd >= 0 && !eos;
                if (pinned) start = granuleAtEnd - frames;                                // the page pins it
                else if (Position != Unknown) start = Position;                           // the running count
                else if (granuleAtEnd >= 0) start = granuleAtEnd - frames;               // the EOS page, with no clock
                else return new Run(frames, 0, Unknown);                                  // nothing placeable yet
                // S-7: after a hole the running count only counts frames that were really decoded, so the first pin AHEAD of it is
                // exactly the span that is missing.
                if (_hole && pinned && Position != Unknown)
                {
                    long gap = start - Position;
                    if (gap > 0 && gap <= MaxPad) _owed += (int)gap;
                    _hole = false;
                }
                Position = start + frames;
                int keep = eos && granuleAtEnd >= 0 ? Ogg.TrimTail(start, frames, granuleAtEnd) : frames;
                int skip = Target != Unknown && Target > start ? (int)Math.Min(keep, Target - start) : 0;
                if (keep - skip <= 0) return new Run(keep, 0, start);
                Target = Unknown;                                                         // reached: frames flow from here
                int pad = _owed;
                _owed = 0;
                return new Run(skip, keep - skip, start, pad);
            }

            /// <summary>The granule of the first frame a decode that resumes at <paramref name="windowOffset"/> will
            /// produce (Vorbis plan §4.3): walk the packets WITHOUT decoding — <c>PeekFrames</c> reads the first byte
            /// of each — to the first one that ends a page, and subtract every packet's frames up to it from that
            /// page's granule. The first packet primes and yields nothing, exactly as <c>DecodePacket</c> after
            /// <c>Prime</c> does. <see cref="Unknown"/> when the window ends first (<paramref name="needMore"/>) or the
            /// page is the EOS page. Either way the reader is left restarted at the window, so the decode that follows
            /// walks the same packets.</summary>
            public static long LandingStart(Ogg.Reader reader, Vorbis.Decoder decoder, ReadOnlySpan<byte> window,
                long windowOffset, out bool needMore)
            {
                needMore = false;
                Restart(reader, windowOffset);
                long start = Unknown, frames = 0;
                int prevN = 0;
                while (true)
                {
                    Ogg.Reader.Next next = reader.NextPacket(window, out ReadOnlySpan<byte> packet, out long granule);
                    if (next == Ogg.Reader.Next.Corrupt) continue;
                    if (next == Ogg.Reader.Next.NeedMore) { needMore = true; break; }
                    // Anything else ends the peek — a `Hole` (S-7) included: frames counted before a gap cannot pin a granule after it,
                    // so the start stays Unknown and the clock runs unpinned until the first page granule the decode meets.
                    if (next != Ogg.Reader.Next.Packet) break;
                    int f = decoder.PeekFrames(packet, ref prevN);
                    if (f < 0) continue;                                    // not audio: DecodePacket skips it too
                    frames += f;
                    if (granule < 0) continue;
                    if (!reader.SawEos) start = granule - frames;
                    break;
                }
                Restart(reader, windowOffset);
                return start;
            }

            /// <summary>Re-point <paramref name="reader"/> at <paramref name="windowOffset"/> as a SEEK, never a refill:
            /// <c>Reposition</c> keeps a spanning packet whenever the offset happens to equal <c>WindowOffset +
            /// Cursor</c>, and −1 is never that.</summary>
            public static void Restart(Ogg.Reader reader, long windowOffset)
            {
                reader.Reposition(-1);
                reader.Reposition(windowOffset);
            }

            /// <summary>The engine's seek frame — MIX domain, counted from the decoder's own first frame — as a granule.</summary>
            public static long TargetGranule(long mixFrame, int sourceRate, int mixRate, long origin)
                => ToSource(Math.Max(0, mixFrame), sourceRate, mixRate) + origin;

            /// <summary>A granule as the MIX-domain frame counted from the decoder's first frame (what a seek answers).</summary>
            public static long MixFrameOf(long granule, int sourceRate, int mixRate, long origin)
                => ToMix(Math.Max(0, granule - origin), sourceRate, mixRate);

            public static long ToMix(long sourceFrames, int sourceRate, int mixRate)
                => sourceRate <= 0 || mixRate <= 0 || sourceRate == mixRate
                    ? sourceFrames : (long)Math.Round((double)sourceFrames * mixRate / sourceRate);

            public static long ToSource(long mixFrames, int sourceRate, int mixRate)
                => sourceRate <= 0 || mixRate <= 0 || sourceRate == mixRate
                    ? mixFrames : (long)Math.Round((double)mixFrames * sourceRate / mixRate);

            /// <summary>§4.4's <see cref="GaplessInfo"/> from the granules, in MIX frames. The lead-in is the stream
            /// before granule 0; the exact length is the last granule counted from where the emitted audio starts.
            /// With no tail it is <see cref="GaplessInfo.None"/>-shaped: the engine takes the declared duration and the
            /// decoder still trims at the EOS granule when it gets there.</summary>
            public static GaplessInfo Gapless(long origin, long lastGranule, int sourceRate, int mixRate)
            {
                int leadIn = origin < 0 ? (int)Math.Min(int.MaxValue, ToMix(-origin, sourceRate, mixRate)) : 0;
                if (lastGranule < 0) return new GaplessInfo(leadIn, 0, -1, TailKnown: false);
                long exact = Math.Max(0, lastGranule - Math.Max(0, origin));
                return new GaplessInfo(leadIn, 0, ToMix(exact, sourceRate, mixRate), TailKnown: true);
            }

            /// <summary>Is a tail granule believable for a track of <paramref name="durationMs"/>? The stream layer finds
            /// it with a byte scan for the last <c>OggS</c>; a figure more than max(5 s, 5 %) from the catalogue's
            /// duration is not the last page, and a wrong exact length would cut (or pad) the join.</summary>
            public static bool PlausibleTail(long granule, long durationMs, int sourceRate)
            {
                if (granule < 0) return false;
                if (durationMs <= 0 || sourceRate <= 0) return true;
                long expected = durationMs * sourceRate / 1000;
                return Math.Abs(granule - expected) <= Math.Max(5L * sourceRate, expected / 20);
            }

            /// <summary>The granule of the last CRC-valid page in <paramref name="tail"/> that ends a packet, or −1 —
            /// a local file's exact length, read at open from its last bytes.</summary>
            public static long LastGranule(ReadOnlySpan<byte> tail)
            {
                long last = -1;
                int at = 0;
                while ((at = Ogg.FindPage(tail, at, out Ogg.Page page, out _)) >= 0)
                {
                    if (page.Granule >= 0) last = page.Granule;
                    at += page.Length;
                }
                return last;
            }
        }

        /// <summary>The byte seam's random-access face (Vorbis plan §5.5), reached by type-testing the
        /// <see cref="IMediaByteSource"/> a decoder is handed. CONTAINER offsets. <see cref="RingSource"/> implements it;
        /// a local file keeps the engine's <c>Seek</c> + <c>Read</c>.</summary>
        public interface IRandomAccessBytes
        {
            /// <summary>Copy [offset, offset + dst.Length): &gt;0 copied (short at a store's edge), 0 only at EOF, −1 when
            /// <paramref name="epoch"/> is no longer the source's or the source is gone, <see cref="InterruptedRead"/> (−2)
            /// when a seek interrupted the wait — no bytes, not the end: the adapter hands out silence until its own
            /// <c>Seek</c> arrives (and flushes it) — and <see cref="StarvedRead"/> (−3) when the bounded wait ran out: the
            /// link is starving and the caller asks AGAIN (D5). Blocks, bounded.</summary>
            int ReadAt(long offset, Span<byte> dst, uint epoch);

            /// <summary>A seek: adopt <paramref name="epoch"/>, cancel what is in flight, and put [probeOffset,
            /// probeOffset + probeBytes) on the wire FIRST unless it is already resident.</summary>
            void Retarget(long probeOffset, int probeBytes, uint epoch);

            /// <summary>The seek landed at <paramref name="offset"/>: the sequential fill continues from there.</summary>
            void ResumeFrom(long offset);

            /// <summary>The epoch the source serves.</summary>
            uint Epoch { get; }

            /// <summary>The last Ogg page's granule once the tail has been read, else −1.</summary>
            long TailGranule { get; }
        }

        /// <summary>What <see cref="IRandomAccessBytes.ReadAt"/> answers while a seek has interrupted its wait
        /// (= <see cref="Spotify.Audio.Body.Interrupted"/>).</summary>
        public const int InterruptedRead = Spotify.Audio.Body.Interrupted;

        /// <summary>What <see cref="IRandomAccessBytes.ReadAt"/> answers when its bounded wait ran out with the source alive
        /// (= <see cref="Spotify.Audio.Body.Starved"/>): never the end of the track.</summary>
        public const int StarvedRead = Spotify.Audio.Body.Starved;

        /// <summary>A byte source that knows the normalization figure of the file behind it, possibly only once its first
        /// bytes have landed (an Ogg body opened with no header at hand learns the gain off chunk 0, G-107). The Vorbis
        /// adapter reads it after the header packets — by then the header bytes have been served — so a late figure still
        /// reaches the first sample.</summary>
        public interface INormalizationSource
        {
            /// <summary>The track gain in dB; 0 when none.</summary>
            float GainDb { get; }
            /// <summary>The linear true peak that caps a boost; 0 when unknown.</summary>
            float Peak { get; }
            /// <summary>The ALBUM gain in dB (D5, V-PA13); 0 when the file carries none. A default member: a source that knows no album
            /// (a fake, a module stream) need not say so.</summary>
            float AlbumGainDb => 0f;
            /// <summary>The album's linear true peak; 0 when unknown.</summary>
            float AlbumPeak => 0f;
        }

        /// <summary>A decoder that folds the normalization factor into the PCM it hands out (V-PA15): the value it FOLDED. The pump
        /// records it per voice (<see cref="s_bakedFactor"/>) so a live normalization change can ramp a playing voice by
        /// <c>now / baked</c> through the voice's gain slot without reopening its decoder. 1 = unity (off, or no figure).</summary>
        public interface IGainFolding
        {
            /// <summary>The linear factor folded into this decoder's output (the figure it OPENED with, the byte source's
            /// late one included).</summary>
            float AppliedGainLinear { get; }

            /// <summary>The figures behind <see cref="AppliedGainLinear"/> — the ones a live mode switch recomputes the factor from: the
            /// byte source's late figure and a local file's ReplayGain tags included (the pump's <see cref="Opened"/> knows neither).
            /// Null = this decoder does not say, and the pump uses the <see cref="Opened"/>'s own figures. A default member so a decoder
            /// with nothing to add (MP3, AAC) stays as it is.</summary>
            NormalizationFigures? AppliedFigures => null;
        }

        /// <summary>A voice's baked normalization: the factor its decoder folded and the figures it was computed from.</summary>
        readonly record struct BakedVoice(float Factor, NormalizationFigures Figures);

        /// <summary>Remember the linear factor <paramref name="decoder"/> folded into voice <paramref name="voiceId"/>'s PCM
        /// (V-PA15), with the figures behind it. A decoder that does not expose it (none of the shipped ones: Vorbis, FLAC, MP3 and
        /// AAC all do) is assumed to have folded what <see cref="GainLinear"/> answers for its <paramref name="opened"/> figures right
        /// now. Caller holds <see cref="s_gate"/>. The table stays tiny: entries for voices that are neither the live one, the one
        /// being added nor the pending join's are dropped.</summary>
        static void NoteBakedFactor(long voiceId, IAudioDecoder? decoder, in Opened opened)
        {
            var openedFigures = new NormalizationFigures(opened.GainDb, opened.Peak, opened.AlbumGainDb, opened.AlbumPeak);
            s_bakedFactor[voiceId] = decoder is IGainFolding folding
                ? new BakedVoice(folding.AppliedGainLinear, folding.AppliedFigures ?? openedFigures)
                : new BakedVoice(GainLinear(opened.GainDb, opened.Peak, opened.AlbumGainDb, opened.AlbumPeak), openedFigures);
            if (s_bakedFactor.Count <= 6) return;
            foreach (long id in new List<long>(s_bakedFactor.Keys))
                if (id != voiceId && id != s_activePrimaryId && id != s_joinVoiceId) s_bakedFactor.Remove(id);
        }

        /// <summary>At most this many frames of silence per decoder read while a seek's interrupt is pending: small enough
        /// that the decode-ahead ring reaches its target in a few passes and the producer returns to its seek mailbox.</summary>
        const int InterruptSilenceFrames = 1_024;

        /// <summary>Read and clear an adapter's interrupt latch.</summary>
        static bool TakeInterrupt(ref bool latch)
        {
            if (!latch) return false;
            latch = false;
            return true;
        }

        /// <summary>Hand out up to <see cref="InterruptSilenceFrames"/> frames of silence (interleaved, mix domain).</summary>
        static int Silence(Span<float> dst, int wantFrames, int channels)
        {
            int frames = Math.Min(wantFrames, InterruptSilenceFrames);
            dst[..(frames * channels)].Clear();
            return frames;
        }

        /// <summary>One read at a container offset through the random-access face. A −1 from an epoch that moved
        /// underneath the caller (a refused head splice supersedes the ring — the only epoch change a decoder does not
        /// make itself) is retried ONCE at the new epoch, where the ring serves the true bytes. An
        /// <see cref="InterruptedRead"/> passes straight through. A <see cref="StarvedRead"/> is asked again, as long as it
        /// takes (D5): the pump reports the stall and owns its budget, and a Stop closes the source, which is what ends
        /// the wait.</summary>
        public static int ReadAtEpoch(IRandomAccessBytes source, long offset, Span<byte> dst, ref uint epoch)
        {
            bool retried = false;
            while (true)
            {
                int n = source.ReadAt(offset, dst, epoch);
                if (n == StarvedRead) continue;
                if (n >= 0 || n == InterruptedRead || retried) return n;
                uint now = source.Epoch;
                if (now == epoch) return n;
                epoch = now;
                retried = true;
            }
        }

        /// <summary>The Vorbis adapter's per-track working set — the 192 KiB pinned probe window and the page reader's spanning
        /// buffer — kept for the next track instead of re-pinned per track (G-134). Two sets: the playing voice and the prepared
        /// one; the retiring voice of a crossfade takes a fresh set. A set is returned only by the adapter's <c>Dispose</c>, which
        /// the engine calls once the decode producer that used it has stopped. The CORE decoder (its 0.3–0.7 MB of setup tables)
        /// is NOT part of the set any more: it is rented by setup hash AFTER the header packets are read
        /// (<see cref="VorbisDecoderPool"/>, V-PA35) — the window is needed to read the headers, the decoder only to open them.</summary>
        public sealed class VorbisWorkingSet
        {
            public const int PoolSize = 2;

            internal byte[] Window = [];
            internal Ogg.Reader? Reader;

            static readonly Lock Gate = new();
            static readonly VorbisWorkingSet?[] s_pool = new VorbisWorkingSet?[PoolSize];

            /// <summary>Sets waiting for the next track.</summary>
            public static int Pooled
            {
                get
                {
                    lock (Gate)
                    {
                        int n = 0;
                        foreach (VorbisWorkingSet? set in s_pool) if (set is not null) n++;
                        return n;
                    }
                }
            }

            internal static VorbisWorkingSet Rent()
            {
                lock (Gate)
                {
                    for (int i = 0; i < s_pool.Length; i++)
                    {
                        if (s_pool[i] is { } set) { s_pool[i] = null; return set; }
                    }
                }
                return new VorbisWorkingSet();
            }

            internal static void Return(VorbisWorkingSet set)
            {
                lock (Gate)
                {
                    for (int i = 0; i < s_pool.Length; i++)
                    {
                        if (s_pool[i] is null) { s_pool[i] = set; return; }
                        if (ReferenceEquals(s_pool[i], set)) return;
                    }
                }
            }
        }

        /// <summary>The Vorbis CORE decoders, pooled by SETUP HASH (V-PA35). A second decoder opened on the track that is already
        /// playing — a seek's voice swap, a scrub's grain decoder — is handed one whose tables are already built for exactly that
        /// setup: <c>Vorbis.Decoder.Open</c> then resets the lapping state and skips the parse, a few microseconds instead of the
        /// ~1 ms table build. <see cref="Rent"/> prefers a pooled decoder whose <c>SetupHash</c> equals the asked one, else any
        /// pooled one (it re-parses in place, allocating only if the new setup is larger), else a new one. Capacity four: the
        /// playing voice, the prepared one, the retiring voice of a crossfade, and the seek/scrub voice. A decoder is returned only
        /// by the adapter's <c>Dispose</c>, once its decode producer has stopped.</summary>
        public static class VorbisDecoderPool
        {
            public const int Capacity = 4;

            static readonly Lock Gate = new();
            static readonly Vorbis.Decoder?[] s_pool = new Vorbis.Decoder?[Capacity];

            /// <summary>Decoders waiting for the next open.</summary>
            public static int Pooled
            {
                get
                {
                    lock (Gate)
                    {
                        int n = 0;
                        foreach (Vorbis.Decoder? d in s_pool) if (d is not null) n++;
                        return n;
                    }
                }
            }

            /// <summary>A decoder for <paramref name="setupHash"/> (<c>Vorbis.Decoder.HashSetup</c>): a matching pooled one first.</summary>
            public static Vorbis.Decoder Rent(ulong setupHash)
            {
                lock (Gate)
                {
                    int any = -1;
                    for (int i = 0; i < s_pool.Length; i++)
                    {
                        if (s_pool[i] is not { } d) continue;
                        if (d.SetupHash == setupHash) { s_pool[i] = null; return d; }
                        if (any < 0) any = i;
                    }
                    if (any >= 0) { Vorbis.Decoder d = s_pool[any]!; s_pool[any] = null; return d; }
                }
                return new Vorbis.Decoder();
            }

            internal static void Return(Vorbis.Decoder decoder)
            {
                lock (Gate)
                {
                    for (int i = 0; i < s_pool.Length; i++)
                    {
                        if (s_pool[i] is null) { s_pool[i] = decoder; return; }
                        if (ReferenceEquals(s_pool[i], decoder)) return;
                    }
                }
            }
        }

        /// <summary>Ogg Vorbis over the CORE reader and decoder (Vorbis plan §6.2). Blocks in the byte seam and nowhere
        /// else; allocates in <see cref="TryOpen"/> and nowhere after (P8) — the window, the reader's spanning buffer and
        /// the decoder's tables are sized once there, out of a pooled <see cref="VorbisWorkingSet"/>.</summary>
        public sealed class VorbisAudioDecoder : IAudioDecoder, IDisposable, IGainFolding
        {
            /// <summary>The probe window's 192 KiB ceiling, and more than two maximal pages, so a landing's peek always
            /// sees the page that pins it.</summary>
            const int WindowBytes = 192 * 1024;

            /// <summary>The most hole-silence one read hands out (source frames), and the shared zero buffer it is cut from — a
            /// hole's pad is a rare event, so ONE static buffer serves every adapter (≤ 8 mix channels).</summary>
            const int PadChunkFrames = 1_024;
            static readonly float[] s_padZeros = new float[PadChunkFrames * 8];

            /// <summary>How much of a local file's end is read at open for its last granule.</summary>
            const int TailScanBytes = 64 * 1024;

            /// <summary>Header packets are a few KB; a stream whose three headers are not parsed inside this many bytes
            /// is not one this decoder plays.</summary>
            const int HeaderScanLimit = 1 << 20;

            float _gainLinear;
            // The figures `_gainLinear` was computed from (the ctor's, then the byte source's late ones, then the file's ReplayGain tags).
            NormalizationFigures _figures;
            readonly long _durationMs;
            VorbisWorkingSet? _set;
            Vorbis.Decoder? _dec;
            Ogg.Reader? _ogg;
            byte[] _win = [];
            IMediaByteSource? _src;
            IRandomAccessBytes? _ra;
            MixFormat _target;
            PolyphaseResampler? _resampler;
            float[] _conform = [];            // the held packet re-laid for a mix that is not stereo (never, today)
            VorbisClock _clock;
            long _winStart;                   // container offset of _win[0]
            int _winLen;
            int _hold, _holdOffset;           // frames of the current packet not yet handed out, and where they start
            long _heldStart;                  // the granule of the first held frame
            long _firstAudioPage, _origin, _tail = -1;
            int _rate;
            uint _epoch;
            bool _eof, _interrupted;
            // The stream's last packet is out: what is left to hand out is the resampler's trailing half kernel (V-PE21), over as many
            // reads as `dst` needs. `_eof` follows once `Flush` runs dry.
            bool _ending;
            // S-1 (V-PA16): the LANDING read of the last `Seek` was interrupted (a scrub's next seek is on its way), so the decoder
            // is not primed and the clock is not placed. Not the end of the track: `Read` hands out silence and repeats the
            // read of [_landOffset, _landOffset + _landBytes) until it lands — or the next `Seek` drops the latch.
            bool _needsLanding;
            long _landOffset, _landTarget;
            int _landBytes;
            // S-7: silence (SOURCE-rate frames) owed BEFORE the held packet — the span a page-sequence hole or an undecodable packet
            // cost, named by the next page granule (`VorbisClock.Run.Pad`). `Read` hands it out, through the resampler, ahead of
            // the held frames, so the track keeps its length and the audio after the hole keeps its place.
            int _pad;
            // V-PA36: how many of the stream layer's landing-time page-index entries are already merged into the reader's index.
            int _indexSeeded;

            /// <summary>The linear factor folded into this decoder's output (<see cref="IGainFolding"/>, V-PA15).</summary>
            public float AppliedGainLinear => _gainLinear;

            /// <summary>The figures behind <see cref="AppliedGainLinear"/> (<see cref="IGainFolding.AppliedFigures"/>).</summary>
            public NormalizationFigures? AppliedFigures => _figures;

            /// <param name="gainDb">The normalization figure (`Opened.GainDb`: the catalogue's, else the header's byte 144).</param>
            /// <param name="durationMs">The declared duration: the seek estimate's slope until the tail is known, and the
            /// yardstick a tail granule is checked against.</param>
            /// <param name="peak">The track's linear true peak (`Opened.Peak`, header byte 148), which caps a boost; 0 = unknown.</param>
            /// <param name="albumGainDb">The ALBUM gain (`Opened.AlbumGainDb`, header byte 152), 0 = none: album mode's figure (D5).</param>
            /// <param name="albumPeak">The album's linear true peak (`Opened.AlbumPeak`, header byte 156); 0 = unknown.</param>
            public VorbisAudioDecoder(float gainDb, long durationMs = 0, float peak = 0f, float albumGainDb = 0f, float albumPeak = 0f)
            {
                _figures = new NormalizationFigures(gainDb, peak, albumGainDb, albumPeak);
                _gainLinear = GainLinear(gainDb, peak, albumGainDb, albumPeak);
                _durationMs = Math.Max(0, durationMs);
            }

            public GaplessInfo Gapless { get; private set; } = GaplessInfo.None;

            public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
            {
                info = default;
                _src = src;
                _ra = src as IRandomAccessBytes;
                _target = target;
                _eof = false;
                _ending = false;
                _needsLanding = false;
                _hold = _holdOffset = 0;
                _pad = 0;
                if (!src.TryOpen(new DataSpec { Position = 0, Length = -1 })) return false;
                if (_set is null)
                {
                    VorbisWorkingSet set = VorbisWorkingSet.Rent();
                    if (set.Window.Length < WindowBytes) set.Window = GC.AllocateUninitializedArray<byte>(WindowBytes, pinned: true);
                    set.Reader ??= new Ogg.Reader();
                    _set = set;
                    _win = set.Window;
                    _ogg = set.Reader;
                }
                Ogg.Reader ogg = _ogg!;
                ogg.Reset();
                ogg.Index.Clear();
                _indexSeeded = 0;
                _epoch = _ra?.Epoch ?? 0;

                long localTail = _ra is null ? ScanLocalTail() : -1;          // first: it moves a sequential source
                _winStart = 0;
                _winLen = 0;
                VorbisClock.Restart(ogg, 0);

                // The three header packets (Vorbis I §4.2): identification, comment, setup — inside the clear head.
                Span<byte> ident = stackalloc byte[64];                        // the identification header is 30 bytes
                if (!NextHeader(out ReadOnlySpan<byte> first) || Vorbis.HeaderType(first) != 1 || first.Length > ident.Length)
                    return false;
                first.CopyTo(ident);
                ident = ident[..first.Length];
                if (!NextHeader(out ReadOnlySpan<byte> comment) || Vorbis.HeaderType(comment) != 3) return false;
                // Read NOW: the next header's refill may move the window the packet is a view of (N-3, V-PA18).
                ReplayGainTags replayGain = ReplayGainTags.FromVorbisComment(comment);
                if (!NextHeader(out ReadOnlySpan<byte> setup)) return false;
                // The header pages have been served, so the file's own normalization figure is known by now even when the
                // body opened with no header at hand (G-107): the source's figure wins over the one this adapter was built with.
                if (src is INormalizationSource normalization)
                    _figures = new NormalizationFigures(normalization.GainDb, normalization.Peak, normalization.AlbumGainDb, normalization.AlbumPeak);
                // N-3, V-PA18: and when neither the catalogue nor the Spotify header carried a figure, the file's own ReplayGain tags (a
                // local .ogg), read from the comment packet at open — at +4 dB, into the −14 frame everything else is in.
                _figures = NormalizationFigures.WithReplayGain(in _figures, in replayGain);
                _gainLinear = GainLinear(_figures.GainDb, _figures.Peak, _figures.AlbumGainDb, _figures.AlbumPeak);
                // V-PA35: the decoder is rented NOW, by the hash of the setup just read — a pooled decoder whose tables were built
                // from exactly these bytes skips the parse (a seek's second decoder on the playing track always does).
                ulong setupHash = Vorbis.Decoder.HashSetup(ident, setup);
                Vorbis.Decoder dec = _dec ??= VorbisDecoderPool.Rent(setupHash);
                bool setupHit = dec.SetupHash == setupHash;
                long setupStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!dec.Open(ident, setup, _gainLinear)) return false;
                Log.Info("audio", "audio.open.vorbis setupMs="
                    + System.Diagnostics.Stopwatch.GetElapsedTime(setupStart).TotalMilliseconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                    + " setupHit=" + (setupHit ? 1 : 0));
                _rate = dec.SampleRate;
                if (_rate <= 0) return false;

                // Audio begins on a fresh page (§4.2): the cursor past the setup is the first audio page, and the
                // origin is peeked from it before a single audio packet decodes (§4.4).
                _firstAudioPage = ogg.WindowOffset + ogg.Cursor;
                DropBefore(_firstAudioPage);
                _origin = PeekLanding();
                if (_origin == VorbisClock.Unknown) _origin = 0;
                _clock = VorbisClock.At(_origin);

                long tail = _ra?.TailGranule ?? localTail;
                _tail = VorbisClock.PlausibleTail(tail, _durationMs, _rate) ? tail : -1;
                Gapless = VorbisClock.Gapless(_origin, _tail, _rate, target.SampleRate);
                _resampler = _rate != target.SampleRate ? new PolyphaseResampler(_rate, target.SampleRate, target.Channels) : null;
                int ch = Math.Max(1, target.Channels);
                if (ch != Vorbis.OutputChannels && _conform.Length < dec.MaxFrames * ch) _conform = new float[dec.MaxFrames * ch];

                double seconds = _tail >= 0 ? (double)Math.Max(0, _tail - Math.Max(0, _origin)) / _rate
                               : _durationMs / 1000.0;
                info = new DecodedInfo(new MediaContentType(Container.Ogg, CodecId.None, CodecId.Vorbis),
                    new MixFormat(_rate, Vorbis.OutputChannels), TimeSpan.FromSeconds(seconds), default);
                return true;
            }

            /// <summary>The voice's exact length in MIX frames once the stream's last granule is known — including a tail
            /// that landed AFTER the open (the usual case for the playing track: the head starts it before the tail range
            /// lands), which the engine's voice cannot learn once it is built. −1 while unknown. What the pump schedules a
            /// gapless join from instead of the catalogue duration (G-113). Any thread.</summary>
            public long LateExactFrames(int mixRate)
            {
                if (_rate <= 0 || mixRate <= 0) return -1;
                long tail = _tail >= 0 ? _tail : _ra?.TailGranule ?? -1;
                if (!VorbisClock.PlausibleTail(tail, _durationMs, _rate)) return -1;
                return VorbisClock.ToMix(Math.Max(0, tail - Math.Max(0, _origin)), _rate, mixRate);
            }

            /// <summary>The engine disposes the decoder once its decode producer has stopped: the working set goes back to
            /// the pool for the next track, and every later call on this instance answers "nothing".</summary>
            public void Dispose()
            {
                VorbisWorkingSet? set = Interlocked.Exchange(ref _set, null);
                Vorbis.Decoder? dec = Interlocked.Exchange(ref _dec, null);
                _ogg = null;
                _win = [];
                if (set is not null) VorbisWorkingSet.Return(set);
                if (dec is not null) VorbisDecoderPool.Return(dec);
            }

            /// <summary>The next header packet, refilling as it goes. A header is never corrupt and never the end.</summary>
            bool NextHeader(out ReadOnlySpan<byte> packet)
            {
                Ogg.Reader ogg = _ogg!;
                while (_winStart + _winLen <= HeaderScanLimit)
                {
                    Ogg.Reader.Next next = ogg.NextPacket(_win.AsSpan(0, _winLen), out packet, out _);
                    if (next == Ogg.Reader.Next.Packet) return true;
                    if (next == Ogg.Reader.Next.Hole) continue;           // a header page lost: the header that follows will not be the one asked for
                    if (next != Ogg.Reader.Next.NeedMore || !Fill()) break;
                }
                packet = default;
                return false;
            }

            /// <summary>A local file's exact length: its last granule, from its last bytes, read before anything else.
            /// Only where a seek is cheap — a module stream answers from the declared duration instead.</summary>
            long ScanLocalTail()
            {
                IMediaByteSource src = _src!;
                SourceCaps caps = src.Caps;
                if (!caps.Seekable || caps.ExpensiveSeek || src.Length is not long length || length <= 0) return -1;
                long from = Math.Max(0, length - TailScanBytes);
                long last = -1;
                if (src.Seek(from) == from)
                {
                    int want = (int)(length - from), got = 0;
                    while (got < want)
                    {
                        int n = src.Read(_win.AsSpan(got, want - got));
                        if (n <= 0) break;
                        got += n;
                    }
                    last = VorbisClock.LastGranule(_win.AsSpan(0, got));
                }
                return src.Seek(0) == 0 ? last : -1;
            }

            /// <summary>Discard the window before <paramref name="offset"/> and restart the reader there.</summary>
            void DropBefore(long offset)
            {
                int drop = (int)Math.Clamp(offset - _winStart, 0, _winLen);
                if (drop > 0)
                {
                    _win.AsSpan(drop, _winLen - drop).CopyTo(_win);
                    _winLen -= drop;
                    _winStart += drop;
                }
                VorbisClock.Restart(_ogg!, _winStart);
            }

            /// <summary>The landing granule for the window as it stands, growing the window (never moving its start)
            /// while the peek runs out of bytes before a page granule.</summary>
            long PeekLanding()
            {
                for (int grow = 0; ; grow++)
                {
                    long start = VorbisClock.LandingStart(_ogg!, _dec!, _win.AsSpan(0, _winLen), _winStart, out bool needMore);
                    if (!needMore || _winLen >= _win.Length || grow >= 8) return start;
                    int n = ReadRaw(_winStart + _winLen, _win.AsSpan(_winLen));
                    if (n <= 0) return start;
                    _winLen += n;
                }
            }

            /// <summary>Refill the window: drop what the reader consumed, keep the unread tail, and read ONCE. The head,
            /// the ring or the disk hands over whatever it holds, so a decode never waits for bytes it does not need
            /// yet — a window-sized read at the clear head's edge would hold the first sound until the first body range
            /// landed. The continuation is <c>Reposition(WindowOffset + Cursor)</c>, which keeps a spanning packet.</summary>
            bool Fill()
            {
                Ogg.Reader ogg = _ogg!;
                int consumed = Math.Clamp(ogg.Cursor, 0, _winLen);
                if (consumed > 0)
                {
                    _win.AsSpan(consumed, _winLen - consumed).CopyTo(_win);
                    _winLen -= consumed;
                    _winStart += consumed;
                }
                ogg.Reposition(_winStart);
                if (_winLen == _win.Length)
                {
                    // A full window the reader found no page in is not audio: resync past it rather than refuse the
                    // track. (A sequential source's cursor is already at the new start.)
                    _winStart += _winLen;
                    _winLen = 0;
                    VorbisClock.Restart(ogg, _winStart);
                }
                int n = ReadRaw(_winStart + _winLen, _win.AsSpan(_winLen));
                if (n <= 0) return false;
                _winLen += n;
                return true;
            }

            /// <summary>One read at a container offset: the random-access face when the source has one, else the
            /// sequential <c>Read</c>, whose cursor this adapter keeps at <c>_winStart + _winLen</c> by construction. An
            /// interrupted read is "no bytes" here and latches <c>_interrupted</c> for <see cref="Read"/>.</summary>
            int ReadRaw(long offset, Span<byte> dst)
            {
                if (_ra is not { } ra) return _src!.Read(dst);
                int n = ReadAtEpoch(ra, offset, dst, ref _epoch);
                if (n != InterruptedRead) return n;
                _interrupted = true;
                return 0;
            }

            /// <summary>Decode packets until one yields frames the clock hands out. A corrupt or undecodable packet costs
            /// itself, never the track; the next page granule re-pins the clock.</summary>
            bool NextFrames()
            {
                Ogg.Reader ogg = _ogg!;
                Vorbis.Decoder dec = _dec!;
                while (true)
                {
                    Ogg.Reader.Next next = ogg.NextPacket(_win.AsSpan(0, _winLen), out ReadOnlySpan<byte> packet, out long granule);
                    if (next == Ogg.Reader.Next.NeedMore) { if (!Fill()) return false; continue; }
                    if (next == Ogg.Reader.Next.Eos) return false;
                    // S-7 (OV_HOLE): packets are MISSING here (a page-sequence gap, an oversized packet, an undecodable one). The
                    // decoder must not overlap-add the next good packet onto a window two packets back (a click): it re-primes, and the
                    // clock owes the lost span, which the next page granule names and `Read` pads as silence.
                    if (next is Ogg.Reader.Next.Hole or Ogg.Reader.Next.Corrupt) { dec.Prime(); _clock.Hole(); continue; }
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Vorbis.PacketResult decoded = dec.DecodePacket(packet);
                    RecordDecode(DecodeVorbis, decoded == Vorbis.PacketResult.Ok ? dec.Frames : 0, _rate,
                        System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                    if (decoded != Vorbis.PacketResult.Ok)
                    {
                        if (decoded != Vorbis.PacketResult.NotAudio) { dec.Prime(); _clock.Hole(); }   // a stray header packet costs nothing
                        continue;
                    }
                    bool eos = granule >= 0 && ogg.SawEos;
                    VorbisClock.Run run = _clock.Admit(dec.Frames, granule, eos);
                    if (run.Count > 0)
                    {
                        _holdOffset = run.Skip;
                        _hold = run.Count;
                        _heldStart = run.Start + run.Skip;
                        _pad = run.Pad;
                        if (_target.Channels != Vorbis.OutputChannels) ConformHeld(dec);
                        return true;
                    }
                    if (eos) return false;
                }
            }

            /// <summary>Re-lay the held stereo frames for a mix of another width: mono averages, wider pads.</summary>
            void ConformHeld(Vorbis.Decoder dec)
            {
                int ch = Math.Max(1, _target.Channels);
                ReadOnlySpan<float> stereo = dec.OutputSpan.Slice(_holdOffset * 2, _hold * 2);
                Span<float> dst = _conform.AsSpan(0, _hold * ch);
                for (int f = 0; f < _hold; f++)
                {
                    float l = stereo[2 * f], r = stereo[2 * f + 1];
                    int o = f * ch;
                    if (ch == 1) { dst[o] = 0.5f * (l + r); continue; }
                    dst[o] = l;
                    dst[o + 1] = r;
                    for (int c = 2; c < ch; c++) dst[o + c] = 0f;
                }
                _holdOffset = 0;
            }

            ReadOnlySpan<float> Held()
            {
                int ch = Math.Max(1, _target.Channels);
                return ch == Vorbis.OutputChannels
                    ? _dec!.OutputSpan.Slice(_holdOffset * 2, _hold * 2)
                    : _conform.AsSpan(_holdOffset * ch, _hold * ch);
            }

            void Take(int frames)
            {
                _holdOffset += frames;
                _hold -= frames;
                _heldStart += frames;
            }

            public int Read(Span<float> dst)
            {
                if (_dec is null || _eof) return 0;
                int ch = Math.Max(1, _target.Channels);
                int want = dst.Length / ch;
                if (want <= 0) return 0;
                if (_ending) return DrainTail(dst);
                if (_needsLanding && !TryLand())
                {
                    // S-1: still not landed. Interrupted again → silence (the seek that interrupted it is on its way and will
                    // flush this); anything else means the bytes are really gone (a closed body, a true end) — the old ending.
                    if (TakeInterrupt(ref _interrupted)) return Silence(dst, want, ch);
                    _needsLanding = false;
                    _eof = true;
                    return 0;
                }
                while (true)
                {
                    if (_hold == 0 && _pad == 0 && !NextFrames())
                    {
                        // A seek interrupted the byte wait: silence, never an EOF the engine would latch — the seek that is
                        // on its way flushes it (and a seek that never arrives ends the interrupt within a second).
                        if (TakeInterrupt(ref _interrupted)) return Silence(dst, want, ch);
                        _ending = true;                                          // the last packet is out: the resampler's tail, then the end
                        return DrainTail(dst);
                    }
                    if (_pad > 0)
                    {
                        // S-7: the span a hole cost, as silence AHEAD of the frames that follow it — through the resampler, because
                        // the owed count is in source frames and the mix is not.
                        int owed = Math.Min(_pad, Math.Max(1, Math.Min(PadChunkFrames, s_padZeros.Length / ch)));
                        if (_resampler is { IsActive: true } padRs)
                        {
                            ResampleResult pr = padRs.Process(s_padZeros.AsSpan(0, owed * ch), owed, dst);
                            _pad -= pr.Consumed;
                            if (pr.Produced > 0 || pr.Consumed == 0) return pr.Produced;
                            continue;
                        }
                        int silent = Math.Min(want, owed);
                        dst[..(silent * ch)].Clear();
                        _pad -= silent;
                        return silent;
                    }
                    ReadOnlySpan<float> held = Held();
                    if (_resampler is { IsActive: true } rs)
                    {
                        ResampleResult rr = rs.Process(held, _hold, dst);
                        Take(rr.Consumed);
                        // A packet can go wholly into the interpolation history (0 produced): that is not the end.
                        if (rr.Produced > 0 || rr.Consumed == 0) return rr.Produced;
                        continue;
                    }
                    int frames = Math.Min(want, _hold);
                    held[..(frames * ch)].CopyTo(dst);
                    Take(frames);
                    return frames;
                }
            }

            /// <summary>The end of the stream: the resampler's trailing half kernel (V-PE21) over as many reads as <paramref name="dst"/>
            /// needs, then 0 — and only then <c>_eof</c>. With no resampler there is no tail and this is the old end.</summary>
            int DrainTail(Span<float> dst)
            {
                int tail = _resampler is { IsActive: true } rs ? rs.Flush(dst) : 0;
                if (tail > 0) return tail;
                _eof = true;
                return 0;
            }

            /// <summary>MIX-domain frame in (counted from the decoder's first frame), MIX-domain frame reached out, or −1
            /// when the byte seam failed. The plan is the CORE's (<c>Ogg.BeginSeek</c> / <c>TryNextProbe</c> /
            /// <c>Observe</c>); this is the I/O around it: the source is re-targeted BEFORE each probe window is read,
            /// so the probe is the one request in flight, and the landing peeks its position before it decodes.</summary>
            public long Seek(long frame)
            {
                if (_dec is null || _ogg is null || _src is null) return -1;
                if (_ra is null && !_src.Caps.Seekable) return -1;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _hold = _holdOffset = 0;
                _pad = 0;                                                // a hole's owed silence belongs to the position being left
                _eof = false;
                _ending = false;                                         // the tail belonged to the stream being left
                _needsLanding = false;                                   // a new seek supersedes a landing still waiting
                _interrupted = false;                                    // the seek the interrupt was waiting for is here
                _resampler?.Reset();
                if (_ra is { } ra)
                {
                    long tail = ra.TailGranule;                          // the tail usually lands after the open
                    if (_tail < 0 && VorbisClock.PlausibleTail(tail, _durationMs, _rate)) _tail = tail;
                    _epoch = Math.Max(_epoch, ra.Epoch) + 1;
                    // V-PA36: the pages the stream layer already walked as their bytes landed (CDN and disk alike) seed the planner,
                    // so a target inside the fetched window resolves from the index with no probe at all.
                    if (ra is RingSource ring) ring.SeedPageIndex(_ogg.Index, ref _indexSeeded);
                }

                long target = VorbisClock.TargetGranule(frame, _rate, _target.SampleRate, _origin);
                if (_tail >= 0 && target >= _tail)
                {
                    _eof = true;                                         // at or past the end: nothing left to play
                    return VorbisClock.MixFrameOf(_tail, _rate, _target.SampleRate, _origin);
                }
                long total = _tail >= 0 ? _tail : _durationMs > 0 ? _durationMs * _rate / 1000 : -1;
                Ogg.SeekPlan plan = Ogg.BeginSeek(_ogg.Index, _firstAudioPage, _src.Length ?? 0, total,
                    Math.Max(0, target), _ogg.MaxPageSeen);
                while (Ogg.TryNextProbe(ref plan, out long at))
                {
                    if (!ReadWindowAt(at, plan.WindowBytes, landing: false)) break;
                    if (Ogg.Observe(ref plan, _ogg.Index, _win.AsSpan(0, _winLen), _winStart) != Ogg.ProbeResult.NoPage) continue;
                    // A window with no page that ends a packet (one long packet spanning it): read ONE more window forward
                    // before landing on the best page so far — the page that pins the target may be just past it (G-134).
                    long further = _winStart + _winLen;
                    if (_winLen < plan.WindowBytes || !ReadWindowAt(further, plan.WindowBytes, landing: false)) break;
                    if (Ogg.Observe(ref plan, _ogg.Index, _win.AsSpan(0, _winLen), _winStart) == Ogg.ProbeResult.NoPage) break;
                }

                // Land: the page the plan resumes at primes the decoder; the peek fixes the first frame's granule from
                // the first page granule ahead (never from OffsetGranule, which ENDS that page); the clock drops what
                // precedes the target inside the packet that holds it.
                if (!ReadWindowAt(plan.Offset, plan.WindowBytes, landing: true))
                {
                    // The landing window never arrived. Never a throw: −1 is the engine's `InvalidOperationException("Decoder
                    // could not seek to the requested frame.")`, and a seek that failed because the bytes are missing is a fact
                    // about the link, not a bug in the caller. The seek completes at the target either way. What happens NEXT
                    // depends on WHY it never arrived:
                    //   • INTERRUPTED (S-1, V-PA16) — an interrupt for the NEXT seek of a scrub, which is on its way and will
                    //     flush this. Latching `_eof` here ended the track for every seek storm that raced it. The decoder is not
                    //     primed, so it cannot keep decoding like the FLAC arm; it latches `_needsLanding` instead and `Read`
                    //     serves silence while it repeats the landing read.
                    //   • anything else (a closed body, a true end) — the track ends, as it did before; `Read`'s 0 says so.
                    bool interrupted = _interrupted;
                    if (interrupted)
                    {
                        _needsLanding = true;
                        _landOffset = plan.Offset;
                        _landBytes = plan.WindowBytes;
                        _landTarget = target;
                        _interrupted = false;                            // consumed: the latch is the landing's now
                    }
                    else _eof = true;
                    Log.Warn("audio", $"audio.seek.short codec=vorbis target={target} page={plan.Offset} eof={(_eof ? 1 : 0)} "
                                      + $"interrupted={(interrupted ? 1 : 0)} needsLanding={(_needsLanding ? 1 : 0)}");
                    return VorbisClock.MixFrameOf(target, _rate, _target.SampleRate, _origin);
                }
                PlaceLanding(target);
                bool landed = NextFrames();
                if (!landed && !_interrupted) _eof = true;               // an interrupted first decode is silence, not the end (S-1)
                long reached = landed ? _heldStart : _clock.Position != VorbisClock.Unknown ? _clock.Position : target;
                Log.Info("audio", $"audio.seek codec=vorbis target={target} landed={reached} page={plan.Offset} "
                                  + $"probes={plan.Probes} tier={plan.Tier} resolved={(plan.Resolved ? 1 : 0)} "
                                  + $"ms={System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0}");
                return VorbisClock.MixFrameOf(reached, _rate, _target.SampleRate, _origin);
            }

            /// <summary>The landing window is in: peek the first frame's granule, prime the decoder on the page, and place the
            /// clock so it drops what precedes <paramref name="target"/> inside the packet that holds it.</summary>
            void PlaceLanding(long target)
            {
                long start = PeekLanding();
                _dec!.Prime();
                _clock = VorbisClock.At(start);
                _clock.Target = target;
            }

            /// <summary>S-1: repeat the landing read a <c>Seek</c> left latched in <c>_needsLanding</c>. WITHOUT re-targeting the
            /// source: the failed attempt's retarget stands (the bytes are already requested), and a re-targeting read would also
            /// end the interrupt window the NEXT seek's <c>InterruptPendingRead</c> opened — leaving the producer blocked on a
            /// position the seek is about to abandon. True once the window is in and the decoder is primed.</summary>
            bool TryLand()
            {
                if (!ReadWindowAt(_landOffset, _landBytes, landing: true, retarget: false)) return false;
                PlaceLanding(_landTarget);
                _needsLanding = false;
                return true;
            }

            /// <summary>Read a window at an absolute container offset. On the random-access face the source is
            /// re-targeted first (a resident window costs nothing) and, for the landing, the sequential fill is resumed
            /// there before a byte is read. <paramref name="retarget"/> false repeats a read the source was already
            /// re-targeted for (S-1's retry).</summary>
            bool ReadWindowAt(long offset, int bytes, bool landing, bool retarget = true)
            {
                if (_ra is { } ra)
                {
                    if (retarget)
                    {
                        ra.Retarget(offset, bytes, _epoch);
                        if (landing) ra.ResumeFrom(offset);
                    }
                }
                else if (retarget && _src!.Seek(offset) != offset) return false;
                _winStart = offset;
                _winLen = 0;
                VorbisClock.Restart(_ogg!, offset);
                int want = Math.Clamp(bytes, 1, _win.Length);
                while (_winLen < want)
                {
                    int n = ReadRaw(offset + _winLen, _win.AsSpan(_winLen, want - _winLen));
                    if (n <= 0) break;
                    _winLen += n;
                }
                return _winLen > 0;
            }
        }

        /// <summary>Spotify's bytes, for the engine and the decoders (Vorbis plan §5.5): a THIN wrapper over the stream
        /// layer's <see cref="Spotify.Audio.Body"/> — the clear head, the read-ahead ring, the fetch thread and the disk
        /// cache all live there (owner F). This adds the engine's sequential face (MP3, and anything else that only
        /// speaks <c>Read</c>/<c>Seek</c>) and the random-access face the Vorbis and FLAC adapters read. Container
        /// coordinates throughout: the 0xa7 skip is inside <c>Body</c>.
        /// <para>OWNING or a VIEW (V-PA4, V-PA20). The source the track was opened with OWNS the body: its reads move the ring's
        /// cursor and epoch, a seek's <c>Retarget</c>/<c>ResumeFrom</c> steer the fill, and closing it disposes the body. A
        /// seek's SECOND decoder (design A) reads the same body through a non-owning view (<c>ownsBody: false</c>): its reads
        /// are <c>Body.ProbeAt</c> (resident bytes copied without touching the cursor, the epoch or the interrupt; a miss
        /// queued BEHIND the live fill), its <c>Retarget</c> is <c>Body.ProbeRange</c>, its <c>ResumeFrom</c> and
        /// <c>InterruptPendingRead</c> do nothing, and closing it never disposes the body — the retiring voice's teardown
        /// can therefore never take the playing voice's bytes with it. <see cref="ReleaseOwnership"/> /
        /// <see cref="TakeOwnership"/> hand the body over, once, just BEFORE the swap that makes the view the live voice.</para></summary>
        public sealed class RingSource : IMediaByteSource, IRandomAccessBytes, INormalizationSource
        {
            /// <summary>How long ONE probe read waits for a missing range before it answers <see cref="StarvedRead"/> — the
            /// caller asks again (the decoders loop on it), so a cancelled view ends within a slice.</summary>
            const int ProbeSliceMs = 250;

            readonly Spotify.Audio.Body _body;
            long _cursor;
            int _owns;                  // 1 = this source owns the body (Interlocked)
            int _cancelled;             // a view only: the prepare that built it was cancelled — every read answers −1
            int _residentOnly;          // a view only: reads answer what is resident and never wait (the scrub's recentre)
            long _probeEnd;             // a view only: the end of the bytes it read last — where it takes the sequential fill over

            public RingSource(Spotify.Audio.Body body, bool ownsBody = true)
            {
                _body = body;
                _owns = ownsBody ? 1 : 0;
            }

            public Spotify.Audio.Body Body => _body;

            /// <summary>This source owns the body (its reads steer the ring; closing it disposes the body).</summary>
            public bool OwnsBody => Volatile.Read(ref _owns) != 0;

            /// <summary>A view's reads answer only what is resident (0 at once for the rest), never waiting on the wire — the
            /// scrub source toggles it around its recentre (V-PA30). Ignored by an owning source.</summary>
            public bool ResidentOnly
            {
                get => Volatile.Read(ref _residentOnly) != 0;
                set => Volatile.Write(ref _residentOnly, value ? 1 : 0);
            }

            /// <summary>Stop being the body's owner: reads become probes, <c>Close</c> stops disposing it. Idempotent.</summary>
            public void ReleaseOwnership() => Interlocked.Exchange(ref _owns, 0);

            /// <summary>Become the body's owner (the swap that makes this view the live voice is about to land): reads move the ring
            /// again, and the sequential fill continues from where this source last read, so the new playhead's bytes are the ones
            /// the ring keeps ahead. Idempotent.</summary>
            public void TakeOwnership()
            {
                if (Interlocked.Exchange(ref _owns, 1) != 0) return;
                long end = Volatile.Read(ref _probeEnd);
                if (end > 0) _body.ResumeFrom(end);
            }

            /// <summary>V-PA36: merge the stream layer's landing-time page index (every Ogg page walked as its bytes landed, CDN and
            /// disk alike) into <paramref name="index"/> — only what is new since <paramref name="seeded"/> (the body's array is
            /// grow-only). The ONE call site of <c>Body.PageIndexSnapshot</c>.</summary>
            public void SeedPageIndex(Ogg.PageIndex index, ref int seeded)
            {
                _body.PageIndexSnapshot(out ReadOnlySpan<long> offsets, out ReadOnlySpan<long> granules);
                int count = Math.Min(offsets.Length, granules.Length);
                if (count <= seeded) return;
                index.Merge(offsets[seeded..count], granules[seeded..count]);
                seeded = count;
            }

            /// <summary>One probe read for a view (never the owning path): resident bytes at <paramref name="offset"/>, else one
            /// slice's wait behind the live fill. >0 copied; −1 cancelled or the body is gone; 0 at the true end — or, with
            /// <see cref="ResidentOnly"/>, "not resident"; <see cref="StarvedRead"/> after a slice with nothing (never the end:
            /// the caller asks again).</summary>
            int ProbeRead(long offset, Span<byte> dst)
            {
                if (Volatile.Read(ref _cancelled) != 0 || _body.Disposed) return -1;
                bool residentOnly = ResidentOnly;
                int n = _body.ProbeAt(offset, dst, ProbeSliceMs, residentOnly);
                if (n > 0)
                {
                    if (offset + n > Volatile.Read(ref _probeEnd)) Volatile.Write(ref _probeEnd, offset + n);
                    return n;
                }
                if (n < 0 || residentOnly) return n < 0 ? n : 0;
                if (_body.LengthKnown && offset >= _body.Length) return 0;
                return StarvedRead;
            }

            /// <summary>The body's figure — the catalogue's, the header's, or chunk 0's once it landed.</summary>
            public float GainDb => _body.GainDb;

            public float Peak => _body.Peak;

            /// <summary>The body's ALBUM pair (D5, V-PA13) — the header's bytes 152/156, the catalogue's album params, or chunk 0's.</summary>
            public float AlbumGainDb => _body.AlbumGainDb;

            public float AlbumPeak => _body.AlbumPeak;

            /// <summary>Container bytes — the catalogue estimate until the first range's <c>Content-Range</c> names it.</summary>
            public long? Length => _body.Length;

            /// <summary>A seek here is a ring lookup or one probe range, never a stream re-open.</summary>
            public SourceCaps Caps => new() { Seekable = true, KnownLength = _body.LengthKnown, ExpensiveSeek = false };

            public uint Epoch => _body.Epoch;

            public long TailGranule => _body.TailGranule;

            public bool TryOpen(in DataSpec spec)
            {
                _cursor = Math.Max(0, spec.Position);
                return !_body.Disposed && Volatile.Read(ref _cancelled) == 0;
            }

            /// <summary>The sequential read at the cursor. 0 only at EOF (the ring's rule). NEVER interruptible: a sequential
            /// consumer (NLayer through <see cref="ByteSourceStream"/>) cannot tell "a seek is coming" from the end of the
            /// track, so it keeps waiting — through every starve too (D5), until bytes arrive or a Stop closes the body. A −1
            /// from a moved epoch is retried once, as <see cref="ReadAtEpoch"/> does. A VIEW reads by probe (it never moves the
            /// ring), asking again after every starved slice until bytes arrive, it is cancelled or the body goes.</summary>
            public int Read(Span<byte> dst)
            {
                if (!OwnsBody)
                {
                    while (true)
                    {
                        int p = ProbeRead(_cursor, dst);
                        if (p == StarvedRead) continue;
                        if (p > 0) _cursor += p;
                        return p;
                    }
                }
                uint epoch = _body.Epoch;
                bool retried = false;
                while (true)
                {
                    int n = _body.ReadAt(_cursor, dst, epoch);
                    if (n == StarvedRead) continue;
                    if (n < 0 && !retried && _body.Epoch != epoch)
                    {
                        epoch = _body.Epoch;
                        retried = true;
                        continue;
                    }
                    if (n > 0) _cursor += n;
                    return n;
                }
            }

            /// <summary>A position write only: the next read that misses asks for its bytes. A cancelling seek is
            /// <see cref="Retarget"/>'s, for the decoders that plan their own probes.</summary>
            public long Seek(long offset)
            {
                _cursor = _body.LengthKnown ? Math.Clamp(offset, 0, _body.Length) : Math.Max(0, offset);
                return _cursor;
            }

            /// <summary>The random-access face the Vorbis and FLAC adapters read — INTERRUPTIBLE, because both hand out
            /// silence for <see cref="InterruptedRead"/> until their own seek arrives.</summary>
            public int ReadAt(long offset, Span<byte> dst, uint epoch)
                => OwnsBody ? _body.ReadAt(offset, dst, epoch, interruptible: true) : ProbeRead(offset, dst);

            /// <summary>A seek. The ring aligns the probe out to whole slots at both ends, so the one range on the wire
            /// covers the whole window the planner is about to read. A view only REQUESTS the range
            /// (<c>Body.ProbeRange</c>, queued behind the live fill): the ring's cursor, epoch and in-flight fill are not its to move.</summary>
            public void Retarget(long probeOffset, int probeBytes, uint epoch)
            {
                if (OwnsBody) { _body.Retarget(probeOffset, probeBytes, epoch); return; }
                if (Volatile.Read(ref _cancelled) == 0) _body.ProbeRange(probeOffset, probeBytes);
            }

            /// <summary>A seek is on its way (see <see cref="Spotify.Audio.Body.InterruptPendingRead"/>): release a
            /// decoder blocked in the ring's wait so the engine's producer reaches its seek mailbox now, not after the
            /// 8 s bound. Any thread; non-blocking. A view has no wait of the ring's to interrupt.</summary>
            public void InterruptPendingRead()
            {
                if (OwnsBody) _body.InterruptPendingRead();
            }

            /// <summary>The seek landed at <paramref name="offset"/>: the sequential fill continues from there. A view only moves
            /// its own cursor — the live voice's fill is not its to steer until <see cref="TakeOwnership"/>.</summary>
            public void ResumeFrom(long offset)
            {
                _cursor = Math.Max(0, offset);
                if (OwnsBody) _body.ResumeFrom(offset);
            }

            /// <summary>The engine cancels only a source that is going away — an abandoned open or prepare, a retiring
            /// voice — so the owning source releases the body: a read blocked in the ring's bounded wait returns −1 at once. A
            /// view disposes nothing (the body is the live voice's): it only stops answering, so a probe read in flight ends within
            /// its slice.</summary>
            public void Cancel()
            {
                if (OwnsBody) _body.Dispose();
                else Volatile.Write(ref _cancelled, 1);
            }

            public void Close() => Cancel();
        }

        /// <summary>MP3, over NLayer. The LAME/Xing gapless numbers are read from the tag before the codec takes the
        /// stream — never a hardcoded constant, because the delay and padding are the encoder's own facts.</summary>
        public sealed class Mp3AudioDecoder : IAudioDecoder, IGainFolding
        {
            readonly float _gain;
            NLayer.MpegFile? _file;
            PolyphaseResampler? _resampler;
            MixFormat _target;
            float[] _src = [];
            float[] _pull = [];        // NLayer speaks float[] only; ONE buffer, never one per block (P8)
            int _hold, _srcChannels;

            readonly PullSamples _pullFn;

            public Mp3AudioDecoder(float gainDb, float peak = 0f)
            {
                _gain = GainLinear(gainDb, peak);
                _pullFn = ReadSource;
            }

            /// <summary>The linear factor folded into this decoder's output (<see cref="IGainFolding"/>, V-PA15).</summary>
            public float AppliedGainLinear => _gain;

            public GaplessInfo Gapless { get; private set; } = GaplessInfo.None;

            public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
            {
                info = default;
                _target = target;
                try
                {
                    var stream = new ByteSourceStream(src);
                    Mp3Tag tag = default;
                    bool hasTag = stream.CanSeek && Mp3Tag.TryProbe(stream, out tag);
                    _file = new NLayer.MpegFile(stream);
                    _srcChannels = Math.Max(1, _file.Channels);
                    int rate = _file.SampleRate > 0 ? _file.SampleRate : target.SampleRate;
                    _resampler = rate != target.SampleRate ? new PolyphaseResampler(rate, target.SampleRate, target.Channels) : null;
                    _src = new float[4096 * Math.Max(_srcChannels, target.Channels)];
                    Gapless = hasTag ? tag.ToGapless(rate, target.SampleRate) : GaplessInfo.None;
                    info = new DecodedInfo(new MediaContentType(Container.Mp3, CodecId.None, CodecId.Mp3),
                        new MixFormat(rate, _srcChannels), _file.Duration, default);
                    return true;
                }
                catch (Exception ex) { Log.Warn("audio", "mp3 open failed", ex); return false; }
            }

            public int Read(Span<float> dst) => PullConform(dst, _target, _srcChannels, _gain, ref _hold, _src, _resampler, _pullFn);

            int ReadSource(Span<float> into)
            {
                if (_file is null) return 0;
                if (_pull.Length < into.Length) _pull = new float[into.Length];
                int n;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { n = _file.ReadSamples(_pull, 0, into.Length); } catch { return 0; }
                // NLayer reads its Stream inside this call, so the MP3 figure includes the byte seam's waits — an honest
                // floor for the decoder, not a pure CPU number like the Vorbis and FLAC ones.
                RecordDecode(DecodeMp3, n > 0 ? n / _srcChannels : 0, _file.SampleRate,
                    System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                if (n > 0) _pull.AsSpan(0, n).CopyTo(into);
                return n;
            }

            public long Seek(long frame)
            {
                if (_file is null) return -1;
                try
                {
                    long srcFrame = _target.SampleRate == _file.SampleRate
                        ? frame
                        : (long)Math.Round((double)frame * _file.SampleRate / _target.SampleRate);
                    // NLayer's Position is in BYTES of decoded float samples, not samples (its Length is samples × 4): the
                    // old `srcFrame × channels` landed a quarter of the way to the target.
                    _file.Position = srcFrame * _srcChannels * sizeof(float);
                    _hold = 0;
                    _resampler?.Reset();
                    // NLayer lands on an MPEG frame boundary (up to ~1 152 samples off): report where it actually is, so
                    // the clock rebases on the audio that plays rather than on the ask.
                    long landedSrc = _file.Position / (sizeof(float) * (long)_srcChannels);
                    return _target.SampleRate == _file.SampleRate
                        ? landedSrc
                        : (long)Math.Round((double)landedSrc * _target.SampleRate / _file.SampleRate);
                }
                catch { return -1; }
            }
        }

        /// <summary>The LAME/Xing gapless block. Header bytes only, no audio decode, seekable streams only, and the
        /// position is restored — a probe failure is never a playback failure.</summary>
        public readonly record struct Mp3Tag(int DelaySamples, int PaddingSamples, long TotalSamples)
        {
            /// <summary>The MDCT/filterbank delay a conforming decoder adds ON TOP of the encoder delay. The gapless
            /// convention is "skip delay + 529, trim padding − 529".</summary>
            public const int DecoderDelaySamples = 529;

            public GaplessInfo ToGapless(int srcRate, int mixRate)
            {
                int leadSrc = DelaySamples + DecoderDelaySamples;
                int padSrc = Math.Max(0, PaddingSamples - DecoderDelaySamples);
                long exact = TotalSamples;
                return new GaplessInfo(
                    (int)ToMix(leadSrc, srcRate, mixRate),
                    (int)ToMix(padSrc, srcRate, mixRate),
                    exact > 0 ? ToMix(exact, srcRate, mixRate) : -1,
                    TailKnown: exact > 0);
            }

            static long ToMix(long n, int srcRate, int mixRate)
                => srcRate <= 0 || srcRate == mixRate ? n : (long)Math.Round(n * (double)mixRate / srcRate);

            public static bool TryProbe(Stream stream, out Mp3Tag tag)
            {
                tag = default;
                if (!stream.CanSeek) return false;
                long restore = stream.Position;
                try
                {
                    stream.Position = 0;
                    Span<byte> head = stackalloc byte[10];
                    if (stream.Read(head) < 10) return false;
                    long frameStart = 0;
                    if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
                    {
                        int size = ((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14)
                                 | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F);
                        frameStart = 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);   // the footer flag adds 10
                    }
                    stream.Position = frameStart;
                    Span<byte> buf = stackalloc byte[512];
                    int n = stream.Read(buf);
                    if (n < 160) return false;
                    buf = buf[..n];

                    int sync = -1;
                    for (int i = 0; i + 1 < Math.Min(buf.Length, 192); i++)
                    {
                        if (buf[i] == 0xFF && (buf[i + 1] & 0xE0) == 0xE0) { sync = i; break; }
                    }
                    if (sync < 0) return false;
                    ReadOnlySpan<byte> h = buf[sync..];
                    if (h.Length < 160) return false;

                    int versionBits = (h[1] >> 3) & 0x3;
                    int layerBits = (h[1] >> 1) & 0x3;
                    if (layerBits != 1 || versionBits == 1) return false;          // Layer III, and a real MPEG version
                    bool mpeg1 = versionBits == 3;
                    bool mono = ((h[3] >> 6) & 0x3) == 3;
                    int samplesPerFrame = mpeg1 ? 1152 : 576;
                    int xing = 4 + (mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17));   // past the side info
                    if (h.Length < xing + 8) return false;
                    if (!h.Slice(xing, 4).SequenceEqual("Xing"u8) && !h.Slice(xing, 4).SequenceEqual("Info"u8)) return false;

                    int flags = (h[xing + 4] << 24) | (h[xing + 5] << 16) | (h[xing + 6] << 8) | h[xing + 7];
                    int cursor = xing + 8;
                    long frames = 0;
                    if ((flags & 0x1) != 0)
                    {
                        if (h.Length < cursor + 4) return false;
                        frames = ((long)h[cursor] << 24) | ((long)h[cursor + 1] << 16) | ((long)h[cursor + 2] << 8) | h[cursor + 3];
                        cursor += 4;
                    }
                    if ((flags & 0x2) != 0) cursor += 4;
                    if ((flags & 0x4) != 0) cursor += 100;
                    if ((flags & 0x8) != 0) cursor += 4;

                    // The LAME extension: a 9-byte encoder string, then delay and padding packed 12 + 12 bits at 21.
                    if (h.Length < cursor + 24) return false;
                    int delay = (h[cursor + 21] << 4) | (h[cursor + 22] >> 4);
                    int padding = ((h[cursor + 22] & 0x0F) << 8) | h[cursor + 23];
                    if (delay <= 0 && padding <= 0) return false;                  // no gapless data was written
                    if (delay > 4096 || padding > 4608) return false;              // outside any real encoder's range
                    long total = frames > 0 ? frames * samplesPerFrame - delay - padding : -1;
                    tag = new Mp3Tag(delay, padding, total);
                    return true;
                }
                catch { return false; }
                finally { try { stream.Position = restore; } catch { } }
            }
        }

        /// <summary>One pull from a third-party codec: interleaved source-rate samples into the caller's span, and
        /// the SAMPLE count back (not frames — that is what both of these libraries answer). A named delegate because
        /// `Func&lt;Span&lt;float&gt;, int&gt;` cannot exist: a ref struct is not a valid type argument.</summary>
        delegate int PullSamples(Span<float> into);

        /// <summary>Pull → channel conform → gain → resample, for the MP3 pull decoder. The FLAC and Vorbis adapters
        /// have their own loops because their cores hand over whole decoded blocks rather than an interleaved pull. When the pull
        /// runs dry the resampler's trailing half kernel (V-PE21) is handed out over as many reads as <paramref name="dst"/> needs
        /// (<c>Flush</c> answers 0 once it is spent, which is the end); the pull is asked again on each of those reads, and a
        /// finished stream answers 0 again.</summary>
        static int PullConform(Span<float> dst, MixFormat target, int srcChannels, float gain,
            ref int hold, float[] scratch, PolyphaseResampler? resampler, PullSamples pull)
        {
            int ch = target.Channels;
            int want = dst.Length / ch;
            if (want <= 0 || scratch.Length == 0) return 0;

            while (true)
            {
                if (hold == 0)
                {
                    int maxFrames = Math.Min(scratch.Length / Math.Max(ch, srcChannels), 4096);
                    int samples = pull(scratch.AsSpan(0, maxFrames * srcChannels));
                    if (samples <= 0) return resampler is { IsActive: true } tail ? tail.Flush(dst) : 0;
                    int frames = samples / srcChannels;
                    Conform(scratch, frames, srcChannels, ch, gain);
                    hold = frames;
                }

                if (resampler is { IsActive: true } rs)
                {
                    ResampleResult rr = rs.Process(scratch.AsSpan(0, hold * ch), hold, dst);
                    int unread = hold - rr.Consumed;
                    if (unread > 0 && rr.Consumed > 0) scratch.AsSpan(rr.Consumed * ch, unread * ch).CopyTo(scratch);
                    hold = unread;
                    // A pull that went wholly into the filter's history (the group-delay pre-roll) produced nothing: that is not the end.
                    if (rr.Produced > 0 || rr.Consumed == 0) return rr.Produced;
                    continue;
                }

                int take = Math.Min(want, hold);
                scratch.AsSpan(0, take * ch).CopyTo(dst);
                int rest = hold - take;
                if (rest > 0) scratch.AsSpan(take * ch, rest * ch).CopyTo(scratch);
                hold = rest;
                return take;
            }
        }

        /// <summary>In-place channel conform + gain, walked BACKWARD when it grows so mono→stereo needs no second
        /// buffer. >2 channels are downmixed, which is the honest answer for a stereo device.</summary>
        static void Conform(float[] buf, int frames, int srcChannels, int dstChannels, float gain)
        {
            if (srcChannels == dstChannels)
            {
                if (gain == 1f) return;
                Span<float> all = buf.AsSpan(0, frames * dstChannels);
                for (int i = 0; i < all.Length; i++) all[i] *= gain;
                return;
            }
            if (srcChannels == 1 && dstChannels == 2)
            {
                for (int f = frames - 1; f >= 0; f--)
                {
                    float v = buf[f] * gain;
                    buf[f * 2] = v;
                    buf[f * 2 + 1] = v;
                }
                return;
            }
            for (int f = 0; f < frames; f++)
            {
                float l = 0f, r = 0f;
                for (int c = 0; c < srcChannels; c++)
                {
                    float v = buf[f * srcChannels + c];
                    if ((c & 1) == 0) l += v; else r += v;
                }
                int pairs = Math.Max(1, (srcChannels + 1) / 2);
                buf[f * dstChannels] = l / pairs * gain;
                if (dstChannels > 1) buf[f * dstChannels + 1] = r / Math.Max(1, srcChannels / 2) * gain;
            }
        }

        /// <summary>An `IMediaByteSource` as a `Stream`, for NLayer, the one third-party decoder left that only speaks
        /// `Stream`.
        /// It is the thinnest possible adapter: the engine's seam already owns the read-ahead, the bounded wait and
        /// the never-return-zero invariant.</summary>
        public sealed class ByteSourceStream : Stream
        {
            readonly IMediaByteSource _src;
            long _position;

            internal ByteSourceStream(IMediaByteSource src)
            {
                _src = src;
                _src.TryOpen(new DataSpec { Position = 0, Length = -1 });
            }

            public override bool CanRead => true;
            public override bool CanSeek => _src.Caps.Seekable;
            public override bool CanWrite => false;
            public override long Length => _src.Length ?? throw new NotSupportedException();

            public override long Position
            {
                get => _position;
                set => Seek(value, SeekOrigin.Begin);
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                int n = _src.Read(buffer);
                if (n > 0) _position += n;
                return Math.Max(0, n);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    _ => (_src.Length ?? 0) + offset,
                };
                long landed = _src.Seek(Math.Max(0, target));
                if (landed >= 0) _position = landed;
                return _position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ── 10. the gapless / crossfade hand-off ─────────────────────────────────────────────────────────────────────

        /// <summary>How long a prepare waits for the next track's Ogg tail granule before building its voice (G-113): the
        /// engine fixes a voice's exact length when the voice is built, and a length known there is a sample-exact join
        /// later. The prepare runs in the endgame (fade + 8 s out), so two seconds is free.</summary>
        const int PrepareTailWaitMs = 2_000;

        /// <summary>The prepare itself, off the chain under its own token. Nothing it builds is adopted unless the token is
        /// still live and the session it was built for is still the live one; everything else is disposed here. A failure is
        /// a log line and an empty slot — never a fault on the track that is PLAYING.</summary>
        static async Task PrepareCoreAsync(EntityRef row, EntityId id, PlayableKind kind, CancellationToken token)
        {
            IMediaByteSource? bytes = null;
            IPreparedItem? item = null;
            IAudioDecoder? decoder = null;
            try
            {
                PcmAudioSession? session;
                PcmAudioPlayer? backend;
                PlayableKind activeKind;
                bool silent;
                lock (s_gate) { session = s_session; backend = s_backend; activeKind = s_activeKind; silent = s_silent; }
                if (session is null || backend is null || silent || token.IsCancellationRequested) return;   // nothing to hand off from

                // `prepared`: the next ring is sized for the hand-off out of the shared read-ahead budget (Vorbis plan §5.3).
                bytes = Open(id, token, out Opened opened, out Fault fault, prepared: true);
                if (bytes is null)
                {
                    if (fault != Fault.None && !token.IsCancellationRequested) Log.Info("audio", $"prepare skipped: {fault}");
                    return;
                }
                using CancellationTokenRegistration closeOnCancel = token.Register(static b => ((IMediaByteSource)b!).Close(), bytes);
                if (bytes is RingSource ring && !ring.Body.WaitForTail(PrepareTailWaitMs, token))
                    Log.Info("audio", $"prepare: no tail within {PrepareTailWaitMs} ms — the join will ride the catalogue duration");
                if (token.IsCancellationRequested) return;

                decoder = CreateDecoderFor(opened);
                s_decoderForOpen.Value = decoder;
                MediaSource source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);
                // `PrepareContext.For` stamps the mix rate B is resampled FOR, which is what every splice site checks.
                PrepareContext ctx = PrepareContext.For(session.Format, session.NormalizationMode, session.ReferenceLufsValue);
                item = await backend.PrepareAsync(source, ctx, token).ConfigureAwait(false);
                s_decoderForOpen.Value = null;

                lock (s_gate)
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(s_session, session) || s_prepBuildingId != id) return;
                    s_prepItem = item;
                    s_prepBytes = bytes;
                    s_prepDecoder = decoder;
                    s_prepOpened = opened;
                    s_prepId = id;
                    s_prepDurMs = opened.DurationMs;
                    // A cross-kind boundary (audio → video, or either → a local file) changes HOSTS, so there is
                    // nothing to splice into: `MediaSwitch` is the one authority on that.
                    s_prepOverlap = MediaSwitch.AllowCrossfade(activeKind, kind);
                    item = null;                                    // adopted: the finally below leaves them alone
                    bytes = null;
                }
                _ = row;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("audio", "prepare failed — the boundary will reload", ex); }
            finally
            {
                if (item is not null) { try { await item.DisposeAsync().ConfigureAwait(false); } catch { } }
                else if (decoder is IDisposable owned && bytes is not null) { try { owned.Dispose(); } catch { } }
                if (bytes is not null) { try { bytes.Close(); } catch { } }
                Interlocked.Decrement(ref s_prepInFlight);
            }
        }

        /// <summary>Arm B into the LIVE mixer at the active track's natural-end frame. A is never faded and never
        /// truncated and the `IAudioClient` never stops — this is the butt-join, and it is why `GainEnvelope.Constant`
        /// rather than a zero-length fade (rule 3).</summary>
        static bool CommitGaplessJoin(PcmAudioSession sess, IPreparedItem item)
        {
            if (!GaplessJoinClock.PrimedSlotMatches(item.MixRate, sess.Format.SampleRate))
            {
                Log.Info("audio", "join abandoned reason=rate-mismatch");
                Interlocked.Increment(ref s_mGaplessAbandoned);
                DisposePreparedSlot();
                return false;
            }
            long id = ++s_nextVoiceId;
            long clock = sess.SampleClock;
            int rate = sess.Format.SampleRate;
            // EXACT when the outgoing voice's length is decoded truth — the engine's (STREAMINFO, the LAME tag, a tail known
            // at open, the producer's EOF) or, for an Ogg voice whose tail landed after it opened, the adapter's own
            // (G-113) — so the join is that voice's last sample + 1; otherwise it rides the catalogue duration and is a
            // degraded butt-join (a gap or an overlap of `duration_ms − true length`).
            long exactFrames = ActiveExactFrames(sess);
            bool exact = exactFrames >= 0;
            long activeEnd, remaining = s_activeDurMs - ActivePositionMs();
            lock (s_gate) activeEnd = exact ? JoinFrameExact(s_anchorClock, s_anchorPlayheadMs, exactFrames, rate) : s_activeJoinFrame;
            if (exact) remaining = Math.Max(remaining, (activeEnd - clock) * 1000 / Math.Max(1, rate));
            long join = GaplessJoinClock.ScheduleJoin(activeEnd, clock, remaining, rate);
            float rg = sess.ReplayGainScalarFor(item.Loudness);
            lock (s_gate)
            {
                if (!sess.TryAddCrossfadeVoice(item.AudioVoice!, GainEnvelope.Constant, join, rg, sess.BuildVoiceChain(), id))
                    return false;
                NoteBakedFactor(id, s_prepDecoder, in s_prepOpened);       // V-PA15: what the joining voice's decoder folded
                s_joinExact = exact;
                s_joinPending = true;
                s_joinFrame = join;
                s_joinVoiceId = id;
                s_joinVoice = item.AudioVoice;
                s_joinTotalFrames = item.TotalFrames;
                s_joinId = s_prepId;
                s_joinDurMs = s_prepDurMs;
                s_joinBytes = s_prepBytes;
                s_joinDecoder = s_prepDecoder;
                s_joinOpened = s_prepOpened;
                // The slot is cleared WITHOUT disposing the item: its voice is live in the mixer now.
                s_prepItem = null;
                s_prepBytes = null;
                s_prepDecoder = null;
                s_prepId = default;
                s_prepDurMs = 0;
                s_prepOverlap = false;
            }
            Log.Info("audio", $"gapless armed join={join} clock={clock} remainMs={remaining} exact={(exact ? 1 : 0)}");
            return true;
        }

        /// <summary>The frame a voice whose exact length is <paramref name="exactFrames"/> ends on, on the session clock: the
        /// clock at an anchor where the voice's playhead was known, plus the frames from that playhead to the end (rule 4:
        /// "clock now + frames still to play", never frames from track start). PURE.</summary>
        public static long JoinFrameExact(long anchorClock, long anchorPlayheadMs, long exactFrames, int rate)
            => anchorClock + Math.Max(0, exactFrames - GaplessJoinClock.MsToFrames(Math.Max(0, anchorPlayheadMs), rate));

        /// <summary>The live voice's exact length in mix frames, or −1: the engine's knowledge first, then the Vorbis
        /// adapter's late tail.</summary>
        static long ActiveExactFrames(PcmAudioSession sess)
        {
            if (sess.VoiceLengthIsExact) return sess.ExactVoiceEndFrame;
            IAudioDecoder? decoder;
            lock (s_gate) decoder = s_activeDecoder;
            return decoder is VorbisAudioDecoder vorbis ? vorbis.LateExactFrames(sess.Format.SampleRate) : -1;
        }

        /// <summary>The join went live: flip identity under the lock so `PositionMs` rebases to B and a later seek
        /// reaches B rather than the retired primary.</summary>
        static void AnnounceGaplessJoin(PcmAudioSession sess, long rawPos)
        {
            IAudioSource? voice;
            long totalFrames;
            uint outgoing;
            EntityId joined;
            lock (s_gate)
            {
                if (s_joinExact) Interlocked.Increment(ref s_mGaplessExact);
                else Interlocked.Increment(ref s_mGaplessDegraded);
                voice = s_joinVoice;
                totalFrames = s_joinTotalFrames;
                outgoing = s_loadEpoch;
                joined = s_joinId;
                s_joinVoice = null;
                s_crossfadeInFlight = true;
                s_retiringBytes = s_bytes;
                s_bytes = s_joinBytes;
                s_activeDecoder = s_joinDecoder;
                s_opened = s_joinOpened;
                s_joinBytes = null;
                s_joinDecoder = null;
                s_activeStartMs = rawPos;
                s_activePrimaryId = s_joinVoiceId;
                s_activeDurMs = s_joinDurMs;
                s_id = s_joinId;
                s_activeJoinFrame = s_joinFrame + GaplessJoinClock.MsToFrames(s_joinDurMs, sess.Format.SampleRate);
                s_anchorClock = s_joinFrame;
                s_anchorPlayheadMs = 0;
                s_joinPending = false;
                s_armLogged = false;
                s_lastEndgameAskMs = -1;
                s_endgameAskCount = 0;
            }
            // Post-hand-off seeks must reach B, not the retired primary — that is the whole job of this call.
            if (voice is not null)
            {
                try { sess.SetActiveVoice(s_activePrimaryId, voice, TimeSpan.FromMilliseconds(s_activeDurMs), totalFrames); }
                catch (Exception ex) { Log.Warn("audio", "set active voice failed", ex); }
            }
            PromoteLiveBody();
            PostHandedOff(outgoing, joined, 0);
        }

        /// <summary>A real crossfade: B fades IN and A's current primary fades OUT, same start frame, same length,
        /// one `EqualPower` curve. Both voices run the shared DSP chain and B carries its own ReplayGain scalar.</summary>
        static bool CommitCrossfade(PcmAudioSession sess, IPreparedItem item, long rawPos, int fadeMs)
        {
            if (!GaplessJoinClock.PrimedSlotMatches(item.MixRate, sess.Format.SampleRate))
            {
                Log.Info("audio", "crossfade abandoned reason=rate-mismatch");
                Interlocked.Increment(ref s_mGaplessAbandoned);
                DisposePreparedSlot();
                return false;
            }
            long id = ++s_nextVoiceId;
            long start = sess.SampleClock;
            int fadeFrames = fadeMs * sess.Format.SampleRate / 1000;
            float rg = sess.ReplayGainScalarFor(item.Loudness);
            uint outgoing;
            EntityId joined;
            lock (s_gate)
            {
                if (!sess.TryAddCrossfadeVoice(item.AudioVoice!,
                        GainEnvelope.Fade(FadeKind.In, start, fadeFrames, CrossCurve.EqualPower),
                        start, rg, sess.BuildVoiceChain(), id))
                    return false;
                sess.SetVoiceEnvelope(s_activePrimaryId,
                    GainEnvelope.Fade(FadeKind.Out, start, fadeFrames, CrossCurve.EqualPower));
                NoteBakedFactor(id, s_prepDecoder, in s_prepOpened);       // V-PA15: what the incoming voice's decoder folded

                outgoing = s_loadEpoch;
                joined = s_prepId;
                s_crossfadeInFlight = true;
                s_retiringBytes = s_bytes;
                s_bytes = s_prepBytes;
                s_activeDecoder = s_prepDecoder;
                s_opened = s_prepOpened;
                s_activeStartMs = rawPos;
                s_activePrimaryId = id;
                s_activeDurMs = s_prepDurMs;
                s_id = s_prepId;
                s_activeJoinFrame = start + GaplessJoinClock.MsToFrames(s_prepDurMs, sess.Format.SampleRate);
                s_anchorClock = start;
                s_anchorPlayheadMs = 0;
                s_prepItem = null;
                s_prepBytes = null;
                s_prepDecoder = null;
                s_prepId = default;
                s_prepDurMs = 0;
                s_prepOverlap = false;
                s_armLogged = false;
                s_lastEndgameAskMs = -1;
                s_endgameAskCount = 0;
                if (item.AudioVoice is { } fadeVoice)
                {
                    try { sess.SetActiveVoice(id, fadeVoice, TimeSpan.FromMilliseconds(s_activeDurMs), item.TotalFrames); }
                    catch (Exception ex) { Log.Warn("audio", "set active voice failed", ex); }
                }
            }
            Interlocked.Increment(ref s_mCrossfades);
            Log.Info("audio", $"crossfade committed start={start} frames={fadeFrames}");
            PromoteLiveBody();
            PostHandedOff(outgoing, joined, 0);
            return true;
        }

        /// <summary>The joined voice's body is the PLAYING body now: it is what `Stream.Stats` describes, and its ring grows
        /// from the hand-off's ≤ 30 s to the tier the link has earned (G-114).</summary>
        static void PromoteLiveBody()
        {
            IMediaByteSource? live;
            lock (s_gate) live = s_bytes;
            try { (live as RingSource)?.Body.Promote(); }
            catch (Exception ex) { Log.Warn("audio", "promote failed", ex); }
        }

        /// <summary>Abandon a pending join. The mixer has no voice REMOVAL, so B's envelope is pinned at zero with a
        /// 1-frame fade-out in the past and its byte source is cut: the ring decode faults to EOF and the voice
        /// retires silently. True when a join was really dropped (S-5: only then does a seek owe the reducer a
        /// <c>PrepareLost</c> — see <see cref="AfterSeekLanded"/>).</summary>
        static bool AbandonPendingJoin(PcmAudioSession? sess, string reason)
        {
            long voiceId;
            IMediaByteSource? bytes;
            lock (s_gate)
            {
                if (!s_joinPending) return false;
                s_joinPending = false;
                voiceId = s_joinVoiceId;
                bytes = s_joinBytes;
                s_joinBytes = null;
                s_joinDecoder = null;
                s_joinVoice = null;
                s_joinId = default;
            }
            Interlocked.Increment(ref s_mGaplessAbandoned);
            Log.Info("audio", "join abandoned reason=" + reason);
            try { sess?.SetVoiceEnvelope(voiceId, GainEnvelope.Fade(FadeKind.Out, 0, 1, CrossCurve.Linear)); } catch { }
            if (bytes is not null) { try { bytes.Close(); } catch { } }
            return true;
        }

        static void DisposePreparedSlot()
        {
            IPreparedItem? item;
            IMediaByteSource? bytes;
            lock (s_gate)
            {
                item = s_prepItem;
                bytes = s_prepBytes;
                s_prepItem = null;
                s_prepBytes = null;
                s_prepDecoder = null;
                s_prepId = default;
                s_prepDurMs = 0;
                s_prepOverlap = false;
            }
            if (item is not null) _ = item.DisposeAsync();
            if (bytes is not null) { try { bytes.Close(); } catch { } }
        }

        // ── 11. seek ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>How long a design-A prepare may take to deliver its first block before the OLD voice stops playing the position
        /// the user already left (V-PE14): past it the old voice fades to silence over <see cref="SeekStaleFadeMs"/> and the new
        /// one fades in from silence when it lands.</summary>
        const int SeekStaleMs = 80, SeekStaleFadeMs = 10;

        /// <summary>How a design-A attempt ended: its voice is the live one, a newer seek / a load / a stop superseded it (nothing to
        /// report — the newer op owns the outcome), or it failed and the seek falls back to the in-place path.</summary>
        enum SwapOutcome : byte { Landed, Cancelled, Failed }

        /// <summary>The seek pump (V-PA28, S-4): ONE op per run of seeks, reading the NEWEST target from the mailbox, last write
        /// wins. Three ways to serve it, none of which stops the device (D3):
        /// <list type="number">
        /// <item><b>B — the ring jump.</b> A target inside the decoded span (the kept 1 s behind, the 2 s ahead) is decided and
        /// applied ON the RT thread (<c>TryJumpWithinRingAsync</c>): a 5 ms equal-power blend, the live producer untouched.</item>
        /// <item><b>A — the voice swap.</b> A second decoder opens at the target on a NON-OWNING view of the same bytes
        /// (<see cref="SwapSeekAsync"/>) while the live voice keeps playing; ONE compound engine command replaces it at a block
        /// boundary. Needs a free decoder lease and a Spotify body.</item>
        /// <item><b>Fallback — in place.</b> No lease (or A failed): the engine fades the live voice out and holds, the decoder
        /// seeks, the voice fades back in. The one path that interrupts the live producer's byte wait (V-PA19).</item>
        /// </list>
        /// The achieved frame — not the requested one — is what the reducer is told (U-11).</summary>
        static async Task SeekCoreAsync()
        {
            long packed, t0;
            uint gen, loaded;
            Spotify.Audio.Stream.Stats before;
            PcmAudioSession? sess;
            PcmAudioPlayer? backend;
            IMediaByteSource? bytes;
            Opened opened;
            bool silent;
            lock (s_gate)
            {
                packed = Interlocked.Exchange(ref s_seekMailbox, -1);
                gen = s_seekMailboxGen;
                t0 = s_seekT0;
                before = s_seekBefore;
                sess = s_session;
                silent = s_silent;
                backend = s_backend;
                bytes = s_bytes;
                opened = s_opened;
                loaded = s_loadEpoch;
            }
            if (packed < 0) return;                                  // a load cleared it, or an earlier op already took it
            uint epoch = (uint)(packed >> 32);
            int ms = (int)(uint)packed;
            if (epoch != loaded)
            {
                // A seek for a load whose op is still queued BEHIND this one (an earlier Seek queued this op, then Load(N+1) and
                // Seek(N+1) overwrote the mailbox before the chain got here): hand it back and run it after that load — the load keeps a
                // seek stamped with its own epoch. Nothing queued behind us means no load is coming: the seek is for a dead load.
                if (epoch > loaded && Volatile.Read(ref s_chainDepth) > 1 && Interlocked.CompareExchange(ref s_seekMailbox, packed, -1) == -1)
                {
                    Enqueue(SeekCoreAsync);
                    return;
                }
                ClearPendingSeek(gen);       // for a load that is no longer the live one
                return;
            }

            if (silent && sess is not null)
            {
                // The facade has no silent session, and the silent voice is a signal generator the engine cannot seek.
                if (await SilentSeekAsync(sess, ms, epoch, gen).ConfigureAwait(false) && t0 != 0) RecordSeek(ms, t0, before);
                return;
            }
            MediaPlayer? p = Volatile.Read(ref s_player);
            if (p is null || sess is null || backend is null)
            {
                // Parked: the tick's Ready arm applies the target once a session is there (the target is still pending).
                lock (s_gate) { if (s_pendingSeekGen == gen) s_pendingSeekQueued = false; }
                return;
            }

            if (AbandonPendingJoin(sess, "seek")) lock (s_gate) s_joinLostBySeek = true;   // S-5: owed to the reducer once the seek lands
            long target = GaplessJoinClock.MsToFrames(ms, sess.Format.SampleRate);
            long achieved = -1;
            bool swapped = false;
            try
            {
                if (await sess.TryJumpWithinRingAsync(target, CancellationToken.None).ConfigureAwait(false)) achieved = target;      // B
                else if (bytes is RingSource owner && backend.TryAcquireDecoderLease(out IDisposable lease))                        // A
                {
                    (SwapOutcome outcome, long landed) = await SwapSeekAsync(sess, backend, owner, opened, target, gen, lease).ConfigureAwait(false);
                    if (outcome == SwapOutcome.Cancelled)
                    {
                        // A newer seek (or a load) owns the outcome — its pending target is the one that stays. Only when NOTHING newer
                        // exists (a hand-off replaced the live voice under this seek) is this one's target released.
                        ClearPendingSeek(gen);
                        return;
                    }
                    if (outcome == SwapOutcome.Landed) { achieved = landed; swapped = true; }
                }
                if (achieved < 0)
                {
                    (bytes as RingSource)?.InterruptPendingRead();                                                                  // V-PA19: only HERE
                    achieved = await sess.SeekInPlaceAsync(target, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // A scrub release or the stale timer may have parked the voice at gain 0: a failed seek must give it its level
                // back, or the track plays on in silence until the next seek or load.
                RestoreSilencedVoice(sess);
                SeekFailed(ms, gen, ex);
                return;
            }
            if (!swapped) RestoreSilencedVoice(sess);
            int achievedMs = achieved == target ? ms : (int)Math.Min(int.MaxValue, achieved * 1000L / Math.Max(1, sess.Format.SampleRate));
            AfterSeekLanded(ms, achievedMs, epoch, gen, t0, before);
        }

        /// <summary>Seek A (D3): a second decoder, opened at <paramref name="target"/> on a NON-OWNING view of the live body, replaces
        /// the playing voice in ONE compound engine command while the old voice keeps playing right up to the swap block.
        /// <para>Ordering that matters. (1) The stale timer starts with the prepare (V-PE14). (2) The prepare runs under a token a
        /// newer seek cancels; the lease travels with the prepared voice and is released on every failure path. (3) The BODY is handed
        /// over — the old source stops owning it, the view starts — BEFORE the swap (V-PA4): the retiring voice's teardown closes its
        /// source and must not dispose what the new voice reads. (4) Once handed over the swap is NOT cancellable (a newer seek
        /// waits its turn on the chain): cancelling there would dispose a voice that already owns the body.</para>
        /// <para>Returns <see cref="SwapOutcome.Landed"/> with the achieved frame (the prepared item's start, U-11),
        /// <see cref="SwapOutcome.Cancelled"/> when superseded, <see cref="SwapOutcome.Failed"/> when the caller should fall back.</para></summary>
        static async Task<(SwapOutcome Outcome, long Achieved)> SwapSeekAsync(PcmAudioSession sess, PcmAudioPlayer backend,
            RingSource owner, Opened opened, long target, uint gen, IDisposable lease)
        {
            var cts = new CancellationTokenSource();
            Volatile.Write(ref s_seekPrepare, cts);
            var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            IPreparedItem? item = null;
            RingSource? view = null;
            IAudioDecoder? decoder = null, previous = null;
            Task stale = Task.CompletedTask;
            bool handedOver = false, swapped = false;
            long achieved = 0;
            try
            {
                view = new RingSource(owner.Body, ownsBody: false);         // a second reader: probes, no cursor / epoch moves (V-PA20)
                decoder = CreateDecoderFor(opened);
                s_decoderForOpen.Value = decoder;                           // flows into the engine's factory call inside the prepare
                MediaSource source = MediaSource.FromPull(view).WithKind(MediaKind.PcmAudio);
                PrepareContext ctx = PrepareContext.For(sess.Format, sess.NormalizationMode, sess.ReferenceLufsValue);
                stale = StaleTimerAsync(sess, timerCts.Token);
                item = await backend.PrepareAtAsync(source, ctx, target, lease, cts.Token).ConfigureAwait(false);   // the lease travels with it
                s_decoderForOpen.Value = null;
                cts.Token.ThrowIfCancellationRequested();                   // a newer seek landed its target while this finished: newest wins

                lock (s_gate)
                {
                    // A load, a stop or a hand-off moved the live voice while the prepare ran: it is not ours to replace.
                    if (!ReferenceEquals(s_session, sess) || !ReferenceEquals(s_bytes, owner)) throw new OperationCanceledException();
                    previous = s_activeDecoder;
                    owner.ReleaseOwnership();
                    view.TakeOwnership();
                    handedOver = true;
                    s_bytes = view;
                    s_activeDecoder = decoder;
                }
                achieved = await sess.SwapToPreparedAsync(item, gen, CancellationToken.None).ConfigureAwait(false);
                swapped = true;
                item = null;                                                // its voice is the session's now
                timerCts.Cancel();                                          // the swap is in: the stale fade has nothing left to do
                try { await stale.ConfigureAwait(false); } catch { }
                lock (s_gate)
                {
                    s_activePrimaryId = sess.ActiveVoiceIdValue;
                    NoteBakedFactor(s_activePrimaryId, decoder, in opened); // V-PA15: the new voice's baked factor
                    s_activeSilenced = false;                               // the new voice came in from its own envelope
                }
                return (SwapOutcome.Landed, achieved);
            }
            catch (Exception ex)
            {
                if (swapped) return (SwapOutcome.Landed, achieved);         // nothing after the swap can fail the seek
                bool cancelled = ex is OperationCanceledException || !IsLiveSession(sess);
                if (handedOver)
                {
                    // The hand-over precedes the swap by design; undo it so the still-playing old voice keeps a body that disposes
                    // with IT (best effort: a failure INSIDE the engine's swap races the new ring's retirement, which closes `view`).
                    lock (s_gate)
                    {
                        try { view!.ReleaseOwnership(); owner.TakeOwnership(); } catch { }
                        if (ReferenceEquals(s_bytes, view)) { s_bytes = owner; s_activeDecoder = previous; }
                    }
                }
                timerCts.Cancel();
                if (item is not null) { try { await item.DisposeAsync().ConfigureAwait(false); } catch { } }
                lease.Dispose();                                            // idempotent: the engine already released it on its own failure paths
                if (!cancelled) Log.Warn("audio", $"audio.seek.swap.failed to={target} — falling back to the in-place seek", ex);
                return (cancelled ? SwapOutcome.Cancelled : SwapOutcome.Failed, 0);
            }
            finally
            {
                s_decoderForOpen.Value = null;
                if (ReferenceEquals(Volatile.Read(ref s_seekPrepare), cts)) Volatile.Write(ref s_seekPrepare, null);
                timerCts.Dispose();
                cts.Dispose();
            }
        }

        /// <summary>Is <paramref name="sess"/> still the live session of an engine that is not shutting down?</summary>
        static bool IsLiveSession(PcmAudioSession sess)
        {
            lock (s_gate) return ReferenceEquals(s_session, sess) && !s_disposed;
        }

        /// <summary>V-PE14: the prepare had no block for <see cref="SeekStaleMs"/> — the live voice is still playing a position the
        /// user left, so it fades to silence over <see cref="SeekStaleFadeMs"/> (the new voice comes in from silence when it lands).
        /// Does nothing when the swap landed first or the timer was cancelled. <c>s_activeSilenced</c> goes up BEFORE the fade is
        /// requested: a fade that lands and a flag that did not would leave a silent voice nobody gives its level back to.</summary>
        static async Task StaleTimerAsync(PcmAudioSession sess, CancellationToken ct)
        {
            try { await Task.Delay(SeekStaleMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (ct.IsCancellationRequested || sess.SwapLanded) return;
                lock (s_gate) { if (ReferenceEquals(s_session, sess)) s_activeSilenced = true; }
                await sess.FadeActiveToSilenceAsync(TimeSpan.FromMilliseconds(SeekStaleFadeMs), CancellationToken.None).ConfigureAwait(false);
                Log.Info("audio", $"audio.seek.stale-fade afterMs={SeekStaleMs} fadeMs={SeekStaleFadeMs}");
            }
            catch (Exception ex)
            {
                lock (s_gate) s_activeSilenced = false;
                Log.Warn("audio", "audio.seek.stale-fade failed", ex);
            }
        }

        /// <summary>The seek landed on the SAME voice the stale timer had silenced (a ring jump or the in-place fallback — a swap
        /// replaces the voice instead): give it its level back with a 5 ms fade-in.</summary>
        static void RestoreSilencedVoice(PcmAudioSession sess)
        {
            lock (s_gate) { if (!s_activeSilenced) return; s_activeSilenced = false; }
            int frames = Math.Max(1, sess.Format.SampleRate / 200);
            try { sess.SetVoiceEnvelope(sess.ActiveVoiceIdValue, GainEnvelope.Fade(FadeKind.In, sess.SampleClock, frames, CrossCurve.EqualPower)); }
            catch (Exception ex) { Log.Warn("audio", "audio.seek.restore-voice failed", ex); }
        }

        /// <summary>A seek of generation <paramref name="gen"/> will never land (superseded load): stop reporting its target.</summary>
        static void ClearPendingSeek(uint gen)
        {
            lock (s_gate)
            {
                if (s_pendingSeekGen != gen) return;
                s_pendingSeekMs = -1;
                s_pendingSeekQueued = false;
            }
        }

        /// <summary>The bookkeeping and the report after a seek landed, whichever way it was served. The pending target is released
        /// only when THIS seek is still the newest (<c>s_pendingSeekGen == gen</c>); the endgame is re-armed for a seek back out of
        /// its window (and the arm line may log again); the reducer is told the ACHIEVED position (U-11) under the seek's generation
        /// — and, when the seek dropped a committed join, that its prepared row is lost (S-5, V-PA33), so it re-arms the next row.</summary>
        static void AfterSeekLanded(int requestedMs, int achievedMs, uint epoch, uint gen, long t0, Spotify.Audio.Stream.Stats before)
        {
            bool joinLost;
            lock (s_gate)
            {
                if (s_pendingSeekGen == gen) { s_pendingSeekMs = -1; s_pendingSeekQueued = false; }
                // The engine rebased its position to the target, and `p.Position` is the active voice's own: no offset.
                s_activeStartMs = 0;
                if (s_session is { } sess)
                {
                    s_activeJoinFrame = GaplessJoinClock.JoinFrameFor(sess.SampleClock, s_activeDurMs, achievedMs, sess.Format.SampleRate);
                    s_anchorClock = sess.SampleClock;
                    s_anchorPlayheadMs = Math.Max(0, achievedMs);
                }
                // A seek back out of the endgame window re-arms it: the reducer prepares again when it reopens.
                if (s_activeDurMs > 0 && achievedMs < s_activeDurMs - EndingSoonMs(EffectiveFadeMs, s_activeDurMs))
                { s_lastEndgameAskMs = -1; s_endgameAskCount = 0; s_armLogged = false; }
                joinLost = s_joinLostBySeek;
                s_joinLostBySeek = false;
            }
            if (t0 != 0) RecordSeek(requestedMs, t0, before);
            uint stamp = epoch == 0 ? s_loadEpoch : epoch;
            if (joinLost) Post(Input.PrepareLost(stamp));
            PostSignal(AudioSignal.Seeked, stamp, achievedMs, gen);
        }

        /// <summary>The PARKED seek — the one that reached a session that was not yet serving (a restored track opened at
        /// <paramref name="ms"/>, a deferred open's target applied by the tick's Ready arm): nothing is audible yet, so there is no
        /// voice to protect and the engine's own dispatcher (<c>MediaPlayer.SeekAsync</c>) serves it. A user's seek on a playing
        /// track never comes here — that is <see cref="SeekCoreAsync"/>.</summary>
        static async Task ApplySeekAsync(int ms, uint epoch, uint gen, long t0 = 0, Spotify.Audio.Stream.Stats before = default)
        {
            MediaPlayer? p = Volatile.Read(ref s_player);
            if (p is null) return;
            try { await p.SeekAsync(TimeSpan.FromMilliseconds(Math.Max(0, ms)), SeekMode.Accurate).ConfigureAwait(false); }
            catch (Exception ex) { SeekFailed(ms, gen, ex); return; }
            AfterSeekLanded(ms, ms, epoch, gen, t0, before);
        }

        /// <summary>A seek the decoder could not land — `Decoder could not seek to the requested frame` is the engine
        /// giving up after its own probes read nothing, which is what a seek into a ring that never filled looks like
        /// from the far side. A HANDLED outcome, and the handling is the part that was missing: the PARKED target is
        /// released, because `ActivePositionMs` reports a pending target rather than the real playhead (the bar sat at
        /// the place the seek was aiming for, on a track that was not moving) and `s_pendingSeekQueued` latches — one
        /// swallowed failure and the `Ready` arm never queues another seek for the rest of the track.
        ///
        /// <para>No fault is posted here. A seek fails because the BYTES are not there, and the body that has them is
        /// already being folded by <see cref="FoldStall"/> — which now fails a refused body in seconds. Two verdicts for
        /// one cause would be one fault too many; the line below is what ties them together in the log.</para></summary>
        static void SeekFailed(int ms, uint gen, Exception ex)
        {
            IMediaByteSource? live;
            lock (s_gate)
            {
                live = s_bytes;
                // Only when this is still the newest seek: a newer one's target is the one the bar must keep showing.
                if (s_pendingSeekGen == gen)
                {
                    s_pendingSeekMs = -1;
                    s_pendingSeekQueued = false;
                }
            }
            Log.Warn("audio", $"audio.seek.failed to={ms} stallMs={StallOf(live)} "
                              + $"refusing={(RefusedOf(live) ? 1 : 0)} why={ex.GetType().Name}", ex);
        }

        /// <summary>A seek on a silent VOICE session: a fresh silent session holding what is left of the track from
        /// <paramref name="ms"/> (<see cref="SilentStart"/>), swapped in for <paramref name="old"/>, with the position
        /// offset CARRIED to the target — the old path zeroed it, so a fake track reported 0:00 after every seek. Play
        /// intent is kept: a paused track stays paused at its new position. False when a load replaced the session while
        /// the new one was being built.</summary>
        static async Task<bool> SilentSeekAsync(PcmAudioSession old, int ms, uint epoch, uint gen)
        {
            long durMs;
            bool play;
            lock (s_gate) { durMs = s_activeDurMs; play = s_playIntent; }
            SilentStart start = SilentStart.For(durMs, ms);
            PcmAudioSession fresh = OpenSilentSession(SilentFormat, start.VoiceMs, s_effects, s_volume);
            bool adopted;
            GlitchLedger.Snapshot summary = default;
            EntityId summaryTrack = default;
            lock (s_gate)
            {
                adopted = s_silent && ReferenceEquals(s_session, old);
                if (adopted)
                {
                    summary = RetireXruns(old);
                    summaryTrack = s_id;
                    s_session = fresh;
                    if (s_pendingSeekGen == gen) { s_pendingSeekMs = -1; s_pendingSeekQueued = false; }
                    s_activeStartMs = -start.PositionOffsetMs;
                    s_activePrimaryId = fresh.PrimaryVoiceIdValue;
                    if (ms < durMs - EndingSoonMs(EffectiveFadeMs, durMs)) { s_lastEndgameAskMs = -1; s_endgameAskCount = 0; }
                }
            }
            if (!adopted)
            {
                try { await fresh.DisposeAsync().ConfigureAwait(false); } catch { }
                return false;
            }
            LogSessionSummary(in summary, summaryTrack);
            try { fresh.SetMuted(Muted.Peek()); } catch { }
            try { await old.DisposeAsync().ConfigureAwait(false); } catch { }
            if (play) { try { await fresh.PlayAsync().ConfigureAwait(false); } catch { } }
            PostSignal(AudioSignal.Seeked, epoch == 0 ? s_loadEpoch : epoch, start.PositionOffsetMs, gen);
            return true;
        }

        // ── 12. the 200 ms tick ──────────────────────────────────────────────────────────────────────────────────────

        static void StartTicker()
        {
            s_ticker ??= new Timer(static _ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            try { s_ticker.Change(TickMs, TickMs); } catch { }
        }

        static void StopTicker()
        {
            try { s_ticker?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        /// <summary>Position, in MS, relative to whatever is the active track right now. The engine's clock is
        /// session-relative and a hand-off rebases `_activeStartMs`, which is how a joined B reports from 0.</summary>
        public static void SetRate(float rate)
        {
            try { Volatile.Read(ref s_player)?.SetRate(rate); }
            catch (Exception ex) { Log.Warn("audio", "speed change failed", ex); }
        }

        static long ActivePositionMs()
        {
            if (s_clockStale) return 0;
            MediaPlayer? p = Volatile.Read(ref s_player);
            PcmAudioSession? sess;
            bool silent;
            lock (s_gate) { sess = s_session; silent = s_silent; }
            if (s_id.Kind == EntityKind.Episode && sess is not null && !silent)
            {
                long content = sess.ContentPositionFrames * 1000L / Math.Max(1, sess.Format.SampleRate);
                return SeekGate.ReportedPositionMs(Volatile.Read(ref s_pendingSeekMs), Math.Max(0, content));
            }
            long raw = !silent && p is not null ? (long)p.Position.Peek().TotalMilliseconds
                     : sess is not null ? sess.PlayedFrames * 1000L / Math.Max(1, sess.Format.SampleRate)
                     : 0;
            int pending = Volatile.Read(ref s_pendingSeekMs);
            return SeekGate.ReportedPositionMs(pending, Math.Max(0, raw - s_activeStartMs));
        }

        /// <summary>The timer's callback: ONE tick at a time (H-11). A tick that overruns the 200 ms period makes the next
        /// timer callback skip instead of running on top of it.</summary>
        static void Tick()
        {
            if (Interlocked.CompareExchange(ref s_tickBusy, 1, 0) != 0) return;
            try { TickCore(); }
            finally { Volatile.Write(ref s_tickBusy, 0); }
        }

        static void TickCore()
        {
            if (s_disposed) { StopTicker(); return; }
            MediaPlayer? p = Volatile.Read(ref s_player);
            PcmAudioSession? sess;
            uint epoch, gen;
            bool silent;
            // `gen`: the seek generation every Started / Position this tick posts is stamped with (V-PA2) — the reducer drops a
            // report whose generation is not its own, so the pump must name the newest seek it knows (`Seek` writes it at the call).
            lock (s_gate) { sess = s_session; epoch = s_loadEpoch; silent = s_silent; gen = s_pendingSeekGen; }
            // A session torn down between ticks would otherwise fire a zombie `pos=0, playing` tick off stale state.
            if (sess is null) { StopTicker(); return; }

            PlaybackState state = silent || p is null ? sess.CurrentState : p.State.Peek();
            long rawPos = s_clockStale ? 0
                : silent || p is null ? sess.PlayedFrames * 1000L / Math.Max(1, sess.Format.SampleRate)
                : (long)p.Position.Peek().TotalMilliseconds;
            long pos = ActivePositionMs();
            int fadeMs = EffectiveFadeMs;

            DrainXruns(sess, pos, state);

            if (!s_errorReported && !silent && p?.Error.Peek() is { } err) { PostFault(MapError(err), epoch); return; }

            // (0) the starve rule (D5): a starving read is "Reconnecting", then a network fault — never the end of the track.
            if (!silent && FoldStall(epoch, pos)) return;

            // (a)+(b) the endgame (G-112): EndgamePlan is evaluated fresh every tick, replacing the once-per-load
            // `s_gaplessArmed`/`s_endingSoonSent` latches — an unprepared endgame that asked once and got nothing kept
            // asking never again (the diagnosed bug: a stalled re-seed left nothing to prepare, so the reducer was
            // never nudged a second time). The arm line still logs once per track (diagnostic — `reason` is what
            // tells a log reader why a boundary was a hard cut); the ask re-fires every ~3 s while the window stays
            // open with nothing prepared and nothing in flight.
            long activePos = pos;
            bool prepared = s_prepItem is { IsReady: true };
            bool handOffInFlight = s_crossfadeInFlight || s_joinPending;
            EndgamePlan plan = EndgamePlan.Decide(activePos, s_activeDurMs, fadeMs, prepared, s_prepOverlap,
                handOffInFlight, s_armLogged, s_lastEndgameAskMs, FrameNowMs());

            if (state == PlaybackState.Playing && plan.LogArm)
            {
                s_armLogged = true;
                int reason = s_prepItem is null && Volatile.Read(ref s_prepInFlight) == 0 ? 4
                           : s_prepItem is null ? 2
                           : !s_prepOverlap ? 3 : 0;
                Log.Info("audio", $"[gapless] arm remainMs={plan.RemainMs} fadeMs={fadeMs} "
                    + $"primed={prepared} overlap={s_prepOverlap} reason={reason} clock={sess.SampleClock}");
            }

            if (state == PlaybackState.Playing && plan.Action == EndgameAction.Ask)
            {
                s_lastEndgameAskMs = FrameNowMs();
                s_endgameAskCount++;
                Log.Info("audio", $"[gapless] ask n={s_endgameAskCount} remainMs={plan.RemainMs}");
                PostEndingSoon(epoch);
            }

            // (c) the commit. `EffectiveFadeMs` is the ONLY selector (rule 3). The boundary is the voice's EXACT end once it
            // is known (G-113), not the catalogue duration — a track 1.6 s shorter than its `duration_ms` never commits
            // against the catalogue figure.
            if (state == PlaybackState.Playing && s_prepItem is { IsReady: true } item)
            {
                long boundaryMs = s_activeDurMs;
                long exactFrames = ActiveExactFrames(sess);
                if (exactFrames >= 0) boundaryMs = exactFrames * 1000 / Math.Max(1, sess.Format.SampleRate);
                switch (HandOffAt(activePos, boundaryMs, fadeMs, prepared: true, s_prepOverlap,
                            s_crossfadeInFlight || s_joinPending))
                {
                    case HandOff.Crossfade:
                        if (CommitCrossfade(sess, item, rawPos, fadeMs)) pos = ActivePositionMs();
                        break;
                    case HandOff.Gapless:
                        // Never into a session a device reload may replace, and never off a stale clock: with a stale
                        // playhead `dur − pos` reads as "the whole track remains" and the join is scheduled off a
                        // clock that is not this track's.
                        if (GaplessJoinClock.CanCommit(s_clockStale, softReloading: false)) CommitGaplessJoin(sess, item);
                        break;
                }
            }

            // (d) the join going live, and the hand-off closing.
            if (s_joinPending)
            {
                if (state == PlaybackState.Ended) AbandonPendingJoin(sess, "ended-before-join");
                else if (sess.SampleClock >= s_joinFrame) { AnnounceGaplessJoin(sess, rawPos); pos = ActivePositionMs(); }
            }
            else if (s_crossfadeInFlight && rawPos - s_activeStartMs >= fadeMs)
            {
                IMediaByteSource? retiring;
                lock (s_gate) { s_crossfadeInFlight = false; retiring = s_retiringBytes; s_retiringBytes = null; }
                if (retiring is not null) { try { retiring.Close(); } catch { } }
            }

            // (e) the state fold.
            switch (state)
            {
                case PlaybackState.Playing:
                    if (s_clockStale) break;                            // the flicker guard: a stale clock reports 0
                    if (!s_startedAnnounced)
                    {
                        s_startedAnnounced = true;
                        RecordFirstAudio();
                        PostSignal(AudioSignal.Started, epoch, pos, gen);
                    }
                    else if (s_lastState != PlaybackState.Playing) PostSignal(AudioSignal.Started, epoch, pos, gen);
                    else if (FrameNowMs() - s_lastPositionPostMs >= PositionSampleMs)
                    {
                        s_lastPositionPostMs = FrameNowMs();
                        PostSignal(AudioSignal.Position, epoch, pos, gen);
                    }
                    break;

                case PlaybackState.Paused:
                    if (s_lastState != PlaybackState.Paused) PostSignal(AudioSignal.Paused, epoch, pos);
                    break;

                case PlaybackState.Opening:
                case PlaybackState.Buffering:
                case PlaybackState.Stalled:
                    if (s_lastState != state) PostSignal(AudioSignal.Buffering, epoch, pos);
                    break;

                case PlaybackState.Ready:
                    int parked = Volatile.Read(ref s_pendingSeekMs);
                    if (parked >= 0 && !s_pendingSeekQueued)
                    {
                        s_pendingSeekQueued = true;                     // once: a 200 ms tick must not queue a seek per tick
                        Enqueue(() => ApplySeekAsync(parked, epoch, gen));
                    }
                    break;

                case PlaybackState.Ended:
                    bool endedEdge = s_lastState != PlaybackState.Ended;
                    // A LIVE body never ends: its end of stream is a dropped connection, which is a network fault (G-128).
                    bool liveBody; lock (s_gate) liveBody = s_opened.IsLive;
                    if (liveBody) { if (endedEdge) { StopTicker(); PostFault(Fault.Network, epoch); } break; }
                    if (!endedEdge && s_endedHold <= 0) break;
                    // A degraded join beats a hard cut: hold `Ended` while a prepare is still filling.
                    if (Volatile.Read(ref s_prepInFlight) > 0 && (endedEdge || s_endedHold > 0))
                    {
                        if (endedEdge) { s_endedHold = EndedHoldMaxTicks; Log.Info("audio", "[gapless] ended-hold"); }
                        else s_endedHold--;
                        if (s_endedHold > 0) break;
                    }
                    s_endedHold = 0;
                    StopTicker();
                    Post(Input.Ended(epoch, FrameNowMs()));
                    break;

                case PlaybackState.Failed:
                    PostFault(Fault.DecodeFailed, epoch);
                    break;
            }
            s_lastState = state;
        }

        /// <summary>Fold the live body's stall into the reducer (D5, G-102): "Reconnecting" (<c>RecoveryKind.Network</c> +
        /// Buffering) once a read has waited <see cref="StarvePolicy.StallReportMs"/>, cleared (+ Buffered) the moment bytes
        /// flow again, and <see cref="Fault.Network"/> once it has waited <see cref="StarvePolicy.StallFailMs"/> — the
        /// reducer's Stop then closes the body, which is what ends the read. True when the load failed.
        ///
        /// <para>A body whose mirrors are REFUSING gets <see cref="StarvePolicy.RefusedFailMs"/> instead: the user still
        /// sees "Reconnecting" first (a re-resolve really can fix expired urls), but the load fails in seconds rather
        /// than holding a silent track for a minute and a half. The FAULT is the same `Network` either way — what the
        /// user can do about it is the same, and only the log line needs to know which it was.</para></summary>
        static bool FoldStall(uint epoch, long posMs)
        {
            IMediaByteSource? live;
            lock (s_gate) live = s_bytes;
            long stallMs = StallOf(live);
            bool refused = RefusedOf(live);
            switch (StarvePolicy.Decide(stallMs, refused))
            {
                case StarvePolicy.Verdict.Failed:
                    Log.Warn("audio", $"audio.starve stallMs={stallMs} reason={(refused ? "refused" : "slow")} "
                                      + "— failing the load");
                    PostFault(Fault.Network, epoch);
                    return true;
                case StarvePolicy.Verdict.Recovering when !s_recovering:
                    s_recovering = true;
                    s_ledger.RecordByteWait(stallMs);                      // D1: the byte seam starved long enough to be reported
                    Log.Warn("audio", $"audio.starve stallMs={stallMs} posMs={posMs} "
                                      + $"reason={(refused ? "refused" : "slow")} — reconnecting");
                    Post(Input.Recovering(RecoveryKind.Network, epoch));
                    PostSignal(AudioSignal.Buffering, epoch, posMs);
                    return false;
                case StarvePolicy.Verdict.Flowing when s_recovering:
                    s_recovering = false;
                    Log.Info("audio", $"audio.starve.recovered posMs={posMs}");
                    Post(Input.Recovering(RecoveryKind.None, epoch));
                    PostSignal(AudioSignal.Buffered, epoch, posMs);
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>Drain the RT feed's xrun events on the TICK thread, one structured Warning (`audio.glitch`) per incident and
        /// never a cumulative count — a count says "something has been wrong for a while", an incident says when. Each incident
        /// also goes into the session's <see cref="GlitchLedger"/> (D1), whose verdict rides on the line: the log and the
        /// health card tell the same story.</summary>
        static void DrainXruns(PcmAudioSession sess, long posMs, PlaybackState state)
        {
            bool live;
            EntityId track = default;
            lock (s_gate)
            {
                // The live session's running totals, for `Metrics`; a torn-down session's were folded in by RetireXruns.
                live = ReferenceEquals(s_session, sess);
                if (live)
                {
                    Volatile.Write(ref s_liveXruns, sess.XrunCount);
                    Volatile.Write(ref s_liveXrunFrames, sess.XrunFramesLost);
                    track = s_id;
                }
            }
            int rate = sess.Format.SampleRate;
            // Read BEFORE draining: an incident's event carries only its FIRST block's shortfall (the feed latches once per
            // incident) while every later block of the same stall accrues into XrunFramesLost. What the drained events do not
            // account for is the open incident's tail — folded in below so the card's "longest stall" is the real length.
            long lostBeforeDrain = sess.XrunFramesLost;
            Span<AudioFeedThread.XrunEvent> buf = stackalloc AudioFeedThread.XrunEvent[16];
            int n;
            while ((n = sess.DrainXrunEvents(buf)) > 0)
            {
                long lost = sess.XrunFramesLost;
                for (int i = 0; i < n; i++)
                {
                    AudioFeedThread.XrunEvent ev = buf[i];
                    double gapMs = rate > 0 && ev.GapFrames > 0 ? ev.GapFrames * 1000.0 / rate : 0.0;
                    // `ev.Timestamp` is a QPC value, NOT a tick count: subtracting it from TickCount64 always clamped
                    // to zero, which is how this line read "ageMs=0" for a year.
                    long ageMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(ev.Timestamp).TotalMilliseconds;
                    // A session already retired (the tick captured it before the swap) must not pollute the NEW session's ledger.
                    if (live) s_ledger.Record(ev.GapFrames, ev.RingFrames, ev.GcPauseTicksDelta, rate, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    GlitchLedger.Snapshot snap = live ? s_ledger.Read() : default;
                    Log.Event(WaveeLogLevel.Warning, "audio", "audio.glitch", "underrun", null, (long)Math.Round(gapMs), null,
                        WaveeLogField.Of("stallMs", (long)Math.Round(gapMs)), WaveeLogField.Of("gapFrames", ev.GapFrames),
                        WaveeLogField.Of("ringFramesAtMiss", ev.RingFrames), WaveeLogField.Of("gcTicks", ev.GcPauseTicksDelta),
                        WaveeLogField.Of("posMs", posMs), WaveeLogField.Of("state", state.ToString()),
                        WaveeLogField.Of("voice", ev.VoiceId), WaveeLogField.Of("totalFramesLost", lost),
                        WaveeLogField.Of("ageMs", ageMs), WaveeLogField.Of("track", track.Text),
                        WaveeLogField.Of("verdict", snap.Verdict));
                }
                if (n < buf.Length) break;
            }
            if (live && rate > 0)
            {
                long tail = lostBeforeDrain - s_ledger.Read().FramesLost;
                if (tail > 0) s_ledger.Extend(tail, rate, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            long now = FrameNowMs();
            if (now - s_lastWorkLogMs < WorkLogPeriodMs) return;
            s_lastWorkLogMs = now;
            var w = sess.ReadWorkCounters();
            Log.Info("audio", $"[audio-work] clock={sess.SampleClock} gain={w.Gain} gainSkipped={w.GainSkipped} "
                + $"channel={w.Channel} transport={w.Transport} meter={w.Meter} managerWakes={w.ManagerWakes} "
                + $"managerPasses={w.ManagerPasses} xruns={sess.XrunCount}");
        }

        // ── 13. the device-format reload ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The default endpoint changed its mix format. A rate-changed `RebuildSink` latches
        /// `RequiresGraphRebuild` ONE-WAY: the graph bound at prepare time cannot render on the new device and the
        /// session is SILENT until a new graph exists. So "leave the old session playing" is only benign for a
        /// same-rate swap, and `DeviceRecoveryPlan` is the one place that distinction is made — silence-with-retry
        /// beats off-pitch-forever, and a silent session that nobody reloads is the worst of the three.</summary>
        static void OnDeviceFormatChanged(MixFormat newFormat)
        {
            if (s_disposed) return;
            Log.Info("audio", $"device format changed to {newFormat.SampleRate} Hz × {newFormat.Channels}");
            PcmAudioSession? sess;
            IMediaByteSource? bytes;
            uint epoch;
            bool playIntent;
            lock (s_gate) { sess = s_session; bytes = s_bytes; epoch = s_loadEpoch; playIntent = s_playIntent; }
            bool rebuild = sess?.RequiresGraphRebuild ?? true;
            DeviceRecoveryAction action = DeviceRecoveryPlan.Decide(rebuild, canReopen: bytes?.Caps.Seekable ?? false);
            Log.Info("audio", $"device recovery action={action}");
            switch (action)
            {
                case DeviceRecoveryAction.KeepSession:
                    // Same rate, body not reopenable (a live stream): keep it, and make sure the swap did not leave the
                    // transport parked.
                    if (playIntent)
                    {
                        Enqueue(static async () =>
                        {
                            MediaPlayer? p = Volatile.Read(ref s_player);
                            if (p is not null) { try { await p.PlayAsync().ConfigureAwait(false); } catch { } }
                        });
                    }
                    return;
                case DeviceRecoveryAction.AdoptIntoExistingGraph:
                    // A same-rate swap: a prepared voice primed for the old rate is still valid, the graph is not
                    // rebuilt, and nothing needs a reload.
                    return;
                default:
                    // The graph must be rebuilt: a prepared slot primed for the old rate would play at the wrong
                    // speed, so it goes first, and the REDUCER reloads the row at its position (G-108). `DeviceLost` was
                    // the Connect roster's input and only ever moved a foreign owner: the session sat "Playing" in silence.
                    AbandonPendingJoin(sess, "device-format");
                    DisposePreparedSlot();
                    PostDeviceReload(epoch);
                    return;
            }
        }

        static void OnDeviceRebuilt(MixFormat format, long frame)
        {
            Log.Info("audio", $"device rebuilt at {format.SampleRate} Hz, frame {frame}");
            RefreshDevices();
        }

        // ── 14. posting back ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><paramref name="gen"/>: the seek generation the report belongs to (V-PA2). The reducer drops a
        /// <see cref="AudioSignal.Position"/> / <see cref="AudioSignal.Started"/> / <see cref="AudioSignal.Seeked"/> whose generation
        /// is not its own, so those three always carry the pump's newest one; every other signal ignores it.</summary>
        static void PostSignal(AudioSignal signal, uint epoch, long arg = 0, uint gen = 0)
            => Post(Input.Audio(signal, epoch, FrameNowMs(), arg, gen));

        static void PostFault(Fault fault, uint epoch = 0)
        {
            if (s_errorReported) return;
            s_errorReported = true;
            StopTicker();
            Log.Warn("audio", "fault=" + fault);
            Post(Input.Audio(AudioSignal.Failed, epoch == 0 ? s_loadEpoch : epoch, FrameNowMs(), (long)fault));
        }

        static Fault MapError(MediaError err) => err.Category switch
        {
            MediaErrorCategory.Network => Fault.Network,
            MediaErrorCategory.Decode or MediaErrorCategory.UnsupportedCodec => Fault.DecodeFailed,
            MediaErrorCategory.Drm => Fault.DrmRequired,
            MediaErrorCategory.Source => Fault.Unavailable,
            MediaErrorCategory.Output => Fault.RuntimeMissing,
            _ => Fault.Unknown,
        };

        // ── 14a. the reducer signals of gap batch B3's shapes (G-100 · G-108 · G-112) ────────────────────────────────
        //
        // The reducer (`Playback.cs` / `Playback.Transitions.cs`, B3) defines the inputs; the pump posts them through
        // `Playback.Host`'s named reports and answers the reducer's effects through the three `Pump*` partial methods at
        // the end of this file. Every one of them is stamped with the load epoch it belongs to (C4).

        /// <summary>A gapless butt-join went live or a crossfade committed: the reducer advances WITHOUT a Load (D4) and
        /// answers with an adoption (<see cref="AdoptHandOff"/>). <paramref name="outgoing"/> is the load epoch of the track
        /// that just handed over. Replaces the old `Started(outgoing, 0)` post, which never moved the cursor, so the UI kept
        /// showing A while B played and B's own end loaded B again.</summary>
        static void PostHandedOff(uint outgoing, EntityId joined, int positionMs)
        {
            Log.Info("audio", $"audio.handoff epoch={outgoing} joined={(joined.IsEmpty ? "?" : "next")} posMs={positionMs}");
            ReportHandedOff(outgoing, joined, positionMs);
        }

        /// <summary>This load's endgame window opened (<see cref="EndingSoonMs"/>: fade + 8 s), once per load — the reducer
        /// prepares the next row now rather than at the load.</summary>
        static void PostEndingSoon(uint epoch) => ReportEndingSoon(epoch);

        /// <summary>The device changed format under a graph that cannot render on it: the reducer reloads the row at its
        /// position with play intent kept (G-108).</summary>
        static void PostDeviceReload(uint epoch)
        {
            Log.Info("audio", $"audio.device-reload epoch={epoch}");
            ReportDeviceReload(epoch);
        }

        /// <summary>The reducer's answer to <see cref="PostHandedOff"/> (<c>Effects.Adopt</c>): the voice already playing
        /// belongs to <paramref name="toEpoch"/> now — IF the pump is still on <paramref name="fromEpoch"/> (a load that
        /// replaced the voice in between wins). The joined row's duration and badge are posted again under the new epoch,
        /// because the reducer put a fresh row on the deck and cleared both. UI thread.</summary>
        public static void AdoptHandOff(uint fromEpoch, uint toEpoch)
        {
            Opened opened;
            lock (s_gate)
            {
                if (s_loadEpoch != fromEpoch || s_session is null) return;
                s_loadEpoch = toEpoch;
                opened = s_opened;
            }
            PostOpenedFacts(in opened, toEpoch);
        }

        // ── the five sources, as one switch ──────────────────────────────────────────────────────────────────────────
        //
        // THE ROUTING TABLE IS ONE SWITCH (plan §4.9, 5.10). There is no provider registry, no interface per source
        // and no container: `Open` is a `switch` over `EntityId.Provider` and adding a provider is adding an arm.
        //
        // Plan §4.9 sketched `AudioSource : IDisposable { long Length; int Read(long offset, Span<byte> into) }` — a
        // byte source keyed by absolute offset. That IS the engine's `IMediaByteSource` with the position folded into
        // `Seek` + `Read`, so the five are written AS engine byte sources and the pump hands them to `PcmAudioPlayer`
        // through `MediaSource.FromPull` (FLAC plan §4.2). Nothing here needs an engine change.
        //
        // THE ONE INVARIANT EVERY SOURCE OWES THE DECODER: `Read` returns 0 ONLY at a true end of stream. Every codec
        // above this seam latches the first zero as PERMANENT EOF — the engine's own decoder, `AdtsFrameParser` and
        // `DecoderAudioSource` all do — so a transient miss handed up as a short read silently TRUNCATES the track. A
        // Spotify body guarantees it in its ring's bounded wait (`RingSource`, §9a); §16 is the same primitive for a
        // module's `Stream`.

        // ── 15. the five sources: the routing table ──────────────────────────────────────────────────────────────────

        /// <summary>What the pump knows about an opened track beyond its bytes. <see cref="Format"/> is what the
        /// decoder factory switches on; <see cref="Label"/> is what the stage badge and the deck faces print
        /// (ch 21 §10 item 77, ch 23 W18/W19). <see cref="Peak"/> is the linear true peak the normalization gain is capped
        /// by (<see cref="NormalizationFactor"/>), 0 when unknown. <see cref="AlbumGainDb"/>/<see cref="AlbumPeak"/> are the same
        /// pair for the album (D5): what album mode picks, 0 when the source carries none.</summary>
        public readonly record struct Opened(
            Spotify.Audio.Format Format, long DurationMs, float GainDb, string Label, int BitrateKbps, bool IsLive,
            float Peak = 0f, float AlbumGainDb = 0f, float AlbumPeak = 0f);

        /// <summary>Where a module playable's bytes come from (owner T's `Platform/Modules.Host.cs`, Wave 6). A SEAM
        /// rather than a call, so Wave 3 builds and runs with no module host at all: an unattached provider answers
        /// <c>Fault.Unavailable</c> instead of failing to compile.</summary>
        public static Func<EntityId, CancellationToken, (Stream? Stream, Opened Opened)>? ModuleOpen { get; set; }

        /// <summary>Where a local playable's file is (owner T's `Platform/Modules.cs` `PlayableLinks`, Wave 6). Same
        /// late bind; the FILE read and the format sniff are this file's.</summary>
        public static Func<EntityId, string?>? LocalPath { get; set; }

        /// <summary>Where a Wavee podcast episode's audio is (the podcast owner's episode store, owner M): the enclosure
        /// url and the episode's duration, or null when the episode has none. Same late bind (G-109): an unattached store
        /// answers <c>Fault.Unavailable</c>.</summary>
        public static Func<EntityId, (string Url, long DurationMs)?>? PodcastEnclosure { get; set; }

        /// <summary>Which source a provider's bytes come from — the routing table as a value, so every arm is one
        /// assertion (G-109: a `wavee:episode:` used to fall into the Spotify track ladder and fail as Restricted).</summary>
        public enum SourceRoute : byte
        {
            /// <summary>The file-id ladder, the key, the CDN (<c>Spotify.Audio.Open</c>).</summary>
            Spotify,
            /// <summary>A Wavee podcast episode's enclosure, a plain body on its own host.</summary>
            Podcast,
            /// <summary>A file on disk.</summary>
            Local,
            /// <summary>A playback module's stream.</summary>
            Module,
            /// <summary>No bytes: the silent voice (`--fake`).</summary>
            Silent,
            /// <summary>Not playable.</summary>
            None,
        }

        /// <inheritdoc cref="SourceRoute"/>
        public static SourceRoute RouteOf(EntityProvider provider) => provider switch
        {
            EntityProvider.Spotify => SourceRoute.Spotify,
            EntityProvider.WaveePodcast => SourceRoute.Podcast,
            EntityProvider.Local => SourceRoute.Local,
            EntityProvider.Module => SourceRoute.Module,
            EntityProvider.Fake => SourceRoute.Silent,
            _ => SourceRoute.None,
        };

        /// <summary>Open the bytes for <paramref name="id"/>. Blocks: metadata, the ladder, the CDN, the key. Runs on
        /// the pump, never on the UI thread. A failure answers a typed <see cref="Playback.Fault"/> and a null
        /// source; it never throws into the reducer. <paramref name="prepared"/>: the next track, opened ahead of a
        /// hand-off — its read-ahead comes out of the shared budget sized for that (Vorbis plan §5.3).</summary>
        public static IMediaByteSource? Open(EntityId id, CancellationToken ct, out Opened opened, out Fault fault,
            bool prepared = false)
        {
            opened = default;
            fault = Fault.None;
            switch (RouteOf(id.Provider))
            {
                case SourceRoute.Spotify:
                    return SpotifySource(id, ct, prepared, out opened, out fault);
                case SourceRoute.Podcast:
                    return PodcastSource(id, ct, prepared, out opened, out fault);
                case SourceRoute.Local:
                    return LocalSource(id, out opened, out fault);
                case SourceRoute.Module:
                    return ModuleSource(id, ct, out opened, out fault);
                case SourceRoute.Silent:
                    // ch 31 GAP 6: the demo catalog has no bytes at all. The pump substitutes the silent sink and the
                    // ticker advances position off the frame clock — ten surfaces light up with no network.
                    opened = new Opened(Spotify.Audio.Format.Unknown, 0, 0f, "", 0, false);
                    return null;
                default:
                    fault = Fault.Unavailable;
                    return null;
            }
        }

        /// <summary>A Wavee podcast episode: its enclosure url from the episode store, opened as a plain external body
        /// (<c>Spotify.Audio.ExternalChoice</c>) — no ladder, no key, no CDN resolve.</summary>
        static IMediaByteSource? PodcastSource(EntityId id, CancellationToken ct, bool prepared, out Opened opened, out Fault fault)
        {
            opened = default;
            (string Url, long DurationMs)? enclosure;
            try { enclosure = PodcastEnclosure?.Invoke(id); }
            catch (Exception ex) { Log.Warn("audio", "podcast enclosure lookup failed", ex); enclosure = null; }
            if (enclosure is not { Url.Length: > 0 } found) { fault = Fault.Unavailable; return null; }
            Spotify.Audio.FileChoice choice = Spotify.Audio.ExternalChoice(found.Url, found.DurationMs);
            Spotify.Audio.Opened o;
            try { o = Spotify.Audio.Open(in choice, ct, prepared); }
            catch (OperationCanceledException) { fault = Fault.None; return null; }
            catch (Exception ex) { Log.Warn("audio", "podcast open failed", ex); fault = Fault.Network; return null; }
            return BodySource(in o, prepared, out opened, out fault);
        }

        /// <summary>Spotify: the fileId ladder, the key, the CDN, and the byte stores — all of it owner F's
        /// `Spotify.Audio.Open`. This wraps the opened <see cref="Spotify.Audio.Body"/> in <see cref="RingSource"/> (Vorbis
        /// plan §5.5) and reads the format, duration and normalization gain off the answer. The body's own `ReadAt` never
        /// answers 0 before the end, so no shim sits in between.</summary>
        static IMediaByteSource? SpotifySource(EntityId id, CancellationToken ct, bool prepared, out Opened opened, out Fault fault)
        {
            opened = default;
            Spotify.Audio.Opened o;
            try { o = Spotify.Audio.Open(id.Text, QualityFor(id), 0, ct, prepared); }
            catch (OperationCanceledException) { fault = Fault.None; return null; }
            catch (Exception ex) { Log.Warn("audio", "spotify open failed", ex); fault = Fault.Network; return null; }
            return BodySource(in o, prepared, out opened, out fault);
        }

        /// <summary>An opened body as the pump's byte source, with what the pump knows about it.</summary>
        static IMediaByteSource? BodySource(in Spotify.Audio.Opened o, bool prepared, out Opened opened, out Fault fault)
        {
            opened = default;
            if (!o.Ok || o.Body is not { } body)
            {
                try { o.Stream?.Dispose(); } catch { }
                fault = o.Fault == Spotify.Audio.Fault.None ? Fault.Unavailable : MapFault(o.Fault);
                return null;
            }

            fault = Fault.None;
            int kbps = o.DurationMs > 0 && o.Length > 0 ? (int)(o.Length * 8 / o.DurationMs) : BitrateHintKbps(o.Fmt);
            opened = new Opened(o.Fmt, o.DurationMs, o.GainDb, LabelFor(o.Fmt, 0, 0), kbps, IsLive: false, o.Peak, o.AlbumGainDb, o.AlbumPeak);
            Log.Info("audio", $"audio.open fmt={o.Fmt} len={o.Length} durMs={o.DurationMs} gain={o.GainDb:0.0} dB "
                              + $"peak={o.Peak:0.000} album={o.AlbumGainDb:0.0} dB albumPeak={o.AlbumPeak:0.000} kbps={kbps} head={body.HeadBytes} ring={body.Ring.Seconds}s/"
                              + $"{body.Ring.Slots} slots prepared={(prepared ? 1 : 0)} tail={body.TailGranule}");
            return new RingSource(body);
        }

        static Fault MapFault(Spotify.Audio.Fault f) => f switch
        {
            Spotify.Audio.Fault.None => Fault.None,
            // A refused body reaches here only when the 320 demotion did not apply (the rung was already Ogg, or the
            // ladder had nothing else to offer). Retryable, like the network: a fresh open re-resolves the mirrors, and
            // a url set that expired under a long pause is exactly the case a retry fixes.
            Spotify.Audio.Fault.Offline or Spotify.Audio.Fault.Network or Spotify.Audio.Fault.Refused => Fault.Network,
            Spotify.Audio.Fault.NoDeriver => Fault.RuntimeMissing,
            _ => Fault.Unavailable,
        };

        /// <summary>A file the user dropped or imported. The FORMAT comes from the first bytes, never the extension —
        /// the extension gate is the user-facing rule ("Wavee plays .mp3, .ogg, .flac and .mp4 files"), the magic is
        /// what the decoder is chosen by.</summary>
        static IMediaByteSource? LocalSource(EntityId id, out Opened opened, out Fault fault)
        {
            opened = default;
            string? path = LocalPath?.Invoke(id);
            if (path is not { Length: > 0 } || !File.Exists(path)) { fault = Fault.Unavailable; return null; }

            Span<byte> head = stackalloc byte[64];
            Span<byte> after = stackalloc byte[64];
            int n = 0, m = 0;
            long id3 = 0;
            try
            {
                using FileStream probe = new(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 32 * 1024);
                n = probe.Read(head);
                // P-5: `ID3` names a TAG, not a codec — a tagged .flac (or an ADTS file) starts with one too. Re-read just past
                // it (10 + the syncsafe size, +10 for a footer) and sniff THAT; the old rule called every ID3 file MP3.
                id3 = Id3v2Length(head[..n]);
                if (id3 > 0 && id3 < probe.Length)
                {
                    probe.Position = id3;
                    m = probe.Read(after);
                }
            }
            catch (Exception ex) { Log.Warn("audio", "local probe failed", ex); fault = Fault.Unavailable; return null; }

            Spotify.Audio.Format format = (id3 > 0 ? SniffFormat(after[..m]) : SniffFormat(head[..n])) ?? Spotify.Audio.Format.Mp3;
            fault = Fault.None;
            opened = new Opened(format, 0, 0f, LabelFor(format, 0, 0), 0, IsLive: false);
            return new FileByteSource(path);
        }

        static IMediaByteSource? ModuleSource(EntityId id, CancellationToken ct, out Opened opened, out Fault fault)
        {
            opened = default;
            if (ModuleOpen is not { } open) { fault = Fault.Unavailable; return null; }
            Stream? stream;
            try { (stream, opened) = open(id, ct); }
            catch (OperationCanceledException) { fault = Fault.None; return null; }
            catch (Exception ex) { Log.Warn("audio", "module open failed", ex); fault = Fault.Unavailable; return null; }
            if (stream is null) { fault = Fault.Unavailable; return null; }

            fault = Fault.None;
            if (opened.Format == Spotify.Audio.Format.Unknown)
            {
                // A module that serves an encrypted or opaque body cannot always name its container up front, so the
                // bytes decide — content type first (the module told us), then the magic.
                Span<byte> head = stackalloc byte[64];
                int n = stream.CanSeek ? stream.Read(head) : 0;
                if (stream.CanSeek) stream.Position = 0;
                opened = opened with { Format = SniffFormat(head[..n]) ?? Spotify.Audio.Format.Mp3 };
            }
            return new Prefetching(stream, stream.CanSeek);
        }

        /// <summary>A live ICY / SHOUTcast broadcast. Not on the provider switch: a radio station is a MODULE playable
        /// whose host answers with an endless HTTP body, so this is the byte source that host hands back. It is here
        /// because the ICY framing belongs beside the other byte sources, and because its header parse is pure and is
        /// the one part of radio a test can pin.</summary>
        public static IMediaByteSource Radio(Stream body, int metaInt, Action<string>? onTitle = null)
            => new IcySource(body, metaInt, onTitle);

        // ── 16. the never-return-zero shim ───────────────────────────────────────────────────────────────────────────

        /// <summary>The byte seam a module's `Stream` is seen through (a Spotify body is a <see cref="RingSource"/>). A
        /// module hands CONTAINER-relative bytes (its own business to strip any wrapper; the 167-byte skip this used to sniff
        /// for skipped the first 167 bytes of every module MP3). One job: a transient miss is a WAIT, never a short read.
        /// `Read` answers 0 only when the inner stream itself reports a genuine end — every codec above latches the first
        /// zero as permanent EOF, and a slow-but-alive body would otherwise silently truncate the track mid-song.</summary>
        public sealed class Prefetching : IMediaByteSource
        {
            /// <summary>Poll granularity on the fast path, while a range is filling.</summary>
            const int PollDelayMs = 4;

            /// <summary>Poll granularity once the fast path is spent: a stalled module body is looked at ten times a second.</summary>
            const int SlowPollMs = 100;

            /// <summary>The fast path's bound: NOT a deadline after which the track ends (G-103) — past it the read keeps
            /// waiting, coarser, until <see cref="TotalWaitMs"/>.</summary>
            public const int FastWaitMs = 8_000;

            /// <summary>0.2.9's blocking-read budget: only a body silent this long reads as ended.</summary>
            public const int TotalWaitMs = 90_000;

            readonly Stream _inner;
            readonly bool _seekable;
            readonly int _fastWaitMs, _totalWaitMs;

            /// <param name="fastWaitMs">The fast-poll phase (tests shorten it).</param>
            /// <param name="totalWaitMs">The whole wait before a silent body is taken as ended (tests shorten it).</param>
            public Prefetching(Stream inner, bool seekable, int fastWaitMs = FastWaitMs, int totalWaitMs = TotalWaitMs)
            {
                _inner = inner;
                _seekable = seekable && inner.CanSeek;
                _fastWaitMs = Math.Max(0, fastWaitMs);
                _totalWaitMs = Math.Max(_fastWaitMs, totalWaitMs);
            }

            public long? Length => _seekable ? Math.Max(0, _inner.Length) : null;

            public SourceCaps Caps => new() { Seekable = _seekable, KnownLength = _seekable, ExpensiveSeek = true };

            public bool TryOpen(in DataSpec spec)
            {
                try
                {
                    if (!_seekable) return true;
                    _inner.Position = Math.Max(0, spec.Position);
                    return true;
                }
                catch { return false; }
            }

            public int Read(Span<byte> dst)
            {
                if (dst.Length == 0) return 0;
                long start = Environment.TickCount64;
                while (true)
                {
                    int n;
                    try { n = _inner.Read(dst); }
                    catch (ObjectDisposedException) { return 0; }
                    catch (Exception ex) { Log.Warn("audio", "byte source read failed", ex); return -1; }
                    if (n > 0) return n;
                    // A genuine zero from a SEEKABLE stream at its end is real EOF; from a growing one it is a miss.
                    try { if (_seekable && _inner.Position >= _inner.Length) return 0; }
                    catch (ObjectDisposedException) { return 0; }
                    long waited = Environment.TickCount64 - start;
                    if (waited >= _totalWaitMs)
                    {
                        Log.Warn("audio", $"module body silent for {waited} ms — taken as ended");
                        return 0;
                    }
                    Thread.Sleep(waited < _fastWaitMs ? PollDelayMs : SlowPollMs);
                }
            }

            public long Seek(long offset)
            {
                if (!_seekable) return -1;
                try { return _inner.Seek(Math.Max(0, offset), SeekOrigin.Begin); }
                catch { return -1; }
            }

            /// <summary>The engine cancels only a source that is going away: closing the stream releases a read blocked in it.</summary>
            public void Cancel() => Close();

            public void Close() { try { _inner.Dispose(); } catch { } }
        }

        // ── 17. the silent sink (ch 31 GAP 6) ────────────────────────────────────────────────────────────────────────

        /// <summary>What `--fake` plays. Not a byte source: an endpoint that consumes everything and produces a clock,
        /// so `Playback.Host`'s ticker advances position exactly as it would over a real device and the player bar,
        /// the rail, the NPV, the deck machines, the lyric wipe, the queue, the stage, SMTC, the taskbar and the jump
        /// list all light up with no network and no audio device (ch 31 §7.4's note — the highest-value single fixture
        /// in that chapter).
        /// <para>The graph, the mixer, the DSP chain, the RT feed and the level tap all run exactly as they do on a real
        /// device; only the leaf is swapped. The leaf is <see cref="PacedSilentEndpoint"/> rather than the engine's
        /// <c>HeadlessAudioEndpoint</c>, whose null sink accepts every frame (<c>WritableFrames</c> = int.MaxValue) and
        /// so ran a fake track's clock as fast as the feed could spin — headless plan §8 Q4, decided here.</para></summary>
        public static IAudioEndpoint SilentSink(MixFormat format) => new PacedSilentEndpoint(format);

        /// <summary>A silent device that keeps TIME: its "hardware" consumes queued frames at the wall clock's rate while
        /// started, and its buffer is finite (<see cref="DefaultCapacityMs"/>), so a session renders into it at exactly
        /// real time — the RT feed waits on <see cref="WaitForWritable"/> the way it waits on a WASAPI event. The played
        /// count is the authoritative clock, QPC-stamped in the same 100 ns domain the session projects with. A starved
        /// stretch LOSES time (the clock stalls at what was written), as a device's played count does, instead of bursting
        /// ahead when data returns. Allocation-free; one lock (the feed, the clock tick and a disposer call in).
        /// <para>THE TAIL is the engine's problem to solve, not this endpoint's: <c>PcmAudioSession.RenderBlock</c>
        /// (<c>DrainVerdict</c>) now stops rendering/submitting anything once a session's mixer is drained, so a drained
        /// mixer's trailing silence never reaches <see cref="Write"/> here at all. This endpoint just has to behave like
        /// any other finite buffered sink and read empty once its content has drained and nothing further arrives — the
        /// engine's <c>Ended</c> gate (mixer drained AND <c>WritableFrames &gt;= CapacityFrames</c>) then opens on its
        /// own, the same way it does for a real device.</para></summary>
        public sealed class PacedSilentEndpoint : IAudioEndpoint, IBufferedAudioSink, IAudioClockSource
        {
            /// <summary>The buffer a WASAPI shared-mode client gets by default, and what the feed's sizing assumes.</summary>
            public const int DefaultCapacityMs = 100;

            readonly Lock _gate = new();
            readonly Func<long> _now;
            readonly long _ticksPerSecond;
            long _written, _played, _lastTicks;
            bool _started;

            /// <param name="format">The mix format the "device" opened at.</param>
            /// <param name="capacityMs">The buffer, in milliseconds of audio.</param>
            /// <param name="clock">The monotonic clock in <paramref name="ticksPerSecond"/> units; the
            /// <see cref="System.Diagnostics.Stopwatch"/> by default. Tests pass a manual one.</param>
            public PacedSilentEndpoint(MixFormat format, int capacityMs = DefaultCapacityMs, Func<long>? clock = null,
                long ticksPerSecond = 0)
            {
                Format = format;
                CapacityFrames = Math.Max(1, (int)((long)Math.Max(1, format.SampleRate) * Math.Max(1, capacityMs) / 1000));
                if (clock is null) _now = System.Diagnostics.Stopwatch.GetTimestamp;
                else _now = clock;
                _ticksPerSecond = clock is null || ticksPerSecond <= 0 ? System.Diagnostics.Stopwatch.Frequency : ticksPerSecond;
            }

            public MixFormat Format { get; }
            public IAudioSink Sink => this;
            public IAudioClockSource Clock => this;
            public bool IsReady => true;
            public int CapacityFrames { get; }
            public int MixRate => Format.SampleRate;
            public long StreamLatencyFrames => 0;
            public long WrittenFrames { get { lock (_gate) return _written; } }
            /// <summary>Frames queued and not yet "played".</summary>
            public int PaddingFrames { get { lock (_gate) { AdvanceLocked(); return (int)(_written - _played); } } }
            /// <summary>Whether the "device" is running.</summary>
            public bool IsStarted { get { lock (_gate) return _started; } }

            public int WritableFrames { get { lock (_gate) { AdvanceLocked(); return CapacityFrames - (int)(_written - _played); } } }

            public int Write(ReadOnlySpan<float> src, int frames)
            {
                lock (_gate)
                {
                    AdvanceLocked();
                    int room = CapacityFrames - (int)(_written - _played);
                    int accepted = Math.Clamp(frames, 0, Math.Max(0, room));
                    _written += accepted;
                    return accepted;
                }
            }

            public void Start()
            {
                lock (_gate)
                {
                    if (_started) return;
                    _started = true;
                    _lastTicks = _now();
                }
            }

            public void Stop()
            {
                lock (_gate)
                {
                    AdvanceLocked();
                    _started = false;
                }
            }

            /// <summary>Flush the queue while stopped (the engine stops before it resets) and start a new device epoch.</summary>
            public void Reset()
            {
                lock (_gate)
                {
                    _written = 0;
                    _played = 0;
                    _lastTicks = _now();
                }
            }

            public bool TryGetPlayed(out long playedFrames, out long qpc)
            {
                lock (_gate)
                {
                    AdvanceLocked();
                    playedFrames = _played;
                    qpc = (long)(_lastTicks * (1e7 / _ticksPerSecond));
                    return true;
                }
            }

            /// <summary>Wait until a quarter of the buffer is free, a control command wakes the feed, or
            /// <paramref name="timeoutMs"/> — always at least 1 ms, so a feed with nothing to render never spins.</summary>
            public void WaitForWritable(WaitHandle controlWake, int timeoutMs)
            {
                int waitMs;
                lock (_gate)
                {
                    AdvanceLocked();
                    int writable = CapacityFrames - (int)(_written - _played);
                    int threshold = Math.Max(1, CapacityFrames / 4);
                    // Stopped (paused, ended, not yet started): nothing renders, so sleep until a transport command wakes the
                    // feed or the engine's timeout passes, as a WASAPI client does. Started with room: render now.
                    waitMs = !_started
                        ? timeoutMs
                        : writable >= threshold
                            ? 1
                            : (int)Math.Ceiling((threshold - writable) * 1000.0 / Math.Max(1, Format.SampleRate));
                }
                controlWake.WaitOne(Math.Clamp(waitMs, 1, Math.Max(1, timeoutMs)));
            }

            public void Dispose() => Stop();

            /// <summary>Consume queued frames for the time elapsed since the last look. Whole frames only; the remainder
            /// of a tick stays on the clock so the pace does not drift.</summary>
            void AdvanceLocked()
            {
                if (!_started) return;
                long now = _now();
                long elapsed = now - _lastTicks;
                if (elapsed <= 0) return;
                long frames = elapsed * Format.SampleRate / _ticksPerSecond;
                if (frames <= 0) return;
                long queued = _written - _played;
                if (frames >= queued)
                {
                    // Starved (or exactly drained): the clock stops at what was written and the idle time is lost.
                    _played = _written;
                    _lastTicks = now;
                    return;
                }
                _played += frames;
                _lastTicks += frames * _ticksPerSecond / Format.SampleRate;
            }
        }

        /// <summary>A silent VOICE for a fake track: the right number of frames of nothing, so the engine's own clock
        /// retires it at exactly the declared duration and `Ended` arrives on time.</summary>
        public static IAudioSource SilentVoice(MixFormat format, long durationMs)
        {
            long frames = Math.Max(1, durationMs * format.SampleRate / 1000);
            return new SignalGeneratorSource(format.Channels, format.SampleRate, 0d, 0f, frames);
        }

        // ── 18. sniffing ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>P-5: where an ID3v2 block ends — <c>10 + syncsafe(size)</c>, plus 10 when the header's footer flag (byte 5,
        /// bit 4) is set — as a source offset; 0 when <paramref name="head"/> does not start with a well-formed ID3v2 header
        /// (fewer than 10 bytes, no <c>ID3</c>, a 0xFF version, or a size byte with its top bit set). PURE.</summary>
        public static long Id3v2Length(ReadOnlySpan<byte> head)
        {
            if (head.Length < 10 || head[0] != (byte)'I' || head[1] != (byte)'D' || head[2] != (byte)'3') return 0;
            if (head[3] == 0xFF || head[4] == 0xFF) return 0;                              // version / revision are never 0xFF
            if (((head[6] | head[7] | head[8] | head[9]) & 0x80) != 0) return 0;           // syncsafe: seven bits per byte
            long size = ((long)head[6] << 21) | ((long)head[7] << 14) | ((long)head[8] << 7) | head[9];
            return 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
        }

        /// <summary>Magic bytes → format. The order is load-bearing: ADTS and MPEG audio share an 11-bit sync and only
        /// the LAYER field separates them.</summary>
        public static Spotify.Audio.Format? SniffFormat(ReadOnlySpan<byte> head)
        {
            if (head.Length >= 4)
            {
                if (head[..4].SequenceEqual("OggS"u8)) return Spotify.Audio.Format.OggVorbis320;
                if (head[..4].SequenceEqual("fLaC"u8)) return Spotify.Audio.Format.Flac;
            }
            if (head.Length >= 3 && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
                return Spotify.Audio.Format.Mp3;
            for (int i = 0; i + 1 < head.Length; i++)
            {
                if (head[i] != 0xFF || (head[i + 1] & 0xE0) != 0xE0) continue;
                int layer = (head[i + 1] >> 1) & 0x03;
                return layer == 0 ? Spotify.Audio.Format.Aac : Spotify.Audio.Format.Mp3;   // layer 00 = ADTS/AAC
            }
            return null;
        }

        /// <summary>A Content-Type → format fold, for a module or a radio body that named its container. `mp4` returns
        /// null BEFORE the aac test: `audio/mp4` is a container this pipeline does not open, and routing it to the raw
        /// ADTS decoder produces noise rather than an honest refusal.</summary>
        public static Spotify.Audio.Format? SniffContentType(string? contentType)
        {
            if (contentType is not { Length: > 0 }) return null;
            string ct = contentType.ToLowerInvariant();
            if (ct.Contains("mp4", StringComparison.Ordinal)) return null;
            if (ct.Contains("aac", StringComparison.Ordinal)) return Spotify.Audio.Format.Aac;
            if (ct.Contains("mpeg", StringComparison.Ordinal) || ct.Contains("mp3", StringComparison.Ordinal))
                return Spotify.Audio.Format.Mp3;
            if (ct.Contains("ogg", StringComparison.Ordinal) || ct.Contains("vorbis", StringComparison.Ordinal))
                return Spotify.Audio.Format.OggVorbis320;
            if (ct.Contains("flac", StringComparison.Ordinal)) return Spotify.Audio.Format.Flac;
            return null;
        }

        /// <summary>The bitrate a format implies when the catalogue carried none — the read-ahead window is sized off
        /// it. MP3 and AAC are per-file CBR/VBR and answer 0, which means "measure instead".</summary>
        public static int BitrateHintKbps(Spotify.Audio.Format format) => format switch
        {
            Spotify.Audio.Format.OggVorbis96 => 96,
            Spotify.Audio.Format.OggVorbis160 => 160,
            Spotify.Audio.Format.OggVorbis320 => 320,
            Spotify.Audio.Format.Flac => 1_000,
            Spotify.Audio.Format.Flac24 => 1_800,
            _ => 0,
        };

        /// <summary>What the stage badge and the deck faces print. The honest label is the SOURCE format — never a
        /// claim about the output, which is resampled to the device rate like everything else (FLAC plan §4.1/§4.4).</summary>
        public static string LabelFor(Spotify.Audio.Format format, int bps, int sampleRate)
        {
            if (format is Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24)
            {
                if (bps <= 0) return format == Spotify.Audio.Format.Flac24 ? "FLAC 24-bit" : "FLAC";
                return sampleRate > 0
                    ? $"FLAC {bps}/{(sampleRate / 1000.0):0.#}"
                    : bps > 16 ? "FLAC 24-bit" : "FLAC";
            }
            return format switch
            {
                Spotify.Audio.Format.OggVorbis96 => "OGG 96",
                Spotify.Audio.Format.OggVorbis160 => "OGG 160",
                Spotify.Audio.Format.OggVorbis320 => "OGG 320",
                Spotify.Audio.Format.Mp3 => "MP3",
                Spotify.Audio.Format.Aac => "AAC",
                _ => "",
            };
        }

        // ── 19. ICY: the radio framing ───────────────────────────────────────────────────────────────────────────────

        /// <summary>What a SHOUTcast/ICY response head says. Parsed rather than handed to `HttpClient` because
        /// SHOUTcast v1 answers with the status line <c>ICY 200 OK</c>, which the BCL parser rejects outright.</summary>
        public readonly record struct IcyHead(int Status, string? ContentType, string? Name, string? Genre,
            int BitrateKbps, int MetaInt, string? Location);

        /// <summary>Parse a response head. Pure over bytes, so every rule below is one assertion in a test: the `ICY`
        /// status line, bare-LF line endings, lower-cased last-wins header names, and the five headers radio actually
        /// carries. <paramref name="consumed"/> is where the BODY starts — everything past it is already-read audio
        /// and must never be fetched from the socket a second time.</summary>
        public static bool TryParseIcyHead(ReadOnlySpan<byte> data, out IcyHead head, out int consumed)
        {
            head = default;
            consumed = 0;
            int end = -1, sepLen = 0;
            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == (byte)'\n' && data[i + 1] == (byte)'\n') { end = i; sepLen = 2; break; }
                if (i + 3 < data.Length && data[i] == (byte)'\r' && data[i + 1] == (byte)'\n'
                    && data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n') { end = i; sepLen = 4; break; }
            }
            if (end < 0) return false;

            consumed = end + sepLen;
            string text = Encoding.Latin1.GetString(data[..end]);
            string[] lines = text.Split('\n');
            if (lines.Length == 0) return false;

            // The status line: `HTTP/1.x <code> …` or `ICY <code> …`.
            string status = lines[0].TrimEnd('\r');
            int firstSpace = status.IndexOf(' ');
            if (firstSpace <= 0) return false;
            string proto = status[..firstSpace];
            if (!proto.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
                && !proto.Equals("ICY", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = status[(firstSpace + 1)..].TrimStart();
            int codeEnd = rest.IndexOf(' ');
            if (!int.TryParse(codeEnd > 0 ? rest[..codeEnd] : rest, out int code)) return false;

            string? contentType = null, name = null, genre = null, location = null;
            int kbps = 0, metaInt = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line[..colon].ToLowerInvariant();
                string value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "content-type": contentType = value; break;
                    case "icy-name": name = value; break;
                    case "icy-genre": genre = value; break;
                    case "location": location = value; break;
                    case "icy-br": if (int.TryParse(value, out int br) && br > 0) kbps = br; break;
                    case "icy-metaint": if (int.TryParse(value, out int mi) && mi > 0) metaInt = mi; break;
                }
            }
            head = new IcyHead(code, contentType, name, genre, kbps, metaInt, location);
            return true;
        }

        /// <summary>Pull the title out of one ICY metadata block. The terminator is NOT simply the first <c>';</c>:
        /// titles legitimately contain apostrophes (<c>Don't Stop</c>) and blocks carry further <c>key='value'</c>
        /// pairs, so a candidate only ends the value when what follows is the end of the block, its NUL padding, or
        /// another key.</summary>
        public static string? IcyStreamTitle(string block)
        {
            const string Key = "StreamTitle='";
            int at = block.IndexOf(Key, StringComparison.Ordinal);
            if (at < 0) return null;
            int start = at + Key.Length;
            int scan = start;
            while (true)
            {
                int term = block.IndexOf("';", scan, StringComparison.Ordinal);
                if (term < 0) return block[start..].TrimEnd('\0').TrimEnd('\'');
                if (IsFieldBoundary(block, term + 2)) return block[start..term];
                scan = term + 1;
            }

            static bool IsFieldBoundary(string block, int index)
            {
                int i = index;
                while (i < block.Length && (block[i] == '\0' || block[i] == ' ')) i++;
                if (i >= block.Length) return true;
                if (!char.IsAsciiLetter(block[i])) return false;
                int j = i;
                while (j < block.Length && (char.IsAsciiLetterOrDigit(block[j]) || block[j] == '_')) j++;
                return j + 1 < block.Length && block[j] == '=' && block[j + 1] == '\'';
            }
        }

        /// <summary>Split an ICY title on the FIRST <c>" - "</c>. With no separator the whole string is the title and
        /// the artist is null — the caller then falls back to the station name.</summary>
        public static (string Title, string? Artist) SplitIcyTitle(string raw)
        {
            int at = raw.IndexOf(" - ", StringComparison.Ordinal);
            if (at <= 0) return (raw.Trim(), null);
            string artist = raw[..at].Trim();
            string title = raw[(at + 3)..].Trim();
            return title.Length == 0 || artist.Length == 0 ? (raw.Trim(), null) : (title, artist);
        }

        /// <summary>The metaint state machine over an endless body. It is a BYTE state machine, not a per-read split,
        /// precisely because chunk boundaries are arbitrary: a block, its length byte, and even the audio run before
        /// it routinely straddle three reads.</summary>
        public sealed class IcySource : IMediaByteSource
        {
            const int MaxMetaBytes = 255 * 16;

            readonly Stream _body;
            readonly int _metaInt;
            readonly Action<string>? _onTitle;
            readonly byte[] _meta = new byte[MaxMetaBytes];
            int _untilMeta;
            bool _needLengthByte;
            int _metaRemaining, _metaFilled;
            string _currentTitle = "";

            public IcySource(Stream body, int metaInt, Action<string>? onTitle)
            {
                _body = body;
                _metaInt = Math.Max(0, metaInt);
                _untilMeta = _metaInt;
                _onTitle = onTitle;
            }

            public long? Length => null;                       // an endless stream has no length…
            public SourceCaps Caps => default;                 // …and no seek, which also keeps the MP3 tag probe off it

            public bool TryOpen(in DataSpec spec) => true;

            public long Seek(long offset) => -1;

            /// <summary>The engine cancels only a source that is going away: closing the body releases a read blocked on
            /// the socket.</summary>
            public void Cancel() => Close();

            public void Close() { try { _body.Dispose(); } catch { } }

            public int Read(Span<byte> dst)
            {
                if (_metaInt == 0) return ReadRaw(dst);        // no metaint ⇒ pure pass-through
                while (true)
                {
                    if (_metaRemaining > 0 && !FillMeta()) return 0;
                    if (_needLengthByte && !ConsumeLengthByte()) return 0;
                    if (_metaRemaining > 0 || _needLengthByte) continue;

                    int take = Math.Min(_untilMeta, dst.Length);
                    if (take <= 0) { _needLengthByte = true; continue; }
                    int n = ReadRaw(dst[..take]);
                    if (n <= 0) return n;
                    _untilMeta -= n;
                    if (_untilMeta == 0) _needLengthByte = true;
                    return n;
                }
            }

            int ReadRaw(Span<byte> dst)
            {
                try { return _body.Read(dst); }
                catch (ObjectDisposedException) { return 0; }
                catch (Exception ex) { Log.Warn("audio", "icy body read failed", ex); return -1; }
            }

            bool ConsumeLengthByte()
            {
                Span<byte> one = stackalloc byte[1];
                if (ReadRaw(one) <= 0) return false;
                _needLengthByte = false;
                _untilMeta = _metaInt;
                _metaRemaining = one[0] * 16;
                _metaFilled = 0;
                return true;
            }

            bool FillMeta()
            {
                int n = ReadRaw(_meta.AsSpan(_metaFilled, _metaRemaining));
                if (n <= 0) return false;
                _metaFilled += n;
                _metaRemaining -= n;
                if (_metaRemaining > 0) return true;
                PublishTitle(_meta.AsSpan(0, _metaFilled));
                _metaFilled = 0;
                return true;
            }

            void PublishTitle(ReadOnlySpan<byte> payload)
            {
                while (payload.Length > 0 && payload[^1] == 0) payload = payload[..^1];   // 16-byte alignment padding
                if (payload.Length == 0) return;
                string block;
                // Strict UTF-8 first, Latin-1 second: a mis-decoded Latin-1 title is still readable, U+FFFD is not.
                try { block = new UTF8Encoding(false, true).GetString(payload); }
                catch (DecoderFallbackException) { block = Encoding.Latin1.GetString(payload); }
                if (IcyStreamTitle(block) is not { Length: > 0 } title) return;
                // Servers re-send the SAME block every interval; only a real change is an event.
                if (string.Equals(title, _currentTitle, StringComparison.Ordinal)) return;
                _currentTitle = title;
                _onTitle?.Invoke(title);
            }
        }

        // ── 20. FLAC: the engine-facing adapter (FLAC plan §4.3) ─────────────────────────────────────────────────────
        //
        // The CORE decoder — the bit reader, the four subframes, the Rice residual, the decorrelation, CRC-8/16, the
        // seek-point search — is owner U's `Playback.Audio.Flac.cs`, pure over spans. THIS is the 220 lines that make
        // it an `IAudioDecoder`: a byte window over the engine's byte seam, the float conversion with the
        // normalization gain folded into its scale constant, and the engine's own resampler at the decode edge.
        //
        // TWO NOTES ON WHAT THIS ADAPTER TRUSTS.
        //   THE BYTE SEAM IS THE READ-AHEAD. A Spotify FLAC arrives as a `RingSource` (§9a): the window reads through
        //   its random-access face, which the clear head, the seconds-sized ring and the disk cache answer, and which
        //   returns 0 only at true EOF. A local file keeps the engine's sequential `Read`. A zero here IS the end.
        //   A SEEK IS THE PROBES THE PLANNER ASKS FOR. Interpolation lands within a frame or two of the target on a
        //   ~700-1,400 kbit/s file; each probe re-targets the ring first, so it is one range request (none when the
        //   window is resident) with any in-flight fill cancelled, and the bound is 32 probes. 0.2.9's answer to a
        //   FLAC seek was a SILENT NO-OP that reported success, so a scrub desynced position from audio for the rest
        //   of the track.

        /// <summary>The engine-facing FLAC decoder: a byte window over an <see cref="IMediaByteSource"/> (its
        /// <see cref="IRandomAccessBytes"/> face when it has one), the CORE <c>Flac.Decoder</c> over the window, float
        /// conversion + gain, and the engine's resampler. Runs on the engine's decode-ahead thread; blocks in the byte
        /// seam and nowhere else.</summary>
        public sealed class FlacAudioDecoder : IAudioDecoder, IGainFolding
        {
            const int WindowBytes = 64 * 1024;
            const int SeekPointCapacity = 1024;
            const int MaxProbes = 32;

            readonly Flac.Decoder _dec = new();
            readonly Flac.SeekPoint[] _seek = new Flac.SeekPoint[SeekPointCapacity];
            float _gainLinear;
            NormalizationFigures _figures;      // what `_gainLinear` was computed from (the ctor's, else the file's ReplayGain tags)
            IMediaByteSource? _src;
            IRandomAccessBytes? _ra;
            uint _epoch;
            MixFormat _target;
            Flac.StreamInfo _si;
            int _seekCount;
            long _firstFrame;               // byte offset of the first frame in the SOURCE
            byte[] _win = new byte[WindowBytes];
            long _winStart;                 // source offset of _win[0]
            int _winLen;                    // valid bytes
            int _cursor;                    // next frame's sync byte, relative to _win[0]
            float[] _conformed = [];        // interleaved stereo floats at the SOURCE rate; [0.._hold) unread
            int _hold;
            int _skipSamples;               // samples to drop after a seek (the target was inside the frame)
            long _samplePos;                // next source sample to be emitted
            bool _eof, _interrupted;
            bool _ending;                   // the last frame is out: the resampler's trailing half kernel (V-PE21), then `_eof`
            PolyphaseResampler? _resampler;

            /// <param name="gainDb">The normalization figure (the catalogue's for a Spotify FLAC).</param>
            /// <param name="peak">The linear true peak that caps a boost (`Opened.Peak`); 0 = unknown.</param>
            /// <param name="albumGainDb">The catalogue's ALBUM gain (`Opened.AlbumGainDb`), 0 = none: album mode's figure (D5).</param>
            /// <param name="albumPeak">The album's linear true peak; 0 = unknown.</param>
            public FlacAudioDecoder(float gainDb, float peak = 0f, float albumGainDb = 0f, float albumPeak = 0f)
            {
                _figures = new NormalizationFigures(gainDb, peak, albumGainDb, albumPeak);
                _gainLinear = GainLinear(gainDb, peak, albumGainDb, albumPeak);
            }

            /// <summary>The linear factor folded into this decoder's output (<see cref="IGainFolding"/>, V-PA15).</summary>
            public float AppliedGainLinear => _gainLinear;

            /// <summary>The figures behind <see cref="AppliedGainLinear"/> (<see cref="IGainFolding.AppliedFigures"/>).</summary>
            public NormalizationFigures? AppliedFigures => _figures;

            public GaplessInfo Gapless { get; private set; } = GaplessInfo.None;

            /// <summary>What the badge prints for THIS file: the source bit depth and rate, which is the only honest
            /// claim (the output is float32 at the device rate like everything else).</summary>
            public string Label { get; private set; } = "FLAC";

            public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
            {
                info = default;
                _src = src;
                _ra = src as IRandomAccessBytes;
                _epoch = _ra?.Epoch ?? 0;
                _target = target;
                if (!src.TryOpen(new DataSpec { Position = 0, Length = -1 })) return false;
                _winStart = 0; _winLen = 0; _cursor = 0;
                if (!Fill()) return false;

                // P-5: a tagged .flac starts with an ID3v2 block and only then `fLaC`. The window restarts at the stream marker, so
                // every offset below (the first frame, the seek table's, the length the planner interpolates over) stays a
                // SOURCE offset — the prefix is just bytes before the file's own start.
                long id3 = Id3v2Length(_win.AsSpan(0, _winLen));
                if (id3 > 0 && !ReadWindowAt(id3)) return false;

                Flac.Headers h = Flac.ParseHeaders(_win.AsSpan(0, _winLen), _seek);
                while (!h.Valid && !h.Complete && _winLen == _win.Length)      // a huge PICTURE block: grow, read on
                {
                    Array.Resize(ref _win, _win.Length * 2);
                    if (!Fill()) return false;
                    h = Flac.ParseHeaders(_win.AsSpan(0, _winLen), _seek);
                }
                if (!h.Valid) return false;
                // N-3, V-PA18: a local file's ReplayGain tags, when the catalogue carried no figure (a Spotify FLAC always has one or none
                // and no tags). The ranges index THIS window, so they are read before anything refills or resizes it.
                if (_figures.GainDb == 0f && (!h.Tags.RgTrackGain.IsEmpty || !h.Tags.RgAlbumGain.IsEmpty))
                {
                    ReadOnlySpan<byte> head = _win.AsSpan(0, _winLen);
                    var tags = ReplayGainTags.Of(TagText(head, h.Tags.RgTrackGain), TagText(head, h.Tags.RgTrackPeak),
                        TagText(head, h.Tags.RgAlbumGain), TagText(head, h.Tags.RgAlbumPeak));
                    _figures = NormalizationFigures.WithReplayGain(in _figures, in tags);
                    _gainLinear = GainLinear(_figures.GainDb, _figures.Peak, _figures.AlbumGainDb, _figures.AlbumPeak);
                }
                _si = h.Info;
                _seekCount = h.SeekPointCount;
                _firstFrame = _winStart + h.FirstFrame;                // window[0] is the stream marker: its source offset is _winStart
                _cursor = h.FirstFrame;
                // S-9: the window holds TWO maximum-size frames, so a false sync candidate inside the first still leaves a whole
                // frame's worth of lookahead behind it (`Observe` skips an `Overrun` candidate while that much remains).
                if (2L * _si.MaxFrame > _win.Length)
                    Array.Resize(ref _win, (int)Math.Min(2L * _si.MaxFrame, 16L << 20));
                _dec.Open(_si);
                if (_conformed.Length < _si.MaxBlock * 2) _conformed = new float[_si.MaxBlock * 2];
                _resampler = _si.SampleRate != target.SampleRate
                    ? new PolyphaseResampler(_si.SampleRate, target.SampleRate, target.Channels) : null;

                // A FLAC has no encoder delay and no padding: 0/0 is the TRUTH here, not a guess, and STREAMINFO's
                // total-sample count pins the exact end so two tracks of one album join sample-exact.
                long mixTotal = _si.TotalSamples > 0 ? ToMix(_si.TotalSamples) : -1;
                Gapless = mixTotal >= 0 ? new GaplessInfo(0, 0, mixTotal, TailKnown: true) : GaplessInfo.None;
                Label = LabelFor(_si.Bps > 16 ? Spotify.Audio.Format.Flac24 : Spotify.Audio.Format.Flac,
                    _si.Bps, _si.SampleRate);
                var codec = new MediaContentType(Container.Flac, CodecId.None, CodecId.Flac);
                info = new DecodedInfo(codec, new MixFormat(_si.SampleRate, _si.Channels),
                    TimeSpan.FromMilliseconds(_si.DurationMs), default);
                return true;
            }

            /// <summary>A tag value out of the header window, empty when the tag is absent or its range does not lie inside it.</summary>
            static ReadOnlySpan<byte> TagText(ReadOnlySpan<byte> head, Flac.ByteRange range)
                => range.Length > 0 && range.Offset >= 0 && range.Offset <= head.Length && range.Length <= head.Length - range.Offset
                    ? head.Slice(range.Offset, range.Length) : default;

            long ToMix(long srcFrames) => (long)Math.Round((double)srcFrames * _target.SampleRate / _si.SampleRate);

            long ToSrc(long mixFrames) => (long)Math.Round((double)mixFrames * _si.SampleRate / _target.SampleRate);

            /// <summary>Top up the window from the source. Keeps the unread tail, reads until full or EOF/error, and
            /// answers whether any byte ARRIVED — not whether the window holds some: at the end of a file whose last
            /// frame is cut short, "the window is not empty" kept the frame loop asking forever.</summary>
            bool Fill()
            {
                if (_cursor > 0 && _cursor < _winLen)
                {
                    _win.AsSpan(_cursor, _winLen - _cursor).CopyTo(_win);
                    _winStart += _cursor;
                    _winLen -= _cursor;
                    _cursor = 0;
                }
                else if (_cursor >= _winLen) { _winStart += _winLen; _winLen = 0; _cursor = 0; }
                bool arrived = false;
                while (_winLen < _win.Length)
                {
                    int n = ReadRaw(_winStart + _winLen, _win.AsSpan(_winLen));
                    if (n <= 0) break;
                    _winLen += n;
                    arrived = true;
                }
                return arrived;
            }

            /// <summary>One read at a source offset: the random-access face when the source has one, else the sequential
            /// <c>Read</c>, whose cursor this adapter keeps at <c>_winStart + _winLen</c> by construction. An interrupted
            /// read is "no bytes" here and latches <c>_interrupted</c> for <see cref="Read"/>.</summary>
            int ReadRaw(long offset, Span<byte> dst)
            {
                if (_ra is not { } ra) return _src!.Read(dst);
                int n = ReadAtEpoch(ra, offset, dst, ref _epoch);
                if (n != InterruptedRead) return n;
                _interrupted = true;
                return 0;
            }

            /// <summary>Decode the next frame into <c>_conformed</c>. False at EOF or on an unrecoverable error. A
            /// corrupt frame is skipped by ONE byte and the sync scan resumes — 0.2.9 latched the first zero read as
            /// EOF; here a bad frame costs one resync, never the track.</summary>
            bool NextBlock()
            {
                while (true)
                {
                    if (_cursor + 16 > _winLen && !Fill()) return false;
                    ReadOnlySpan<byte> win = _win.AsSpan(_cursor, _winLen - _cursor);
                    int at = Flac.FindHeader(win, 0, _si, out _);
                    if (at < 0) { if (_winLen == _win.Length) _cursor = _winLen; else return false; continue; }
                    _cursor += at;
                    win = _win.AsSpan(_cursor, _winLen - _cursor);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Flac.FrameResult fr = _dec.DecodeFrame(win, out int consumed, out Flac.Block block);
                    if (fr == Flac.FrameResult.Overrun)
                    {
                        // A "frame" longer than the whole window is a false sync, not a frame.
                        if (_winLen - _cursor >= _win.Length) { _cursor++; continue; }
                        if (!Fill()) return false;
                        continue;
                    }
                    // Bad CRC / reserved: resync one byte on. `Unsupported` lands here too, but only for the two cases left once the
                    // wide (33-bit side) path exists — an LPC shift below zero, which a conforming encoder never writes, and a
                    // decoder that was never opened. Both are a damaged or foreign stream, not audio worth a block of silence.
                    if (fr != Flac.FrameResult.Ok) { _cursor++; continue; }
                    _cursor += consumed;
                    _samplePos = block.SampleNumber;
                    if (block.Channels == 2)
                        Flac.ToFloat(block.Channel(0), block.Channel(1), block.Bps, _gainLinear, _conformed);
                    else
                        Flac.ToFloatMulti(block.Planar, block.Channels, block.BlockSize, block.Bps, _gainLinear, _conformed);
                    RecordDecode(DecodeFlac, block.BlockSize, _si.SampleRate, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                    _hold = block.BlockSize;
                    if (_skipSamples > 0)
                    {
                        int drop = Math.Min(_skipSamples, _hold);
                        _conformed.AsSpan(drop * 2, (_hold - drop) * 2).CopyTo(_conformed);
                        _hold -= drop;
                        _skipSamples -= drop;
                        _samplePos += drop;
                        if (_hold == 0) continue;
                    }
                    return true;
                }
            }

            public int Read(Span<float> dst)
            {
                if (_src is null || _eof) return 0;
                int ch = _target.Channels;                     // 2 for every session the app opens
                int want = dst.Length / ch;
                if (want <= 0) return 0;
                if (_ending) return DrainTail(dst);

                if (_hold == 0 && !NextBlock()) return EndOrSilence(dst, want, ch);

                while (_resampler is { IsActive: true } rs)
                {
                    ResampleResult rr = rs.Process(_conformed.AsSpan(0, _hold * ch), _hold, dst);
                    int unread = _hold - rr.Consumed;
                    if (unread > 0 && rr.Consumed > 0) _conformed.AsSpan(rr.Consumed * ch, unread * ch).CopyTo(_conformed);
                    _hold = unread;
                    _samplePos += rr.Consumed;
                    // A block that went wholly into the interpolation history produced nothing: that is not the end.
                    if (rr.Produced > 0 || rr.Consumed == 0) return rr.Produced;
                    if (_hold == 0 && !NextBlock()) return EndOrSilence(dst, want, ch);
                }

                int frames = Math.Min(want, _hold);
                _conformed.AsSpan(0, frames * ch).CopyTo(dst);
                int rest = _hold - frames;
                if (rest > 0) _conformed.AsSpan(frames * ch, rest * ch).CopyTo(_conformed);
                _hold = rest;
                _samplePos += frames;
                return frames;
            }

            /// <summary>No next frame: silence while a seek's interrupt is pending (never an EOF the engine would latch —
            /// the seek on its way flushes it), else the end — after the resampler's trailing half kernel (V-PE21).</summary>
            int EndOrSilence(Span<float> dst, int want, int ch)
            {
                if (TakeInterrupt(ref _interrupted)) return Silence(dst, want, ch);
                _ending = true;
                return DrainTail(dst);
            }

            /// <summary>The resampler's tail over as many reads as <paramref name="dst"/> needs, then 0 — and only then <c>_eof</c>.
            /// With no resampler there is no tail and this is the old end.</summary>
            int DrainTail(Span<float> dst)
            {
                int tail = _resampler is { IsActive: true } rs ? rs.Flush(dst) : 0;
                if (tail > 0) return tail;
                _eof = true;
                return 0;
            }

            /// <summary>Seek to a MIX-domain frame (the engine's contract). Returns the frame actually reached in the
            /// mix domain, or −1. Sample-exact: the remainder inside the found frame is dropped rather than played.
            /// <para>The three-tier PLAN — the file's own seek table, a bracketed interpolation search, and
            /// decode-forward — is the CORE's (`Flac.BeginSeek` / `TryNextProbe` / `Observe`), and this adapter is
            /// only the I/O around it: the arithmetic must not be restated here, because a second copy of it is a
            /// second thing to get wrong.</para></summary>
            public long Seek(long frame)
            {
                if (_src is null) return -1;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long target = Math.Clamp(ToSrc(frame), 0, _si.TotalSamples > 0 ? _si.TotalSamples : long.MaxValue);
                _hold = 0; _eof = false; _ending = false; _skipSamples = 0;
                _interrupted = false;                          // the seek the interrupt was waiting for is here
                _resampler?.Reset();
                if (_ra is { } ra) _epoch = Math.Max(_epoch, ra.Epoch) + 1;

                Flac.SeekPlan plan = Flac.BeginSeek(in _si, _seek.AsSpan(0, _seekCount), _firstFrame,
                    _src.Length ?? 0, target, MaxProbes);
                while (_src.Caps.Seekable && Flac.TryNextProbe(ref plan, out long probeAt))
                {
                    if (!ReadWindowAt(probeAt)) break;
                    if (Flac.Observe(ref plan, _dec, _win.AsSpan(0, _winLen), _winStart) == Flac.ProbeResult.NoFrame) break;
                }

                // Land on the plan's best frame, decode forward, drop the remainder inside the frame that holds the
                // target. `NextBlock` has already converted that frame, so the drop is a span shift. On the ring the
                // landing is re-targeted (free when resident) and the sequential fill resumes there.
                if (_ra is { } landing)
                {
                    landing.Retarget(plan.Offset, _win.Length, _epoch);
                    landing.ResumeFrom(plan.Offset);
                }
                else _src.Seek(plan.Offset);
                _winStart = plan.Offset; _winLen = 0; _cursor = 0;
                _samplePos = plan.Sample;
                Log.Info("audio", $"audio.seek codec=flac target={target} page={plan.Offset} probes={plan.Probes} "
                                  + $"tier={plan.Tier} ms={System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0}");
                while (true)
                {
                    if (!NextBlock())
                    {
                        // The bytes the decode-forward needed never arrived — a ring that starved for its whole bound,
                        // a body a superseding load closed, or an interrupt for the next seek of a scrub. NOT −1: the
                        // engine turns −1 into `InvalidOperationException("Decoder could not seek to the requested
                        // frame.")`, which is how a CDN that refused every range surfaced as a crash-shaped line after
                        // 56 s of waiting. The contract's own words are "the frame actually reached", and that is the
                        // plan's landing — within one FLAC block of the target. `_eof` only when the source really is
                        // at its end; otherwise the next `Read` waits on the ring like any other read (D5), and the
                        // pump's starve fold — never the seek — is what decides the track is over.
                        _eof = _src.Length is { } len && _winStart + _winLen >= len;
                        Log.Warn("audio", $"audio.seek.short codec=flac target={target} landed={_samplePos} "
                                          + $"eof={(_eof ? 1 : 0)} interrupted={(_interrupted ? 1 : 0)}");
                        return ToMix(_samplePos);
                    }
                    if (_samplePos + _hold > target)
                    {
                        int drop = (int)(target - _samplePos);
                        if (drop > 0)
                        {
                            _conformed.AsSpan(drop * 2, (_hold - drop) * 2).CopyTo(_conformed);
                            _hold -= drop;
                            _samplePos += drop;
                        }
                        break;
                    }
                    _hold = 0;                                 // a whole frame before the target: discard it
                }
                return ToMix(target);
            }

            /// <summary>Read one probe window at an absolute source offset — on the ring, re-targeted first so the probe is
            /// the one request in flight. The CORE's `Observe` does everything else — including rejecting a false sync
            /// by its frame CRC-16, which is the only thing that can tell audio data that happens to look like a sync
            /// word from a real frame.</summary>
            bool ReadWindowAt(long offset)
            {
                if (_ra is { } ra) ra.Retarget(offset, _win.Length, _epoch);
                else if (_src!.Seek(offset) < 0) return false;
                _winStart = offset;
                _winLen = 0;
                _cursor = 0;
                return Fill();
            }
        }

        // ── 21. metrics (headless plan §3.5) ─────────────────────────────────────────────────────────────────────────
        //
        // What the headless `stats` line, a smoke script's `xruns==0` / `gapless.exact>=1` / `prefetched` conditions and
        // the Vorbis/FLAC gates read. Every counter is written where the event happens — the pump chain, the 200 ms tick,
        // the decode-ahead thread — as an Interlocked/Volatile primitive, and read only through `Metrics.Read()` as ONE
        // value. Nothing here allocates or takes a lock on the decode path.

        /// <summary>How a seek was served, derived from the stream counters across it (<see cref="SeekKindOf"/>) — never
        /// guessed from the distance.</summary>
        public enum SeekKind : byte
        {
            /// <summary>No probe on the wire and no disk chunk: the ring (or nothing at all) answered.</summary>
            Ring = 0,
            /// <summary>At least one probe range went to the CDN.</summary>
            Far = 1,
            /// <summary>No probe on the wire, but the disk cache answered chunks.</summary>
            Disk = 2,
        }

        /// <summary>The pump's measurements as one value, readable from any thread.</summary>
        /// <param name="FirstAudioMs">Load → the first <c>Started</c> (PCM through the graph), the last load's; −1 before any.</param>
        /// <param name="FirstAudioFromHead">That load's first byte came off the clear head (a head-served start), not the body.</param>
        /// <param name="LastSeekMs">The last timed seek's target.</param>
        /// <param name="LastSeekLatencyMs">The <see cref="Seek"/> call → the engine's PCM at the target; −1 before any.</param>
        /// <param name="LastSeekKind">How the last seek was served.</param>
        /// <param name="Seeks">Timed seeks completed.</param>
        /// <param name="RingSeekLatencyMs">The last <see cref="SeekKind.Ring"/> seek's latency, −1 before any.</param>
        /// <param name="FarSeekLatencyMs">The last <see cref="SeekKind.Far"/> seek's latency, −1 before any.</param>
        /// <param name="DiskSeekLatencyMs">The last <see cref="SeekKind.Disk"/> seek's latency, −1 before any.</param>
        /// <param name="Xruns">RT-feed underrun incidents, every session this process (the engine's <c>XrunCount</c>).</param>
        /// <param name="XrunFramesLost">Frames of silence those underruns wrote.</param>
        /// <param name="GaplessExact">Gapless joins that went live with the outgoing length decoded exactly.</param>
        /// <param name="GaplessDegraded">Gapless joins that went live on the catalogue duration.</param>
        /// <param name="GaplessAbandoned">Joins or crossfades armed and then abandoned (a seek, a rate mismatch, the end first).</param>
        /// <param name="Crossfades">Crossfades committed.</param>
        /// <param name="DecodeXRealtime">Decode throughput of the PLAYING format, in multiples of real time.</param>
        /// <param name="VorbisXRealtime">Vorbis decode throughput (packet decode only), this process.</param>
        /// <param name="FlacXRealtime">FLAC decode throughput (frame decode + float conversion), this process.</param>
        /// <param name="Mp3XRealtime">MP3 decode throughput (NLayer's read, byte-seam waits included), this process.</param>
        /// <param name="PrepareArmed">A prepared next track is ready for the hand-off right now.</param>
        /// <param name="GlitchIncidents">RT underrun incidents on the CURRENT session (the <see cref="GlitchLedger"/> is per session —
        /// it restarts at every load; <see cref="Xruns"/> is the process total).</param>
        /// <param name="LongestStallMs">The longest single underrun gap on the current session, in ms.</param>
        /// <param name="GlitchVerdictKey">Why the session glitched: exactly one of <c>clean</c>, <c>gcPauses</c>, <c>byteStarved</c>,
        /// <c>producerStarved</c>, <c>deviceLate</c> (loc KEYS — the card translates them); null only in <c>default(Metrics)</c>.</param>
        /// <param name="MmcssRegistered">The RT thread is registered with MMCSS "Pro Audio" (<c>MmcssProAudio.ProAudioRegistered</c>).</param>
        /// <param name="RingTargetMs">The decode-ahead ring's target fill in ms; <b>0 when no session is open</b>.</param>
        /// <param name="RingFillMs">What the active voice's ring holds right now, in ms; 0 without a session.</param>
        /// <param name="DevicePaddingMs">Audio queued in the device buffer, in ms; 0 without a session.</param>
        public readonly record struct Metrics(
            int FirstAudioMs, bool FirstAudioFromHead,
            int LastSeekMs, int LastSeekLatencyMs, SeekKind LastSeekKind, int Seeks,
            int RingSeekLatencyMs, int FarSeekLatencyMs, int DiskSeekLatencyMs,
            long Xruns, long XrunFramesLost,
            int GaplessExact, int GaplessDegraded, int GaplessAbandoned, int Crossfades,
            float DecodeXRealtime, float VorbisXRealtime, float FlacXRealtime, float Mp3XRealtime,
            bool PrepareArmed,
            int GlitchIncidents, long LongestStallMs, string? GlitchVerdictKey,
            bool MmcssRegistered, int RingTargetMs, int RingFillMs, int DevicePaddingMs)
        {
            /// <summary>The live measurements.</summary>
            public static Metrics Read()
            {
                float vorbis = CodecXRealtime(DecodeVorbis), flac = CodecXRealtime(DecodeFlac), mp3 = CodecXRealtime(DecodeMp3);
                float playing = s_opened.Format switch
                {
                    Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24 => flac,
                    Spotify.Audio.Format.Mp3 => mp3,
                    Spotify.Audio.Format.Unknown => 0f,
                    _ => vorbis,
                };
                GlitchLedger.Snapshot glitch = s_ledger.Read();
                PcmAudioSession? sess = Volatile.Read(ref s_session);
                int rate = sess?.Format.SampleRate ?? 0;
                bool open = sess is not null && rate > 0;
                return new Metrics(
                    Volatile.Read(ref s_mFirstAudioMs), Volatile.Read(ref s_mFirstFromHead) != 0,
                    Volatile.Read(ref s_mLastSeekMs), Volatile.Read(ref s_mLastSeekLatencyMs),
                    (SeekKind)Volatile.Read(ref s_mLastSeekKind), Volatile.Read(ref s_mSeeks),
                    Volatile.Read(ref s_mSeekLatencyByKind[(int)SeekKind.Ring]),
                    Volatile.Read(ref s_mSeekLatencyByKind[(int)SeekKind.Far]),
                    Volatile.Read(ref s_mSeekLatencyByKind[(int)SeekKind.Disk]),
                    Interlocked.Read(ref s_retiredXruns) + Interlocked.Read(ref s_liveXruns),
                    Interlocked.Read(ref s_retiredXrunFrames) + Interlocked.Read(ref s_liveXrunFrames),
                    Volatile.Read(ref s_mGaplessExact), Volatile.Read(ref s_mGaplessDegraded),
                    Volatile.Read(ref s_mGaplessAbandoned), Volatile.Read(ref s_mCrossfades),
                    playing, vorbis, flac, mp3,
                    Volatile.Read(ref s_prepItem) is { IsReady: true },
                    glitch.Incidents, glitch.LongestStallMs, glitch.Verdict,
                    MmcssProAudio.ProAudioRegistered,
                    open ? FramesToMs(sess!.TargetAheadFrames, rate) : 0,
                    open ? FramesToMs(sess!.BufferedFrames, rate) : 0,
                    open ? FramesToMs(sess!.DevicePaddingFrames, rate) : 0);
            }
        }

        /// <summary>Frames → ms at <paramref name="rate"/>, rounded down; 0 for a nonsense input. PURE.</summary>
        static int FramesToMs(int frames, int rate)
            => rate <= 0 || frames <= 0 ? 0 : (int)Math.Min(int.MaxValue, frames * 1000L / rate);

        /// <summary>Zero every counter (the headless `stats reset`). The live session's xrun total is re-read at the next
        /// tick, so only what happens after the reset is counted.</summary>
        public static void ResetMetrics()
        {
            s_ledger.Reset();
            Volatile.Write(ref s_mFirstAudioMs, -1);
            Volatile.Write(ref s_mFirstFromHead, 0);
            Volatile.Write(ref s_mLastSeekMs, 0);
            Volatile.Write(ref s_mLastSeekLatencyMs, -1);
            Volatile.Write(ref s_mLastSeekKind, 0);
            Volatile.Write(ref s_mSeeks, 0);
            for (int i = 0; i < s_mSeekLatencyByKind.Length; i++) Volatile.Write(ref s_mSeekLatencyByKind[i], -1);
            lock (s_gate)
            {
                PcmAudioSession? live = s_session;
                Interlocked.Exchange(ref s_retiredXruns, live is null ? 0 : -live.XrunCount);
                Interlocked.Exchange(ref s_retiredXrunFrames, live is null ? 0 : -live.XrunFramesLost);
                Interlocked.Exchange(ref s_liveXruns, live?.XrunCount ?? 0);
                Interlocked.Exchange(ref s_liveXrunFrames, live?.XrunFramesLost ?? 0);
            }
            Volatile.Write(ref s_mGaplessExact, 0);
            Volatile.Write(ref s_mGaplessDegraded, 0);
            Volatile.Write(ref s_mGaplessAbandoned, 0);
            Volatile.Write(ref s_mCrossfades, 0);
            for (int i = 0; i < s_decodeMicros.Length; i++)
            {
                Interlocked.Exchange(ref s_decodeMicros[i], 0);
                Interlocked.Exchange(ref s_decodeTicks[i], 0);
            }
        }

        /// <summary>The seek's kind from the stream counters sampled at its start and its end: a probe on the wire is a
        /// FAR seek, a disk chunk with no probe is a DISK seek, neither is a RING seek. The fill that resumes after the
        /// landing does not count — it is a plain range, not a probe. PURE.</summary>
        public static SeekKind SeekKindOf(in Spotify.Audio.Stream.Stats before, in Spotify.Audio.Stream.Stats after)
            => after.Probes > before.Probes ? SeekKind.Far
             : after.CacheHits > before.CacheHits ? SeekKind.Disk
             : SeekKind.Ring;

        /// <summary>Decode throughput: audio seconds produced per wall second spent decoding. 0 before any decode. PURE.</summary>
        public static float XRealtime(long sourceMicros, long wallTicks, long ticksPerSecond)
            => sourceMicros <= 0 || wallTicks <= 0 || ticksPerSecond <= 0
                ? 0f
                : (float)(sourceMicros / 1e6 / ((double)wallTicks / ticksPerSecond));

        const int DecodeVorbis = 0, DecodeFlac = 1, DecodeMp3 = 2;

        static readonly long[] s_decodeMicros = new long[3], s_decodeTicks = new long[3];
        static readonly int[] s_mSeekLatencyByKind = [-1, -1, -1];
        static long s_loadStartedTicks, s_liveXruns, s_liveXrunFrames, s_retiredXruns, s_retiredXrunFrames;
        static int s_mFirstAudioMs = -1, s_mFirstFromHead, s_mLastSeekMs, s_mLastSeekLatencyMs = -1, s_mLastSeekKind, s_mSeeks;
        static int s_mGaplessExact, s_mGaplessDegraded, s_mGaplessAbandoned, s_mCrossfades;
        static bool s_joinExact;                   // the pending join's exactness, decided at commit (s_gate)

        /// <summary>Decode-thread: one decoded packet/frame of <paramref name="frames"/> source frames took
        /// <paramref name="ticks"/>. Two Interlocked adds, no allocation.</summary>
        static void RecordDecode(int codec, long frames, int sourceRate, long ticks)
        {
            if (ticks > 0) Interlocked.Add(ref s_decodeTicks[codec], ticks);
            if (frames > 0 && sourceRate > 0) Interlocked.Add(ref s_decodeMicros[codec], frames * 1_000_000L / sourceRate);
        }

        static float CodecXRealtime(int codec)
            => XRealtime(Interlocked.Read(ref s_decodeMicros[codec]), Interlocked.Read(ref s_decodeTicks[codec]),
                System.Diagnostics.Stopwatch.Frequency);

        /// <summary>Tick thread, at a load's first <c>Started</c>: the load's time to first audio, and whether the head
        /// served it.</summary>
        static void RecordFirstAudio()
        {
            long started = Interlocked.Exchange(ref s_loadStartedTicks, 0);
            if (started == 0) return;
            int ms = (int)Math.Min(int.MaxValue, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            IMediaByteSource? bytes;
            lock (s_gate) bytes = s_bytes;
            bool fromHead = bytes is RingSource ring && ring.Body.FirstFromHead;
            Volatile.Write(ref s_mFirstAudioMs, ms);
            Volatile.Write(ref s_mFirstFromHead, fromHead ? 1 : 0);
            Log.Info("audio", $"audio.first-audio ms={ms} from={(fromHead ? "head" : "body")}");
        }

        /// <summary>Pump chain, when a timed seek's PCM is at the target: its latency and its kind.</summary>
        static void RecordSeek(int targetMs, long t0, Spotify.Audio.Stream.Stats before)
        {
            Spotify.Audio.Stream.Stats after = Spotify.Audio.Stream.Stats.Read();
            SeekKind kind = SeekKindOf(in before, in after);
            int ms = (int)Math.Min(int.MaxValue, System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            Volatile.Write(ref s_mLastSeekMs, targetMs);
            Volatile.Write(ref s_mLastSeekLatencyMs, ms);
            Volatile.Write(ref s_mLastSeekKind, (int)kind);
            Volatile.Write(ref s_mSeekLatencyByKind[(int)kind], ms);
            Interlocked.Increment(ref s_mSeeks);
            Log.Info("audio", $"audio.seek.done to={targetMs} kind={kind} latencyMs={ms} probes={after.Probes - before.Probes} "
                              + $"requests={after.Requests - before.Requests} cacheHits={after.CacheHits - before.CacheHits}");
        }

        /// <summary>A session is going away (caller holds <see cref="s_gate"/>): fold its xrun totals into the retired ones, and
        /// take the session's glitch ledger (D1) — returned for <see cref="LogSessionSummary"/>, which the caller emits once it
        /// has released the gate — leaving a fresh one for the next session.</summary>
        static GlitchLedger.Snapshot RetireXruns(PcmAudioSession session)
        {
            Interlocked.Add(ref s_retiredXruns, session.XrunCount);
            Interlocked.Add(ref s_retiredXrunFrames, session.XrunFramesLost);
            Interlocked.Exchange(ref s_liveXruns, 0);
            Interlocked.Exchange(ref s_liveXrunFrames, 0);
            GlitchLedger.Snapshot summary = s_ledger.Read();
            s_ledger.Reset();
            return summary;
        }

        /// <summary>`audio.session.summary` (D1): one line per session — Info when it was clean, a Warning when the RT feed
        /// underran. <paramref name="track"/> is the track the session was playing when it was retired.</summary>
        static void LogSessionSummary(in GlitchLedger.Snapshot s, EntityId track)
        {
            bool clean = s.Incidents == 0;
            Log.Event(clean ? WaveeLogLevel.Info : WaveeLogLevel.Warning, "audio", "audio.session.summary", clean ? "clean" : "glitches",
                null, -1, null,
                WaveeLogField.Of("incidents", s.Incidents), WaveeLogField.Of("producerStarves", s.ProducerStarves),
                WaveeLogField.Of("deviceLate", s.DeviceLate), WaveeLogField.Of("gcImplicated", s.GcImplicated),
                WaveeLogField.Of("byteWaits", s.ByteWaits), WaveeLogField.Of("framesLost", s.FramesLost),
                WaveeLogField.Of("longestStallMs", s.LongestStallMs), WaveeLogField.Of("longestByteWaitMs", s.LongestByteWaitMs),
                WaveeLogField.Of("track", track.Text), WaveeLogField.Of("verdict", s.Verdict));
        }
    }

    // ── 22. the pump's answers to the reducer's effects (the partial methods `Playback.Host.cs` declares, B3) ───────────

    /// <summary><c>Effects.Adopt</c>: a hand-off advanced the deck without a Load (G-100, D4).</summary>
    static partial void PumpAdopt(uint fromEpoch, uint toEpoch) => Audio.AdoptHandOff(fromEpoch, toEpoch);

    /// <summary><c>Effects.Prefetch</c>: warm the next row only — ladder, head, mirrors, key; no ring, no decoder (G-112).</summary>
    static partial void PumpPrefetch(EntityRef row, EntityId id) => Audio.Prefetch(id);

    /// <summary><c>Effects.CancelPrepared</c>: the prepared row stopped being next (G-112).</summary>
    static partial void PumpCancelPrepared() => Audio.CancelPrepared();
}
