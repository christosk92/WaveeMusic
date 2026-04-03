using System.Threading;
using System.Threading.Tasks;
using Wavee.Controls.Lyrics.Models.Lyrics;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Fetches lyrics from available providers and converts them to the
/// <see cref="LyricsData"/> format expected by the NowPlayingCanvas control.
/// </summary>
public interface ILyricsService
{
    Task<LyricsData?> GetLyricsForTrackAsync(
        string trackId,
        string? title,
        string? artist,
        double durationMs,
        string? imageUrl,
        CancellationToken ct = default);
}
