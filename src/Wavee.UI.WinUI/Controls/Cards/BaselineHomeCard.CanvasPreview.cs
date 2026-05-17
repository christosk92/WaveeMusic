using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Canvas video preview lifecycle for <see cref="BaselineHomeCard"/>: shared-
/// service lease acquire / release, host realization, ready/measured gating,
/// retry-on-not-ready, and the version counter that suppresses out-of-order
/// completions.
///
/// <para>Kept inline (rather than extracted to a behavior) because the start
/// flow is tightly interleaved with hover state — every step re-checks
/// <c>_isPointerOver</c>, the preview-track URL, and the version counter to
/// abort cleanly when the user moves the pointer or navigates to another
/// preview track mid-acquire.</para>
/// </summary>
public sealed partial class BaselineHomeCard
{
    private CanvasPreviewLease? _canvasPreviewLease;
    private int _canvasPreviewVersion;
    private string? _activeCanvasUrl;

    private async Task StartCanvasPreviewAsync()
    {
        var canvasUrl = GetActiveCanvasUrl();
        if (string.IsNullOrWhiteSpace(canvasUrl))
        {
            TraceCard("StartCanvasPreviewAsync skipped: no canvas url");
            StopCanvasPreview();
            return;
        }

        try
        {
            var previewVersion = ++_canvasPreviewVersion;
            TraceCard($"StartCanvasPreviewAsync begin previewVersion={previewVersion}");

            EnsureCanvasPreviewHostRealized();
            var isCanvasHostReady = await EnsureCanvasPreviewHostReadyAsync();
            if (CanvasPreviewHost == null || !isCanvasHostReady)
            {
                TraceCard($"StartCanvasPreviewAsync host not ready previewVersion={previewVersion}");
                if (_isPointerOver &&
                    previewVersion == _canvasPreviewVersion &&
                    string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
                {
                    _ = RetryCanvasPreviewInitializationAsync(previewVersion, canvasUrl);
                }

                return;
            }

            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
                return;

            CanvasPreviewHost.Visibility = Visibility.Visible;
            CanvasPreviewHost.Opacity = 0;

            var isCanvasHostMeasured = await EnsureCanvasPreviewHostMeasuredAsync();
            if (CanvasPreviewHost == null || !isCanvasHostMeasured)
            {
                TraceCard($"StartCanvasPreviewAsync host not measured previewVersion={previewVersion}");
                if (_isPointerOver &&
                    previewVersion == _canvasPreviewVersion &&
                    string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
                {
                    _ = RetryCanvasPreviewInitializationAsync(previewVersion, canvasUrl);
                }

                return;
            }

            if (_sharedCanvasPreviewService == null)
                throw new InvalidOperationException("Shared canvas preview service is unavailable.");

            TraceCard($"StartCanvasPreviewAsync acquiring shared canvas preview previewVersion={previewVersion}");
            var lease = await _sharedCanvasPreviewService.AcquireAsync(CanvasPreviewHost, canvasUrl);
            if (lease == null)
            {
                TraceCard($"StartCanvasPreviewAsync acquire returned null previewVersion={previewVersion}");
                CanvasPreviewHost.Visibility = Visibility.Collapsed;
                CanvasPreviewHost.Opacity = 0;
                return;
            }

            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
            {
                await _sharedCanvasPreviewService.ReleaseAsync(lease);
                if (CanvasPreviewHost != null)
                {
                    CanvasPreviewHost.Visibility = Visibility.Collapsed;
                    CanvasPreviewHost.Opacity = 0;
                }
                return;
            }

            _canvasPreviewLease = lease;
            _activeCanvasUrl = canvasUrl;
            TraceCard($"StartCanvasPreviewAsync acquired lease={lease.Id} previewVersion={previewVersion}");

            // Set opacity directly (skip animation for now) to verify rendering works
            CanvasPreviewHost.Opacity = 1;

            System.Diagnostics.Debug.WriteLine(
                $"[BaselineHomeCard] CanvasHost opacity={CanvasPreviewHost.Opacity} " +
                $"vis={CanvasPreviewHost.Visibility} " +
                $"size={CanvasPreviewHost.ActualWidth:F0}x{CanvasPreviewHost.ActualHeight:F0} " +
                $"children={CanvasPreviewHost.Children.Count}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Canvas preview failed: {ex.Message}");
            StopCanvasPreview();
        }
    }

    private async Task RetryCanvasPreviewInitializationAsync(int previewVersion, string canvasUrl)
    {
        TraceCard($"RetryCanvasPreviewInitializationAsync scheduled previewVersion={previewVersion}");
        await Task.Delay(90);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(_activeCanvasUrl))
            {
                TraceCard($"RetryCanvasPreviewInitializationAsync aborted previewVersion={previewVersion}");
                return;
            }

            TraceCard($"RetryCanvasPreviewInitializationAsync restarting previewVersion={previewVersion}");
            _ = StartCanvasPreviewAsync();
        });
    }

    private async Task<bool> EnsureCanvasPreviewHostReadyAsync()
    {
        var host = CanvasPreviewHost;
        if (host == null)
            return false;

        if (host.IsLoaded)
            return true;

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            host.Loaded -= loadedHandler;
            loaded.TrySetResult();
        };

        host.Loaded += loadedHandler;
        await Task.WhenAny(loaded.Task, Task.Delay(220));

        if (loadedHandler != null)
            host.Loaded -= loadedHandler;

        return host.IsLoaded;
    }

    private async Task<bool> EnsureCanvasPreviewHostMeasuredAsync()
    {
        var host = CanvasPreviewHost;
        if (host == null)
            return false;

        if (host.ActualWidth > 0 && host.ActualHeight > 0)
            return true;

        await WaitForNextUiTickAsync();
        host.UpdateLayout();

        if (host.ActualWidth > 0 && host.ActualHeight > 0)
            return true;

        var sized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SizeChangedEventHandler? sizeChangedHandler = null;
        sizeChangedHandler = (_, args) =>
        {
            if (args.NewSize.Width <= 0 || args.NewSize.Height <= 0)
                return;

            host.SizeChanged -= sizeChangedHandler;
            sized.TrySetResult();
        };

        host.SizeChanged += sizeChangedHandler;
        await Task.WhenAny(sized.Task, Task.Delay(150));

        if (sizeChangedHandler != null)
            host.SizeChanged -= sizeChangedHandler;

        return host.ActualWidth > 0 && host.ActualHeight > 0;
    }

    private Task WaitForNextUiTickAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => tcs.TrySetResult()))
            tcs.TrySetResult();

        return tcs.Task;
    }

    private void StopCanvasPreview()
    {
        TraceCard("StopCanvasPreview");
        _canvasPreviewVersion++;
        _activeCanvasUrl = null;
        var lease = _canvasPreviewLease;
        _canvasPreviewLease = null;

        if (CanvasPreviewHost != null)
        {
            CanvasPreviewHost.Visibility = Visibility.Collapsed;
            CanvasPreviewHost.Opacity = 0;
        }

        if (_sharedCanvasPreviewService != null)
        {
            if (lease != null)
                _ = _sharedCanvasPreviewService.ReleaseAsync(lease);
            else if (CanvasPreviewHost != null)
                _ = _sharedCanvasPreviewService.ReleaseHostAsync(CanvasPreviewHost);
        }
    }
}
