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

public sealed class SharedCardCanvasPreviewService : ISharedCardCanvasPreviewService, IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger? _logger;

    private MediaPlayerElement? _playerElement;
    private MediaPlayer? _currentPlayer;
    private Panel? _activeHost;
    private CanvasPreviewLease? _activeLease;
    private long _nextLeaseId;

    public SharedCardCanvasPreviewService(ILogger<SharedCardCanvasPreviewService>? logger = null)
    {
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    [Conditional("DEBUG")]
    private void TraceCanvas(string message)
    {
        Debug.WriteLine(
            $"[SharedCardCanvasPreviewService] {message} | " +
            $"activeLease={_activeLease?.Id.ToString() ?? "<null>"} " +
            $"activeHost={(_activeHost != null ? _activeHost.GetHashCode().ToString("x8") : "<null>")} " +
            $"hasElement={_playerElement != null}");
    }

    private int _initialized;

    // Eagerly create the shared MediaPlayerElement so the first hover doesn't pay
    // the 100–300ms MediaFoundation + DirectX surface setup cost on the UI thread.
    // Without this, the first pointer-exit while a preview is mid-start visibly
    // hangs the UI — the in-flight creation is still saturating the dispatcher
    // queue when the exit animations + teardown try to run. Subsequent exits are
    // fast because the element already exists.
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        try
        {
            await RunOnUiAsync(() =>
            {
                EnsurePlayerElementOnUi();
                TraceCanvas("EnsureInitializedAsync warmed player element");
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Warmup is best-effort. If it fails, AcquireOnUi will create the
            // element on first use (paying the original latency cost).
            Interlocked.Exchange(ref _initialized, 0);
            _logger?.LogDebug(ex, "Shared canvas preview warmup failed");
        }
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

        _activeLease = null;
        _activeHost = null;
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
        try
        {
            _gate.Wait();
            RunOnUiAsync(DisposePlayerElementOnUi, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch
            {
            }

            _gate.Dispose();
        }
    }
}
