using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Controls.Imaging;

namespace Wavee.UI.WinUI.Behaviors.Track;

/// <summary>
/// Attached behavior on <see cref="CompositionImage"/> that performs ONE retry
/// per URL after an <see cref="CompositionImage.ImageFailed"/> event, then gives
/// up. Prevents infinite retry loops on a genuinely broken URL while still
/// recovering from transient surface-cache / CDN glitches that race with
/// virtualized-row recycle.
///
/// Owner contract: the host (typically <c>TrackItem</c>) registers a retry
/// callback via <see cref="Attach"/> with the URL it just bound. The behavior
/// invokes the callback on the dispatcher thread when a fail occurs AND the
/// URL hasn't already been retried. The callback is expected to re-set the
/// image URL through the host's normal "Apply*AlbumArt" code path so cache
/// invalidation and dedup checks run again.
///
/// State (per-element retry latch) is kept in a
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> so it's collected with the
/// host image — no manual teardown required. Call <see cref="Reset"/> when the
/// bound URL changes so the new URL gets its own one-shot retry budget.
/// </summary>
public static class TrackImageRetryBehavior
{
    private static readonly ConditionalWeakTable<CompositionImage, State> _states = new();

    private sealed class State
    {
        /// <summary>The URL we've already retried once after ImageFailed. Reset when
        /// the URL changes so a new URL gets its own retry budget.</summary>
        public string? RetriedUrl;
        public Action<string>? OnRetry;
        public bool Subscribed;
    }

    /// <summary>
    /// Wires the <paramref name="image"/>'s <see cref="CompositionImage.ImageFailed"/>
    /// event to a one-shot retry that invokes <paramref name="onRetry"/> with the
    /// URL that just failed. Idempotent — calling twice with different callbacks
    /// updates the callback but doesn't double-subscribe.
    /// </summary>
    public static void Attach(CompositionImage? image, Action<string> onRetry)
    {
        if (image is null || onRetry is null) return;
        var state = _states.GetValue(image, static _ => new State());
        state.OnRetry = onRetry;
        if (!state.Subscribed)
        {
            image.ImageFailed += OnImageFailed;
            state.Subscribed = true;
        }
    }

    /// <summary>
    /// Drops the cached "already-retried URL" for <paramref name="image"/> so the
    /// next failure gets a fresh retry. Call when the bound URL changes.
    /// </summary>
    public static void Reset(CompositionImage? image)
    {
        if (image is null) return;
        if (!_states.TryGetValue(image, out var state)) return;
        state.RetriedUrl = null;
    }

    private static void OnImageFailed(object? sender, EventArgs e)
    {
        if (sender is not CompositionImage image) return;
        if (!_states.TryGetValue(image, out var state)) return;

        // The current URL is what the host most recently set on the
        // CompositionImage. If it's been cleared between fail and dispatch (the
        // row recycled), there's nothing to retry.
        var url = image.ImageUrl;
        if (string.IsNullOrEmpty(url)) return;

        var alreadyRetried = string.Equals(state.RetriedUrl, url, StringComparison.Ordinal);
        state.RetriedUrl = url;

        // Reset the surface so the placeholder shows through while we retry (or
        // give up). Matches the legacy behaviour where the host always reset
        // the image's visibility/opacity before deciding whether to dispatch.
        image.ImageUrl = null;
        image.Visibility = Visibility.Visible;
        image.Opacity = 1;
        if (alreadyRetried) return;

        // CompositionImage already invalidated the cache entry. Re-set the URL
        // via the host's Apply* code path to trigger a fresh GetOrCreate.
        var callback = state.OnRetry;
        if (callback is null) return;
        image.DispatcherQueue?.TryEnqueue(() => callback(url));
    }
}
