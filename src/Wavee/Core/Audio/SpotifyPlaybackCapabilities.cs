namespace Wavee.Core.Audio;

/// <summary>
/// Build/runtime capability flags for Spotify-encrypted local audio playback.
/// </summary>
public static class SpotifyPlaybackCapabilities
{
#if WAVEE_SPOTIFY_PLAYBACK_STUBS
    public const bool DefaultLocalSpotifyPlaybackEnabled = false;
#else
    public const bool DefaultLocalSpotifyPlaybackEnabled = true;
#endif

    public const string DisabledReason = "spotify_playback_not_enabled";
    public const string DisabledMessage = "Sorry, Spotify Playback is not enabled right now";

    public static bool IsSpotifyAudioPlaybackUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        if (!uri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            return false;

        return !uri.StartsWith("spotify:local:", StringComparison.OrdinalIgnoreCase)
               && !uri.StartsWith("spotify:internal:", StringComparison.OrdinalIgnoreCase);
    }
}
