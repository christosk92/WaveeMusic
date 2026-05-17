using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Controls.Imaging;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Behaviors.Card;

/// <summary>
/// Attached behavior on <see cref="CompositionImage"/> that owns "retry the cache
/// fetch once if it failed for this URL". Lives outside of <c>ContentCard</c> so
/// the same gate logic can be reused by other image-hosting controls without
/// inheriting the rest of ContentCard's state machine.
///
/// <para>Per-instance state — current retry URL + retry count — is stored in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed on the
/// <see cref="CompositionImage"/> instance, so element GC isn't blocked by static
/// bookkeeping. There are no per-scroll allocations: the holder is allocated
/// once at attach time and reused for the lifetime of the element.</para>
///
/// <para>Wire-up: set <c>card:CardImageRetryBehavior.Enable="True"</c> in XAML
/// on the image element, and subscribe to <see cref="RetryRequested"/> via
/// <see cref="AddRetryHandler"/> / <see cref="RemoveRetryHandler"/> from the host
/// control. The handler receives the failed URL and is expected to invoke its
/// own image-loading entry point (e.g. <c>ContentCard.LoadImage</c>).</para>
///
/// <para>The behavior gates retries on <see cref="ImageLoadingSuspension.IsSuspended"/>
/// — during list scrolls (suspension on), failed images stay failed; the consumer's
/// own reload pass picks them up when suspension lifts. This avoids hammering the
/// cache during scroll.</para>
/// </summary>
public static class CardImageRetryBehavior
{
    private static readonly ConditionalWeakTable<CompositionImage, RetryState> _states = new();

    private sealed class RetryState
    {
        public string? CurrentUrl;
        public int RetryCount;
        public EventHandler? ImageFailedHandler;
        public Action<string>? RetryRequestedHandler;
    }

    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable",
            typeof(bool),
            typeof(CardImageRetryBehavior),
            new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject obj) => (bool)obj.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject obj, bool value) => obj.SetValue(EnableProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CompositionImage image) return;

        // Detach any prior wiring first — DPs can flip multiple times during template apply.
        Detach(image);

        if (e.NewValue is true)
            Attach(image);
    }

    private static void Attach(CompositionImage image)
    {
        var state = new RetryState();
        state.ImageFailedHandler = (sender, _) => OnImageFailed(image, state);
        image.ImageFailed += state.ImageFailedHandler;
        _states.AddOrUpdate(image, state);
    }

    private static void Detach(CompositionImage image)
    {
        if (!_states.TryGetValue(image, out var state)) return;
        if (state.ImageFailedHandler != null)
            image.ImageFailed -= state.ImageFailedHandler;
        // Drop callback ref so the host control isn't kept alive transitively.
        state.RetryRequestedHandler = null;
        state.ImageFailedHandler = null;
        state.CurrentUrl = null;
        state.RetryCount = 0;
        _states.Remove(image);
    }

    private static void OnImageFailed(CompositionImage image, RetryState state)
    {
        // CompositionImage already cleared its own surface and invalidated the cache
        // entry before raising ImageFailed. We only own the "should we ask the host
        // to LoadImage again?" decision.
        var failedUrl = state.CurrentUrl;

        if (string.IsNullOrEmpty(failedUrl)
            || !image.IsLoaded
            || ImageLoadingSuspension.IsSuspended
            || state.RetryCount >= 1)
        {
            return;
        }

        state.RetryCount++;
        var handler = state.RetryRequestedHandler;
        if (handler is null) return;

        // Dispatch the actual reload — the host's LoadImage path will check its own
        // viewport gate / cache-state guards before issuing a new request.
        var dispatcher = image.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        dispatcher?.TryEnqueue(() =>
        {
            if (!image.IsLoaded
                || ImageLoadingSuspension.IsSuspended)
            {
                return;
            }

            handler(failedUrl);
        });
    }

    /// <summary>
    /// Records the URL the host most recently asked the image to load. Resets the
    /// retry counter when a new URL is set so a fresh URL gets its own single retry.
    /// </summary>
    public static void NotifyLoadStarted(CompositionImage image, string? url)
    {
        if (!_states.TryGetValue(image, out var state)) return;
        if (!string.Equals(state.CurrentUrl, url, StringComparison.Ordinal))
        {
            state.CurrentUrl = url;
            state.RetryCount = 0;
        }
    }

    /// <summary>Registers a callback invoked when the behavior decides a retry should fire.
    /// The callback receives the URL that previously failed.</summary>
    public static void AddRetryHandler(CompositionImage image, Action<string> handler)
    {
        if (!_states.TryGetValue(image, out var state)) return;
        state.RetryRequestedHandler = handler;
    }

    public static void RemoveRetryHandler(CompositionImage image)
    {
        if (!_states.TryGetValue(image, out var state)) return;
        state.RetryRequestedHandler = null;
    }
}
