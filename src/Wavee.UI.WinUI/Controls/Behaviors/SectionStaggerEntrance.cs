using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.Behaviors;

/// <summary>
/// Gives below-the-fold detail-page sections (album / playlist / show / episode /
/// artist footer sections) the same fade + slide-up entrance the track rows get
/// from <see cref="StaggeredEntrance"/> — but triggered by the section <b>scrolling
/// into view</b> rather than by realization.
///
/// <para>Why viewport-triggered, not <c>Loaded</c>-triggered: footer sections sit
/// inside an <c>InRowsScroll</c> footer (a synthetic last row of the track grid's
/// scroller), so they are below the fold. A realization-time cascade would play
/// off-screen and never be seen. <see cref="FrameworkElement.EffectiveViewportChanged"/>
/// (the same signal virtualizing panels use, modelled here on
/// <c>CardEffectiveViewportBehavior</c>) reveals each section as it approaches the
/// viewport — and naturally covers the "appears one by one" case where a section's
/// data hydrates late (skeleton → real swap, or a late shelf) while already in view.</para>
///
/// <para>Each element reveals <b>once</b>. Sections crossing into view within
/// <see cref="BurstGapMs"/> of one another get an incrementing stagger delay
/// (<see cref="StepMs"/>, capped at <see cref="MaxDelayMs"/>) so a jump-to-footer
/// or short page cascades top-to-bottom; a section entering later resets to delay 0
/// (a clean solo entrance). Honors the OS reduce-motion setting via
/// <see cref="ReducedMotion"/>.</para>
///
/// <para>Wire-up: <c>behaviors:SectionStaggerEntrance.IsEntranceAnimated="True"</c>
/// on each section root element. Apply to both the skeleton and the real variant of
/// a paired section so the skeleton → real swap animates. Do <b>not</b> apply to
/// interactive self-managing controls (e.g. the discography <c>ExpandableAlbumGrid</c>)
/// or to virtualized track / episode list rows (those use <see cref="StaggeredEntrance"/>).</para>
/// </summary>
public static class SectionStaggerEntrance
{
    // Reveal slightly before the section is fully on-screen so the rise reads as
    // anticipatory rather than late. EffectiveViewportChanged reports how far the
    // element still is from the viewport in BringIntoViewDistance{X,Y}.
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
        public bool ViewportAttached;
        public RoutedEventHandler? LoadedHandler;
        public RoutedEventHandler? UnloadedHandler;
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
        state.UnloadedHandler ??= static (s, _) => { if (s is FrameworkElement fe) DetachViewport(fe); };
        element.Loaded += state.LoadedHandler;
        element.Unloaded += state.UnloadedHandler;
    }

    private static void DetachLifecycle(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.LoadedHandler is not null) element.Loaded -= state.LoadedHandler;
        if (state.UnloadedHandler is not null) element.Unloaded -= state.UnloadedHandler;
        DetachViewport(element);
    }

    private static void OnElementLoaded(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.Revealed) return; // already shown — a virtualization re-attach must not re-animate.

        // Hide until the section approaches the viewport so there is no flash of
        // un-animated content. Off-screen, this is invisible anyway.
        EntranceAnimations.PrepareHidden(element);
        AttachViewport(element);
    }

    private static void AttachViewport(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.ViewportAttached) return;
        state.ViewportHandler ??= OnEffectiveViewportChanged;
        element.EffectiveViewportChanged += state.ViewportHandler;
        state.ViewportAttached = true;
    }

    private static void DetachViewport(FrameworkElement element)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.ViewportAttached && state.ViewportHandler is not null)
        {
            element.EffectiveViewportChanged -= state.ViewportHandler;
            state.ViewportAttached = false;
        }
    }

    private static void OnEffectiveViewportChanged(FrameworkElement element, EffectiveViewportChangedEventArgs args)
    {
        if (!_states.TryGetValue(element, out var state)) return;
        if (state.Revealed) return;

        // Skip empty / pre-layout samples (the element has no real size yet, or the
        // viewport itself is empty during a navigation re-attach). Stay subscribed and
        // wait for a real sample — same caution as CardEffectiveViewportBehavior.
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var vp = args.EffectiveViewport;
        if (vp.Width <= 0 || vp.Height <= 0) return;

        if (args.BringIntoViewDistanceX > ProximityPx || args.BringIntoViewDistanceY > ProximityPx)
            return; // not close enough to the viewport yet.

        state.Revealed = true;
        DetachViewport(element);
        EntranceAnimations.FadeSlideUp(element, NextStaggerDelayMs());
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
