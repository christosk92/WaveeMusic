// ── Playback/Playback.Audio.Scrub.cs ─────────────────────────────────────────────────────────────────────────────────
// The scrub voice and the pump's four scrub arms: the tape source, its admission rule, and ScrubBegin / ScrubMove /
// ScrubEnd / ScrubCancel (a partial of `Playback.Audio`, so they share the chain, `s_gate` and the seek mailbox).
//
// Role: SHELL
// Wave: playback smoothness WP-3c (D4; V-PA3, V-PA5, V-PA9, V-PA20, V-PA23, V-PA24, V-PA25, V-PA29, V-PA30, V-PA41)
//       — docs/plans/wavee/playback-smoothness-implementation.md §4.12
//
// WHAT THIS FILE IS. Hold the main voice, play a 3 s PCM cache around the pointer like tape under a moving head (speed and
// direction follow the drag, pitch follows speed), and give the main voice back — by a seek — on release. The pieces:
//
//   • `ScrubTapeSource`  an `IAudioSource` over a SECOND decoder reading a NON-OWNING view of the live body. The engine's
//                         `RingAudioSource` wraps it, so every decode, recentre and window runs on the ring's own producer
//                         thread and the RT thread only copies (the ring's firewall). It reads RESIDENT bytes only — a
//                         target nobody has fetched parks the tape instead of waiting on the wire (V-PA20, V-PA30) — and
//                         it parks itself after 150 ms without a `Retarget` (V-PA23), so the reducer carries no park state.
//   • `ScrubAdmission`    the pure rule for "does this press get a scrub voice, or is it visual only" (paused, silent
//                         session, local file, a join in flight …) — the decision as a value, so it is a unit fact.
//   • the pump arms       `ScrubBegin` / `ScrubMove` / `ScrubEnd` / `ScrubCancel`, each ONE chain op except `ScrubMove`
//                         (a lock and two volatile stores). They are ordered with Load / Seek by the chain, so an `End`
//                         always runs after its `Begin`. `ScrubDrop` (internal) is `Load` / `Stop`'s hook: the reducer
//                         drops a gesture a load interrupted without sending a cancel, so the pump forgets it there.
//
// THE RULES THAT MAY NOT BE SIMPLIFIED.
//   1. A SCRUB END IS ALWAYS A SEEK. The reducer treats a scrub that began as audible and emits `fx.ScrubEnd` INSTEAD of
//      `fx.Seek`, but `ScrubBegin` can decline at any point (no lease, a pause race, a silent session, a local file, an
//      open that failed). Whatever happened to the tape, `ScrubEnd` ends in `Seek(ms, epoch, gen)` — otherwise the
//      reducer has moved `PosMs`/`SeekGen` for a seek nobody performs.
//   2. THE GRAIN SOURCE NEVER BLOCKS. Its byte view is resident-only for its whole life, so a decoder read answers 0 at once
//      on a miss; a miss parks (silence) and the next `Retarget` tries again. The producer keeps delivering blocks.
//   3. THE LEASE DIES WITH THE SOURCE. The ring disposes its inner source on the producer thread once the producer has
//      stopped (V-PA3); this file never disposes a source that a ring owns (V-PA25).
//   4. A VISUAL-ONLY SCRUB IS NOT AN ERROR. Every decline logs one `audio.scrub.visual-only` line and returns; a failure after
//      the scrub voice exists unwinds through `CancelScrubAsync` (idempotent) and the ring's own disposal.

using FluentGpu.Media;

namespace Wavee;

public static partial class Playback
{
    public static partial class Audio
    {
        // ── 1. the byte view, as the tape source sees it ────────────────────────────────────────────────────────────

        /// <summary>The two things the tape source asks of its byte view: stay resident-only, and stop answering. A
        /// <see cref="RingSource"/> view is adapted to it; the seam exists so the source's decisions are unit facts without a
        /// stream-layer <c>Body</c>.</summary>
        public interface IScrubBytes
        {
            /// <summary>Reads answer what is resident and never wait (<see cref="RingSource.ResidentOnly"/>).</summary>
            bool ResidentOnly { get; set; }

            /// <summary>Stop answering (<see cref="RingSource.Close"/> on a view: nothing is disposed, reads end).</summary>
            void Close();
        }

        sealed class RingSourceBytes(RingSource view) : IScrubBytes
        {
            public bool ResidentOnly { get => view.ResidentOnly; set => view.ResidentOnly = value; }

            public void Close() => view.Close();
        }

        // ── 2. the tape source ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The scrub voice, played like tape under a moving head: a second decoder over a NON-OWNING byte-source view fills a
        /// <see cref="CacheMs"/> PCM cache around the pointer from RESIDENT bytes only, and <see cref="Read"/> plays that cache at a varying
        /// speed — forward, backward, faster or slower — so a drag sounds like the music being wound by hand (pitch follows speed), not like
        /// repeated grains. The head chases the pointer's POSITION through a critically damped follower (<see cref="FollowSec"/> follow,
        /// <see cref="RateSmoothSec"/> slew = a quarter of it, so it never overshoots the pointer or rings when the finger stops), fades with
        /// speed toward a stop (no DC, no rumble), and a far jump fades out, relocates and fades back in instead of fast-forwarding through
        /// everything between. The pointer's velocity only says which way the tape is heading (where to put the head in a new cache): fed
        /// forward into the speed it would carry the tape past a pointer that just stopped.
        /// <para>Coordinates. The cache and every position are CONTENT frames (the mix-rate domain the engine's trim decorator reports); the
        /// decoder speaks decoder frames, which differ by <see cref="GaplessInfo.LeadInFrames"/>, so every decoder seek adds it.</para>
        /// <para>Threading. <see cref="Retarget"/> is called from the pump / host thread (≤ 20 Hz); everything else belongs to the ring's
        /// producer thread. No allocation after construction.</para></summary>
        public sealed class ScrubTapeSource : IAudioSource, ICancellableAudioSource, IDisposable
        {
            /// <summary>The PCM cache around the head, in milliseconds of mix-rate audio.</summary>
            public const int CacheMs = 3_000;

            /// <summary>The block the ring pumps, in milliseconds.</summary>
            public const int BlockMs = 10;

            /// <summary>The fastest the head winds, either way, as a multiple of normal speed.</summary>
            public const double MaxRate = 4.0;

            /// <summary>How quickly the head closes on the pointer: the position-follow time constant.</summary>
            public const double FollowSec = 0.06;

            /// <summary>How quickly the head's speed may change: the slew time constant (no zipper, no clicks on a jerky drag). A quarter of
            /// <see cref="FollowSec"/> makes the follower critically damped.</summary>
            public const double RateSmoothSec = FollowSec / 4;

            /// <summary>Farther than this from the head, the pointer is a jump: fade out, relocate, fade in — never a fast-forward through
            /// everything in between.</summary>
            public const double JumpSec = 0.5;

            /// <summary>The speed at which the head is fully audible; slower fades toward silence the way a stopping tape does.</summary>
            public const double AudibleRate = 0.12;

            /// <summary>Without a <see cref="Retarget"/> for this long and with the head at rest, the source parks (silence, no decode).</summary>
            public const int ParkAfterMs = 250;

            /// <summary>The pointer's velocity is trusted as the tape's heading for this long after a <see cref="Retarget"/> (a resting
            /// finger sends no more moves, so its last velocity must not keep saying "forward").</summary>
            public const int VelocityFreshMs = 100;

            /// <summary>A cache cut short (the track's end, or bytes not yet resident) is retried at most this often while the pointer
            /// wants what lies past it.</summary>
            public const int ShortCacheRetryMs = 100;

            readonly IAudioDecoder _dec;
            readonly IScrubBytes _bytes;
            readonly IDisposable _lease;
            readonly TimeProvider _time;
            readonly int _ch, _rate, _block, _leadIn, _cacheCap;
            readonly float[] _cache;
            readonly double _follow, _slew, _gainSlew, _jumpSlew, _jumpFrames, _margin;
            readonly double[] _dcX, _dcY;
            long _cacheStart, _target, _lastRetargetStamp, _framesRead, _lastRecentreStamp;
            int _cacheFrames;
            bool _cacheValid, _faultLogged, _jumping;
            double _velocity, _pos, _speed, _gain;
            volatile bool _parked;
            int _disposed;

            /// <summary>Frames per <see cref="Read"/>: the ring is built with this as its pump size.</summary>
            public int BlockFrames => _block;

            /// <summary>The source of the plan's shape: a decoder already OPEN on <paramref name="view"/>, the decoder lease that bounds how
            /// many decoders run, and the content frame the gesture started at.</summary>
            public ScrubTapeSource(IAudioDecoder dec, RingSource view, IDisposable lease, int rate, int channels, long startFrame)
                : this(dec, new RingSourceBytes(view), lease, rate, channels, startFrame)
            {
            }

            /// <summary>The testable core: the byte view is the <see cref="IScrubBytes"/> seam and the clock is injectable
            /// (<paramref name="time"/> null = <see cref="TimeProvider.System"/>). The view is switched to resident-only HERE and stays so.</summary>
            public ScrubTapeSource(IAudioDecoder dec, IScrubBytes bytes, IDisposable lease, int rate, int channels, long startFrame,
                TimeProvider? time = null)
            {
                _dec = dec;
                _bytes = bytes;
                _lease = lease;
                _time = time ?? TimeProvider.System;
                _ch = Math.Max(1, channels);
                _rate = Math.Max(1, rate);
                _block = Math.Max(1, _rate * BlockMs / 1000);
                _leadIn = Math.Max(0, dec.Gapless.LeadInFrames);
                _cacheCap = Math.Max(_block * 16, _rate * CacheMs / 1000);
                _cache = new float[_cacheCap * _ch];
                _follow = 1.0 / (FollowSec * _rate);                                   // speed toward the pointer, per frame of distance
                _slew = 1.0 - Math.Exp(-1.0 / (RateSmoothSec * _rate));
                _gainSlew = 1.0 - Math.Exp(-1.0 / (0.005 * _rate));                     // 5 ms: the speed fade
                _jumpSlew = 1.0 - Math.Exp(-1.0 / (0.001 * _rate));                     // 1 ms: a jump's fade-out lands inside one block
                _jumpFrames = JumpSec * _rate;
                _margin = MaxRate * _block + 4;                                         // a block of travel at full speed never leaves the cache
                _dcX = new double[_ch];
                _dcY = new double[_ch];
                _target = Math.Max(0, startFrame);
                _pos = _target;
                _lastRetargetStamp = _time.GetTimestamp();
                bytes.ResidentOnly = true;
            }

            /// <summary>The pointer moved (≤ 20 Hz): the content frame it is over and the drag velocity in audio-ms per wall-ms (a playback
            /// rate). Un-parks. Any thread; three volatile stores and a clock read, no allocation.</summary>
            public void Retarget(long frame, double velocity)
            {
                Volatile.Write(ref _target, Math.Max(0, frame));
                Volatile.Write(ref _velocity, double.IsFinite(velocity) ? Math.Clamp(velocity, -MaxRate, MaxRate) : 0.0);
                Volatile.Write(ref _lastRetargetStamp, _time.GetTimestamp());
                _parked = false;
            }

            /// <summary>True while the source is emitting silence: rested (the head stopped and no move for <see cref="ParkAfterMs"/>) or its
            /// target is not resident.</summary>
            public bool Parked => _parked;

            /// <summary>Frames this source has handed out, audio and silence alike.</summary>
            public long FramesRead => Volatile.Read(ref _framesRead);

            /// <summary>The head's content frame (the pointer's neighbourhood); the start frame before the first read.</summary>
            public long PositionFrames => (long)Volatile.Read(ref _pos);

            /// <summary>Tape (or silence) for every whole frame <paramref name="dst"/> holds. A destination shorter than one frame is refused
            /// with 0 (never EOF; <see cref="Exhausted"/> stays false). Producer thread only.</summary>
            public int Read(Span<float> dst, int channels)
            {
                int ch = _ch, frames = Math.Min(dst.Length / ch, _block);
                if (frames <= 0 || Volatile.Read(ref _disposed) != 0) return 0;
                double sinceMove = _time.GetElapsedTime(Volatile.Read(ref _lastRetargetStamp)).TotalMilliseconds;
                long target = Volatile.Read(ref _target);
                double heading = sinceMove <= VelocityFreshMs ? Volatile.Read(ref _velocity) : _speed;

                if (_parked) return Silence(dst, frames, target);
                if (sinceMove > ParkAfterMs && Math.Abs(_speed) < 0.01 && Math.Abs(target - _pos) < _rate * 0.01)
                {
                    Park(target);
                    return Silence(dst, frames, target);
                }

                // A far pointer is a jump: fade out where the head is, then relocate.
                if (!_jumping && Math.Abs(target - _pos) > _jumpFrames) _jumping = true;
                if (_jumping && _gain < 1e-3)
                {
                    Volatile.Write(ref _pos, (double)target);
                    _speed = 0;
                    _jumping = false;
                    Array.Clear(_dcX);
                    Array.Clear(_dcY);
                }

                // The cache must hold the head and a block of travel either way; a miss parks (silence) until the next move.
                if (!EnsureCached(_pos, heading, target))
                {
                    Park(target);
                    return Silence(dst, frames, target);
                }

                double pos = _pos, speed = _speed, gain = _gain;
                double lo = _cacheStart + 2, hi = _cacheStart + _cacheFrames - 3;
                bool jumping = _jumping;
                for (int f = 0; f < frames; f++)
                {
                    double desired = jumping ? speed : Math.Clamp((target - pos) * _follow, -MaxRate, MaxRate);
                    speed += (desired - speed) * _slew;
                    double next = Math.Clamp(pos + speed, lo, hi);
                    if (jumping) gain -= gain * _jumpSlew;
                    else gain += (Math.Min(1.0, Math.Abs(speed) / AudibleRate) - gain) * _gainSlew;
                    int o = f * ch;
                    for (int c = 0; c < ch; c++)
                    {
                        double x = Sample(pos, next, c) * gain;
                        double y = x - _dcX[c] + 0.995 * _dcY[c];                      // DC blocker (~38 Hz at 48 kHz): a head at rest is silence, not an offset
                        _dcX[c] = x;
                        _dcY[c] = y;
                        dst[o + c] = (float)y;
                    }
                    pos = next;
                }
                Volatile.Write(ref _pos, pos);
                _speed = speed;
                _gain = gain;
                _framesRead += frames;
                return frames;
            }

            /// <summary>The cache value as the head moves from <paramref name="from"/> to <paramref name="to"/>: a 4-point cubic (Catmull-Rom)
            /// at normal speed or slower, the average of the samples passed over when faster (the box filter that keeps a fast wind from
            /// aliasing into hiss).</summary>
            double Sample(double from, double to, int c)
            {
                int ch = _ch;
                if (Math.Abs(to - from) > 1.0)
                {
                    long a = (long)Math.Floor(Math.Min(from, to)) - _cacheStart, b = (long)Math.Floor(Math.Max(from, to)) - _cacheStart;
                    double sum = 0;
                    for (long i = a; i <= b; i++) sum += _cache[i * ch + c];
                    return sum / (b - a + 1);
                }
                double p = to - _cacheStart;
                long i1 = (long)Math.Floor(p);
                double t = p - i1;
                double y0 = _cache[(i1 - 1) * ch + c], y1 = _cache[i1 * ch + c], y2 = _cache[(i1 + 1) * ch + c], y3 = _cache[(i1 + 2) * ch + c];
                return y1 + 0.5 * t * (y2 - y0 + t * (2.0 * y0 - 5.0 * y1 + 4.0 * y2 - y3 + t * (3.0 * (y1 - y2) + y3 - y0)));
            }

            /// <summary>Is the head (and a block of travel at full speed either way) inside the cache? If not, recentre from resident bytes:
            /// ahead of the head when the tape runs forward, behind it when it runs backward, so the next recentre is as far away as it can be.</summary>
            bool EnsureCached(double pos, double heading, long target)
            {
                if (_cacheValid)
                {
                    long end = _cacheStart + _cacheFrames;
                    bool behind = pos - _margin >= _cacheStart + 2 || _cacheStart == 0;   // the track's start: there is nothing more behind
                    bool ahead = pos + _margin <= end - 3;
                    if (!ahead && _cacheFrames < _cacheCap)
                    {
                        // A short cache ended at the track's end or at the resident edge. The head plays up to it (Read clamps); a recentre
                        // is worth trying only while the pointer wants what lies past it, and not on every block.
                        ahead = target < end - 3 || _time.GetElapsedTime(_lastRecentreStamp).TotalMilliseconds < ShortCacheRetryMs;
                    }
                    if (behind && ahead) return true;
                }
                double lead = heading < -0.05 ? 0.75 : 0.25;                             // where the head sits in the new cache
                return RecentreResident((long)(pos - lead * _cacheCap), pos);
            }

            /// <summary>Recentre from RESIDENT bytes only: the decoder is sought (the view serves what the ring holds; a miss answers 0
            /// instead of waiting) and decodes up to <see cref="CacheMs"/>. False when the head is not covered — nothing there is resident,
            /// or the decoder faulted (a miss, never a track fault — V-PA25). The cache is invalid until this returns true.</summary>
            bool RecentreResident(long from, double pos)
            {
                _cacheValid = false;
                _lastRecentreStamp = _time.GetTimestamp();
                from = Math.Max(0, from);
                int got = 0;
                long reached;
                try
                {
                    reached = _dec.Seek(from + _leadIn);
                    if (reached < 0) return false;
                    while (got < _cacheCap)
                    {
                        int n = _dec.Read(_cache.AsSpan(got * _ch));
                        if (n <= 0) break;
                        got += n;
                    }
                }
                catch (Exception ex)
                {
                    if (!_faultLogged) { _faultLogged = true; Log.Warn("audio", "audio.scrub.recentre faulted — the tape parks", ex); }
                    return false;
                }
                _cacheStart = reached - _leadIn;                                       // where the decoder really landed, in content frames
                _cacheFrames = got;
                if (got < 8 || (pos < _cacheStart + 2 && _cacheStart > 0) || pos > _cacheStart + got - 3) return false;   // the head itself must be in it
                _cacheValid = true;
                return true;
            }

            void Park(long target)
            {
                _parked = true;
                _speed = 0;
                _gain = 0;
                _jumping = false;
                Volatile.Write(ref _pos, (double)target);                              // the next un-park starts at the pointer, from silence
                Array.Clear(_dcX);
                Array.Clear(_dcY);
            }

            int Silence(Span<float> dst, int frames, long target)
            {
                dst[..(frames * _ch)].Clear();
                if (_parked) Volatile.Write(ref _pos, (double)target);
                _framesRead += frames;
                return frames;
            }

            /// <summary>Retired by its envelope, never by EOF.</summary>
            public bool Exhausted => false;

            public GaplessInfo Gapless => GaplessInfo.None;

            public ReplayGainInfo Loudness => default;

            /// <summary>The ring is going away: end any read the view is in (resident-only reads never wait, so this is belt and braces).</summary>
            public void CancelPendingRead()
            {
                try { _bytes.Close(); }
                catch (ObjectDisposedException) { }
            }

            /// <summary>The ring's retire path calls this once the producer has stopped (V-PA3): the decoder, the view and the lease die with
            /// the source. Idempotent.</summary>
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { (_dec as IDisposable)?.Dispose(); }
                catch { }
                try { _bytes.Close(); }
                catch { }
                _lease.Dispose();
            }
        }

        // ── 3. admission ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Why a press gets no scrub voice. A refusal is "visual only": the thumb and the labels follow the pointer and the release
        /// is one ordinary seek.</summary>
        public enum ScrubRefusal : byte
        {
            None,
            /// <summary>No live session (nothing loaded, parked, between loads).</summary>
            NoSession,
            /// <summary>The <c>--fake</c> silent session: its voice is a generator the engine cannot seek or hold.</summary>
            Silent,
            /// <summary>The bytes are not a Spotify <see cref="RingSource"/> (a local file, a module source): no second reader exists.</summary>
            NoRingBytes,
            /// <summary>Paused, buffering, stalled — the device is not pulling, so tape would never be heard (V-PA41).</summary>
            NotPlaying,
            /// <summary>A gapless join or a crossfade is in flight: two voices are live and "the" main voice is not one voice.</summary>
            TransitionInFlight,
            /// <summary>All decoder leases are taken (the endgame's prepared voice holds one): no second decoder.</summary>
            NoLease,
            /// <summary>The scrub decoder could not open on the view's resident bytes.</summary>
            OpenFailed,
        }

        /// <summary>The first four gates of a scrub's admission, as a pure rule (the lease and the open are discovered later and named by
        /// <see cref="ScrubRefusal.NoLease"/> / <see cref="ScrubRefusal.OpenFailed"/>). Order matters: the first failing gate names the refusal.</summary>
        public static class ScrubAdmission
        {
            public static ScrubRefusal Check(bool hasSession, bool silent, bool ringBytes, bool playing, bool transitionInFlight)
                => !hasSession ? ScrubRefusal.NoSession
                 : silent ? ScrubRefusal.Silent
                 : !ringBytes ? ScrubRefusal.NoRingBytes
                 : !playing ? ScrubRefusal.NotPlaying
                 : transitionInFlight ? ScrubRefusal.TransitionInFlight
                 : ScrubRefusal.None;
        }

        // ── 4. the pump arms ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The live scrub: which session's voice was held, the tape source feeding the scrub voice, that voice's id, the load epoch
        /// the hold belongs to, and the mix rate the pointer's milliseconds convert at. Immutable; replaced as a whole.</summary>
        sealed class ScrubHold(PcmAudioSession session, ScrubTapeSource tape, long voiceId, uint epoch, int rate)
        {
            public readonly PcmAudioSession Session = session;
            public readonly ScrubTapeSource Tape = tape;
            public readonly long VoiceId = voiceId;
            public readonly uint Epoch = epoch;
            public readonly int Rate = rate;
        }

        /// <summary>The longest <c>ScrubBegin</c> waits for the scrub ring's first block before it gives the press up as visual only.</summary>
        const int ScrubReadyMs = 500;

        // s_gate: the live hold (null = no audible scrub), and the pointer's newest position — kept even before the hold exists, so a Move
        // that races the (asynchronous) open is applied the moment the tape are up instead of being lost.
        static ScrubHold? s_scrub;
        static int s_scrubWantMs = -1;
        static double s_scrubWantVelocity;
        // Bumped by `ScrubDrop` (a load or a stop orphaned the gesture). A `ScrubBegin` op captures it when the press is CALLED and abandons its work
        // — before the open, before the hold, and instead of publishing — once it has moved: the gesture it was for no longer exists.
        static int s_scrubDrops;

        /// <summary>The press: hold the main voice and start the scrub voice at <paramref name="ms"/> — or decline and stay visual only
        /// (<see cref="ScrubAdmission"/>, no lease, a failed open: never a fault, V-PA25). One chain op.</summary>
        public static void ScrubBegin(int ms)
        {
            int drops;
            lock (s_gate) { s_scrubWantMs = ms; s_scrubWantVelocity = 0; drops = s_scrubDrops; }
            Enqueue(() => ScrubBeginAsync(ms, drops));
        }

        static async Task ScrubBeginAsync(int ms, int drops)
        {
            PcmAudioSession? sess;
            PcmAudioPlayer? backend;
            Opened opened;
            IMediaByteSource? bytes;
            bool silent, transition;
            uint epoch;
            lock (s_gate)
            {
                sess = s_session;
                backend = s_backend;
                opened = s_opened;
                bytes = s_bytes;
                silent = s_silent;
                transition = s_joinPending || s_crossfadeInFlight;
                epoch = s_loadEpoch;
                if (s_scrubDrops != drops) return;                                                // orphaned before it ran (a load or a stop): nothing to begin
                if (s_scrub is { } stale)
                {
                    if (ReferenceEquals(stale.Session, sess) && stale.Epoch == epoch) return;     // a live scrub already: a second press is the host's mistake
                    s_scrub = null;                                                               // a dead session's hold: drop it
                }
            }
            ScrubRefusal refusal = ScrubAdmission.Check(sess is not null && backend is not null, silent, bytes is RingSource,
                sess?.CurrentState == PlaybackState.Playing, transition);                         // paused ⇒ visual-only (V-PA41)
            if (refusal != ScrubRefusal.None) { Log.Info("audio", $"audio.scrub.visual-only reason={refusal}"); return; }
            PcmAudioSession session = sess!;
            PcmAudioPlayer player = backend!;
            RingSource owner = (RingSource)bytes!;
            if (!player.TryAcquireDecoderLease(out IDisposable lease))
            {
                Log.Info("audio", $"audio.scrub.visual-only reason={ScrubRefusal.NoLease}");     // no lease: visual-only
                return;
            }

            MixFormat fmt = session.Format;
            IAudioDecoder? dec = null;
            RingSource? view = null;
            ScrubTapeSource? tape = null;
            RingAudioSource? ring = null;
            long id = 0;
            bool posted = false;                                                                   // the hold command reached the engine: a failure after it must release it
            try
            {
                dec = CreateDecoderFor(in opened);
                view = new RingSource(owner.Body, ownsBody: false) { ResidentOnly = true };        // the open too reads resident bytes only: the chain never waits on the wire
                if (!dec.TryOpen(view, fmt, out _))
                {
                    Log.Info("audio", $"audio.scrub.visual-only reason={ScrubRefusal.OpenFailed}");
                    DiscardOpen(dec, view, lease);
                    return;
                }
                long start = GaplessJoinClock.MsToFrames(ms, fmt.SampleRate);
                tape = new ScrubTapeSource(dec, view, lease, fmt.SampleRate, fmt.Channels, start);   // from here the source owns the decoder, the view and the lease
                id = session.NextEngineVoiceId();                                                  // ≥ 1_000_000 (V-PE26)
                // 60 ms ahead: a recentre decodes 3 s of audio on this producer (tens of ms on a slow core) and the head must not starve meanwhile.
                ring = new RingAudioSource(tape, fmt.Channels, ringFrames: fmt.SampleRate / 4, targetAheadFrames: 6 * tape.BlockFrames,
                    pumpFrames: tape.BlockFrames, startFrames: start, rt: player.ThreadCharacteristics);
                ring.StartProducer();
                // The producer decodes the first cache (one recentre, tens of ms) and cuts the first block BEFORE the main voice is held: the main voice
                // keeps playing meanwhile, and the scrub voice is added with audio already in its ring — never an empty voice the mixer's readiness
                // checks would see as starvation once the main voice is out of them. A target with nothing resident parks and fills with silence, so
                // this is never a long wait; the bound is for a producer that never gets scheduled.
                using (var ready = new CancellationTokenSource(ScrubReadyMs))
                    await ring.WaitUntilReadyAsync(tape.BlockFrames, ready.Token).ConfigureAwait(false);
                lock (s_gate) { if (s_scrubDrops != drops) throw new OperationCanceledException(); }   // orphaned while the cache decoded: hold nothing
                RetargetToLatest(tape, ms, fmt.SampleRate);                                      // the pointer's newest position, and a fresh park stamp
                posted = true;
                await session.BeginScrubAsync(ring, id, CancellationToken.None).ConfigureAwait(false);
                bool orphaned;
                lock (s_gate)
                {
                    orphaned = s_scrubDrops != drops;
                    if (!orphaned) s_scrub = new ScrubHold(session, tape, id, epoch, fmt.SampleRate);
                }
                if (orphaned)
                {
                    // Dropped during the hold's few blocks: nobody will end this gesture, so give the voice back — silently, like `ScrubDrop` (the
                    // session is on its way out; the scrub voice fades and retires, the main voice stays at gain 0 until the disposal).
                    try { await session.ReleaseHeldSilentAsync(id, CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                    Log.Info("audio", $"audio.scrub.begin orphaned ms={ms} voice={id}");
                    return;
                }
                RetargetToLatest(tape, ms, fmt.SampleRate);                                      // a Move that arrived during the hold's few blocks
                Log.Info("audio", $"audio.scrub.begin ms={ms} voice={id}");
            }
            catch (Exception ex)
            {
                bool cancelled = ex is OperationCanceledException || !IsLiveSession(session);
                if (!cancelled) Log.Warn("audio", "audio.scrub.begin failed — visual-only", ex);   // V-PA25: never a track fault
                if (ring is not null)
                {
                    // The scrub voice may or may not have reached the mixer: release whatever hold exists (idempotent), then dispose the ring —
                    // its producer disposes the source (decoder, view, lease). A ring the mixer also holds is disposed twice harmlessly.
                    if (posted)
                    {
                        try { await session.CancelScrubAsync(id, CancellationToken.None).ConfigureAwait(false); }
                        catch { }
                    }
                    ring.Dispose();
                }
                else if (tape is not null) tape.Dispose();
                else DiscardOpen(dec, view, lease);
            }
        }

        /// <summary>The newest pointer position (a Move may have arrived while the op ran) onto the tape — which also restarts their park
        /// timer, begun at construction, before the open and the first decode took their tens of milliseconds.</summary>
        static void RetargetToLatest(ScrubTapeSource tape, int fallbackMs, int rate)
        {
            int wantMs;
            double wantVelocity;
            lock (s_gate) { wantMs = s_scrubWantMs; wantVelocity = s_scrubWantVelocity; }
            tape.Retarget(GaplessJoinClock.MsToFrames(wantMs >= 0 ? wantMs : fallbackMs, rate), wantVelocity);
        }

        /// <summary>The open never produced a source that owns its pieces: release them by hand, each guarded (a failed open is no reason to leak a lease).</summary>
        static void DiscardOpen(IAudioDecoder? dec, RingSource? view, IDisposable lease)
        {
            try { (dec as IDisposable)?.Dispose(); }
            catch { }
            try { view?.Close(); }
            catch { }
            lease.Dispose();
        }

        /// <summary>The pointer moved (≤ 20 Hz, already coalesced by the model): retarget the tape. A Move before the hold exists is
        /// remembered and applied when it does; one after the hold ended, or for another session's hold, is dropped. A lock and two
        /// volatile stores — no allocation. Velocity passes through unchanged (V-PA9).</summary>
        public static void ScrubMove(int ms, double velocity)
        {
            ScrubHold? hold;
            lock (s_gate)
            {
                s_scrubWantMs = ms;
                s_scrubWantVelocity = velocity;
                hold = s_scrub;
                if (hold is not null && !ReferenceEquals(hold.Session, s_session)) hold = null;
            }
            if (hold is not null) hold.Tape.Retarget(GaplessJoinClock.MsToFrames(ms, hold.Rate), velocity);
        }

        /// <summary>The release IS the seek (V-PA5): the scrub voice fades out and self-retires (the ring disposes it); the HELD main voice is
        /// released silent (no fade-in) and replaced by an ordinary <see cref="Seek"/> at the release point — a ring jump, a design-A swap
        /// (the bytes are resident, so the prepare lands in ≈ 20 ms and keeps the D8 ring, the trim and gapless) or the in-place fallback.
        /// The released voice is marked silenced (<c>s_activeSilenced</c>, the stale timer's own mechanism), so whichever of those lands
        /// ON that voice gives it its level back with a 5 ms fade-in, and a swap replaces it.
        /// <para>With no hold — the press was visual only, or the tape never came up — this is just the seek: the reducer emitted it
        /// INSTEAD of <c>fx.Seek</c>, so it cannot be dropped. One chain op, behind the <c>Begin</c> it ends.</para></summary>
        public static void ScrubEnd(int ms, uint epoch, uint gen) => Enqueue(() => ScrubEndAsync(ms, epoch, gen));

        static async Task ScrubEndAsync(int ms, uint epoch, uint gen)
        {
            ScrubHold? hold = TakeHold(out PcmAudioSession? live, out uint loaded);
            if (hold is null || !HoldIsLive(hold, live, loaded))
            {
                // No scrub voice (the press was visual only, declined, failed to open, or its session is gone): the reducer already moved PosMs / SeekGen
                // for this seek, so it is the ordinary seek — never dropped.
                SeekUnlessSuperseded(ms, epoch, gen);
                Log.Info("audio", $"audio.scrub.end ms={ms} tape=0");
                return;
            }
            lock (s_gate) s_activeSilenced = true;                                       // BEFORE the release: a release that lands with the flag unset leaves a silent voice nobody restores
            try { await hold.Session.ReleaseHeldSilentAsync(hold.VoiceId, CancellationToken.None).ConfigureAwait(false); }   // scrub voice Out (self-retires); main stays silent + un-held
            catch (Exception ex)
            {
                lock (s_gate) s_activeSilenced = false;
                Log.Warn("audio", "audio.scrub.end release failed — seeking anyway", ex);
            }
            SeekUnlessSuperseded(ms, epoch, gen);
            Log.Info("audio", $"audio.scrub.end ms={ms} tape=1");
        }

        /// <summary>The release's seek: <see cref="Seek"/> (the mailbox write, the pending target / generation, the queued <c>SeekCoreAsync</c>) —
        /// unless a newer seek already holds the mailbox. Its op is queued behind this one and owns the outcome; overwriting it with this older
        /// target would move the playhead back to where the scrub ended.</summary>
        static void SeekUnlessSuperseded(int ms, uint epoch, uint gen)
        {
            if (Volatile.Read(ref s_seekMailbox) < 0) Seek(ms, epoch, gen);
        }

        /// <summary>Capture lost / Escape / unmount: ONE command releases the hold and fades the main voice back in; the scrub voice fades out
        /// and self-retires. Idempotent (V-PA24): no hold ⇒ nothing to do. One chain op.</summary>
        public static void ScrubCancel() => Enqueue(ScrubCancelAsync);

        static async Task ScrubCancelAsync()
        {
            ScrubHold? hold = TakeHold(out PcmAudioSession? live, out uint loaded);
            if (hold is null || !HoldIsLive(hold, live, loaded)) return;
            try { await hold.Session.CancelScrubAsync(hold.VoiceId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("audio", "audio.scrub.cancel failed", ex); }
            Log.Info("audio", "audio.scrub.cancel");
        }

        /// <summary>A load or a stop orphaned the gesture: the reducer drops a gesture that a load interrupted (a drag across a track change) WITHOUT
        /// sending a cancel, so the pump forgets it here. Called synchronously from <c>Load</c> and <c>Stop</c>, before they enqueue the session's
        /// disposal.
        /// <para>Clears the live hold, retires the gesture's pending <c>ScrubBegin</c> (<c>s_scrubDrops</c>), and — as one chain op, ahead of the
        /// disposal — releases the held voice SILENTLY if its session is still the live one: no fade-in (the voice stays at gain 0 until the session
        /// goes), no seek, no <c>Input</c> posted. The scrub voice fades out and the ring retires it, so its decoder lease is back before the next
        /// session needs one. Idempotent; a no-op when no gesture is live.</para></summary>
        internal static void ScrubDrop()
        {
            ScrubHold? hold;
            lock (s_gate)
            {
                s_scrubDrops++;
                hold = s_scrub;
                s_scrub = null;
                s_scrubWantMs = -1;
            }
            if (hold is not null) Enqueue(() => ScrubDropAsync(hold));
        }

        static async Task ScrubDropAsync(ScrubHold hold)
        {
            PcmAudioSession? live;
            uint loaded;
            lock (s_gate) { live = s_session; loaded = s_loadEpoch; }
            if (!HoldIsLive(hold, live, loaded)) return;                                 // the session is already gone: its disposal retired both voices
            try { await hold.Session.ReleaseHeldSilentAsync(hold.VoiceId, CancellationToken.None).ConfigureAwait(false); }
            catch { }                                                                    // a session disposed under the command: nothing left to release
            Log.Info("audio", "audio.scrub.drop");
        }

        /// <summary>Take the live hold (leaving none) together with the session and load epoch it is judged against.</summary>
        static ScrubHold? TakeHold(out PcmAudioSession? live, out uint loaded)
        {
            lock (s_gate)
            {
                ScrubHold? hold = s_scrub;
                s_scrub = null;
                s_scrubWantMs = -1;
                live = s_session;
                loaded = s_loadEpoch;
                return hold;
            }
        }

        /// <summary>The hold still describes the live session's voice: the same session, the same load. A load or a stop in between disposed
        /// the voice the hold froze (and the session's disposal retired the scrub voice), so there is nothing left to release.</summary>
        static bool HoldIsLive(ScrubHold hold, PcmAudioSession? live, uint loaded)
            => ReferenceEquals(hold.Session, live) && hold.Epoch == loaded && !s_disposed;
    }
}
