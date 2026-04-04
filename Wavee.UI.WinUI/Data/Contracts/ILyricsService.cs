using System.Threading;
using System.Threading.Tasks;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Fetches lyrics from available providers and converts them to the
/// <see cref="LyricsData"/> format expected by the NowPlayingCanvas control.
/// </summary>
public interface ILyricsService
{
    Task<(LyricsData? Lyrics, LyricsSearchDiagnostics Diagnostics)> GetLyricsForTrackAsync(
        string trackId,
        string? title,
        string? artist,
        double durationMs,
        string? imageUrl,
        CancellationToken ct = default);

    /// <summary>
    /// Clears cached lyrics for a track so the next fetch re-queries all providers.
    /// </summary>
    Task ClearCacheForTrackAsync(string trackId, CancellationToken ct = default);
}
