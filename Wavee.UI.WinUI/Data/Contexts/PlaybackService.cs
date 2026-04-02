using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Orchestrates playback commands with retry logic, buffering state, and error broadcasting.
/// Routes commands through <see cref="IPlaybackCommandExecutor"/> (currently Spotify Web API).
/// </summary>
internal sealed partial class PlaybackService : ObservableObject, IPlaybackService, IDisposable
{
    private readonly IPlaybackCommandExecutor _executor;
    private readonly Session _session;
    private readonly INotificationService _notificationService;
    private readonly IPlaybackPromptService _promptService;
    private readonly ILogger? _logger;
    private readonly Subject<PlaybackErrorEvent> _errorSubject = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private IDisposable? _stateSubscription;
    private IDisposable? _errorNotificationSubscription;

    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private bool _isExecutingCommand;
    [ObservableProperty] private string? _activeDeviceId;
    [ObservableProperty] private string? _activeDeviceName;
    [ObservableProperty] private bool _isPlayingRemotely;

    public IObservable<PlaybackErrorEvent> Errors => _errorSubject.AsObservable();
    public event Action<string?>? BufferingStarted;

    public PlaybackService(
        IPlaybackCommandExecutor executor,
        Session session,
        INotificationService notificationService,
        IPlaybackPromptService promptService,
        ILogger? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _logger = logger;

        SubscribeToRemoteState();
        SubscribeToErrorNotifications();
    }

    private void SubscribeToErrorNotifications()
    {
        _errorNotificationSubscription = _errorSubject.Subscribe(error =>
        {
            var severity = error.Kind switch
            {
                PlaybackErrorKind.DeviceUnavailable => NotificationSeverity.Warning,
                PlaybackErrorKind.RateLimited => NotificationSeverity.Warning,
                PlaybackErrorKind.Unavailable => NotificationSeverity.Warning,
                _ => NotificationSeverity.Error
            };

            _notificationService.Show(new NotificationInfo
            {
                Message = error.Message,
                Severity = severity,
                AutoDismissAfter = TimeSpan.FromSeconds(5)
            });
        });
    }

    private void SubscribeToRemoteState()
    {
        var stateManager = _session.PlaybackState;
        if (stateManager == null) return;

        _stateSubscription = stateManager.StateChanges
            .Where(s => s.Changes.HasFlag(StateChanges.ActiveDevice))
            .Subscribe(
                state =>
                {
                    ActiveDeviceId = state.ActiveDeviceId;
                    IsPlayingRemotely = state.Source == StateSource.Cluster
                                        && state.ActiveDeviceId != null
                                        && state.ActiveDeviceId != _session.Config.DeviceId;
                },
                ex => _logger?.LogError(ex, "Error in playback state subscription"));
    }

    // ── Play commands (with prompt intercept) ──

    public async Task<PlaybackResult> PlayContextAsync(string contextUri, PlayContextOptions? options, CancellationToken ct)
    {
        var action = await _promptService.ResolvePlayActionAsync();
        return action switch
        {
            Controls.PlayAction.Cancelled => PlaybackResult.Success(),
            Controls.PlayAction.PlayNext => await ExecuteWithRetryAsync(c => _executor.AddToQueueAsync(contextUri, c), nameof(PlayContextAsync), ct),
            Controls.PlayAction.PlayLater => await ExecuteWithRetryAsync(c => _executor.AddToQueueAsync(contextUri, c), nameof(PlayContextAsync), ct),
            _ => await ExecuteWithRetryAsync(c => _executor.PlayContextAsync(contextUri, options, c), nameof(PlayContextAsync), ct, isPlayCommand: true)
        };
    }

    public async Task<PlaybackResult> PlayTrackInContextAsync(string trackUri, string contextUri, PlayContextOptions? options, CancellationToken ct)
    {
        BufferingStarted?.Invoke(ExtractId(trackUri));
        var action = await _promptService.ResolvePlayActionAsync();
        return action switch
        {
            Controls.PlayAction.Cancelled => PlaybackResult.Success(),
            Controls.PlayAction.PlayNext => await ExecuteWithRetryAsync(c => _executor.AddToQueueAsync(trackUri, c), nameof(PlayTrackInContextAsync), ct),
            Controls.PlayAction.PlayLater => await ExecuteWithRetryAsync(c => _executor.AddToQueueAsync(trackUri, c), nameof(PlayTrackInContextAsync), ct),
            _ => await ExecuteWithRetryAsync(c =>
            {
                var merged = (options ?? new PlayContextOptions()) with { StartTrackUri = trackUri };
                return _executor.PlayContextAsync(contextUri, merged, c);
            }, nameof(PlayTrackInContextAsync), ct, isPlayCommand: true)
        };
    }

    public async Task<PlaybackResult> PlayTracksAsync(IReadOnlyList<string> trackUris, int startIndex, CancellationToken ct)
    {
        if (startIndex >= 0 && startIndex < trackUris.Count)
            BufferingStarted?.Invoke(ExtractId(trackUris[startIndex]));
        var action = await _promptService.ResolvePlayActionAsync();
        return action switch
        {
            Controls.PlayAction.Cancelled => PlaybackResult.Success(),
            Controls.PlayAction.PlayNext or Controls.PlayAction.PlayLater =>
                await ExecuteQueueMultipleAsync(trackUris, ct),
            _ => await ExecuteWithRetryAsync(c => _executor.PlayTracksAsync(trackUris, startIndex, c), nameof(PlayTracksAsync), ct, isPlayCommand: true)
        };
    }

    private async Task<PlaybackResult> ExecuteQueueMultipleAsync(IReadOnlyList<string> trackUris, CancellationToken ct)
    {
        foreach (var uri in trackUris)
        {
            var result = await ExecuteWithRetryAsync(c => _executor.AddToQueueAsync(uri, c), "AddToQueue", ct);
            if (!result.IsSuccess) return result;
        }
        return PlaybackResult.Success();
    }

    public Task<PlaybackResult> ResumeAsync(CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.ResumeAsync(c), nameof(ResumeAsync), ct, isPlayCommand: true);

    public Task<PlaybackResult> PauseAsync(CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.PauseAsync(c), nameof(PauseAsync), ct);

    public async Task<PlaybackResult> TogglePlayPauseAsync(CancellationToken ct)
    {
        var stateManager = _session.PlaybackState;
        var currentState = stateManager?.CurrentState;
        var activeDeviceId = currentState?.ActiveDeviceId;
        var hasLocalEngine = stateManager?.IsBidirectional == true;

        // No real playback if: no active device, or we're active with no engine
        var noRealPlayback = string.IsNullOrEmpty(activeDeviceId)
            || (activeDeviceId == _session.Config.DeviceId && !hasLocalEngine);

        var isPlaying = currentState?.Status == PlaybackStatus.Playing && !noRealPlayback;
        return isPlaying
            ? await PauseAsync(ct)
            : await ResumeAsync(ct);
    }

    public Task<PlaybackResult> SkipNextAsync(CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.SkipNextAsync(c), nameof(SkipNextAsync), ct);

    public Task<PlaybackResult> SkipPreviousAsync(CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.SkipPreviousAsync(c), nameof(SkipPreviousAsync), ct);

    public Task<PlaybackResult> SeekAsync(long positionMs, CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.SeekAsync(positionMs, c), nameof(SeekAsync), ct);

    public Task<PlaybackResult> SetShuffleAsync(bool enabled, CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.SetShuffleAsync(enabled, c), nameof(SetShuffleAsync), ct);

    public Task<PlaybackResult> SetRepeatModeAsync(RepeatMode mode, CancellationToken ct)
    {
        var state = mode switch
        {
            RepeatMode.Off => "off",
            RepeatMode.Context => "context",
            RepeatMode.Track => "track",
            _ => "off"
        };
        return ExecuteWithRetryAsync(c => _executor.SetRepeatAsync(state, c), nameof(SetRepeatModeAsync), ct);
    }

    public Task<PlaybackResult> SetVolumeAsync(int volumePercent, CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.SetVolumeAsync(volumePercent, c), nameof(SetVolumeAsync), ct);

    public Task<PlaybackResult> AddToQueueAsync(string trackUri, CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.AddToQueueAsync(trackUri, c), nameof(AddToQueueAsync), ct);

    public Task<PlaybackResult> TransferPlaybackAsync(string deviceId, bool startPlaying, CancellationToken ct)
        => ExecuteWithRetryAsync(c => _executor.TransferPlaybackAsync(deviceId, startPlaying, c), nameof(TransferPlaybackAsync), ct);

    // ── Retry engine ──

    private async Task<PlaybackResult> ExecuteWithRetryAsync(
        Func<CancellationToken, Task<PlaybackResult>> action,
        string commandName,
        CancellationToken ct,
        int maxRetries = 3,
        bool isPlayCommand = false)
    {
        await _commandLock.WaitAsync(ct);
        try
        {
            IsExecutingCommand = true;
            if (isPlayCommand) IsBuffering = true;

            PlaybackResult? lastResult = null;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                lastResult = await action(ct);

                if (lastResult.IsSuccess)
                {
                    _logger?.LogDebug("{Command} succeeded on attempt {Attempt}", commandName, attempt + 1);
                    return lastResult;
                }

                if (!IsRetryable(lastResult.ErrorKind))
                {
                    _logger?.LogWarning("{Command} failed (non-retryable): {Error}", commandName, lastResult.ErrorMessage);
                    break;
                }

                if (attempt < maxRetries)
                {
                    var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                    var delay = baseDelay + jitter;

                    _logger?.LogDebug("{Command} failed (attempt {Attempt}/{Max}), retrying in {Delay}ms: {Error}",
                        commandName, attempt + 1, maxRetries + 1, delay.TotalMilliseconds, lastResult.ErrorMessage);

                    await Task.Delay(delay, ct);
                }
            }

            // Final failure — broadcast error
            if (lastResult != null && !lastResult.IsSuccess)
            {
                var errorEvent = new PlaybackErrorEvent(
                    lastResult.ErrorKind ?? PlaybackErrorKind.Unknown,
                    lastResult.ErrorMessage ?? "Playback command failed.",
                    commandName);

                _errorSubject.OnNext(errorEvent);
                _logger?.LogWarning("{Command} failed after all retries: {Error}", commandName, lastResult.ErrorMessage);
            }

            return lastResult ?? PlaybackResult.Failure(PlaybackErrorKind.Unknown, "No result from command execution.");
        }
        catch (OperationCanceledException)
        {
            return PlaybackResult.Failure(PlaybackErrorKind.Unknown, "Command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error executing {Command}", commandName);
            var error = PlaybackResult.Failure(PlaybackErrorKind.Unknown, "An unexpected error occurred.", ex);
            _errorSubject.OnNext(new PlaybackErrorEvent(PlaybackErrorKind.Unknown, error.ErrorMessage!, commandName));
            return error;
        }
        finally
        {
            IsExecutingCommand = false;
            IsBuffering = false;
            _commandLock.Release();
        }
    }

    private static bool IsRetryable(PlaybackErrorKind? kind) => kind is
        PlaybackErrorKind.Network or
        PlaybackErrorKind.RateLimited or
        PlaybackErrorKind.Unavailable;

    private static string? ExtractId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var lastColon = uri.LastIndexOf(':');
        return lastColon >= 0 ? uri[(lastColon + 1)..] : uri;
    }

    public void Dispose()
    {
        _stateSubscription?.Dispose();
        _errorNotificationSubscription?.Dispose();
        _errorSubject.OnCompleted();
        _errorSubject.Dispose();
        _commandLock.Dispose();
    }
}
