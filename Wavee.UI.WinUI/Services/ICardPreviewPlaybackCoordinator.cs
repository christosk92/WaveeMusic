using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Playback.Contracts;

namespace Wavee.UI.WinUI.Services;

public interface ICardPreviewPlaybackCoordinator
{
    Task ScheduleHover(CardPreviewRequest request, CancellationToken ct = default);
    Task StartImmediate(CardPreviewRequest request, CancellationToken ct = default);
    Task CancelOwner(Guid ownerId, CancellationToken ct = default);
    Task UnregisterOwner(Guid ownerId, CancellationToken ct = default);
}

public sealed record CardPreviewRequest(
    Guid OwnerId,
    string PreviewUrl,
    Action<PreviewVisualizationFrame> OnFrame,
    Action<CardPreviewPlaybackState> OnStateChanged,
    Action OnCompleted,
    Func<bool>? CanStartPlayback = null);

public readonly record struct CardPreviewPlaybackState(
    bool IsPending,
    bool IsPlaying,
    bool HasVisualization,
    string? SessionId);
