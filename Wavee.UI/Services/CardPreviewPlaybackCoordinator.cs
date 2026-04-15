using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Playback.Contracts;
using Wavee.UI.Contracts;
using Wavee.UI.Threading;

namespace Wavee.UI.Services;

public sealed class CardPreviewPlaybackCoordinator : ICardPreviewPlaybackCoordinator, IDisposable
{
    private const int PreviewDuckTargetVolumePercent = 15;
    private const int FadeSteps = 4;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, OwnerRegistration> _owners = [];
    private readonly IPreviewAudioPlaybackEngine _engine;
    private readonly IPlaybackService? _playbackService;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly IUiDispatcher? _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly TimeSpan _hoverDelay;
    private readonly TimeSpan _completionRestoreGraceDelay;
    private readonly TimeSpan _duckFadeDuration;
    private readonly TimeSpan _restoreFadeDuration;

    private CancellationTokenSource? _pendingHoverCts;
    private CancellationTokenSource? _completionRestoreCts;
    private CancellationTokenSource? _activeStartCts;
    private Guid? _pendingOwnerId;
    private int _pendingOwnerVersion;
    private Guid? _activeOwnerId;
    private int _activeOwnerVersion;
    private int _activeSessionVersion;
    private bool _isDucked;
    private int _duckVersion;
    private int _restoreVolumePercent = 100;
    private bool _hasRestoreVolume;
    private bool _isDisposed;
    private bool _isApplyingPreviewVolume;
    private int? _lastRequestedPreviewVolume;

    public CardPreviewPlaybackCoordinator(
        IPreviewAudioPlaybackEngine engine,
        IPlaybackService? playbackService = null,
        IPlaybackStateService? playbackStateService = null,
        TimeProvider? timeProvider = null,
        IUiDispatcher? dispatcher = null,
        ILogger<CardPreviewPlaybackCoordinator>? logger = null,
        TimeSpan? hoverDelay = null,
        TimeSpan? completionRestoreGraceDelay = null,
        TimeSpan? duckFadeDuration = null,
        TimeSpan? restoreFadeDuration = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _playbackService = playbackService;
        _playbackStateService = playbackStateService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dispatcher = dispatcher;
        _logger = logger;
        _hoverDelay = hoverDelay ?? TimeSpan.FromMilliseconds(1000);
        _completionRestoreGraceDelay = completionRestoreGraceDelay ?? TimeSpan.FromMilliseconds(350);
        _duckFadeDuration = duckFadeDuration ?? TimeSpan.FromMilliseconds(120);
        _restoreFadeDuration = restoreFadeDuration ?? TimeSpan.FromMilliseconds(160);

        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged += OnPlaybackStatePropertyChanged;
    }

    [Conditional("DEBUG")]
    private void TraceCoordinator(string message)
    {
        Debug.WriteLine(
            $"[CardPreviewPlaybackCoordinator] {message} | " +
            $"pendingOwner={_pendingOwnerId?.ToString() ?? "<null>"} pendingVersion={_pendingOwnerVersion} " +
            $"activeOwner={_activeOwnerId?.ToString() ?? "<null>"} activeVersion={_activeOwnerVersion} " +
            $"sessionVersion={_activeSessionVersion} ducked={_isDucked}");
    }

    public async Task ScheduleHover(CardPreviewRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        CancelInFlightStart();
        TraceCoordinator($"ScheduleHover owner={request.OwnerId} url='{request.PreviewUrl}'");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var owner = GetOrCreateOwnerRegistration(request.OwnerId);
            if (_activeOwnerId == request.OwnerId &&
                owner.Request != null &&
                string.Equals(owner.Request.PreviewUrl, request.PreviewUrl, StringComparison.Ordinal))
            {
                return;
            }

            if (_pendingOwnerId == request.OwnerId &&
                owner.Request != null &&
                string.Equals(owner.Request.PreviewUrl, request.PreviewUrl, StringComparison.Ordinal))
            {
                return;
            }

            owner.Version++;
            owner.Request = request;

            CancelCompletionRestore_NoLock();
            CancelPendingHover_NoLock();

            var pendingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pendingHoverCts = pendingCts;
            _pendingOwnerId = request.OwnerId;
            _pendingOwnerVersion = owner.Version;

            DispatchState(request, new CardPreviewPlaybackState(
                IsPending: true,
                IsPlaying: false,
                HasVisualization: false,
                SessionId: null));

            TraceCoordinator($"ScheduleHover queued owner={request.OwnerId} ownerVersion={owner.Version}");
            _ = RunScheduledHoverAsync(request.OwnerId, owner.Version, pendingCts.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartImmediate(CardPreviewRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        CancelInFlightStart();
        TraceCoordinator($"StartImmediate owner={request.OwnerId} url='{request.PreviewUrl}'");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var owner = GetOrCreateOwnerRegistration(request.OwnerId);
            owner.Version++;
            owner.Request = request;

            CancelCompletionRestore_NoLock();
            CancelPendingHover_NoLock();

            await StartRequestCoreAsync(request.OwnerId, owner.Version, keepDuckBetweenSessions: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelOwner(Guid ownerId, CancellationToken ct = default)
    {
        CancelInFlightStart();
        TraceCoordinator($"CancelOwner owner={ownerId}");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_isDisposed)
                return;

            if (!_owners.TryGetValue(ownerId, out var owner))
                return;

            owner.Version++;
            CancelCompletionRestore_NoLock();

            if (_pendingOwnerId == ownerId)
            {
                CancelPendingHover_NoLock();
                if (owner.Request != null)
                {
                    DispatchState(owner.Request, new CardPreviewPlaybackState(
                        IsPending: false,
                        IsPlaying: false,
                        HasVisualization: false,
                        SessionId: null));
                }
            }

            if (_activeOwnerId == ownerId)
                await StopActivePreviewCoreAsync(restorePlayback: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnregisterOwner(Guid ownerId, CancellationToken ct = default)
    {
        CancelInFlightStart();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_isDisposed)
                return;

            if (_owners.TryGetValue(ownerId, out var owner))
                owner.Version++;

            CancelCompletionRestore_NoLock();

            if (_pendingOwnerId == ownerId)
                CancelPendingHover_NoLock();

            if (_activeOwnerId == ownerId)
                await StopActivePreviewCoreAsync(restorePlayback: true, notifyState: false).ConfigureAwait(false);

            _owners.Remove(ownerId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunScheduledHoverAsync(Guid ownerId, int ownerVersion, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_hoverDelay, _timeProvider, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            TraceCoordinator($"RunScheduledHoverAsync fired owner={ownerId} ownerVersion={ownerVersion}");
            if (_isDisposed ||
                _pendingOwnerId != ownerId ||
                _pendingOwnerVersion != ownerVersion ||
                !_owners.TryGetValue(ownerId, out var owner) ||
                owner.Version != ownerVersion)
            {
                TraceCoordinator($"RunScheduledHoverAsync ignored owner={ownerId} ownerVersion={ownerVersion}");
                return;
            }

            CancelPendingHover_NoLock();
            await StartRequestCoreAsync(ownerId, ownerVersion, keepDuckBetweenSessions: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartRequestCoreAsync(Guid ownerId, int ownerVersion, bool keepDuckBetweenSessions, CancellationToken ct)
    {
        TraceCoordinator($"StartRequestCoreAsync owner={ownerId} ownerVersion={ownerVersion} keepDuckBetweenSessions={keepDuckBetweenSessions}");
        if (!_owners.TryGetValue(ownerId, out var owner) || owner.Version != ownerVersion || owner.Request == null)
            return;

        if (string.IsNullOrWhiteSpace(owner.Request.PreviewUrl))
        {
            DispatchState(owner.Request, new CardPreviewPlaybackState(
                IsPending: false,
                IsPlaying: false,
                HasVisualization: false,
                SessionId: null));
            return;
        }

        if (!await CanStartRequestPlaybackAsync(owner.Request).ConfigureAwait(false))
        {
            TraceCoordinator($"StartRequestCoreAsync blocked by CanStartPlayback owner={ownerId} ownerVersion={ownerVersion}");
            DispatchState(owner.Request, new CardPreviewPlaybackState(
                IsPending: false,
                IsPlaying: false,
                HasVisualization: false,
                SessionId: null));
            return;
        }

        if (_activeOwnerId.HasValue)
            await StopActivePreviewCoreAsync(restorePlayback: !keepDuckBetweenSessions).ConfigureAwait(false);

        if (!_isDucked)
            await EnsurePreviewDuckingAsync(ct).ConfigureAwait(false);

        var request = owner.Request;
        var sessionVersion = ++_activeSessionVersion;
        _activeOwnerId = ownerId;
        _activeOwnerVersion = ownerVersion;

        CancellationTokenSource? startCts = null;
        try
        {
            startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeStartCts = startCts;

            var startResult = await _engine.StartAsync(
                request.PreviewUrl,
                frame => HandlePreviewFrame(ownerId, ownerVersion, sessionVersion, request, frame),
                () => HandlePreviewCompleted(ownerId, ownerVersion, sessionVersion, request),
                startCts.Token).ConfigureAwait(false);

            if (_activeOwnerId != ownerId ||
                _activeOwnerVersion != ownerVersion ||
                sessionVersion != _activeSessionVersion)
            {
                return;
            }

            DispatchState(request, new CardPreviewPlaybackState(
                IsPending: false,
                IsPlaying: true,
                HasVisualization: startResult.HasVisualization,
                SessionId: startResult.SessionId));
            TraceCoordinator($"StartRequestCoreAsync started owner={ownerId} sessionVersion={sessionVersion} hasViz={startResult.HasVisualization} session='{startResult.SessionId ?? "<null>"}'");
        }
        catch (OperationCanceledException)
        {
            TraceCoordinator($"StartRequestCoreAsync canceled owner={ownerId} ownerVersion={ownerVersion}");
            if (_activeOwnerId == ownerId && _activeOwnerVersion == ownerVersion)
            {
                _activeOwnerId = null;
                _activeOwnerVersion = 0;
                _activeSessionVersion++;
            }

            // Propagate cancellation to the engine so any partially-acquired resources
            // (network streams, audio graph nodes, etc.) are released. StopAsync is
            // expected to be idempotent and safe to call even when the engine never
            // finished starting.
            try
            {
                await _engine.StopAsync().ConfigureAwait(false);
            }
            catch (Exception stopEx)
            {
                _logger?.LogDebug(stopEx, "Engine stop after start-cancel failed for owner {OwnerId}", ownerId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Card preview start failed for owner {OwnerId}", ownerId);

            if (_activeOwnerId == ownerId && _activeOwnerVersion == ownerVersion)
            {
                _activeOwnerId = null;
                _activeOwnerVersion = 0;
                _activeSessionVersion++;
            }

            DispatchState(request, new CardPreviewPlaybackState(
                IsPending: false,
                IsPlaying: false,
                HasVisualization: false,
                SessionId: null));

            await RestorePlaybackIfIdleAsync().ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_activeStartCts, startCts))
                _activeStartCts = null;

            startCts?.Dispose();
        }
    }

    private Task<bool> CanStartRequestPlaybackAsync(CardPreviewRequest request)
    {
        if (request.CanStartPlayback == null)
            return Task.FromResult(true);

        if (_dispatcher == null || _dispatcher.HasThreadAccess)
            return Task.FromResult(request.CanStartPlayback());

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    tcs.TrySetResult(request.CanStartPlayback());
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Card preview CanStartPlayback callback failed");
                    tcs.TrySetResult(false);
                }
            }))
        {
            try
            {
                tcs.TrySetResult(request.CanStartPlayback());
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Card preview CanStartPlayback callback failed");
                tcs.TrySetResult(false);
            }
        }

        return tcs.Task;
    }

    private void HandlePreviewFrame(
        Guid ownerId,
        int ownerVersion,
        int sessionVersion,
        CardPreviewRequest request,
        PreviewVisualizationFrame frame)
    {
        if (_activeOwnerId != ownerId ||
            _activeOwnerVersion != ownerVersion ||
            _activeSessionVersion != sessionVersion)
        {
            return;
        }

        Dispatch(() => request.OnFrame(frame));
    }

    private void HandlePreviewCompleted(Guid ownerId, int ownerVersion, int sessionVersion, CardPreviewRequest request)
    {
        _ = HandlePreviewCompletedAsync(ownerId, ownerVersion, sessionVersion, request);
    }

    private async Task HandlePreviewCompletedAsync(
        Guid ownerId,
        int ownerVersion,
        int sessionVersion,
        CardPreviewRequest request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed ||
                _activeOwnerId != ownerId ||
                _activeOwnerVersion != ownerVersion ||
                _activeSessionVersion != sessionVersion)
            {
                return;
            }

            _activeOwnerId = null;
            _activeOwnerVersion = 0;
            _activeSessionVersion++;

            DispatchState(request, new CardPreviewPlaybackState(
                IsPending: false,
                IsPlaying: false,
                HasVisualization: false,
                SessionId: null));

            CancelCompletionRestore_NoLock();
            _completionRestoreCts = new CancellationTokenSource();
            _ = RunCompletionRestoreAsync(_completionRestoreCts.Token);
        }
        finally
        {
            _gate.Release();
        }

        Dispatch(request.OnCompleted);
    }

    private async Task RunCompletionRestoreAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_completionRestoreGraceDelay, _timeProvider, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || ct.IsCancellationRequested)
                return;

            if (_activeOwnerId != null || _pendingOwnerId != null)
                return;

            await RestorePlaybackIfIdleAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopActivePreviewCoreAsync(bool restorePlayback, bool notifyState = true)
    {
        TraceCoordinator($"StopActivePreviewCoreAsync restorePlayback={restorePlayback} notifyState={notifyState}");
        if (_activeOwnerId is not Guid ownerId ||
            !_owners.TryGetValue(ownerId, out var owner) ||
            owner.Request == null)
        {
            if (restorePlayback)
                await RestorePlaybackIfIdleAsync().ConfigureAwait(false);
            return;
        }

        var request = owner.Request;
        _activeOwnerId = null;
        _activeOwnerVersion = 0;
        _activeSessionVersion++;

        try
        {
            await _engine.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Failed to stop active card preview");
        }

        if (notifyState)
        {
            DispatchState(request, new CardPreviewPlaybackState(
                IsPending: false,
                IsPlaying: false,
                HasVisualization: false,
                SessionId: null));
        }

        if (restorePlayback)
            await RestorePlaybackIfIdleAsync().ConfigureAwait(false);
    }

    private async Task EnsurePreviewDuckingAsync(CancellationToken ct)
    {
        if (_playbackService == null ||
            _playbackStateService == null ||
            _isDucked ||
            !CanDuckPlayback())
        {
            return;
        }

        _restoreVolumePercent = ClampVolumePercent(_playbackStateService.Volume);
        _hasRestoreVolume = true;
        _isDucked = true;
        var duckVersion = ++_duckVersion;

        await FadeVolumeAsync(
            from: _restoreVolumePercent,
            to: PreviewDuckTargetVolumePercent,
            duration: _duckFadeDuration,
            duckVersion: duckVersion,
            ct).ConfigureAwait(false);
    }

    private async Task RestorePlaybackIfIdleAsync()
    {
        if (_activeOwnerId != null || _pendingOwnerId != null || !_isDucked)
            return;

        if (_playbackService == null || _playbackStateService == null || !_hasRestoreVolume)
        {
            ClearDuckState_NoLock();
            return;
        }

        if (!CanRestorePlayback())
        {
            ClearDuckState_NoLock();
            return;
        }

        var restoreVolume = _restoreVolumePercent;
        var duckVersion = ++_duckVersion;
        _isDucked = false;
        _hasRestoreVolume = false;

        await FadeVolumeAsync(
            from: ClampVolumePercent(_playbackStateService.Volume),
            to: restoreVolume,
            duration: _restoreFadeDuration,
            duckVersion: duckVersion,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FadeVolumeAsync(int from, int to, TimeSpan duration, int duckVersion, CancellationToken ct)
    {
        from = Math.Clamp(from, 0, 100);
        to = Math.Clamp(to, 0, 100);

        if (from == to)
        {
            await SetPreviewVolumeAsync(to, ct).ConfigureAwait(false);
            return;
        }

        var stepDelay = duration <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(duration.TotalMilliseconds / FadeSteps);

        for (var step = 1; step <= FadeSteps; step++)
        {
            if (duckVersion != _duckVersion || ct.IsCancellationRequested)
                return;

            var progress = step / (double)FadeSteps;
            var eased = 1 - Math.Pow(1 - progress, 3);
            var current = (int)Math.Round(from + ((to - from) * eased));
            await SetPreviewVolumeAsync(current, ct).ConfigureAwait(false);

            if (step < FadeSteps && stepDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(stepDelay, _timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task SetPreviewVolumeAsync(int volumePercent, CancellationToken ct)
    {
        if (_playbackService == null)
            return;

        volumePercent = Math.Clamp(volumePercent, 0, 100);
        _isApplyingPreviewVolume = true;
        _lastRequestedPreviewVolume = volumePercent;
        try
        {
            await _playbackService.SetVolumeAsync(volumePercent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Failed to set preview duck volume to {Volume}", volumePercent);
        }
        finally
        {
            _isApplyingPreviewVolume = false;
        }
    }

    private void OnPlaybackStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isDucked || _playbackStateService == null)
            return;

        if (e.PropertyName is nameof(IPlaybackStateService.IsPlaying) or
            nameof(IPlaybackStateService.IsPlayingRemotely) or
            nameof(IPlaybackStateService.IsVolumeRestricted))
        {
            _ = HandlePlaybackCapabilityChangedAsync();
            return;
        }

        if (e.PropertyName == nameof(IPlaybackStateService.Volume))
            _ = HandlePlaybackVolumeChangedAsync();
    }

    private async Task HandlePlaybackCapabilityChangedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || !_isDucked || CanDuckPlayback())
                return;

            ClearDuckState_NoLock();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandlePlaybackVolumeChangedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || !_isDucked || _playbackStateService == null)
                return;

            var currentVolume = ClampVolumePercent(_playbackStateService.Volume);
            if (_lastRequestedPreviewVolume.HasValue &&
                Math.Abs(currentVolume - _lastRequestedPreviewVolume.Value) <= 1)
            {
                return;
            }

            if (!CanDuckPlayback())
            {
                ClearDuckState_NoLock();
                return;
            }

            _restoreVolumePercent = currentVolume;
            _hasRestoreVolume = true;
            await SetPreviewVolumeAsync(PreviewDuckTargetVolumePercent, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DispatchState(CardPreviewRequest request, CardPreviewPlaybackState state)
    {
        Dispatch(() => request.OnStateChanged(state));
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher == null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Card preview callback failed");
                }
            }))
        {
            action();
        }
    }

    private OwnerRegistration GetOrCreateOwnerRegistration(Guid ownerId)
    {
        if (_owners.TryGetValue(ownerId, out var owner))
            return owner;

        owner = new OwnerRegistration();
        _owners[ownerId] = owner;
        return owner;
    }

    private void CancelPendingHover_NoLock()
    {
        try
        {
            _pendingHoverCts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            _pendingHoverCts?.Dispose();
            _pendingHoverCts = null;
            _pendingOwnerId = null;
            _pendingOwnerVersion = 0;
        }
    }

    private void CancelCompletionRestore_NoLock()
    {
        try
        {
            _completionRestoreCts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            _completionRestoreCts?.Dispose();
            _completionRestoreCts = null;
        }
    }

    private void CancelInFlightStart()
    {
        try
        {
            _activeStartCts?.Cancel();
        }
        catch
        {
        }
    }

    private bool CanDuckPlayback()
    {
        return _playbackStateService != null &&
               _playbackStateService.IsPlaying &&
               !_playbackStateService.IsPlayingRemotely &&
               !_playbackStateService.IsVolumeRestricted;
    }

    private bool CanRestorePlayback()
    {
        return _playbackStateService != null &&
               !_playbackStateService.IsPlayingRemotely &&
               !_playbackStateService.IsVolumeRestricted &&
               _playbackStateService.IsPlaying;
    }

    private void ClearDuckState_NoLock()
    {
        _isDucked = false;
        _hasRestoreVolume = false;
        _restoreVolumePercent = 100;
        _lastRequestedPreviewVolume = null;
        _duckVersion++;
    }

    private static int ClampVolumePercent(double volume)
    {
        return (int)Math.Round(Math.Clamp(volume, 0, 100));
    }

    private static void ValidateRequest(CardPreviewRequest request)
    {
        if (request.OwnerId == Guid.Empty)
            throw new ArgumentException("Preview owner id must be set.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.PreviewUrl))
            throw new ArgumentException("Preview URL must be set.", nameof(request));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged -= OnPlaybackStatePropertyChanged;

        CancelPendingHover_NoLock();
        CancelCompletionRestore_NoLock();

        try
        {
            _engine.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _gate.Dispose();
    }

    private sealed class OwnerRegistration
    {
        public int Version { get; set; }
        public CardPreviewRequest? Request { get; set; }
    }
}
