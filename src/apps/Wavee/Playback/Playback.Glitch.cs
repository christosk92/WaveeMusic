// ── Playback/Playback.Glitch.cs ──────────────────────────────────────────────────────────────────────────────────────
// The per-session glitch ledger: what the RT feed's underrun incidents add up to, and the one word that says why.
//
// Role: CORE
// Wave: playback smoothness WP-0a (D1; V-PA26/X4, V-PA31) — docs/plans/wavee/playback-smoothness-implementation.md §4.14
//
// `System` only, PRIMITIVES IN, a snapshot out. It is written on the pump's tick thread (`Playback.Audio.DrainXruns`,
// `FoldStall`) and read on the UI thread (`Metrics.Read()` → the Diagnostics "Playback health" card) and by
// `RetireXruns` when a session ends, so EVERY access goes through one lock: a torn read of "incidents = 3, verdict
// from 2" is the failure this file exists to rule out. Nothing here knows an engine type, a clock or a log — the
// caller hands in the frame counts and the wall-clock stamp, which is also what makes the verdict a unit fact.

namespace Wavee;

/// <summary>Per-session glitch ledger (D1). Primitives in, a snapshot out; the tick thread records, the UI reads — one
/// lock, no engine types.</summary>
public sealed class GlitchLedger
{
    /// <summary>The verdict keys (loc KEYS, not English — V-PA34: the card translates them). <see cref="Snapshot.Verdict"/>
    /// is always exactly one of these.</summary>
    public const string VerdictClean = "clean", VerdictGcPauses = "gcPauses", VerdictByteStarved = "byteStarved",
        VerdictProducerStarved = "producerStarved", VerdictDeviceLate = "deviceLate";

    readonly object _gate = new();
    int _incidents, _producerStarves, _deviceLate, _gcImplicated, _byteWaits;
    long _framesLost, _longestStallMs, _lastStallMs, _lastStallAtUnixMs, _longestByteWaitMs, _lastStallFrames;

    /// <summary>How long after its last update an incident can still grow: a stall the tick saw continuing is extended,
    /// silence the feed lost much later (a suppressed seek rebuffer) is not pinned on an old incident.</summary>
    public const long ExtendWindowMs = 2_000;

    /// <summary>One consistent reading of the ledger.</summary>
    /// <param name="Incidents">RT underrun incidents this session.</param>
    /// <param name="ProducerStarves">Incidents where the ring was empty at the miss — the decode-ahead fell behind.</param>
    /// <param name="DeviceLate">Incidents where the ring still held audio — the RT thread itself was late.</param>
    /// <param name="GcImplicated">Incidents with a GC pause inside the window.</param>
    /// <param name="ByteWaits">Times the byte seam starved long enough for the pump to report "Reconnecting".</param>
    /// <param name="FramesLost">Frames of silence the incidents wrote.</param>
    /// <param name="LongestStallMs">The longest single gap, in ms.</param>
    /// <param name="LastStallMs">The latest gap, in ms.</param>
    /// <param name="LastStallAtUnixMs">When the latest gap was recorded (UTC unix ms); 0 before any.</param>
    /// <param name="LongestByteWaitMs">The longest byte-seam stall reported at its edge, in ms.</param>
    /// <param name="Verdict">One of the <c>Verdict*</c> keys.</param>
    public readonly record struct Snapshot(int Incidents, int ProducerStarves, int DeviceLate, int GcImplicated, int ByteWaits,
        long FramesLost, long LongestStallMs, long LastStallMs, long LastStallAtUnixMs, long LongestByteWaitMs, string Verdict);

    /// <summary>One RT incident: <paramref name="gapFrames"/> of silence, the ring's fill at the miss, the GC pause ticks in
    /// the window.</summary>
    public void Record(int gapFrames, int ringFramesAtMiss, long gcPauseTicks, int sampleRate, long nowUnixMs)
    {
        int gap = Math.Max(0, gapFrames);
        long stallMs = sampleRate > 0 ? gap * 1000L / sampleRate : 0;
        lock (_gate)
        {
            _incidents++;
            _framesLost += gap;
            _lastStallFrames = gap;
            _lastStallMs = stallMs;
            _lastStallAtUnixMs = nowUnixMs;
            if (stallMs > _longestStallMs) _longestStallMs = stallMs;
            if (ringFramesAtMiss == 0) _producerStarves++; else _deviceLate++;
            if (gcPauseTicks > 0) _gcImplicated++;
        }
    }

    /// <summary>The open incident ran on: <paramref name="extraFrames"/> more frames of silence than its first block reported
    /// (the RT feed raises ONE event per incident and accrues the rest into its frames-lost total). Grows the latest
    /// incident's length — and the longest — when it is fresh; a no-op with no incident or a stale one.</summary>
    public void Extend(long extraFrames, int sampleRate, long nowUnixMs)
    {
        if (extraFrames <= 0 || sampleRate <= 0) return;
        lock (_gate)
        {
            if (_incidents == 0 || nowUnixMs - _lastStallAtUnixMs > ExtendWindowMs) return;
            _framesLost += extraFrames;
            _lastStallFrames += extraFrames;
            _lastStallMs = _lastStallFrames * 1000L / sampleRate;
            _lastStallAtUnixMs = nowUnixMs;
            if (_lastStallMs > _longestStallMs) _longestStallMs = _lastStallMs;
        }
    }

    /// <summary>The byte seam starved for <paramref name="stallMs"/> before the pump called it "Reconnecting".</summary>
    public void RecordByteWait(long stallMs)
    {
        lock (_gate)
        {
            _byteWaits++;
            if (stallMs > _longestByteWaitMs) _longestByteWaitMs = stallMs;
        }
    }

    /// <summary>Start a fresh session: every counter back to zero.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _incidents = _producerStarves = _deviceLate = _gcImplicated = _byteWaits = 0;
            _framesLost = _longestStallMs = _lastStallMs = _lastStallAtUnixMs = _longestByteWaitMs = _lastStallFrames = 0;
        }
    }

    /// <summary>The ledger as it stands, read under the lock so the counts and the verdict agree.</summary>
    public Snapshot Read()
    {
        lock (_gate)
        {
            return new Snapshot(_incidents, _producerStarves, _deviceLate, _gcImplicated, _byteWaits, _framesLost,
                _longestStallMs, _lastStallMs, _lastStallAtUnixMs, _longestByteWaitMs, VerdictLocked());
        }
    }

    /// <summary>A loc KEY, not English (V-PA34): the card translates it. Order matters: a GC pause in half the incidents
    /// outranks everything (no scheduling fix helps), then a byte-starved link that also starved the ring, then whichever
    /// of "the ring was empty" and "the ring had audio" is commoner (a tie is the device: the RT thread was late).</summary>
    string VerdictLocked()
        => _incidents == 0 ? VerdictClean
         : _gcImplicated * 2 >= _incidents ? VerdictGcPauses
         : _byteWaits > 0 && _producerStarves > 0 ? VerdictByteStarved
         : _producerStarves > _deviceLate ? VerdictProducerStarved
         : VerdictDeviceLate;
}
