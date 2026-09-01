using System;

namespace Wavee.SpotifyLive.Audio;

/// <summary>
/// Pure formatting for one xrun (RT-feed ring underrun) incident — split out of
/// <see cref="FluentMediaAudioHost.DrainXruns"/> for exactly the reason <c>TimeFormat</c>/<c>LiveRail</c> live under
/// <c>Backend/Playback</c>: the host that calls it is engine-bound (it opens a real WASAPI device and cannot be
/// instantiated headlessly), so the one piece of real LOGIC here — turning a lost-frame count into a millisecond gap,
/// and an RT timestamp into "how long ago" — would otherwise only ever be exercised by a live audio glitch nobody can
/// reproduce on demand. <see cref="XrunLogLineTests"/> pins it against crafted incidents instead.
///
/// <para>Before this, a mid-track ring underrun was invisible until the NEXT track boundary, and even then only as a
/// cumulative "xruns=17" callback count — not frames of audio actually dropped, no timestamp, no position, no cause.
/// This is the one-incident-one-line answer: when, how much (in both frames and ms), how starved the ring was, where
/// playback was, what state the session was in, and whether a GC pause was implicated.</para>
/// </summary>
public static class XrunLogLine
{
    /// <summary>The lost audio, in ms, at the negotiated mix sample rate. 0 when the rate or the frame count isn't
    /// positive — never divides by zero, never reports a fabricated gap.</summary>
    public static double GapMs(int gapFrames, int sampleRate)
        => sampleRate > 0 && gapFrames > 0 ? gapFrames * 1000.0 / sampleRate : 0.0;

    /// <summary>How long ago (ms) the incident fired, on the same <see cref="Environment.TickCount64"/> clock the
    /// rest of this host's diagnostics use. Clamped to 0 — a drained event can never be reported as being from the
    /// future (a wrapped/misordered timestamp must not print a negative age).</summary>
    public static long AgeMs(long eventTimestampTicks64, long nowTicks64)
        => Math.Max(0, nowTicks64 - eventTimestampTicks64);

    /// <summary>The one always-on Warning line for a single xrun incident.</summary>
    /// <param name="voiceId">The mixer voice the underrun happened on.</param>
    /// <param name="gapFrames">Frames of silence written in place of real audio (NOT a callback count).</param>
    /// <param name="totalFramesLost">The running total of frames lost this session — lets several incidents in the
    /// log be read as one growing tally, the same way the existing <c>[gapless]</c> lines track xrun deltas.</param>
    /// <param name="ringFrames">How full the decode ring was, in frames, at the moment of the miss.</param>
    /// <param name="gcPauseTicksDelta">The GC-pause tick delta the engine attributed to this incident (0 when no GC
    /// pause was implicated).</param>
    /// <param name="gapMs">See <see cref="GapMs"/>.</param>
    /// <param name="ageMs">See <see cref="AgeMs"/>.</param>
    /// <param name="positionMs">Playback position (ms) at the time this incident was drained.</param>
    /// <param name="sessionState">The session's playback state at the time this incident was drained.</param>
    public static string Format(long voiceId, int gapFrames, long totalFramesLost, int ringFrames,
        long gcPauseTicksDelta, double gapMs, long ageMs, long positionMs, string sessionState)
        => $"[audio] xrun voice={voiceId} gapFrames={gapFrames} gapMs={gapMs:0.0} totalFramesLost={totalFramesLost} " +
           $"ringFramesAtMiss={ringFrames} posMs={positionMs} state={sessionState} " +
           $"gcPauseTicksDelta={gcPauseTicksDelta} ageMs={ageMs}";
}
