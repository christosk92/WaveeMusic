// ── Playback/Playback.Audio.cs ─────────────────────────────────────────────────────────────────────────────────────
// pump over FluentGpu.Media, AudioSource ×5, gapless/crossfade; SilentSink for --fake (ch 31, +50)
//
// Role: SHELL
// Owner: H
// Wave: 3
// Budget: 3010 lines (2250 + 220 for the FLAC adapter + 510 for the Vorbis adapter over the CORE decoder, its pure
//         granule clock, `IRandomAccessBytes` and `RingSource` — Vorbis plan §6.1 estimated +260; over it because the
//         clock and the landing peek are their own public type, so the frame accounting, the gapless trim and the seek
//         hand-off are unit facts rather than prose, and about a third of the rest is doc comments — + 30 for the
//         connected silent session, headless plan §3.5)
// Spec: plan §4.9 + ch 31 §9.4 + FLAC plan §4 + Vorbis plan §5.5, §6
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
//   2. A ZERO-BYTE READ IS PERMANENT EOF TO EVERY CODEC. That is why the byte seam
//      (`Playback.Audio.Sources.cs` §2) is a bounded WAIT and not a short read: a slow-but-alive CDN must never
//      silently truncate a track.
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

        /// <summary>One local render endpoint, as the picker renders it.</summary>
        /// <param name="Id">The endpoint id, or "" for the system default row.</param>
        /// <param name="Name">The short display name — `Playback.AudioDeviceNaming.Shorten`'s answer.</param>
        /// <param name="Kind">The form factor, folded to the picker's glyph vocabulary.</param>
        public readonly record struct LocalAudioDevice(string Id, string Name, byte Kind, bool IsDefault);

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
        static bool s_booted, s_disposed;

        // the epoch pair (C4)
        static uint s_loadEpoch;                   // the reducer's LoadEpoch the live session belongs to
        static EpochGate s_chain;                  // this file's own supersede counter
        static volatile bool s_clockStale = true;  // see rule 1

        // the live track
        static IMediaByteSource? s_bytes;
        static Opened s_opened;
        static EntityId s_id;
        static long s_activeDurMs, s_activeStartMs, s_activeJoinFrame;
        static long s_activePrimaryId;
        static bool s_playIntent, s_errorReported, s_startedAnnounced;
        // `--fake` drives its own session and the `MediaPlayer` facade never opened one, so the facade's Idle state
        // and zero position must NOT be read: everything comes off the session itself.
        static bool s_silent;
        static int s_pendingSeekMs = -1;
        static PlaybackState s_lastState = PlaybackState.Idle;
        static long s_lastPositionPostMs, s_lastWorkLogMs;
        static long s_nextVoiceId;
        static int s_gaplessArmed, s_prepRearmSent, s_endedHold;

        // the prepared slot (B)
        static IPreparedItem? s_prepItem;
        static IMediaByteSource? s_prepBytes;
        static EntityId s_prepId;
        static long s_prepDurMs;
        static bool s_prepOverlap;
        static int s_prepInFlight;

        // the pending hand-off (B armed into the live mixer, not yet audible)
        static bool s_joinPending, s_crossfadeInFlight;
        static long s_joinFrame, s_joinVoiceId, s_joinDurMs;
        static IAudioSource? s_joinVoice;
        static long s_joinTotalFrames;
        static IMediaByteSource? s_joinBytes, s_retiringBytes;
        static EntityId s_joinId;

        // the decoder the factory is about to hand the engine
        static IAudioDecoder? s_pendingDecoder;

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

        /// <summary>Build the backend and the player ONCE, on FIRST USE rather than at `Playback.Boot` — a `--fake`
        /// run, a unit test and a Connect-viewer session must all boot the reducer with no WASAPI client anywhere,
        /// which is exactly what `Playback.Host`'s boot contract promises.</summary>
        public static void Boot()
        {
            if (s_booted) return;
            s_booted = true;
            SeedFromSettings();
            try
            {
                // The factory's only argument is the mix format; it answers the decoder the pump already chose for the
                // track it is about to open (FLAC plan §4.2). A stale null would mean "no codec", so it is set before
                // every open and taken exactly once.
                s_backend = WasapiPcm.CreateBackend(s_effects, decoderFactory: TakePendingDecoder);
                s_player = MediaPlayer.Build().WithBackend(MediaKind.PcmAudio, s_backend).Build();
                ToUi(() => Supported.Value = true);
            }
            catch (Exception ex)
            {
                // No render endpoint, or a machine the WASAPI leaf refused. Local playback is off; Connect still works
                // and every surface renders the foreign device instead of an error.
                Log.Warn("audio", "audio backend unavailable — local playback is off this session", ex);
                ToUi(() => Supported.Value = false);
            }
            RefreshDevices();
            string persisted = Platform.Settings.Get(Platform.Keys.OutputDeviceId);
            if (persisted.Length > 0) ToUi(() => SelectedOutputId.Value = persisted);
        }

        /// <summary>Read the persisted DSP preferences. Seeded BEFORE the first open, so a session never renders one
        /// block with a flat EQ and then ramps.</summary>
        public static void SeedFromSettings()
        {
            SetCrossfade(Platform.Settings.Get(Platform.Keys.CrossfadeEnabled),
                Platform.Settings.Get(Platform.Keys.CrossfadeMs));
            SetEqualizer(Platform.Settings.Get(Platform.Keys.EqualizerEnabled),
                ParseEqGains(Platform.Settings.Get(Platform.Keys.EqualizerGains)));
            s_effects.Normalization.Value = Platform.Settings.Get(Platform.Keys.NormalizationEnabled)
                ? NormMode.Track : NormMode.Off;
            // The PERSISTED number is the slider's own position (that is what `Playback.Host` writes back), so the
            // taper is applied here exactly as it is on a live change — otherwise a restart would be audibly louder.
            float saved = Platform.Settings.Get(Platform.Keys.RememberVolume)
                ? Platform.Settings.Get(Platform.Keys.SavedVolume) : 1f;
            s_volume = VolumeTaper.Amplitude(saved);
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
        /// outgoing track's position for the track that is starting.</summary>
        public static void Load(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs = 0)
        {
            Boot();
            s_clockStale = true;
            long chain = BumpChain();
            Enqueue(() => LoadCoreAsync(row, id, epoch, fromMs, kind, chain));
        }

        /// <summary>Prepare the NEXT playable so the boundary can be a hand-off instead of a hard cut. Best effort by
        /// construction: no prepared slot simply means the next boundary reloads, which is what 0.2.9 did every time
        /// the reducer had not asked in time.</summary>
        public static void Prepare(EntityRef row, EntityId id)
        {
            Boot();
            Interlocked.Increment(ref s_prepInFlight);
            Enqueue(() => PrepareCoreAsync(row, id));
        }

        public static void Seek(int ms, uint epoch = 0)
        {
            Enqueue(() => SeekCoreAsync(ms, epoch));
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
                MediaPlayer? p = Volatile.Read(ref s_player);
                if (p is not null) { try { await p.PauseAsync().ConfigureAwait(false); } catch { } }
            });
        }

        public static void Resume()
        {
            lock (s_gate) s_playIntent = true;
            Enqueue(static async () =>
            {
                MediaPlayer? p = Volatile.Read(ref s_player);
                if (p is not null) { try { await p.PlayAsync().ConfigureAwait(false); } catch { } }
            });
            StartTicker();
        }

        public static void Stop()
        {
            s_clockStale = true;
            BumpChain();
            StopTicker();
            Enqueue(static () => DisposeSessionAsync());
        }

        /// <summary>Set the output amplitude. The taper is applied HERE (§3) so the slider stays linear.</summary>
        public static void SetVolume(float position)
        {
            Boot();
            float amplitude = VolumeTaper.Amplitude(position);
            lock (s_gate) s_volume = amplitude;
            MediaPlayer? p = Volatile.Read(ref s_player);
            try { p?.SetVolume(amplitude); } catch { }
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
                s_tail = s_tail.ContinueWith(async _ =>
                {
                    try { await op().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Log.Warn("audio", "pump op failed", ex);
                        PostFault(Fault.Unknown);
                    }
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

        static async Task LoadCoreAsync(EntityRef row, EntityId id, uint epoch, int fromMs, PlayableKind kind, long chain)
        {
            await DisposeSessionAsync().ConfigureAwait(false);
            if (IsStale(chain)) return;

            lock (s_gate)
            {
                s_pendingSeekMs = -1;          // a seek parked for the OUTGOING track must never land on this one
                s_errorReported = false;
                s_startedAnnounced = false;
                s_lastState = PlaybackState.Idle;
                s_loadEpoch = epoch;
                s_id = id;
                s_playIntent = true;
                s_gaplessArmed = 0;
                s_prepRearmSent = 0;
                s_endedHold = 0;
            }

            // A `--fake` load has no bytes at all: the silent voice runs the real graph for the declared duration.
            if (id.Provider == EntityProvider.Fake)
            {
                await OpenSilentAsync(row, epoch, fromMs, chain).ConfigureAwait(false);
                return;
            }

            using var cts = new CancellationTokenSource();
            IMediaByteSource? bytes = Open(id, cts.Token, out Opened opened, out Fault fault);
            if (IsStale(chain)) { bytes?.Close(); return; }
            if (bytes is null)
            {
                if (fault != Fault.None) PostFault(fault, epoch);
                return;
            }

            // The container skip is a property of the FORMAT and of the first bytes, and it is applied to the byte
            // seam rather than to the decoder, so no codec ever learns that Spotify prefixes its Ogg bodies. A Spotify
            // body is already container-relative (the skip lives in `Spotify.Audio.Body`); a module stream is sniffed.
            ApplySkip(bytes, in opened);

            lock (s_gate) { s_opened = opened; s_activeDurMs = opened.DurationMs; }
            await OpenSessionAsync(bytes, opened, row, epoch, fromMs, chain, autoResume: true).ConfigureAwait(false);
        }

        /// <summary>Re-base the byte seam's logical zero past the Spotify container header. Its own method because a
        /// `stackalloc` cannot live in an async one, and because the RULE — the skip is a property of the format and
        /// of the first bytes, never of the provider — is worth naming.</summary>
        static void ApplySkip(IMediaByteSource bytes, in Opened opened)
        {
            if (bytes is not Prefetching pf) return;
            Span<byte> head = stackalloc byte[Spotify.Audio.Ctr.HeaderBytes + 8];
            int n = 0;
            if (pf.TryOpen(new DataSpec { Position = 0, Length = -1 })) n = pf.Read(head);
            pf.Seek(0);
            pf.SetSkip(SkipFor(opened.Format, head[..Math.Max(0, n)]));
            pf.Seek(0);
        }

        static async Task OpenSessionAsync(IMediaByteSource bytes, Opened opened, EntityRef row, uint epoch,
            int fromMs, long chain, bool autoResume)
        {
            MediaPlayer? player = Volatile.Read(ref s_player);
            if (player is null) { PostFault(Fault.RuntimeMissing, epoch); return; }

            s_pendingDecoder = CreateDecoderFor(opened);
            MediaSource source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);

            if (PlayIntentGate.ShouldAnnounceBuffering(s_playIntent)) PostSignal(AudioSignal.Buffering, epoch);
            try { await player.OpenAsync(source).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("audio", "open failed", ex); PostFault(Fault.DecodeFailed, epoch); return; }

            if (IsStale(chain)) { bytes.Close(); return; }
            if (player.Error.Peek() is { } err) { PostFault(MapError(err), epoch); return; }

            var pcm = player.Session as PcmAudioSession;
            lock (s_gate)
            {
                s_bytes = bytes;
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
                    s_activeJoinFrame = GaplessJoinClock.JoinFrameFor(pcm.SampleClock, opened.DurationMs, 0, pcm.Format.SampleRate);
                    pcm.DeviceFormatChanged += OnDeviceFormatChanged;
                    pcm.DeviceRebuilt += OnDeviceRebuilt;
                }
            }

            try { player.SetVolume(s_volume); player.SetMuted(Muted.Peek()); } catch { }

            // The label and the real duration are facts about the OPENED file, so they are published from here rather
            // than guessed from the catalogue.
            if (opened.Label.Length > 0) Post(Input.Format(Entities.Strings.Intern(opened.Label), epoch));
            if (opened.DurationMs > 0) Post(Input.Duration((int)Math.Min(opened.DurationMs, int.MaxValue), epoch));

            // SeekGate: the engine's seek BLOCKS until the decoder has PCM at the target, and on one serialized chain
            // a seek past the clear head would wait for the body attach that is behind it in the same queue. A deferred
            // seek parks instead, and pauses if there was play intent so the user does not hear the intro of a track
            // they resumed at 3:45.
            if (fromMs > 0)
            {
                bool canServe = bytes.Caps.Seekable;
                if (SeekGate.Decide(hasSession: true, sourceCanServeBeyondHead: canServe) == SeekAdmission.ApplyNow)
                    await ApplySeekAsync(fromMs, epoch).ConfigureAwait(false);
                else
                {
                    lock (s_gate) s_pendingSeekMs = fromMs;
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

        /// <summary>`--fake`'s arm (ch 31 GAP 6). The silent VOICE runs the real graph — mixer, DSP chain, level tap,
        /// the engine's own clock — so what the demo exercises is the shipping path with the device leaf swapped for
        /// the engine's headless endpoint, and `Ended` arrives at exactly the declared duration.</summary>
        static async Task OpenSilentAsync(EntityRef row, uint epoch, int fromMs, long chain)
        {
            long durMs = row.Kind == EntityKind.Track && row.Slot > 0 ? new Track(row.Slot).DurationMs : 0;
            if (durMs <= 0) durMs = 180_000;
            SilentStart start = SilentStart.For(durMs, fromMs);
            PcmAudioSession session = OpenSilentSession(new MixFormat(48_000, 2), start.VoiceMs, s_effects, s_volume);
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
            await session.PlayAsync().ConfigureAwait(false);
            StartTicker();
        }

        /// <summary>The silent session, built OUTSIDE the `MediaPlayer` facade and therefore CONNECTED here. The engine
        /// starts a session's own feeder only inside `ConnectSignals` (`PcmAudioPlayer.cs:1079-1087`) and its `Advance`
        /// returns at once while no signal sink is connected, so an unconnected session never left `Idle`: `PlayAsync`
        /// only set a flag, the tick never posted `Started`, and `--fake` sat in `Loading` forever (headless plan §1.1).
        /// The core behind the sink is private and unobserved — the pump reads <c>CurrentState</c> and
        /// <c>PlayedFrames</c> off the session itself, so no signal write here reaches the UI thread's graph.
        /// <para>Not paced: the headless endpoint's null sink accepts every frame, so the clock runs as fast as the feeder
        /// spins rather than at wall-clock speed. Whether that is fixed here or in the engine is open (headless plan §8
        /// Q4).</para></summary>
        public static PcmAudioSession OpenSilentSession(MixFormat format, long voiceMs, IAudioEffects? effects, float volume)
        {
            IAudioEndpoint endpoint = SilentSink(format);
            var session = new PcmAudioSession(format, endpoint.Sink, endpoint.Clock, 1024, driveWithOwnThread: true, endpoint);
            session.Configure(PcmAudioPlayer.BuildGraphSpec(effects, format));
            if (effects is not null) session.BindEffects(effects);
            IAudioSource voice = SilentVoice(format, voiceMs);
            long frames = Math.Max(1, voiceMs * format.SampleRate / 1000);
            session.SetVoice(voice, TimeSpan.FromMilliseconds(voiceMs), frames, NormMode.Off, -18f, volume);
            session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
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
            PcmAudioSession? session;
            IMediaByteSource? bytes, retiring, join, prep;
            IPreparedItem? prepared;
            bool wasSilent;
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
                s_retiringBytes = null;
                s_joinBytes = null;
                s_prepBytes = null;
                s_prepItem = null;
                s_joinPending = false;
                s_crossfadeInFlight = false;
                s_prepOverlap = false;
                s_activePrimaryId = 0;
                s_activeJoinFrame = 0;
                s_clockStale = true;
                s_lastState = PlaybackState.Idle;
            }
            if (session is not null)
            {
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
            IAudioDecoder? chosen = Interlocked.Exchange(ref s_pendingDecoder, null);
            if (chosen is not null) return chosen;
            Log.Warn("audio", "decoder factory called with no pending decoder — falling back to PCM");
            return new WavAudioDecoder();
        }

        /// <summary>Format → decoder. The routing table is this switch, exactly like `Open`'s (plan §5.10), and it is
        /// public because "which codec does a Flac24 rung get" is a promise worth a test rather than a comment.</summary>
        public static IAudioDecoder CreateDecoderFor(in Opened opened) => opened.Format switch
        {
            Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24 => new FlacAudioDecoder(opened.GainDb),
            Spotify.Audio.Format.Mp3 => new Mp3AudioDecoder(opened.GainDb),
            _ => new VorbisAudioDecoder(opened.GainDb, opened.DurationMs),
        };

        /// <summary>The normalization gain as a linear multiplier, folded into the conversion that happens anyway —
        /// one multiply per sample and no extra pass. `Platform.Keys.NormalizationEnabled` (default true) gates it;
        /// off ⇒ 1. The engine's own `ReplayGainInfo` stays unity for the primary voice so the two do not compound.</summary>
        internal static float GainLinear(float gainDb)
            => NormalizationFactor(Platform.Settings.Get(Platform.Keys.NormalizationEnabled), gainDb);

        /// <summary>The largest boost or cut a gain figure may ask for. Spotify's track gains sit within ±15 dB; the Ogg
        /// figure is four bytes of a header (byte 144), and a garbage float there must never become a deafening
        /// multiply.</summary>
        public const float MaxGainDb = 30f;

        /// <summary>The normalization gain as ONE linear factor (Vorbis plan §6.3): <c>10^(dB/20)</c>, clamped to
        /// ±<see cref="MaxGainDb"/>, and — when the track peak is known — capped so <c>peak × factor ≤ 1</c> (librespot
        /// <c>get_factor</c>, player.rs:383-395). Off, or a non-finite figure, ⇒ exactly 1. PURE; the decoders fold the
        /// answer into the interleave multiply (Vorbis) or the int→float scale constant (FLAC).</summary>
        public static float NormalizationFactor(bool enabled, float gainDb, float peak = 0f)
        {
            if (!enabled || !float.IsFinite(gainDb) || gainDb == 0f) return 1f;
            float factor = MathF.Pow(10f, Math.Clamp(gainDb, -MaxGainDb, MaxGainDb) / 20f);
            if (peak > 0f && float.IsFinite(peak) && factor * peak > 1f) factor = 1f / peak;
            return factor;
        }

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

            public static VorbisClock At(long position) => new() { Position = position, Target = Unknown };

            /// <summary>What to hand out of one decoded packet: skip <see cref="Skip"/> frames of its output, then
            /// hand out <see cref="Count"/>. <see cref="Start"/> is the packet's first granule, <see cref="Unknown"/>
            /// when it could not be placed.</summary>
            public readonly record struct Run(int Skip, int Count, long Start);

            /// <summary>Place one decoded packet. <paramref name="granuleAtEnd"/> is the page granule when the packet
            /// is the last one completed on its page (else −1); <paramref name="eos"/> says that page is the stream's
            /// last, whose granule TRUNCATES rather than ends (libvorbis block.c:864-936).</summary>
            public Run Admit(int frames, long granuleAtEnd, bool eos)
            {
                if (frames < 0) frames = 0;
                long start;
                if (granuleAtEnd >= 0 && !eos) start = granuleAtEnd - frames;            // the page pins it
                else if (Position != Unknown) start = Position;                           // the running count
                else if (granuleAtEnd >= 0) start = granuleAtEnd - frames;               // the EOS page, with no clock
                else return new Run(frames, 0, Unknown);                                  // nothing placeable yet
                Position = start + frames;
                int keep = eos && granuleAtEnd >= 0 ? Ogg.TrimTail(start, frames, granuleAtEnd) : frames;
                int skip = Target != Unknown && Target > start ? (int)Math.Min(keep, Target - start) : 0;
                if (keep - skip <= 0) return new Run(keep, 0, start);
                Target = Unknown;                                                         // reached: frames flow from here
                return new Run(skip, keep - skip, start);
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
            /// <paramref name="epoch"/> is no longer the source's or the bounded wait ran out. Blocks, bounded.</summary>
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

        /// <summary>One read at a container offset through the random-access face. A −1 from an epoch that moved
        /// underneath the caller (a refused head splice supersedes the ring — the only epoch change a decoder does not
        /// make itself) is retried ONCE at the new epoch, where the ring serves the true bytes.</summary>
        static int ReadAtEpoch(IRandomAccessBytes source, long offset, Span<byte> dst, ref uint epoch)
        {
            int n = source.ReadAt(offset, dst, epoch);
            if (n >= 0) return n;
            uint now = source.Epoch;
            if (now == epoch) return n;
            epoch = now;
            return source.ReadAt(offset, dst, epoch);
        }

        /// <summary>Ogg Vorbis over the CORE reader and decoder (Vorbis plan §6.2). Blocks in the byte seam and nowhere
        /// else; allocates in <see cref="TryOpen"/> and nowhere after (P8) — the window, the reader's spanning buffer and
        /// the decoder's tables are sized once there.</summary>
        public sealed class VorbisAudioDecoder : IAudioDecoder
        {
            /// <summary>The probe window's 192 KiB ceiling, and more than two maximal pages, so a landing's peek always
            /// sees the page that pins it.</summary>
            const int WindowBytes = 192 * 1024;

            /// <summary>How much of a local file's end is read at open for its last granule.</summary>
            const int TailScanBytes = 64 * 1024;

            /// <summary>Header packets are a few KB; a stream whose three headers are not parsed inside this many bytes
            /// is not one this decoder plays.</summary>
            const int HeaderScanLimit = 1 << 20;

            readonly float _gainLinear;
            readonly long _durationMs;
            Vorbis.Decoder? _dec;
            Ogg.Reader? _ogg;
            byte[] _win = [];
            IMediaByteSource? _src;
            IRandomAccessBytes? _ra;
            MixFormat _target;
            LinearResampler? _resampler;
            float[] _conform = [];            // the held packet re-laid for a mix that is not stereo (never, today)
            VorbisClock _clock;
            long _winStart;                   // container offset of _win[0]
            int _winLen;
            int _hold, _holdOffset;           // frames of the current packet not yet handed out, and where they start
            long _heldStart;                  // the granule of the first held frame
            long _firstAudioPage, _origin, _tail = -1;
            int _rate;
            uint _epoch;
            bool _eof;

            /// <param name="gainDb">The normalization figure (`Opened.GainDb`: the catalogue's, else the header's byte 144).</param>
            /// <param name="durationMs">The declared duration: the seek estimate's slope until the tail is known, and the
            /// yardstick a tail granule is checked against.</param>
            public VorbisAudioDecoder(float gainDb, long durationMs = 0)
            {
                _gainLinear = GainLinear(gainDb);
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
                _hold = _holdOffset = 0;
                if (!src.TryOpen(new DataSpec { Position = 0, Length = -1 })) return false;
                if (_win.Length == 0) _win = GC.AllocateUninitializedArray<byte>(WindowBytes, pinned: true);
                _dec ??= new Vorbis.Decoder();
                Ogg.Reader ogg = _ogg ??= new Ogg.Reader();
                ogg.Reset();
                ogg.Index.Clear();
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
                if (!NextHeader(out ReadOnlySpan<byte> setup) || !_dec.Open(ident, setup, _gainLinear)) return false;
                _rate = _dec.SampleRate;
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
                _resampler = _rate != target.SampleRate ? new LinearResampler(_rate, target.SampleRate, target.Channels) : null;
                int ch = Math.Max(1, target.Channels);
                if (ch != Vorbis.OutputChannels && _conform.Length < _dec.MaxFrames * ch) _conform = new float[_dec.MaxFrames * ch];

                double seconds = _tail >= 0 ? (double)Math.Max(0, _tail - Math.Max(0, _origin)) / _rate
                               : _durationMs / 1000.0;
                info = new DecodedInfo(new MediaContentType(Container.Ogg, CodecId.None, CodecId.Vorbis),
                    new MixFormat(_rate, Vorbis.OutputChannels), TimeSpan.FromSeconds(seconds), default);
                return true;
            }

            /// <summary>The next header packet, refilling as it goes. A header is never corrupt and never the end.</summary>
            bool NextHeader(out ReadOnlySpan<byte> packet)
            {
                Ogg.Reader ogg = _ogg!;
                while (_winStart + _winLen <= HeaderScanLimit)
                {
                    Ogg.Reader.Next next = ogg.NextPacket(_win.AsSpan(0, _winLen), out packet, out _);
                    if (next == Ogg.Reader.Next.Packet) return true;
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
            /// sequential <c>Read</c>, whose cursor this adapter keeps at <c>_winStart + _winLen</c> by construction.</summary>
            int ReadRaw(long offset, Span<byte> dst)
                => _ra is { } ra ? ReadAtEpoch(ra, offset, dst, ref _epoch) : _src!.Read(dst);

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
                    if (next == Ogg.Reader.Next.Corrupt) continue;
                    if (dec.DecodePacket(packet) != Vorbis.PacketResult.Ok) continue;
                    bool eos = granule >= 0 && ogg.SawEos;
                    VorbisClock.Run run = _clock.Admit(dec.Frames, granule, eos);
                    if (run.Count > 0)
                    {
                        _holdOffset = run.Skip;
                        _hold = run.Count;
                        _heldStart = run.Start + run.Skip;
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
                while (true)
                {
                    if (_hold == 0 && !NextFrames()) { _eof = true; return 0; }
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
                _eof = false;
                _resampler?.Reset();
                if (_ra is { } ra)
                {
                    long tail = ra.TailGranule;                          // the tail usually lands after the open
                    if (_tail < 0 && VorbisClock.PlausibleTail(tail, _durationMs, _rate)) _tail = tail;
                    _epoch = Math.Max(_epoch, ra.Epoch) + 1;
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
                    if (Ogg.Observe(ref plan, _ogg.Index, _win.AsSpan(0, _winLen), _winStart) == Ogg.ProbeResult.NoPage) break;
                }

                // Land: the page the plan resumes at primes the decoder; the peek fixes the first frame's granule from
                // the first page granule ahead (never from OffsetGranule, which ENDS that page); the clock drops what
                // precedes the target inside the packet that holds it.
                if (!ReadWindowAt(plan.Offset, plan.WindowBytes, landing: true)) { _eof = true; return -1; }
                long start = PeekLanding();
                _dec.Prime();
                _clock = VorbisClock.At(start);
                _clock.Target = target;
                bool landed = NextFrames();
                if (!landed) _eof = true;
                long reached = landed ? _heldStart : _clock.Position != VorbisClock.Unknown ? _clock.Position : target;
                Log.Info("audio", $"audio.seek codec=vorbis target={target} landed={reached} page={plan.Offset} "
                                  + $"probes={plan.Probes} tier={plan.Tier} resolved={(plan.Resolved ? 1 : 0)} "
                                  + $"ms={System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0}");
                return VorbisClock.MixFrameOf(reached, _rate, _target.SampleRate, _origin);
            }

            /// <summary>Read a window at an absolute container offset. On the random-access face the source is
            /// re-targeted first (a resident window costs nothing) and, for the landing, the sequential fill is resumed
            /// there before a byte is read.</summary>
            bool ReadWindowAt(long offset, int bytes, bool landing)
            {
                if (_ra is { } ra)
                {
                    ra.Retarget(offset, bytes, _epoch);
                    if (landing) ra.ResumeFrom(offset);
                }
                else if (_src!.Seek(offset) != offset) return false;
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
        /// coordinates throughout: the 0xa7 skip is inside <c>Body</c>.</summary>
        public sealed class RingSource : IMediaByteSource, IRandomAccessBytes
        {
            readonly Spotify.Audio.Body _body;
            long _cursor;

            public RingSource(Spotify.Audio.Body body) => _body = body;

            public Spotify.Audio.Body Body => _body;

            /// <summary>Container bytes — the catalogue estimate until the first range's <c>Content-Range</c> names it.</summary>
            public long? Length => _body.Length;

            /// <summary>A seek here is a ring lookup or one probe range, never a stream re-open.</summary>
            public SourceCaps Caps => new() { Seekable = true, KnownLength = _body.LengthKnown, ExpensiveSeek = false };

            public uint Epoch => _body.Epoch;

            public long TailGranule => _body.TailGranule;

            public bool TryOpen(in DataSpec spec)
            {
                _cursor = Math.Max(0, spec.Position);
                return !_body.Disposed;
            }

            /// <summary>The sequential read, over <see cref="ReadAt"/> at the cursor. 0 only at EOF (the ring's rule).</summary>
            public int Read(Span<byte> dst)
            {
                uint epoch = _body.Epoch;
                int n = ReadAtEpoch(this, _cursor, dst, ref epoch);
                if (n > 0) _cursor += n;
                return n;
            }

            /// <summary>A position write only: the next read that misses asks for its bytes. A cancelling seek is
            /// <see cref="Retarget"/>'s, for the decoders that plan their own probes.</summary>
            public long Seek(long offset)
            {
                _cursor = _body.LengthKnown ? Math.Clamp(offset, 0, _body.Length) : Math.Max(0, offset);
                return _cursor;
            }

            public int ReadAt(long offset, Span<byte> dst, uint epoch) => _body.ReadAt(offset, dst, epoch);

            /// <summary>The ring aligns a probe's START down to its 64 KiB slot and sizes the range from there, so the
            /// bytes between the slot start and <paramref name="probeOffset"/> are added back: the one range on the wire
            /// always covers the whole window the planner is about to read, instead of stopping short at a slot edge
            /// and costing a second range.</summary>
            public void Retarget(long probeOffset, int probeBytes, uint epoch)
            {
                long fileOffset = _body.Skip + Math.Max(0, probeOffset);
                int slack = (int)(fileOffset % Spotify.Audio.Ring.SlotBytes);
                _body.Retarget(probeOffset, probeBytes + slack, epoch);
            }

            public void ResumeFrom(long offset)
            {
                _cursor = Math.Max(0, offset);
                _body.ResumeFrom(offset);
            }

            /// <summary>The engine cancels only a source that is going away — an abandoned open or prepare, a retiring
            /// voice — so the body is released: a read blocked in the ring's bounded wait returns −1 at once.</summary>
            public void Cancel() => _body.Dispose();

            public void Close() => _body.Dispose();
        }

        /// <summary>MP3, over NLayer. The LAME/Xing gapless numbers are read from the tag before the codec takes the
        /// stream — never a hardcoded constant, because the delay and padding are the encoder's own facts.</summary>
        public sealed class Mp3AudioDecoder : IAudioDecoder
        {
            readonly float _gain;
            NLayer.MpegFile? _file;
            LinearResampler? _resampler;
            MixFormat _target;
            float[] _src = [];
            float[] _pull = [];        // NLayer speaks float[] only; ONE buffer, never one per block (P8)
            int _hold, _srcChannels;

            readonly PullSamples _pullFn;

            public Mp3AudioDecoder(float gainDb)
            {
                _gain = GainLinear(gainDb);
                _pullFn = ReadSource;
            }

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
                    _resampler = rate != target.SampleRate ? new LinearResampler(rate, target.SampleRate, target.Channels) : null;
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
                try { n = _file.ReadSamples(_pull, 0, into.Length); } catch { return 0; }
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
                    _file.Position = srcFrame * _srcChannels;
                    _hold = 0;
                    _resampler?.Reset();
                    return frame;
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
        /// have their own loops because their cores hand over whole decoded blocks rather than an interleaved pull.</summary>
        static int PullConform(Span<float> dst, MixFormat target, int srcChannels, float gain,
            ref int hold, float[] scratch, LinearResampler? resampler, PullSamples pull)
        {
            int ch = target.Channels;
            int want = dst.Length / ch;
            if (want <= 0 || scratch.Length == 0) return 0;

            if (hold == 0)
            {
                int maxFrames = Math.Min(scratch.Length / Math.Max(ch, srcChannels), 4096);
                int samples = pull(scratch.AsSpan(0, maxFrames * srcChannels));
                if (samples <= 0) return 0;
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
                return rr.Produced;
            }

            int take = Math.Min(want, hold);
            scratch.AsSpan(0, take * ch).CopyTo(dst);
            int rest = hold - take;
            if (rest > 0) scratch.AsSpan(take * ch, rest * ch).CopyTo(scratch);
            hold = rest;
            return take;
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

        static async Task PrepareCoreAsync(EntityRef row, EntityId id)
        {
            try
            {
                PcmAudioSession? session;
                lock (s_gate) session = s_session;
                if (session is null || s_backend is null) return;          // no live session to hand off from

                using var cts = new CancellationTokenSource();
                IMediaByteSource? bytes = Open(id, cts.Token, out Opened opened, out Fault fault);
                if (bytes is null) { if (fault != Fault.None) Log.Info("audio", $"prepare skipped: {fault}"); return; }
                ApplySkip(bytes, in opened);

                s_pendingDecoder = CreateDecoderFor(opened);
                MediaSource source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);
                // `PrepareContext.For` stamps the mix rate B is resampled FOR, which is what every splice site checks.
                PrepareContext ctx = PrepareContext.For(session.Format, session.NormalizationMode, session.ReferenceLufsValue);
                IPreparedItem item = await s_backend.PrepareAsync(source, ctx, cts.Token).ConfigureAwait(false);

                lock (s_gate)
                {
                    s_prepItem = item;
                    s_prepBytes = bytes;
                    s_prepId = id;
                    s_prepDurMs = opened.DurationMs;
                    // A cross-kind boundary (audio → video, or either → a local file) changes HOSTS, so there is
                    // nothing to splice into: `MediaSwitch` is the one authority on that.
                    s_prepOverlap = MediaSwitch.AllowCrossfade(PlayableKind.Audio, PlayableKind.Audio);
                }
                _ = row;
            }
            finally { Interlocked.Decrement(ref s_prepInFlight); }
        }

        /// <summary>Arm B into the LIVE mixer at the active track's natural-end frame. A is never faded and never
        /// truncated and the `IAudioClient` never stops — this is the butt-join, and it is why `GainEnvelope.Constant`
        /// rather than a zero-length fade (rule 3).</summary>
        static bool CommitGaplessJoin(PcmAudioSession sess, IPreparedItem item)
        {
            if (!GaplessJoinClock.PrimedSlotMatches(item.MixRate, sess.Format.SampleRate))
            {
                Log.Info("audio", "join abandoned reason=rate-mismatch");
                DisposePreparedSlot();
                return false;
            }
            long id = ++s_nextVoiceId;
            long clock = sess.SampleClock;
            long remaining = s_activeDurMs - ActivePositionMs();
            long join = GaplessJoinClock.ScheduleJoin(s_activeJoinFrame, clock, remaining, sess.Format.SampleRate);
            float rg = sess.ReplayGainScalarFor(item.Loudness);
            lock (s_gate)
            {
                if (!sess.TryAddCrossfadeVoice(item.AudioVoice!, GainEnvelope.Constant, join, rg, sess.BuildVoiceChain(), id))
                    return false;
                s_joinPending = true;
                s_joinFrame = join;
                s_joinVoiceId = id;
                s_joinVoice = item.AudioVoice;
                s_joinTotalFrames = item.TotalFrames;
                s_joinId = s_prepId;
                s_joinDurMs = s_prepDurMs;
                s_joinBytes = s_prepBytes;
                // The slot is cleared WITHOUT disposing the item: its voice is live in the mixer now.
                s_prepItem = null;
                s_prepBytes = null;
                s_prepDurMs = 0;
                s_prepOverlap = false;
            }
            Log.Info("audio", $"gapless armed join={join} clock={clock} remainMs={remaining}");
            return true;
        }

        /// <summary>The join went live: flip identity under the lock so `PositionMs` rebases to B and a later seek
        /// reaches B rather than the retired primary.</summary>
        static void AnnounceGaplessJoin(PcmAudioSession sess, long rawPos)
        {
            IAudioSource? voice;
            long totalFrames;
            lock (s_gate)
            {
                voice = s_joinVoice;
                totalFrames = s_joinTotalFrames;
                s_joinVoice = null;
                s_crossfadeInFlight = true;
                s_retiringBytes = s_bytes;
                s_bytes = s_joinBytes;
                s_joinBytes = null;
                s_activeStartMs = rawPos;
                s_activePrimaryId = s_joinVoiceId;
                s_activeDurMs = s_joinDurMs;
                s_id = s_joinId;
                s_activeJoinFrame = s_joinFrame + GaplessJoinClock.MsToFrames(s_joinDurMs, sess.Format.SampleRate);
                s_joinPending = false;
                s_prepRearmSent = 0;
                s_gaplessArmed = 0;
            }
            // Post-hand-off seeks must reach B, not the retired primary — that is the whole job of this call.
            if (voice is not null)
            {
                try { sess.SetActiveVoice(s_activePrimaryId, voice, TimeSpan.FromMilliseconds(s_activeDurMs), totalFrames); }
                catch (Exception ex) { Log.Warn("audio", "set active voice failed", ex); }
            }
            // ONE signal, and the fade is 0: that is what tells the reducer to advance WITHOUT reloading.
            Post(Input.Audio(AudioSignal.Started, s_loadEpoch, FrameNowMs(), 0));
        }

        /// <summary>A real crossfade: B fades IN and A's current primary fades OUT, same start frame, same length,
        /// one `EqualPower` curve. Both voices run the shared DSP chain and B carries its own ReplayGain scalar.</summary>
        static bool CommitCrossfade(PcmAudioSession sess, IPreparedItem item, long rawPos, int fadeMs)
        {
            if (!GaplessJoinClock.PrimedSlotMatches(item.MixRate, sess.Format.SampleRate))
            {
                Log.Info("audio", "crossfade abandoned reason=rate-mismatch");
                DisposePreparedSlot();
                return false;
            }
            long id = ++s_nextVoiceId;
            long start = sess.SampleClock;
            int fadeFrames = fadeMs * sess.Format.SampleRate / 1000;
            float rg = sess.ReplayGainScalarFor(item.Loudness);
            lock (s_gate)
            {
                if (!sess.TryAddCrossfadeVoice(item.AudioVoice!,
                        GainEnvelope.Fade(FadeKind.In, start, fadeFrames, CrossCurve.EqualPower),
                        start, rg, sess.BuildVoiceChain(), id))
                    return false;
                sess.SetVoiceEnvelope(s_activePrimaryId,
                    GainEnvelope.Fade(FadeKind.Out, start, fadeFrames, CrossCurve.EqualPower));

                s_crossfadeInFlight = true;
                s_retiringBytes = s_bytes;
                s_bytes = s_prepBytes;
                s_activeStartMs = rawPos;
                s_activePrimaryId = id;
                s_activeDurMs = s_prepDurMs;
                s_id = s_prepId;
                s_activeJoinFrame = start + GaplessJoinClock.MsToFrames(s_prepDurMs, sess.Format.SampleRate);
                s_prepItem = null;
                s_prepBytes = null;
                s_prepDurMs = 0;
                s_prepOverlap = false;
                s_prepRearmSent = 0;
                s_gaplessArmed = 0;
                if (item.AudioVoice is { } fadeVoice)
                {
                    try { sess.SetActiveVoice(id, fadeVoice, TimeSpan.FromMilliseconds(s_activeDurMs), item.TotalFrames); }
                    catch (Exception ex) { Log.Warn("audio", "set active voice failed", ex); }
                }
            }
            Log.Info("audio", $"crossfade committed start={start} frames={fadeFrames}");
            Post(Input.Audio(AudioSignal.Started, s_loadEpoch, FrameNowMs(), 0));
            return true;
        }

        /// <summary>Abandon a pending join. The mixer has no voice REMOVAL, so B's envelope is pinned at zero with a
        /// 1-frame fade-out in the past and its byte source is cut: the ring decode faults to EOF and the voice
        /// retires silently.</summary>
        static void AbandonPendingJoin(PcmAudioSession? sess, string reason)
        {
            long voiceId;
            IMediaByteSource? bytes;
            lock (s_gate)
            {
                if (!s_joinPending) return;
                s_joinPending = false;
                voiceId = s_joinVoiceId;
                bytes = s_joinBytes;
                s_joinBytes = null;
            }
            Log.Info("audio", "join abandoned reason=" + reason);
            try { sess?.SetVoiceEnvelope(voiceId, GainEnvelope.Fade(FadeKind.Out, 0, 1, CrossCurve.Linear)); } catch { }
            if (bytes is not null) { try { bytes.Close(); } catch { } }
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
                s_prepDurMs = 0;
                s_prepOverlap = false;
            }
            if (item is not null) _ = item.DisposeAsync();
            if (bytes is not null) { try { bytes.Close(); } catch { } }
        }

        // ── 11. seek ─────────────────────────────────────────────────────────────────────────────────────────────────

        static async Task SeekCoreAsync(int ms, uint epoch)
        {
            MediaPlayer? p = Volatile.Read(ref s_player);
            PcmAudioSession? sess;
            lock (s_gate) sess = s_session;
            if (p is null || sess is null) { lock (s_gate) s_pendingSeekMs = ms; return; }
            AbandonPendingJoin(sess, "seek");
            await ApplySeekAsync(ms, epoch).ConfigureAwait(false);
        }

        static async Task ApplySeekAsync(int ms, uint epoch)
        {
            MediaPlayer? p = Volatile.Read(ref s_player);
            if (p is null) return;
            try { await p.SeekAsync(TimeSpan.FromMilliseconds(Math.Max(0, ms)), SeekMode.Accurate).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("audio", "seek failed", ex); return; }

            lock (s_gate)
            {
                s_pendingSeekMs = -1;
                s_activeStartMs = 0;
                if (s_session is { } sess)
                {
                    s_activeJoinFrame = GaplessJoinClock.JoinFrameFor(
                        sess.SampleClock, s_activeDurMs, ms, sess.Format.SampleRate);
                }
            }
            PostSignal(AudioSignal.Seeked, epoch == 0 ? s_loadEpoch : epoch, ms);
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
        static long ActivePositionMs()
        {
            if (s_clockStale) return 0;
            MediaPlayer? p = Volatile.Read(ref s_player);
            PcmAudioSession? sess;
            bool silent;
            lock (s_gate) { sess = s_session; silent = s_silent; }
            long raw = !silent && p is not null ? (long)p.Position.Peek().TotalMilliseconds
                     : sess is not null ? sess.PlayedFrames * 1000L / Math.Max(1, sess.Format.SampleRate)
                     : 0;
            int pending = Volatile.Read(ref s_pendingSeekMs);
            return SeekGate.ReportedPositionMs(pending, Math.Max(0, raw - s_activeStartMs));
        }

        static void Tick()
        {
            if (s_disposed) { StopTicker(); return; }
            MediaPlayer? p = Volatile.Read(ref s_player);
            PcmAudioSession? sess;
            uint epoch;
            bool silent;
            lock (s_gate) { sess = s_session; epoch = s_loadEpoch; silent = s_silent; }
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

            // (a) the arm SNAPSHOT. Diagnostic, not the commit: `reason` is what tells a log reader why a boundary was
            // a hard cut, which is otherwise indistinguishable from a boundary that simply had no next track.
            long activePos = pos;
            if (state == PlaybackState.Playing && !s_crossfadeInFlight && !s_joinPending
                && s_activeDurMs > 0 && s_gaplessArmed == 0
                && activePos >= s_activeDurMs - ArmLeadMs(fadeMs))
            {
                s_gaplessArmed = 1;
                bool primed = s_prepItem is { IsReady: true };
                int reason = s_prepItem is null && Volatile.Read(ref s_prepInFlight) == 0 ? 4
                           : s_prepItem is null ? 2
                           : !s_prepOverlap ? 3 : 0;
                Log.Info("audio", $"[gapless] arm remainMs={s_activeDurMs - activePos} fadeMs={fadeMs} "
                    + $"primed={primed} overlap={s_prepOverlap} reason={reason} clock={sess.SampleClock}");
            }

            // (b) the re-arm nudge, once per track: the endgame opened with nothing prepared AND nothing in flight.
            if (state == PlaybackState.Playing && s_prepRearmSent == 0 && s_activeDurMs > 0
                && !s_crossfadeInFlight && !s_joinPending
                && s_prepItem is null && Volatile.Read(ref s_prepInFlight) == 0
                && activePos >= s_activeDurMs - EndingSoonMs(fadeMs, s_activeDurMs))
            {
                s_prepRearmSent = 1;
                PostSignal(AudioSignal.Position, epoch, pos);        // the reducer's PrepareNext arm reads the position
            }

            // (c) the commit. `EffectiveFadeMs` is the ONLY selector (rule 3).
            if (state == PlaybackState.Playing && s_prepItem is { IsReady: true } item)
            {
                switch (HandOffAt(activePos, s_activeDurMs, fadeMs, prepared: true, s_prepOverlap,
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
                    if (!s_startedAnnounced) { s_startedAnnounced = true; PostSignal(AudioSignal.Started, epoch, pos); }
                    else if (s_lastState != PlaybackState.Playing) PostSignal(AudioSignal.Started, epoch, pos);
                    else if (FrameNowMs() - s_lastPositionPostMs >= PositionSampleMs)
                    {
                        s_lastPositionPostMs = FrameNowMs();
                        PostSignal(AudioSignal.Position, epoch, pos);
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
                    if (s_pendingSeekMs >= 0) Enqueue(() => ApplySeekAsync(s_pendingSeekMs, epoch));
                    break;

                case PlaybackState.Ended:
                    bool endedEdge = s_lastState != PlaybackState.Ended;
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

        /// <summary>Drain the RT feed's xrun events on the TICK thread, one Warning per incident and never a
        /// cumulative count — a count says "something has been wrong for a while", an incident says when.</summary>
        static void DrainXruns(PcmAudioSession sess, long posMs, PlaybackState state)
        {
            int rate = sess.Format.SampleRate;
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
                    Log.Warn("audio", $"[audio] xrun voice={ev.VoiceId} gapFrames={ev.GapFrames} gapMs={gapMs:0.0} "
                        + $"totalFramesLost={lost} ringFramesAtMiss={ev.RingFrames} posMs={posMs} state={state} "
                        + $"gcPauseTicksDelta={ev.GcPauseTicksDelta} ageMs={ageMs}");
                }
                if (n < buf.Length) break;
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
            lock (s_gate) sess = s_session;
            bool rebuild = sess?.RequiresGraphRebuild ?? true;
            DeviceRecoveryAction action = DeviceRecoveryPlan.Decide(rebuild, canReopen: s_bytes?.Caps.Seekable ?? false);
            Log.Info("audio", $"device recovery action={action}");
            switch (action)
            {
                case DeviceRecoveryAction.KeepSession:
                    return;
                case DeviceRecoveryAction.AdoptIntoExistingGraph:
                    // A same-rate swap: a prepared voice primed for the old rate is still valid, the graph is not
                    // rebuilt, and nothing needs a reload.
                    return;
                default:
                    // The graph must be rebuilt: a prepared slot primed for the old rate would play at the wrong
                    // speed, so it goes first, and the reducer owns the reload.
                    AbandonPendingJoin(sess, "device-format");
                    DisposePreparedSlot();
                    Post(Input.DeviceLost());
                    return;
            }
        }

        static void OnDeviceRebuilt(MixFormat format, long frame)
        {
            Log.Info("audio", $"device rebuilt at {format.SampleRate} Hz, frame {frame}");
            RefreshDevices();
        }

        // ── 14. posting back ─────────────────────────────────────────────────────────────────────────────────────────

        static void PostSignal(AudioSignal signal, uint epoch, long arg = 0)
            => Post(Input.Audio(signal, epoch, FrameNowMs(), arg));

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
        /// (ch 21 §10 item 77, ch 23 W18/W19).</summary>
        public readonly record struct Opened(
            Spotify.Audio.Format Format, long DurationMs, float GainDb, string Label, int BitrateKbps, bool IsLive);

        /// <summary>Where a module playable's bytes come from (owner T's `Platform/Modules.Host.cs`, Wave 6). A SEAM
        /// rather than a call, so Wave 3 builds and runs with no module host at all: an unattached provider answers
        /// <c>Fault.Unavailable</c> instead of failing to compile.</summary>
        public static Func<EntityId, CancellationToken, (Stream? Stream, Opened Opened)>? ModuleOpen { get; set; }

        /// <summary>Where a local playable's file is (owner T's `Platform/Modules.cs` `PlayableLinks`, Wave 6). Same
        /// late bind; the FILE read and the format sniff are this file's.</summary>
        public static Func<EntityId, string?>? LocalPath { get; set; }

        /// <summary>Open the bytes for <paramref name="id"/>. Blocks: metadata, the ladder, the CDN, the key. Runs on
        /// the pump, never on the UI thread. A failure answers a typed <see cref="Playback.Fault"/> and a null
        /// source; it never throws into the reducer.</summary>
        public static IMediaByteSource? Open(EntityId id, CancellationToken ct, out Opened opened, out Fault fault)
        {
            opened = default;
            fault = Fault.None;
            switch (id.Provider)
            {
                case EntityProvider.Spotify:
                case EntityProvider.WaveePodcast:
                    return SpotifySource(id, ct, out opened, out fault);
                case EntityProvider.Local:
                    return LocalSource(id, out opened, out fault);
                case EntityProvider.Module:
                    return ModuleSource(id, ct, out opened, out fault);
                case EntityProvider.Fake:
                    // ch 31 GAP 6: the demo catalog has no bytes at all. The pump substitutes the silent sink and the
                    // ticker advances position off the frame clock — ten surfaces light up with no network.
                    opened = new Opened(Spotify.Audio.Format.Unknown, 0, 0f, "", 0, false);
                    return null;
                default:
                    fault = Fault.Unavailable;
                    return null;
            }
        }

        /// <summary>Spotify (and the synthetic podcast source, which takes the same `ExternalUrl` arm): the fileId
        /// ladder, the key, the CDN, and the byte stores — all of it owner F's `Spotify.Audio.Open`. This wraps the
        /// opened <see cref="Spotify.Audio.Body"/> in <see cref="RingSource"/> (Vorbis plan §5.5) and reads the format,
        /// duration and normalization gain off the answer. The body's own `ReadAt` already never answers 0 before the
        /// end, so no shim sits in between.</summary>
        static IMediaByteSource? SpotifySource(EntityId id, CancellationToken ct, out Opened opened, out Fault fault)
        {
            opened = default;
            Spotify.Audio.Opened o;
            try { o = Spotify.Audio.Open(id.Text, ct); }
            catch (OperationCanceledException) { fault = Fault.None; return null; }
            catch (Exception ex) { Log.Warn("audio", "spotify open failed", ex); fault = Fault.Network; return null; }

            if (!o.Ok || o.Body is not { } body)
            {
                try { o.Stream?.Dispose(); } catch { }
                fault = o.Fault == Spotify.Audio.Fault.None ? Fault.Unavailable : MapFault(o.Fault);
                return null;
            }

            fault = Fault.None;
            int kbps = o.DurationMs > 0 && o.Length > 0 ? (int)(o.Length * 8 / o.DurationMs) : BitrateHintKbps(o.Fmt);
            opened = new Opened(o.Fmt, o.DurationMs, o.GainDb, LabelFor(o.Fmt, 0, 0), kbps, IsLive: false);
            Log.Info("audio", $"audio.open fmt={o.Fmt} len={o.Length} durMs={o.DurationMs} gain={o.GainDb:0.0} dB kbps={kbps} "
                              + $"head={body.HeadBytes} ring={body.Ring.Seconds}s tail={body.TailGranule}");
            return new RingSource(body);
        }

        static Fault MapFault(Spotify.Audio.Fault f) => f switch
        {
            Spotify.Audio.Fault.None => Fault.None,
            Spotify.Audio.Fault.Offline or Spotify.Audio.Fault.Network => Fault.Network,
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
            int n = 0;
            try
            {
                using FileStream probe = new(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 32 * 1024);
                n = probe.Read(head);
            }
            catch (Exception ex) { Log.Warn("audio", "local probe failed", ex); fault = Fault.Unavailable; return null; }

            Spotify.Audio.Format format = SniffFormat(head[..n]) ?? Spotify.Audio.Format.Mp3;
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

        /// <summary>The byte seam a module's `Stream` is seen through (a Spotify body is a <see cref="RingSource"/>).
        /// Two jobs and no more:
        /// <list type="number">
        /// <item>A transient miss is a BOUNDED WAIT, never a short read. `Read` answers 0 only when the inner stream
        /// itself reports a genuine end of stream — because every codec above latches the first zero as permanent EOF
        /// and a slow-but-alive CDN would otherwise silently truncate the track mid-song.</item>
        /// <item>The container SKIP: a Spotify Ogg body starts 167 bytes in, and this presents that offset as logical
        /// zero so the decoder never learns about it.</item>
        /// </list></summary>
        public sealed class Prefetching : IMediaByteSource
        {
            /// <summary>Poll granularity while a range is filling.</summary>
            const int PollDelayMs = 4;

            /// <summary>The bound on the fast path. NOT a deadline after which the track ends: exhausting it degrades
            /// to the blocking read, which owns the real ~90 s retry budget and the mirror failover.</summary>
            const int MaxWaitMs = 8_000;

            readonly Stream _inner;
            readonly bool _seekable;
            long _skip;

            public Prefetching(Stream inner, bool seekable, long skip = 0)
            {
                _inner = inner;
                _seekable = seekable && inner.CanSeek;
                _skip = skip;
            }

            public long? Length => _seekable ? Math.Max(0, _inner.Length - _skip) : null;

            public SourceCaps Caps => new() { Seekable = _seekable, KnownLength = _seekable, ExpensiveSeek = true };

            public bool TryOpen(in DataSpec spec)
            {
                try
                {
                    if (!_seekable) return true;
                    _inner.Position = _skip + Math.Max(0, spec.Position);
                    return true;
                }
                catch { return false; }
            }

            public int Read(Span<byte> dst)
            {
                if (dst.Length == 0) return 0;
                long deadline = Environment.TickCount64 + MaxWaitMs;
                while (true)
                {
                    int n;
                    try { n = _inner.Read(dst); }
                    catch (ObjectDisposedException) { return 0; }
                    catch (Exception ex) { Log.Warn("audio", "byte source read failed", ex); return -1; }
                    if (n > 0) return n;
                    // A genuine zero from a SEEKABLE stream at its end is real EOF; from a growing one it is a miss.
                    if (_seekable && _inner.Position >= _inner.Length) return 0;
                    if (Environment.TickCount64 >= deadline) return 0;
                    Thread.Sleep(PollDelayMs);
                }
            }

            public long Seek(long offset)
            {
                if (!_seekable) return -1;
                try { return _inner.Seek(_skip + Math.Max(0, offset), SeekOrigin.Begin) - _skip; }
                catch { return -1; }
            }

            /// <summary>Re-base logical zero. The pump calls this once, after the format is known, because the Spotify
            /// header's presence is a property of the FORMAT (a FLAC body has none) and of the first bytes.</summary>
            public void SetSkip(long skip) => _skip = skip;

            public void Cancel() { }

            public void Close() { try { _inner.Dispose(); } catch { } }
        }

        // ── 17. the silent sink (ch 31 GAP 6) ────────────────────────────────────────────────────────────────────────

        /// <summary>What `--fake` plays. Not a byte source: an endpoint that consumes everything and produces a clock,
        /// so `Playback.Host`'s ticker advances position exactly as it would over a real device and the player bar,
        /// the rail, the NPV, the deck machines, the lyric wipe, the queue, the stage, SMTC, the taskbar and the jump
        /// list all light up with no network and no audio device (ch 31 §7.4's note — the highest-value single fixture
        /// in that chapter).
        /// <para>It is the engine's own headless endpoint rather than a Wavee type: the graph, the mixer, the DSP
        /// chain and the level tap all run exactly as they do on a real device, so what `--fake` exercises is the
        /// shipping path and not a second one.</para></summary>
        public static IAudioEndpoint SilentSink(MixFormat format) => new HeadlessAudioEndpoint(format);

        /// <summary>A silent VOICE for a fake track: the right number of frames of nothing, so the engine's own clock
        /// retires it at exactly the declared duration and `Ended` arrives on time.</summary>
        public static IAudioSource SilentVoice(MixFormat format, long durationMs)
        {
            long frames = Math.Max(1, durationMs * format.SampleRate / 1000);
            return new SignalGeneratorSource(format.Channels, format.SampleRate, 0d, 0f, frames);
        }

        // ── 18. sniffing ─────────────────────────────────────────────────────────────────────────────────────────────

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
                return layer == 0 ? Spotify.Audio.Format.Unknown : Spotify.Audio.Format.Mp3;   // layer 00 = ADTS/AAC
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
            if (ct.Contains("aac", StringComparison.Ordinal)) return Spotify.Audio.Format.Unknown;
            if (ct.Contains("mpeg", StringComparison.Ordinal) || ct.Contains("mp3", StringComparison.Ordinal))
                return Spotify.Audio.Format.Mp3;
            if (ct.Contains("ogg", StringComparison.Ordinal) || ct.Contains("vorbis", StringComparison.Ordinal))
                return Spotify.Audio.Format.OggVorbis320;
            if (ct.Contains("flac", StringComparison.Ordinal)) return Spotify.Audio.Format.Flac;
            return null;
        }

        /// <summary>How many bytes of Spotify container header sit in front of the real stream. A FLAC body has NONE
        /// (a Spotify FLAC is a plain `fLaC` stream — FLAC plan §1.1 item 1); everything else carries 167 unless the
        /// magic is already at offset 0.</summary>
        public static int SkipFor(Spotify.Audio.Format format, ReadOnlySpan<byte> head)
        {
            if (format is Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24) return 0;
            if (head.Length >= 4 && (head[..4].SequenceEqual("OggS"u8) || head[..4].SequenceEqual("fLaC"u8))) return 0;
            return Spotify.Audio.Ctr.HeaderBytes;
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

            public void Cancel() { }

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
        public sealed class FlacAudioDecoder : IAudioDecoder
        {
            const int WindowBytes = 64 * 1024;
            const int SeekPointCapacity = 1024;
            const int MaxProbes = 32;

            readonly Flac.Decoder _dec = new();
            readonly Flac.SeekPoint[] _seek = new Flac.SeekPoint[SeekPointCapacity];
            readonly float _gainLinear;
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
            bool _eof;
            LinearResampler? _resampler;

            public FlacAudioDecoder(float gainDb) => _gainLinear = GainLinear(gainDb);

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

                Flac.Headers h = Flac.ParseHeaders(_win.AsSpan(0, _winLen), _seek);
                while (!h.Valid && !h.Complete && _winLen == _win.Length)      // a huge PICTURE block: grow, read on
                {
                    Array.Resize(ref _win, _win.Length * 2);
                    if (!Fill()) return false;
                    h = Flac.ParseHeaders(_win.AsSpan(0, _winLen), _seek);
                }
                if (!h.Valid) return false;
                _si = h.Info;
                _seekCount = h.SeekPointCount;
                _firstFrame = h.FirstFrame;
                _cursor = h.FirstFrame;
                if (_si.MaxFrame > (uint)_win.Length)
                    Array.Resize(ref _win, (int)Math.Min((long)_si.MaxFrame * 2, 16L * 1024 * 1024));
                _dec.Open(_si);
                if (_conformed.Length < _si.MaxBlock * 2) _conformed = new float[_si.MaxBlock * 2];
                _resampler = _si.SampleRate != target.SampleRate
                    ? new LinearResampler(_si.SampleRate, target.SampleRate, target.Channels) : null;

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
            /// <c>Read</c>, whose cursor this adapter keeps at <c>_winStart + _winLen</c> by construction.</summary>
            int ReadRaw(long offset, Span<byte> dst)
                => _ra is { } ra ? ReadAtEpoch(ra, offset, dst, ref _epoch) : _src!.Read(dst);

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
                    Flac.FrameResult fr = _dec.DecodeFrame(win, out int consumed, out Flac.Block block);
                    if (fr == Flac.FrameResult.Overrun)
                    {
                        // A "frame" longer than the whole window is a false sync, not a frame.
                        if (_winLen - _cursor >= _win.Length) { _cursor++; continue; }
                        if (!Fill()) return false;
                        continue;
                    }
                    if (fr != Flac.FrameResult.Ok) { _cursor++; continue; }   // bad CRC / reserved: resync one byte on
                    _cursor += consumed;
                    _samplePos = block.SampleNumber;
                    if (block.Channels == 2)
                        Flac.ToFloat(block.Channel(0), block.Channel(1), block.Bps, _gainLinear, _conformed);
                    else
                        Flac.ToFloatMulti(block.Planar, block.Channels, block.BlockSize, block.Bps, _gainLinear, _conformed);
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

                if (_hold == 0 && !NextBlock()) { _eof = true; return 0; }

                while (_resampler is { IsActive: true } rs)
                {
                    ResampleResult rr = rs.Process(_conformed.AsSpan(0, _hold * ch), _hold, dst);
                    int unread = _hold - rr.Consumed;
                    if (unread > 0 && rr.Consumed > 0) _conformed.AsSpan(rr.Consumed * ch, unread * ch).CopyTo(_conformed);
                    _hold = unread;
                    _samplePos += rr.Consumed;
                    // A block that went wholly into the interpolation history produced nothing: that is not the end.
                    if (rr.Produced > 0 || rr.Consumed == 0) return rr.Produced;
                    if (_hold == 0 && !NextBlock()) { _eof = true; return 0; }
                }

                int frames = Math.Min(want, _hold);
                _conformed.AsSpan(0, frames * ch).CopyTo(dst);
                int rest = _hold - frames;
                if (rest > 0) _conformed.AsSpan(frames * ch, rest * ch).CopyTo(_conformed);
                _hold = rest;
                _samplePos += frames;
                return frames;
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
                _hold = 0; _eof = false; _skipSamples = 0;
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
                    if (!NextBlock()) { _eof = true; return -1; }
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
    }
}
