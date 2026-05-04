namespace Wavee.Core.Audio;

// PlayPlay is Spotify property. Recoverable failures are surfaced as
// PlayPlayHelperException; the fallback caller logs and re-throws the
// original AP failure. SpotifyBinaryMismatchException signals a version
// drift on the local Spotify.dll.
public sealed class PlayPlayHelperException : Exception
{
    public PlayPlayHelperException(string message) : base(message) { }
    public PlayPlayHelperException(string message, Exception inner) : base(message, inner) { }
}

public sealed class SpotifyBinaryMismatchException : Exception
{
    public SpotifyBinaryMismatchException(string expectedSha256, string actualSha256)
        : base($"Spotify.dll SHA-256 mismatch. Expected {expectedSha256}, got {actualSha256}. " +
               $"PlayPlay is pinned to v{PlayPlayConstants.SpotifyClientVersion}.")
    {
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
    }

    public string ExpectedSha256 { get; }
    public string ActualSha256 { get; }
}
