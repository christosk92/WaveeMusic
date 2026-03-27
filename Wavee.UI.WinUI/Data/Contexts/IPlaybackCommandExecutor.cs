using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Internal strategy interface for executing playback commands against a backend.
/// Implementations handle the protocol-specific details (Web API, local engine, etc.).
/// </summary>
internal interface IPlaybackCommandExecutor
{
    Task<PlaybackResult> PlayContextAsync(string contextUri, PlayContextOptions? options, CancellationToken ct);
    Task<PlaybackResult> PlayTracksAsync(IReadOnlyList<string> trackUris, int startIndex, CancellationToken ct);
    Task<PlaybackResult> ResumeAsync(CancellationToken ct);
    Task<PlaybackResult> PauseAsync(CancellationToken ct);
    Task<PlaybackResult> SkipNextAsync(CancellationToken ct);
    Task<PlaybackResult> SkipPreviousAsync(CancellationToken ct);
    Task<PlaybackResult> SeekAsync(long positionMs, CancellationToken ct);
    Task<PlaybackResult> SetShuffleAsync(bool enabled, CancellationToken ct);
    Task<PlaybackResult> SetRepeatAsync(string state, CancellationToken ct);
    Task<PlaybackResult> SetVolumeAsync(int volumePercent, CancellationToken ct);
    Task<PlaybackResult> AddToQueueAsync(string trackUri, CancellationToken ct);
    Task<PlaybackResult> TransferPlaybackAsync(string deviceId, bool startPlaying, CancellationToken ct);
}
