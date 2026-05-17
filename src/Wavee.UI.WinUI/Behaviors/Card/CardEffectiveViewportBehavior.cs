using System;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Wavee.UI.WinUI.Controls.Cards;

namespace Wavee.UI.WinUI.Behaviors.Card;

/// <summary>
/// Attached behavior on <see cref="ContentCard"/> that owns the
/// <see cref="FrameworkElement.EffectiveViewportChanged"/> wiring: Loaded /
/// Unloaded subscription, per-realization prefetch state, and reporting whether
/// the card is currently inside the effective viewport.
///
/// <para>Two state resets matter on every recycle / re-attach:</para>
/// <list type="bullet">
///   <item><c>HasEffectiveViewport</c> / <c>IsInsideEffectiveViewport</c> are
///   reset so a navigation-cached page that re-attaches re-samples the viewport
///   instead of trusting the stale "outside" result from the previous attach
///   cycle.</item>
///   <item>The album / playlist prefetch single-shot flags are reset so an
///   <c>ItemsRepeater</c> recycle can re-arm prefetch for the next bound item.
///   The prefetcher service itself dedupes across the whole session, so re-fires
///   are cheap; the single-shot guard here is just a hot-path optimisation.</item>
/// </list>
///
/// <para>Wire-up: <c>card:CardEffectiveViewportBehavior.IsAttached="True"</c>
/// on the <see cref="ContentCard"/> root. The behavior raises the
/// <c>Prefetch</c> and <c>ViewportIntersectionChanged</c> events back through
/// <see cref="ContentCard"/>'s internal entry points
/// (<c>HandleViewportPrefetch</c> / <c>HandleViewportIntersectionChanged</c>),
/// keeping all consumer logic on the card.</para>
/// </summary>
public static class CardEffectiveViewportBehavior
{
    private const double AlbumPrefetchTriggerDistance = 500;
    private const string AlbumUriPrefix = "spotify:album:";
    private const string PlaylistUriPrefix = "spotify:playlist:";

    private static readonly ConditionalWeakTable<ContentCard, ViewportState> _states = new();

    private static readonly ILogger? _logger =
        Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger("CardEffectiveViewportBehavior");

    private sealed class ViewportState
    {
        public bool Attached;
        public bool AlbumPrefetchKicked;
        public bool PlaylistPrefetchKicked;
        public bool HasEffectiveViewport;
        public bool IsInsideEffectiveViewport = true;
        // [home-scroll] one-shot log gates per attach so each empty↔non-empty
        // transition fires once instead of flooding every viewport sample.
        public bool LoggedEmptyViewport;
        public bool LoggedNonEmptyViewport;
        public RoutedEventHandler? LoadedHandler;
        public RoutedEventHandler? UnloadedHandler;
        public TypedEventHandler<FrameworkElement, EffectiveViewportChangedEventArgs>? ViewportHandler;
    }

    public static readonly DependencyProperty IsAttachedProperty =
        DependencyProperty.RegisterAttached(
            "IsAttached",
            typeof(bool),
            typeof(CardEffectiveViewportBehavior),
            new PropertyMetadata(false, OnIsAttachedChanged));

    public static bool GetIsAttached(DependencyObject obj) => (bool)obj.GetValue(IsAttachedProperty);
    public static void SetIsAttached(DependencyObject obj, bool value) => obj.SetValue(IsAttachedProperty, value);

    private static void OnIsAttachedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentCard card) return;

        DetachLifecycle(card);

        if (e.NewValue is not true)
            return;

        AttachLifecycle(card);

        // If the card is already loaded by the time the DP flips, wire the
        // viewport handler immediately so we don't lose the first sample.
        if (card.IsLoaded)
            AttachViewport(card);
    }

    private static void AttachLifecycle(ContentCard card)
    {
        var state = _states.GetValue(card, static _ => new ViewportState());
        state.LoadedHandler ??= (sender, _) =>
        {
            if (sender is ContentCard c) AttachViewport(c);
        };
        state.UnloadedHandler ??= (sender, _) =>
        {
            if (sender is ContentCard c) DetachViewport(c);
        };
        card.Loaded += state.LoadedHandler;
        card.Unloaded += state.UnloadedHandler;
    }

    private static void DetachLifecycle(ContentCard card)
    {
        if (!_states.TryGetValue(card, out var state)) return;
        if (state.LoadedHandler is not null)
            card.Loaded -= state.LoadedHandler;
        if (state.UnloadedHandler is not null)
            card.Unloaded -= state.UnloadedHandler;
        DetachViewport(card);
    }

    private static void AttachViewport(ContentCard card)
    {
        if (!_states.TryGetValue(card, out var state)) return;
        if (state.Attached) return;

        state.ViewportHandler ??= OnEffectiveViewportChanged;
        card.EffectiveViewportChanged += state.ViewportHandler;
        state.Attached = true;
        // Re-arm the empty↔non-empty transition log so each fresh attach gets
        // a single log per direction.
        state.LoggedEmptyViewport = false;
        state.LoggedNonEmptyViewport = false;
    }

    private static void DetachViewport(ContentCard card)
    {
        if (!_states.TryGetValue(card, out var state)) return;
        if (state.Attached && state.ViewportHandler is not null)
        {
            card.EffectiveViewportChanged -= state.ViewportHandler;
            state.Attached = false;
        }

        // Re-arm prefetch on the next attach (ItemsRepeater recycle). The
        // prefetcher's own dedup HashSet still prevents a duplicate POST.
        state.AlbumPrefetchKicked = false;
        state.PlaylistPrefetchKicked = false;

        // EffectiveViewportChanged can report an empty / stale viewport while a
        // navigation-cached page is being detached. The next attach must take a
        // fresh viewport sample instead of trusting the old "outside viewport"
        // result and skipping reload.
        state.HasEffectiveViewport = false;
        state.IsInsideEffectiveViewport = true;

        // Mirror that reset onto the card so its image-loading guards line up.
        card.HandleViewportReset();
    }

    private static void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (sender is not ContentCard card) return;
        if (!_states.TryGetValue(card, out var state)) return;

        // Album metadata prefetch — fires once per realization when the card
        // is within AlbumPrefetchTriggerDistance px of the viewport. The
        // prefetcher itself dedupes URIs across the whole session, so the
        // single-shot guard here is just a hot-path optimisation. Runs
        // independently of the image-loading viewport check below; the
        // distance threshold is lenient (BringIntoViewDistance ≤ 500) so we
        // catch cards approaching the viewport, not just ones fully visible.
        if (!state.AlbumPrefetchKicked || !state.PlaylistPrefetchKicked)
        {
            var navUri = card.NavigationUri;
            if (!string.IsNullOrEmpty(navUri)
                && args.BringIntoViewDistanceX <= AlbumPrefetchTriggerDistance
                && args.BringIntoViewDistanceY <= AlbumPrefetchTriggerDistance)
            {
                if (!state.AlbumPrefetchKicked && navUri.StartsWith(AlbumUriPrefix, StringComparison.Ordinal))
                {
                    state.AlbumPrefetchKicked = true;
                    card.HandleViewportPrefetch(navUri, ContentCard.ViewportPrefetchKind.Album);
                }
                else if (!state.PlaylistPrefetchKicked && navUri.StartsWith(PlaylistUriPrefix, StringComparison.Ordinal))
                {
                    state.PlaylistPrefetchKicked = true;
                    card.HandleViewportPrefetch(navUri, ContentCard.ViewportPrefetchKind.Playlist);
                }
            }
        }

        if (!TryGetEffectiveViewportIntersection(card, args.EffectiveViewport, out var isInside))
        {
            // [home-scroll] log once per transition into the empty-sample
            // branch — distinguishes mid-scroll empty viewport samples
            // (ActualSize > 0 but evp == 0) from genuine page-reattach
            // (ActualSize <= 0).
            if (!state.LoggedEmptyViewport)
            {
                state.LoggedEmptyViewport = true;
                state.LoggedNonEmptyViewport = false;
                _logger?.LogDebug(
                    "[home-scroll] card.emptyViewport actualSize=({W:F0}x{H:F0}) evp=({EW:F0}x{EH:F0})",
                    card.ActualWidth, card.ActualHeight,
                    args.EffectiveViewport.Width, args.EffectiveViewport.Height);
            }

            // During page re-attach WinUI can raise this before layout has a
            // real size. That is an unknown viewport sample, not an offscreen
            // card; releasing here races the Loaded reload and leaves
            // placeholders behind.
            state.HasEffectiveViewport = false;
            state.IsInsideEffectiveViewport = true;
            card.HandleViewportIntersectionChanged(hasViewport: false, isInside: true);
            return;
        }

        if (!state.LoggedNonEmptyViewport)
        {
            state.LoggedNonEmptyViewport = true;
            state.LoggedEmptyViewport = false;
            _logger?.LogDebug(
                "[home-scroll] card.viewport actualSize=({W:F0}x{H:F0}) evp=({EW:F0}x{EH:F0}) inside={Inside}",
                card.ActualWidth, card.ActualHeight,
                args.EffectiveViewport.Width, args.EffectiveViewport.Height, isInside);
        }

        state.HasEffectiveViewport = true;
        state.IsInsideEffectiveViewport = isInside;
        card.HandleViewportIntersectionChanged(hasViewport: true, isInside: isInside);
    }

    private static bool TryGetEffectiveViewportIntersection(
        FrameworkElement element,
        Rect effectiveViewport,
        out bool intersects)
    {
        intersects = true;

        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        if (effectiveViewport.Width <= 0 || effectiveViewport.Height <= 0)
            return false;

        intersects = effectiveViewport.Right > 0
                     && effectiveViewport.Bottom > 0
                     && effectiveViewport.Left < element.ActualWidth
                     && effectiveViewport.Top < element.ActualHeight;
        return true;
    }
}
