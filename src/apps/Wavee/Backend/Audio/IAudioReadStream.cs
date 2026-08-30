using System.IO;
using Wavee.Sdk.Streams;

namespace Wavee.Backend.Audio;

/// <summary>What the engine's single decode loop needs from an audio byte stream, regardless of source — the Spotify
/// encrypted CDN stream (<see cref="SpotifyAudioStream"/>) or the plain-HTTP external stream
/// (<see cref="PlainHttpAudioStream"/>). Lets one <c>DecodeLoop</c> drive both instead of two near-duplicate loops.</summary>
internal interface IAudioReadStream : IDisposable
{
    /// <summary>The stream itself (both implementers ARE Streams) for wrapping in a SkipStream / handing to a decoder.</summary>
    Stream AsStream();
    long CurrentOffset { get; }
    bool IsBodyAttached { get; }
    long KnownSize { get; }
    int ClearHeadLength { get; }
    IDisposable PauseReadAhead();
    void ResumeReadAheadAtCurrentOffset();
}

/// <summary>Optional recovery telemetry exposed by ranged streams to the decode pipeline.</summary>
internal interface IAudioNetworkRecoverySource
{
    event Action<AudioNetworkRecoveryEvent>? NetworkRecovery;
}
