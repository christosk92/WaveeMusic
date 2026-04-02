using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http.Lyrics;

namespace Wavee.UI.WinUI.Services.Lyrics.Providers;

/// <summary>
/// Wraps the existing <see cref="LrcLibClient"/> as an <see cref="ILyricsProvider"/>.
/// </summary>
public sealed class LrcLibLyricsProvider : ILyricsProvider
{
    private readonly LrcLibClient _client;

    public string Id => "LRCLIB";
    public string DisplayName => "LRCLIB";

    public LrcLibLyricsProvider(LrcLibClient client)
    {
        _client = client;
    }

    public async Task<LyricsSearchResult?> SearchAsync(
        string title, string artist, string? album,
        double durationMs, string? trackId, string? imageUri,
        CancellationToken ct)
    {
        try
        {
            var (response, wordTimings) = await _client.GetLyricsAsync(title, artist, album, durationMs, ct);

            if (response?.Lyrics == null || response.Lyrics.Lines.Count == 0)
                return null;

            // LRCLIB searches by metadata match — score 100 for direct match
            return new LyricsSearchResult(response, wordTimings, 100);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
