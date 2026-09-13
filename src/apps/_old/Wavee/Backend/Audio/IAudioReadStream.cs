using System;
using System.IO;
using Wavee.Sdk.Streams;

namespace Wavee.Backend.Audio;

/// <summary>What the engine's single decode loop needs from an audio byte stream, regardless of source — the Spotify
/// encrypted CDN stream (<see cref="SpotifyAudioStream"/>) or the plain-HTTP external stream
/// (<see cref="PlainHttpAudioStream"/>). Lets one <c>DecodeLoop</c> drive both instead of two near-duplicate loops.</summary>
internal interface IAudioReadStream : IDisposable
{
    /// <summary>The stream itself (both implementers ARE Streams) for wrapping in a decode-stream view / handing to a
    /// decoder — see <see cref="PrefetchingReadStream"/>.</summary>
    Stream AsStream();
    long CurrentOffset { get; }
    bool IsBodyAttached { get; }
    long KnownSize { get; }
    int ClearHeadLength { get; }
    IDisposable PauseReadAhead();
    void ResumeReadAheadAtCurrentOffset();

    /// <summary>Non-blocking read for the engine's dedicated decode thread, which must never do synchronous network
    /// I/O: return the bytes available RIGHT NOW (possibly fewer than requested, or zero) instead of blocking on a
    /// CDN fetch, setting <paramref name="wouldBlock"/> on a miss so the caller knows a zero here is NOT end-of-stream
    /// (every codec above this seam latches a zero read as permanent EOF — see <see cref="PrefetchingReadStream"/>,
    /// which turns this into a bounded-wait primitive instead of ever surfacing a transient miss as a short read).
    /// The default forwards to a plain blocking read and never reports <c>wouldBlock</c> — correct for a source with
    /// no fetch that can actually block (a local file, a module byte stream, a live ring that never blocks past
    /// prefill). <see cref="SpotifyAudioStream"/> overrides this with a real non-blocking implementation that kicks
    /// an async prefetch on a miss.</summary>
    int TryRead(Span<byte> dst, out bool wouldBlock) { wouldBlock = false; return AsStream().Read(dst); }

    /// <summary>Feed the negotiated bitrate + connection-metered state to a ranged source's adaptive read-ahead
    /// window. No-op default — a source with no read-ahead window has nothing to configure;
    /// <see cref="PlainHttpAudioStream"/> overrides this to forward to its own ranged source.</summary>
    void ConfigureReadAhead(int bitrateBitsPerSec, bool metered) { }
}

/// <summary>Optional recovery telemetry exposed by ranged streams to the decode pipeline.</summary>
internal interface IAudioNetworkRecoverySource
{
    event Action<AudioNetworkRecoveryEvent>? NetworkRecovery;
}
