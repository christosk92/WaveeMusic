using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Services;

public sealed partial class SharedCardCanvasPreviewService : ISharedCardCanvasPreviewService, IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger? _logger;

    private MediaPlayerElement? _playerElement;
    private MediaPlayer? _currentPlayer;
    private Panel? _activeHost;
    private CanvasPreviewLease? _activeLease;
    private long _nextLeaseId;
    private DispatcherQueueTimer? _idleTeardownTimer;

    // Reclaim the shared player's MediaFoundation + GPU video decode surface
    // (~25-40 MB) when no card has been previewed for this long. The keep-lease
    // resume path otherwise holds that surface for the rest of the session after
    // any hover; the next hover after idle re-creates the element.
    private static readonly TimeSpan IdleTeardownDelay = TimeSpan.FromSeconds(90);

    public SharedCardCanvasPreviewService(ILogger<SharedCardCanvasPreviewService>? logger = null)
    {
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    [Conditional("DEBUG")]
    private void TraceCanvas(string message)
    {
        if (!Wavee.UI.Diagnostics.UiTrace.Verbose) return;
        Debug.WriteLine(
            $"[SharedCardCanvasPreviewService] {message} | " +
            $"activeLease={_activeLease?.Id.ToString() ?? "<null>"} " +
            $"activeHost={(_activeHost != null ? _activeHost.GetHashCode().ToString("x8") : "<null>")} " +
            $"hasElement={_playerElement != null}");
    }


    public async Task<CanvasPreviewLease?> AcquireAsync(Panel host, string canvasUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (string.IsNullOrWhiteSpace(canvasUrl))
            return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunOnUiAsync(() => AcquireOnUi(host, canvasUrl), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(CanvasPreviewLease? lease, CancellationToken ct = default)
    {
        if (lease == null)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunOnUiAsync(() =>
            {
                if (_activeLease?.Id != lease.Id)
                    return;

                TeardownOnUi();
                RestartIdleTeardownTimer();
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseHostAsync(Panel host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunOnUiAsync(() =>
            {
                if (!ReferenceEquals(_activeHost, host))
                    return;

                TeardownOnUi();
                RestartIdleTeardownTimer();
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private CanvasPreviewLease? AcquireOnUi(Panel host, string canvasUrl)
    {
        TraceCanvas($"AcquireOnUi host={host.GetHashCode():x8} url='{canvasUrl}'");
        if (!host.IsLoaded || host.XamlRoot == null)
        {
            TraceCanvas($"AcquireOnUi host not ready");
            return null;
        }

        // Activity — cancel any pending idle teardown of the shared player.
        StopIdleTeardownTimer();

        // Same host + same URL + element still parented → just resume
        if (_activeLease != null &&
            ReferenceEquals(_activeHost, host) &&
            string.Equals(_activeLease.CanvasUrl, canvasUrl, StringComparison.Ordinal) &&
            _playerElement?.Parent != null)
        {
            _currentPlayer?.Play();
            TraceCanvas("AcquireOnUi resumed existing");
            return _activeLease;
        }

        var lease = new CanvasPreviewLease(Interlocked.Increment(ref _nextLeaseId), host, canvasUrl);

        try
        {
            EnsurePlayerElementOnUi();

            if (_playerElement?.Parent is Panel currentParent && !ReferenceEquals(currentParent, host))
                currentParent.Children.Remove(_playerElement);

            if (!ReferenceEquals(_playerElement?.Parent, host))
                host.Children.Insert(0, _playerElement!);

            var shouldReloadSource =
                !string.Equals(_activeLease?.CanvasUrl, canvasUrl, StringComparison.Ordinal) ||
                _playerElement?.Source == null;
            if (shouldReloadSource && _playerElement != null)
                _playerElement.Source = MediaSource.CreateFromUri(new Uri(canvasUrl));

            _currentPlayer?.Play();

            _activeHost = host;
            _activeLease = lease;

            Debug.WriteLine(
                $"[SharedCanvasPreview] ACQUIRE lease={lease.Id} " +
                $"playerState={_currentPlayer?.PlaybackSession?.PlaybackState} " +
                $"elementInTree={_playerElement.Parent != null} " +
                $"elementPlayer={_playerElement.MediaPlayer != null} " +
                $"hostSize={host.ActualWidth:F0}x{host.ActualHeight:F0}");

            return lease;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Shared card canvas preview acquire failed for {Url}", canvasUrl);
            TeardownOnUi();
            return null;
        }
    }

    private void TeardownOnUi()
    {
        TraceCanvas("TeardownOnUi");

        if (_currentPlayer != null)
        {
            try
            {
                _currentPlayer.Pause();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.LogDebug(ex, "Failed to stop canvas preview player");
            }
        }

        // Intentionally DO NOT:
        // - null _playerElement.Source: the setter synchronously unwinds the
        //   MediaFoundation source reader on the UI thread (50–200ms stall).
        // - remove _playerElement from its current Panel: reparenting is also
        //   a UI-thread cost, and the host's Visibility is Collapsed by the
        //   caller (BaselineHomeCard.StopCanvasPreview), so the element is
        //   already invisible. If a different card Acquires next, AcquireOnUi
        //   reparents in one step. If the same card re-hovers, nothing to do.
        // Only one MediaPlayerElement exists per app, so leaving it parented
        // to the last host is safe — the host (a Panel inside a realized card)
        // holds a single reference and will drop it if the card is unrealized.
        // (Do NOT Dispose the internal MediaPlayer either — it's owned by the
        //  element and disposing externally crashes the renderer.)
        //
        // Deliberately KEEP _activeLease / _activeHost. Nulling them here defeated
        // AcquireOnUi's resume fast-path, so re-hovering the SAME card recreated
        // the MediaSource (100–300ms MediaFoundation init) on every exit→re-enter.
        // Leaving them set lets a re-hover just resume the paused player; a
        // different host or URL overwrites them in AcquireOnUi.
    }

    private void EnsurePlayerElementOnUi()
    {
        if (_playerElement != null && _currentPlayer != null)
            return;

        _playerElement = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            AutoPlay = true,
            IsHitTestVisible = false,
            Stretch = Stretch.UniformToFill
        };

        _currentPlayer = _playerElement.MediaPlayer;
        if (_currentPlayer == null)
            return;

        _currentPlayer.IsLoopingEnabled = true;
        _currentPlayer.IsMuted = true;
        _currentPlayer.MediaOpened += OnMediaPlayerMediaOpened;
        _currentPlayer.MediaFailed += OnMediaPlayerMediaFailed;
        _currentPlayer.CurrentStateChanged += OnMediaPlayerCurrentStateChanged;
    }

    private void DisposePlayerElementOnUi()
    {
        StopIdleTeardownTimer();
        TeardownOnUi();

        if (_currentPlayer != null)
        {
            try
            {
                _currentPlayer.MediaOpened -= OnMediaPlayerMediaOpened;
                _currentPlayer.MediaFailed -= OnMediaPlayerMediaFailed;
                _currentPlayer.CurrentStateChanged -= OnMediaPlayerCurrentStateChanged;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.LogDebug(ex, "Failed to detach canvas preview player handlers");
            }

            _currentPlayer = null;
        }

        _playerElement = null;
    }

    // Idle teardown (UI thread). Started when a preview is released, cancelled
    // when a new one is acquired; on fire it fully disposes the shared element so
    // its video surface stops being a permanent baseline cost.
    private void RestartIdleTeardownTimer()
    {
        _idleTeardownTimer ??= CreateIdleTeardownTimer();
        _idleTeardownTimer.Stop();
        _idleTeardownTimer.Start();
    }

    private void StopIdleTeardownTimer() => _idleTeardownTimer?.Stop();

    private DispatcherQueueTimer CreateIdleTeardownTimer()
    {
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = IdleTeardownDelay;
        timer.IsRepeating = false;
        timer.Tick += OnIdleTeardownTick;
        return timer;
    }

    private void OnIdleTeardownTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_playerElement is null)
            return;
        TraceCanvas("idle teardown - releasing shared player element");
        DisposePlayerElementOnUi();
    }

    private void OnMediaPlayerMediaOpened(MediaPlayer sender, object args)
    {
        Debug.WriteLine(
            $"[SharedCanvasPreview] MediaOpened " +
            $"state={sender.PlaybackSession?.PlaybackState} " +
            $"naturalW={sender.PlaybackSession?.NaturalVideoWidth} " +
            $"naturalH={sender.PlaybackSession?.NaturalVideoHeight}");
    }

    private void OnMediaPlayerMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Debug.WriteLine(
            $"[SharedCanvasPreview] MediaFailed " +
            $"error={args.Error} hresult=0x{args.ExtendedErrorCode.HResult:x8} " +
            $"msg='{args.ErrorMessage}'");
    }

    private void OnMediaPlayerCurrentStateChanged(MediaPlayer sender, object args)
    {
        Debug.WriteLine($"[SharedCanvasPreview] StateChanged state={sender.CurrentState}");
    }

    private Task RunOnUiAsync(Action action, CancellationToken ct)
        => RunOnUiAsync<object?>(() =>
        {
            action();
            return null;
        }, ct);

    private Task<T> RunOnUiAsync<T>(Func<T> action, CancellationToken ct)
    {
        if (_dispatcherQueue.HasThreadAccess)
            return Task.FromResult(action());

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        if (ct.CanBeCanceled)
        {
            registration = ct.Register(() => tcs.TrySetCanceled(ct));
        }

        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                registration.Dispose();
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            registration.Dispose();
            tcs.TrySetException(new InvalidOperationException("Failed to enqueue shared canvas preview work."));
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        // Never block the caller (often the UI thread): a sync Wait() on _gate
        // combined with GetAwaiter().GetResult() on a UI dispatch would deadlock
        // when the gate is held by an in-flight Acquire/Release that is itself
        // waiting on the UI dispatcher.
        var gateAcquired = _gate.Wait(0);
        try
        {
            if (_dispatcherQueue.HasThreadAccess)
            {
                DisposePlayerElementOnUi();
            }
            else
            {
                _dispatcherQueue.TryEnqueue(DisposePlayerElementOnUi);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Shared canvas preview dispose dispatch failed");
        }
        finally
        {
            if (gateAcquired)
            {
                try { _gate.Release(); } catch { }
            }
            _gate.Dispose();
        }
    }
}