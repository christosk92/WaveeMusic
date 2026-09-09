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

    /// <summary>Read only available bytes. Zero with wouldBlock is never EOF.</summary>
    int TryRead(Span<byte> dst, out bool wouldBlock);

    /// <summary>Changes after data, completion, failure, seek or disposal. Read before TryRead.</summary>
    long DataVersion { get; }

    /// <summary>Wait until the observed version changes; cancellation wakes the actual source wait.</summary>
    void WaitForData(long observedVersion, System.Threading.CancellationToken cancellationToken);

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
