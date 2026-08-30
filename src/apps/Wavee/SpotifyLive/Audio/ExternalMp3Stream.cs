using System.Net.Http;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Factory for external HTTP MP3 streams used by the play engine.</summary>
static class ExternalMp3Stream
{
    public static Task<PlainHttpAudioStream> OpenAsync(HttpClient http, string url, WaveeLogger log = default,
        CancellationToken ct = default)
        => PlainHttpAudioStream.OpenAsync(http, url, log, ct);
}
