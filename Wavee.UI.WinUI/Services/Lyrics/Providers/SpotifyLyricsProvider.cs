using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Session;

namespace Wavee.UI.WinUI.Services.Lyrics.Providers;

/// <summary>
/// Wraps Spotify's built-in lyrics API (SpClient) as an <see cref="ILyricsProvider"/>.
/// </summary>
public sealed class SpotifyLyricsProvider : ILyricsProvider
{
    private readonly ISession _session;

    public string Id => "Spotify";
    public string DisplayName => "Spotify";

    public SpotifyLyricsProvider(ISession session)
    {
        _session = session;
    }

    public async Task<LyricsSearchResult?> SearchAsync(
        string title, string artist, string? album,
        double durationMs, string? trackId, string? imageUri,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(trackId))
            return null;

        try
        {
            // Spotify needs an image URI; provide fallback
            imageUri ??= "spotify:image:ab67616d0000b273";

            var response = await _session.SpClient.GetLyricsAsync(trackId, imageUri, ct);

            if (response?.Lyrics == null || response.Lyrics.Lines.Count == 0)
                return null;

            // Exact Spotify track ID match — highest confidence
            return new LyricsSearchResult(response, null, 100);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
