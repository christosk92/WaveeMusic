using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.Behaviors;

/// <summary>
/// Gives below-the-fold detail-page sections (album / playlist / show / episode /
/// artist footer sections) the same fade + slide-up entrance the track rows get
/// from <see cref="StaggeredEntrance"/> — but revealed as the section <b>comes into
/// view</b> rather than at realization.
///
/// <para>Why view-driven, not <c>Loaded</c>-driven: footer sections sit inside an
/// <c>InRowsScroll</c> footer (a synthetic last row of the track grid's scroller),
/// so they are below the fold. A realization-time cascade would play off-screen and
/// never be seen. Revealing on view also covers the "appears one by one" case where
/// a section's data hydrates late (skeleton → real swap, or a late shelf) while
/// already on-screen.</para>
///
/// <para>Each element reveals <b>once</b>. Sections becoming visible within
/// <see cref="BurstGapMs"/> of one another get an incrementing stagger delay
/// (<see cref="StepMs"/>, capped at <see cref="MaxDelayMs"/>) so a short page or a
/// jump-to-footer cascades top-to-bottom; a section appearing later resets to delay 0
/// (a clean solo entrance). Honors the OS reduce-motion setting via
/// <see cref="ReducedMotion"/>.</para>
///
/// <para>Robustness: the reveal is driven by three signals — <see cref="FrameworkElement.SizeChanged"/>
/// (first measure / a <c>Visibility</c> flip to visible), <see cref="FrameworkElement.EffectiveViewportChanged"/>
/// (scroll), and a deferred post-load check — all funnelling through a manual
/// viewport-intersection test against the nearest scroll container. Relying on
/// <c>EffectiveViewportChanged</c> alone left sections stuck hidden until a nudge
/// scroll when the first sample arrived before the element had a size.</para>
///
/// <para>Wire-up: <c>entranceAnim:SectionStaggerEntrance.IsEntranceAnimated="True"</c>
/// on each section root element. Apply to both the skeleton and the real variant of
/// a paired section so the skeleton → real swap animates. Do <b>not</b> apply to
/// interactive self-managing controls (e.g. the discography <c>ExpandableAlbumGrid</c>)
/// or to virtualized track / episode list rows (those use <see cref="StaggeredEntrance"/>).</para>
/// </summary>
public static class SectionStaggerEntrance
{
    // Reveal slightly before the section is fully on-screen so the rise reads as
    // anticipatory rather than late.
    private const double ProximityPx = 140;
    private const long BurstGapMs = 110;   // reveals farther apart than this start a fresh (delay-0) burst
    private const double StepMs = 80;      // per-section stagger within a burst
    private const double MaxDelayMs = 600; // matches StaggeredEntrance's cap

    private static bool? _animationsEnabled;
    private static bool AnimationsEnabled => _animationsEnabled ??= ReducedMotion.AnimationsEnabled;

    // Shared burst cursor: reveals close together in time cascade; spread-out reveals
    // restart at delay 0. Only one detail page is ever revealing at a time.
    private static long _lastRevealTicks;
    private static int _burstIndex;

    private static readonly ConditionalWeakTable<FrameworkElement, SectionState> _states = new();

    private sealed class SectionState
    {
        public bool Revealed;
        public bool TriggersAttached;
        public RoutedEventHandler? LoadedHandler;
        public RoutedEventHandler? UnloadedHandler;
        public SizeChangedEventHandler? SizeChangedHandler;
        public TypedEventHandler<FrameworkElement, EffectiveViewportChangedEventArgs>? ViewportHandler;
    }

    public static readonly DependencyProperty IsEntranceAnimatedProperty =
        DependencyProperty.RegisterAttached(
            "IsEntranceAnimated", typeof(bool), typeof(SectionStaggerEntrance),
            new PropertyMetadata(false, OnIsEntranceAnimatedChanged));

    public static bool GetIsEntranceAnimated(DependencyObject o) => (bool)o.GetValue(IsEntranceAnimatedProperty);
    public static void SetIsEntranceAnimated(DependencyObject o, bool value) => o.SetValue(IsEntranceAnimatedProperty, value);

    private static void OnIsEntranceAnimatedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        DetachLifecycle(element);

        if (e.NewValue is not true) return;

        // Reduced motion: never hide, never animate — sections snap in as before.
        if (!AnimationsEnabled) return;

        AttachLifecycle(element);

        if (element.IsLoaded)
            OnElementLoaded(element);
    }

    private static void AttachLifecycle(FrameworkElement element)
    {
        var state = _states.GetValue(element, static _ => new SectionState());
        state.LoadedHandler ??= static (s, _) => { if (s is FrameworkElement fe) OnElementLoaded(fe); };
        state.UnloadedHandler ??= static (s, _) => { if (s is FrameworkElement fe) DetachTriggers(fe); };
        element.Loaded += state.LoadedHandler;
        element.Unloaded += state.UnloadedHandler;
    }

    private static void DetachLifecycle(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.LoadedHandler is not null) element.Loaded -= state.LoadedHandler;
        if (state.UnloadedHandler is not null) element.Unloaded -= state.UnloadedHandler;
        DetachTriggers(element);
    }

    private static void OnElementLoaded(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.Revealed) return; // already shown — a virtualization re-attach must not re-animate.

        // Hide until the section is on/near screen so there is no flash of
        // un-animated content. Off-screen, this is invisible anyway.
        EntranceAnimations.PrepareHidden(element);
        AttachTriggers(element);

        // Initial check once this layout pass settles — covers sections already in
        // view at load (SizeChanged / EffectiveViewportChanged may have fired their
        // first sample before the element had a size, leaving it stuck hidden).
        element.DispatcherQueue?.TryEnqueue(() => TryReveal(element));
    }

    private static void AttachTriggers(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.TriggersAttached) return;
        state.SizeChangedHandler ??= static (s, _) => { if (s is FrameworkElement fe) TryReveal(fe); };
        state.ViewportHandler ??= static (s, _) => TryReveal(s);
        element.SizeChanged += state.SizeChangedHandler;
        element.EffectiveViewportChanged += state.ViewportHandler;
        state.TriggersAttached = true;
    }

    private static void DetachTriggers(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (!state.TriggersAttached) return;
        if (state.SizeChangedHandler is not null) element.SizeChanged -= state.SizeChangedHandler;
        if (state.ViewportHandler is not null) element.EffectiveViewportChanged -= state.ViewportHandler;
        state.TriggersAttached = false;
    }

    private static void TryReveal(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.Revealed) return;

        try
        {
            // Wait until the element has a real size — Visibility=Collapsed or a
            // pre-measure sample reports 0 and must not consume the one-shot reveal.
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            if (!IsNearViewport(element, ProximityPx)) return;

            state.Revealed = true;
            DetachTriggers(element);
            EntranceAnimations.FadeSlideUp(element, NextStaggerDelayMs());
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // The element was torn down (fast navigation) between a queued check and
            // now — its CsWinRT projection is gone. Nothing to reveal; stop listening.
            DetachTriggers(element);
        }
    }

    /// <summary>
    /// Manual viewport-intersection test against the nearest scroll container.
    /// Reliable once the element is measured and in the live tree, unlike a lone
    /// <c>EffectiveViewportChanged</c> sample. Fails open (treats as visible) when
    /// no scroll container is found or the transform can't be computed.
    /// </summary>
    private static bool IsNearViewport(FrameworkElement element, double proximityPx)
    {
        var container = FindScrollContainer(element);
        if (container is null) return true;

        var viewportHeight = container.ActualHeight;
        var viewportWidth = container.ActualWidth;
        if (viewportHeight <= 0 || viewportWidth <= 0) return false;

        try
        {
            var transform = element.TransformToVisual(container);
            var bounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            // Sections stack vertically — gate on the vertical band only.
            return bounds.Bottom >= -proximityPx && bounds.Top <= viewportHeight + proximityPx;
        }
        catch
        {
            return true;
        }
    }

    private static FrameworkElement? FindScrollContainer(DependencyObject start)
    {
        var node = VisualTreeHelper.GetParent(start);
        while (node is not null)
        {
            if (node is ScrollView sv) return sv;
            if (node is ScrollViewer legacy) return legacy;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static double NextStaggerDelayMs()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var gapMs = (now - _lastRevealTicks) / TimeSpan.TicksPerMillisecond;
        _lastRevealTicks = now;

        if (gapMs > BurstGapMs)
            _burstIndex = 0;
        else
            _burstIndex++;

        return Math.Min(_burstIndex * StepMs, MaxDelayMs);
    }
}
