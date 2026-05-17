using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Multi-preview-track navigation for <see cref="BaselineHomeCard"/>: prev/next
/// button handlers, the queued-delta transition state machine that collapses
/// rapid clicks into a single in-flight animation, the in/out animations on
/// the four motion hosts (Hero, CoverThumb, TitleOverlay, BottomContent), and
/// the accessors that resolve the "active" preview track / audio URL /
/// canvas URL / hero image URL from the current track index.
///
/// <para>Animations use <c>AnimationBuilder</c> on individual UI elements
/// instead of a single Storyboard so each element starts from its current
/// composition state — important when a transition is cancelled mid-flight
/// and a fresh transition starts immediately.</para>
/// </summary>
public sealed partial class BaselineHomeCard
{
    private const double PreviewTransitionDistance = 24d;

    private static readonly TimeSpan PreviewTransitionOutDuration = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan PreviewTransitionInDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan PreviewMotionResetDuration = TimeSpan.FromMilliseconds(1);

    private int _previewTrackIndex;
    private bool _isPreviewTransitioning;
    private int? _queuedPreviewDelta;
    private int _previewTransitionVersion;

    // ── Click handlers ───────────────────────────────────────────────────────

    private async void PreviousPreviewTrackButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangePreviewTrackAsync(-1);
    }

    private async void NextPreviewTrackButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangePreviewTrackAsync(1);
    }

    // ── Transition state machine ─────────────────────────────────────────────

    private async Task ChangePreviewTrackAsync(int delta)
    {
        delta = Math.Sign(delta);
        if (delta == 0)
            return;

        var item = Item;
        if (item == null || item.PreviewTracks.Count <= 1)
            return;

        if (_isPreviewTransitioning)
        {
            _queuedPreviewDelta = delta;
            return;
        }

        _isPreviewTransitioning = true;
        var version = ++_previewTransitionVersion;

        try
        {
            var wasPreviewAudioPlaying = _isPreviewAudioPlaying;

            await AnimatePreviewOutAsync(delta, version);
            if (!IsPreviewTransitionCurrent(version))
                return;

            var shouldRestartPreviewAudio = ApplyPreviewTrackChange(delta, keepPreviewAudioPlaying: wasPreviewAudioPlaying);
            if (shouldRestartPreviewAudio && IsPreviewTransitionCurrent(version))
                _ = StartPreviewAudioAsync();

            await AnimatePreviewInAsync(delta, version);
            if (IsPreviewTransitionCurrent(version))
                ResetPreviewMotionHosts();
        }
        finally
        {
            if (version == _previewTransitionVersion)
                _isPreviewTransitioning = false;
        }

        if (version == _previewTransitionVersion)
            await RunQueuedPreviewTransitionAsync();
    }

    private bool ApplyPreviewTrackChange(int delta, bool keepPreviewAudioPlaying)
    {
        var item = Item;
        if (item == null || item.PreviewTracks.Count <= 1)
            return false;

        _previewTrackIndex = (_previewTrackIndex + delta + item.PreviewTracks.Count) % item.PreviewTracks.Count;
        var shouldRestartPreviewAudio =
            keepPreviewAudioPlaying &&
            !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl(item, GetActivePreviewTrack(item)));

        StopCanvasPreview();
        StopPreviewVisualization();
        if (!shouldRestartPreviewAudio)
            StopPreviewAudio();

        UpdateFromItem();
        return shouldRestartPreviewAudio;
    }

    private async Task RunQueuedPreviewTransitionAsync()
    {
        var queuedDelta = _queuedPreviewDelta;
        _queuedPreviewDelta = null;

        if (queuedDelta == null || !_isPointerOver || !IsLoaded)
            return;

        await ChangePreviewTrackAsync(queuedDelta.Value);
    }

    // ── Animations ───────────────────────────────────────────────────────────

    private async Task AnimatePreviewOutAsync(int direction, int version)
    {
        var targetOffset = direction > 0 ? -PreviewTransitionDistance : PreviewTransitionDistance;

        await Task.WhenAll(
            AnimatePreviewElementOutAsync(HeroMotionHost, targetOffset, scaleTo: 1f),
            AnimatePreviewElementOutAsync(CoverThumbBorder, targetOffset, scaleTo: 0.97f),
            AnimatePreviewElementOutAsync(TitleOverlay, targetOffset, scaleTo: 1f),
            AnimatePreviewElementOutAsync(BottomContentMotionHost, targetOffset, scaleTo: 1f));

        if (!IsPreviewTransitionCurrent(version) && IsLoaded)
            ResetPreviewMotionHosts();
    }

    private async Task AnimatePreviewInAsync(int direction, int version)
    {
        var startOffset = direction > 0 ? PreviewTransitionDistance : -PreviewTransitionDistance;
        PreparePreviewMotionForIncoming(startOffset);

        if (!IsPreviewTransitionCurrent(version))
        {
            if (IsLoaded)
                ResetPreviewMotionHosts();
            return;
        }

        await Task.WhenAll(
            AnimatePreviewElementInAsync(HeroMotionHost, startOffset, scaleFrom: 1f),
            AnimatePreviewElementInAsync(CoverThumbBorder, startOffset, scaleFrom: 0.97f),
            AnimatePreviewElementInAsync(TitleOverlay, startOffset, scaleFrom: 1f),
            AnimatePreviewElementInAsync(BottomContentMotionHost, startOffset, scaleFrom: 1f));

        if (!IsPreviewTransitionCurrent(version) && IsLoaded)
            ResetPreviewMotionHosts();
    }

    private Task AnimatePreviewElementOutAsync(UIElement element, double targetOffset, float scaleTo)
    {
        return AnimationBuilder.Create()
            .Opacity(to: 0, duration: PreviewTransitionOutDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn)
            .Translation(Axis.X, to: targetOffset, duration: PreviewTransitionOutDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn)
            .Scale(to: new System.Numerics.Vector3(scaleTo, scaleTo, 1f), duration: PreviewTransitionOutDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn)
            .StartAsync(element);
    }

    private Task AnimatePreviewElementInAsync(UIElement element, double startOffset, float scaleFrom)
    {
        return AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: PreviewTransitionInDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Translation(Axis.X, from: startOffset, to: 0, duration: PreviewTransitionInDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Scale(from: new System.Numerics.Vector3(scaleFrom, scaleFrom, 1f),
                to: System.Numerics.Vector3.One,
                duration: PreviewTransitionInDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .StartAsync(element);
    }

    private void PreparePreviewMotionForIncoming(double startOffset)
    {
        foreach (var element in GetPreviewMotionElements())
        {
            element.Opacity = 0;
            var scale = ReferenceEquals(element, CoverThumbBorder) ? 0.97f : 1f;
            AnimationBuilder.Create()
                .Translation(Axis.X, to: startOffset, duration: PreviewMotionResetDuration)
                .Scale(to: new System.Numerics.Vector3(scale, scale, 1f), duration: PreviewMotionResetDuration)
                .Start(element);
        }
    }

    private void CancelPreviewTransition(bool resetMotionHosts)
    {
        _previewTransitionVersion++;
        _queuedPreviewDelta = null;
        _isPreviewTransitioning = false;

        if (resetMotionHosts)
            ResetPreviewMotionHosts();
    }

    private bool IsPreviewTransitionCurrent(int version)
    {
        return version == _previewTransitionVersion && IsLoaded;
    }

    private void ResetPreviewMotionHosts()
    {
        foreach (var element in GetPreviewMotionElements())
        {
            element.Opacity = 1;
            AnimationBuilder.Create()
                .Translation(Axis.X, to: 0, duration: PreviewMotionResetDuration)
                .Scale(to: System.Numerics.Vector3.One, duration: PreviewMotionResetDuration)
                .Start(element);
        }
    }

    private UIElement[] GetPreviewMotionElements()
    {
        return [HeroMotionHost, CoverThumbBorder, TitleOverlay, BottomContentMotionHost];
    }

    // ── Active-track accessors ───────────────────────────────────────────────

    private void ClampPreviewTrackIndex(HomeSectionItem item)
    {
        if (item.PreviewTracks.Count == 0)
        {
            _previewTrackIndex = 0;
            return;
        }

        _previewTrackIndex = Math.Clamp(_previewTrackIndex, 0, item.PreviewTracks.Count - 1);
    }

    private HomeBaselinePreviewTrack? GetActivePreviewTrack(HomeSectionItem? item = null)
    {
        item ??= Item;
        if (item == null || item.PreviewTracks.Count == 0)
            return null;

        ClampPreviewTrackIndex(item);
        return item.PreviewTracks[_previewTrackIndex];
    }

    private string? GetActiveAudioPreviewUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.AudioPreviewUrl ?? item?.AudioPreviewUrl;
    }

    private string? GetActiveCanvasUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.CanvasUrl ?? item?.CanvasUrl;
    }

    private string? GetActiveHeroImageUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.CanvasThumbnailUrl
            ?? track?.CoverArtUrl
            ?? item?.HeroImageUrl
            ?? item?.CanvasThumbnailUrl
            ?? item?.BestLargeImageUrl;
    }
}
