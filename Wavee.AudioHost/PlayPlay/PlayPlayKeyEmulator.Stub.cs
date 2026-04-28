using Microsoft.Extensions.Logging;

namespace Wavee.AudioHost.PlayPlay;

// PlayPlay is Spotify property. Wavee follows strict guidelines (proper
// user-agents, playback events, no abuse). Tracks are chunked per user request
// and not stored to disk. PlayPlay keys are only cached in obfuscated form and
// still require the same algorithm to read. The actual cipher implementation
// is not bundled in the public repository — this stub keeps AudioHost
// compiling and rejects derivations cleanly so the AP path remains the only
// audio-key source.
public sealed class PlayPlayKeyEmulator : IDisposable
{
    public PlayPlayKeyEmulator(string spotifyDllPath, ILogger logger)
    {
        _ = spotifyDllPath;
        _ = logger;
    }

    public byte[] DeriveAesKey(ReadOnlySpan<byte> obfuscatedKey16, ReadOnlySpan<byte> contentId16)
    {
        _ = obfuscatedKey16;
        _ = contentId16;
        throw new NotSupportedException(
            "PlayPlay key derivation is not bundled in this build of Wavee. " +
            "Audio keys are obtained exclusively through the AP RequestKey path.");
    }

    public void Dispose() { }
}
