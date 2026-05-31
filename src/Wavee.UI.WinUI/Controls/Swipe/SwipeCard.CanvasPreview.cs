using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Swipe;

/// <summary>
/// Canvas-as-card-background for <see cref="SwipeCard"/>. When the bound <see cref="CanvasUrl"/> is
/// set, the pooled <see cref="ISharedCardCanvasPreviewService"/> reparents its single muted/looping
/// <c>MediaPlayerElement</c> into <c>CanvasHost</c> (full-bleed, behind the art); the album art fades
/// out and re-appears as the small <c>ArtThumb</c> by the title. Mirrors the BaselineHomeCard lease
/// pattern — a version counter + post-await stale guards prevent fast swipes from leaking the shared
/// element. All mode changes are cross-faded.
/// </summary>
public sealed partial class SwipeCard
{
    private static readonly TimeSpan FadeCanvas = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan FadeArt = TimeSpan.FromMilliseconds(300);

    private readonly ISharedCardCanvasPreviewService? _canvasService;
    private CanvasPreviewLease? _canvasLease;
    private int _canvasVersion;

    public static readonly DependencyProperty CanvasUrlProperty = DependencyProperty.Register(
        nameof(CanvasUrl), typeof(string), typeof(SwipeCard), new PropertyMetadata(null, OnCanvasUrlChanged));
    public string? CanvasUrl { get => (string?)GetValue(CanvasUrlProperty); set => SetValue(CanvasUrlProperty, value); }

    private static void OnCanvasUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SwipeCard)d).ApplyCanvasState((string?)e.NewValue);

    private void ApplyCanvasState(string? url)
    {
        if (string.IsNullOrEmpty(url)) _ = EnterAlbumArtModeAsync();
        else _ = EnterCanvasModeAsync(url);
    }

    private async Task EnterCanvasModeAsync(string url)
    {
        var version = ++_canvasVersion;
        if (_canvasService is null) return;

        if (!CanvasHost.IsLoaded || CanvasHost.XamlRoot is null)
        {
            if (!await EnsureHostLoadedAsync()) return;
            if (version != _canvasVersion || !string.Equals(CanvasUrl, url, StringComparison.Ordinal)) return;
        }

        // CanvasHost is always visible (opacity 0 when idle) so it's measured to the card size before
        // AcquireAsync reparents the video into it — no realize/measure race.
        var lease = await _canvasService.AcquireAsync(CanvasHost, url);

        // Stale guard for fast swipes: a newer card/state took over while we awaited the acquire.
        if (lease is null || version != _canvasVersion || !string.Equals(CanvasUrl, url, StringComparison.Ordinal))
        {
            if (lease is not null) await _canvasService.ReleaseAsync(lease);
            return;
        }

        _canvasLease = lease;
        ShowCanvasVisuals();
    }

    private async Task EnterAlbumArtModeAsync()
    {
        _canvasVersion++;
        var lease = _canvasLease;
        _canvasLease = null;
        ArtThumb.Visibility = Visibility.Collapsed;   // collapse the column → full-width title (synchronous)
        AnimationBuilder.Create().Opacity(to: 1d, duration: FadeArt).Start(Art);
        AnimationBuilder.Create().Opacity(to: 0d, duration: FadeCanvas).Start(CanvasHost);
        if (_canvasService is not null)
        {
            if (lease is not null) await _canvasService.ReleaseAsync(lease);
            else await _canvasService.ReleaseHostAsync(CanvasHost);
        }
    }

    private void ShowCanvasVisuals()
    {
        // Make the thumb visible (measured) BEFORE setting the URL, then force a load — a
        // CompositionImage whose ImageUrl is assigned while Collapsed never realizes its surface.
        ArtThumb.Visibility = Visibility.Visible;
        ArtThumb.ImageUrl = ImageUrl;
        DispatcherQueue?.TryEnqueue(() => ArtThumb.RefreshCurrentImage());
        AnimationBuilder.Create().Opacity(to: 0d, duration: FadeArt).Start(Art);
        AnimationBuilder.Create().Opacity(to: 1d, duration: FadeCanvas).Start(CanvasHost);
    }

    /// <summary>Page calls this on navigate-away (the page is not host-cached) to stop the video + free the lease.</summary>
    public void ReleaseCanvas()
    {
        _canvasVersion++;
        var lease = _canvasLease;
        _canvasLease = null;
        if (_canvasService is null) return;
        if (lease is not null) _ = _canvasService.ReleaseAsync(lease);
        else _ = _canvasService.ReleaseHostAsync(CanvasHost);
    }

    private async Task<bool> EnsureHostLoadedAsync()
    {
        if (CanvasHost.IsLoaded) return true;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? handler = null;
        handler = (_, _) => { CanvasHost.Loaded -= handler; tcs.TrySetResult(); };
        CanvasHost.Loaded += handler;
        await Task.WhenAny(tcs.Task, Task.Delay(220));
        CanvasHost.Loaded -= handler;
        return CanvasHost.IsLoaded;
    }
}
