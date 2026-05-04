using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Playback.Contracts;

namespace Wavee.UI.Services;

public interface IPreviewAudioPlaybackEngine
{
    string? CurrentSessionId { get; }

    Task<PreviewStartResult> StartAsync(
        string previewUrl,
        Action<PreviewVisualizationFrame> onFrame,
        Action onCompleted,
        CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}

public readonly record struct PreviewStartResult(bool HasVisualization, string? SessionId);
