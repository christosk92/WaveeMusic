using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Services;

public interface ISpotifyVideoPlaybackDetails : INotifyPropertyChanged
{
    IReadOnlyList<SpotifyVideoQualityOption> AvailableQualities { get; }
    SpotifyVideoQualityOption? CurrentQuality { get; }
    SpotifyVideoPlaybackMetadata? PlaybackMetadata { get; }
    bool CanSelectQuality { get; }

    Task SelectQualityAsync(int videoProfileId, CancellationToken cancellationToken = default);
}

public sealed record SpotifyVideoQualityOption(
    int ProfileId,
    string Label,
    int Width,
    int Height,
    int Bandwidth,
    string Codec);

public sealed record SpotifyVideoPlaybackMetadata(
    string DrmSystem,
    string Container,
    string VideoCodec,
    string AudioCodec,
    string? LicenseServerEndpoint,
    int SegmentLengthSeconds,
    int SegmentCount,
    long DurationMs);
